using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReverseProxy;

/// <summary>
/// ASP.NET Core middleware that sits in front of YARP.
///
/// For JSON responses that contain media items, it:
///   1. Removes items whose effective rating exceeds <see cref="ProxyOptions.MaxRating"/>.
///   2. For unrated items, checks the local cache for a TMDB-resolved rating.
///   3. Fires background TMDB lookups for items that have never been seen or
///      whose retry cooldown has expired.
/// </summary>
public sealed class ParentalFilterMiddleware
{
    // Jellyfin item types that carry an age rating
    private static readonly HashSet<string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Movie", "Series", "Episode", "Trailer", "Program" };

    private readonly RequestDelegate _next;
    private readonly AgeRating _maxAllowed;
    private readonly RatingCache _cache;
    private readonly TmdbService _tmdb;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _jellyfinUrl;
    private readonly ILogger<ParentalFilterMiddleware> _log;

    public ParentalFilterMiddleware(
        RequestDelegate next,
        IOptions<ProxyOptions> options,
        RatingCache cache,
        TmdbService tmdb,
        IHttpClientFactory httpClientFactory,
        ILogger<ParentalFilterMiddleware> log)
    {
        _next = next;
        _cache = cache;
        _tmdb = tmdb;
        _httpClientFactory = httpClientFactory;
        _jellyfinUrl = options.Value.JellyfinUrl;
        _log = log;
        _maxAllowed = AgeRatingParser.Parse(options.Value.MaxRating);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            // Let YARP (or any other downstream middleware) run.
            await _next(context);
        }
        finally
        {
            // Always restore the original stream so we can write back to it.
            context.Response.Body = originalBody;
        }

        buffer.Seek(0, SeekOrigin.Begin);

        // Only attempt filtering on JSON responses.
        var contentType = context.Response.ContentType ?? string.Empty;
        if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await buffer.CopyToAsync(originalBody);
            return;
        }

        // Check if the payload is compressed
        var contentEncoding = context.Response.Headers.ContentEncoding.ToString() ?? string.Empty;
        Stream readStream = buffer;
        IDisposable? streamToDispose = null;

        if (contentEncoding.Contains("br", StringComparison.OrdinalIgnoreCase))
        {
            var br = new BrotliStream(buffer, CompressionMode.Decompress, leaveOpen: true);
            readStream = br;
            streamToDispose = br;
        }
        else if (contentEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            var gz = new GZipStream(buffer, CompressionMode.Decompress, leaveOpen: true);
            readStream = gz;
            streamToDispose = gz;
        }
        else if (contentEncoding.Contains("deflate", StringComparison.OrdinalIgnoreCase))
        {
            var df = new DeflateStream(buffer, CompressionMode.Decompress, leaveOpen: true);
            readStream = df;
            streamToDispose = df;
        }

        // Read the optionally decompressed body
        string body = string.Empty;
        try
        {
            using var reader = new StreamReader(readStream, Encoding.UTF8, leaveOpen: true);
            body = await reader.ReadToEndAsync();
        }
        catch
        {
            // Ignore decompression/read errors; we'll fallback to passing the original buffer through
        }
        finally
        {
            streamToDispose?.Dispose();
        }

        JsonNode? json = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try { json = JsonNode.Parse(body); }
            catch { /* Ignore parse errors */ }
        }

        if (json is not null)
        {
            // Capture the auth header so we can query Jellyfin later if we need to
            var authHeader = context.Request.Headers.Authorization.ToString();

            var (filtered, wasModified) = await FilterJsonAsync(json, authHeader);

            if (wasModified)
            {
                var outBytes = JsonSerializer.SerializeToUtf8Bytes(filtered);

                // If we are supplying a modified, fixed-length uncompressed payload, 
                // we MUST remove chunking and encoding headers to avoid protocol violations.
                context.Response.Headers.Remove("Transfer-Encoding");
                context.Response.Headers.Remove("Content-Encoding");

                context.Response.ContentLength = outBytes.Length;
                await originalBody.WriteAsync(outBytes);
                return;
            }
        }

        // If the payload was unmodified OR couldn't be parsed,
        // leave headers completely intact and write the exact original buffered bytes back.
        buffer.Seek(0, SeekOrigin.Begin);
        await buffer.CopyToAsync(originalBody);
    }

    // -------------------------------------------------------------------------
    // Filtering logic
    // -------------------------------------------------------------------------

    private async Task<(JsonNode Result, bool WasModified)> FilterJsonAsync(JsonNode json, string authHeader)
    {
        // Scenario 1: QueryResult  — { "Items": [...], "TotalRecordCount": N }
        if (json is JsonObject obj && obj["Items"]?.AsArray() is JsonArray items)
        {
            var (filtered, removed) = await FilterArrayAsync(items, authHeader);
            obj["Items"] = filtered;

            if (obj["TotalRecordCount"] is JsonValue totalNode &&
                totalNode.TryGetValue<int>(out var total))
            {
                obj["TotalRecordCount"] = Math.Max(0, total - removed);
            }

            return (obj, removed > 0);
        }

        // Scenario 2: Raw array  — Next Up, Latest Media, etc.
        if (json is JsonArray rawArray)
        {
            var (filtered, removed) = await FilterArrayAsync(rawArray, authHeader);
            return (filtered, removed > 0);
        }

        return (json, false);
    }

    private async Task<(JsonArray Filtered, int RemovedCount)> FilterArrayAsync(JsonArray items, string authHeader)
    {
        var filtered = new JsonArray();
        var removed = 0;

        foreach (var item in items)
        {
            if (item is null) continue;

            if (await ShouldAllowAsync(item, authHeader))
                filtered.Add(item.DeepClone());
            else
                removed++;
        }

        return (filtered, removed);
    }

    private async Task<bool> ShouldAllowAsync(JsonNode item, string authHeader)
    {
        var itemType = item["Type"]?.GetValue<string>() ?? string.Empty;

        if (!MediaTypes.Contains(itemType))
            return true; // Non-media items always pass through.

        var jellyfinId = item["Id"]?.GetValue<string>() ?? string.Empty;
        var jellyfinRating = item["OfficialRating"]?.GetValue<string>();
        var parsedRating = AgeRatingParser.Parse(jellyfinRating);
        var isUnrated = parsedRating == AgeRating.Unrated;

        // Determine the effective rating.
        AgeRating effectiveRating;

        if (!isUnrated)
        {
            effectiveRating = parsedRating;
        }
        else if (!string.IsNullOrEmpty(jellyfinId) && _cache.GetRating(jellyfinId) is AgeRating cached)
        {
            effectiveRating = cached;
        }
        else
        {
            effectiveRating = AgeRating.Unrated;
        }

        // Fire background TMDB lookup if appropriate.
        if (effectiveRating == AgeRating.Unrated
            && !string.IsNullOrEmpty(jellyfinId)
            && _tmdb.IsEnabled
            && _cache.ShouldLookup(jellyfinId))
        {
            var tmdbIdStr = item["ProviderIds"]?["Tmdb"]?.GetValue<string>();
            var itemName = item["Name"]?.GetValue<string>() ?? "Unknown";

            // Pass down to the background task (which will fetch the ID if missing)
            _ = Task.Run(() => TmdbLookupAsync(jellyfinId, tmdbIdStr, itemType, itemName, authHeader));
        }

        var allowed = effectiveRating <= _maxAllowed;

        if (!allowed)
        {
            _log.LogDebug(
                "Blocked {Type} '{Name}' (id={Id}) — effective rating {Rating}",
                itemType,
                item["Name"]?.GetValue<string>() ?? "Unknown",
                jellyfinId,
                effectiveRating);
        }

        return allowed;
    }

    // -------------------------------------------------------------------------
    // Background TMDB lookup
    // -------------------------------------------------------------------------

    private async Task TmdbLookupAsync(string jellyfinId, string? tmdbIdStr, string itemType, string itemName, string authHeader)
    {
        long tmdbId = 0;

        // Try to parse the ID from the payload, otherwise fall back to asking Jellyfin
        if (!long.TryParse(tmdbIdStr, out tmdbId))
        {
            tmdbId = await FetchTmdbIdFromJellyfinAsync(jellyfinId, authHeader);
        }

        if (tmdbId == 0)
        {
            _log.LogDebug("Could not resolve TMDB ID for {Type} '{Name}' (id={JellyfinId}). Marking as failed.", itemType, itemName, jellyfinId);
            await _cache.RecordFailedLookupAsync(jellyfinId);
            return;
        }

        _log.LogDebug("TMDB lookup: {Type} '{Name}' (tmdb_id={TmdbId})", itemType, itemName, tmdbId);

        AgeRating? rating = itemType switch
        {
            "Movie" or "Trailer" => await _tmdb.GetMovieRatingAsync(tmdbId),
            "Series" or "Episode" or "Program" => await _tmdb.GetTvRatingAsync(tmdbId),
            _ => null,
        };

        if (rating.HasValue)
        {
            _log.LogInformation(
                "TMDB resolved {Type} '{Name}' → {Rating}", itemType, itemName, rating.Value);

            await _cache.StoreRatingAsync(jellyfinId, rating.Value);
        }
        else
        {
            _log.LogWarning(
                "TMDB found no rating for {Type} '{Name}' (tmdb_id={TmdbId})",
                itemType, itemName, tmdbId);

            await _cache.RecordFailedLookupAsync(jellyfinId);
        }
    }

    private async Task<long> FetchTmdbIdFromJellyfinAsync(string jellyfinId, string authHeader)
    {
        if (string.IsNullOrWhiteSpace(authHeader))
            return 0;

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", authHeader);

            var url = $"{_jellyfinUrl.TrimEnd('/')}/Items/{jellyfinId}";
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
                return 0;

            var jsonString = await response.Content.ReadAsStringAsync();
            var json = JsonNode.Parse(jsonString);

            var idStr = json?["ProviderIds"]?["Tmdb"]?.GetValue<string>();
            return long.TryParse(idStr, out var tmdbId) ? tmdbId : 0;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to retrieve full item metadata from Jellyfin for {Id}", jellyfinId);
            return 0;
        }
    }
}