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
/// For JSON responses that contain media items it:
///   1. Removes items whose effective rating exceeds <see cref="ProxyOptions.MaxRating"/>.
///   2. For unrated items checks the local cache for a TMDB-resolved rating.
///   3. Enqueues background TMDB lookups (via <see cref="ITmdbLookupQueue"/>) for
///      items that have never been seen or whose retry cooldown has expired.
///   4. For Episodes and Seasons, falls back to the parent Series rating when no
///      individual rating is available.
/// </summary>
public sealed class ParentalFilterMiddleware
{
    // Jellyfin item types that carry an age rating
    private static readonly HashSet<string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Movie", "Series", "Episode", "Season", "Trailer", "Program" };

    // Item types that should inherit from their parent Series when unrated
    private static readonly HashSet<string> SeriesChildTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Episode", "Season" };

    private readonly RequestDelegate                    _next;
    private readonly AgeRating                          _maxAllowed;
    private readonly IRatingCache                       _cache;
    private readonly ITmdbLookupQueue                   _queue;
    private readonly IHttpClientFactory                 _httpClientFactory;
    private readonly string                             _jellyfinUrl;
    private readonly ILogger<ParentalFilterMiddleware>  _log;

    public ParentalFilterMiddleware(
        RequestDelegate                    next,
        IOptions<ProxyOptions>             options,
        IRatingCache                       cache,
        ITmdbLookupQueue                   queue,
        IHttpClientFactory                 httpClientFactory,
        ILogger<ParentalFilterMiddleware>  log)
    {
        _next              = next;
        _cache             = cache;
        _queue             = queue;
        _httpClientFactory = httpClientFactory;
        _jellyfinUrl       = options.Value.JellyfinUrl;
        _log               = log;
        _maxAllowed        = AgeRatingParser.Parse(options.Value.MaxRating);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try   { await _next(context); }
        finally { context.Response.Body = originalBody; }

        buffer.Seek(0, SeekOrigin.Begin);

        var contentType = context.Response.ContentType ?? string.Empty;
        if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await buffer.CopyToAsync(originalBody);
            return;
        }

        // Decompress if needed
        var contentEncoding = context.Response.Headers.ContentEncoding.ToString() ?? string.Empty;
        Stream readStream = buffer;
        IDisposable? streamToDispose = null;

        if (contentEncoding.Contains("br", StringComparison.OrdinalIgnoreCase))
        {
            var br = new BrotliStream(buffer, CompressionMode.Decompress, leaveOpen: true);
            readStream = br; streamToDispose = br;
        }
        else if (contentEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            var gz = new GZipStream(buffer, CompressionMode.Decompress, leaveOpen: true);
            readStream = gz; streamToDispose = gz;
        }
        else if (contentEncoding.Contains("deflate", StringComparison.OrdinalIgnoreCase))
        {
            var df = new DeflateStream(buffer, CompressionMode.Decompress, leaveOpen: true);
            readStream = df; streamToDispose = df;
        }

        string body = string.Empty;
        try
        {
            using var reader = new StreamReader(readStream, Encoding.UTF8, leaveOpen: true);
            body = await reader.ReadToEndAsync();
        }
        catch { }
        finally { streamToDispose?.Dispose(); }

        JsonNode? json = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try { json = JsonNode.Parse(body); }
            catch { }
        }

        if (json is not null)
        {
            var authHeader = context.Request.Headers.Authorization.ToString();
            var (filtered, wasModified) = await FilterJsonAsync(json, authHeader);

            if (wasModified)
            {
                var outBytes = JsonSerializer.SerializeToUtf8Bytes(filtered);
                context.Response.Headers.Remove("Transfer-Encoding");
                context.Response.Headers.Remove("Content-Encoding");
                context.Response.ContentLength = outBytes.Length;
                await originalBody.WriteAsync(outBytes);
                return;
            }
        }

        buffer.Seek(0, SeekOrigin.Begin);
        await buffer.CopyToAsync(originalBody);
    }

    // -------------------------------------------------------------------------
    // Filtering logic
    // -------------------------------------------------------------------------

    private async Task<(JsonNode Result, bool WasModified)> FilterJsonAsync(
        JsonNode json, string authHeader)
    {
        if (json is JsonObject obj && obj["Items"]?.AsArray() is JsonArray items)
        {
            var (filtered, removed) = await FilterArrayAsync(items, authHeader);
            obj["Items"] = filtered;

            if (obj["TotalRecordCount"] is JsonValue totalNode &&
                totalNode.TryGetValue<int>(out var total))
                obj["TotalRecordCount"] = Math.Max(0, total - removed);

            return (obj, removed > 0);
        }

        if (json is JsonArray rawArray)
        {
            var (filtered, removed) = await FilterArrayAsync(rawArray, authHeader);
            return (filtered, removed > 0);
        }

        return (json, false);
    }

    private async Task<(JsonArray Filtered, int RemovedCount)> FilterArrayAsync(
        JsonArray items, string authHeader)
    {
        var filtered = new JsonArray();
        var removed  = 0;

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
            return true;

        var jellyfinId     = item["Id"]?.GetValue<string>()             ?? string.Empty;
        var jellyfinRating = item["OfficialRating"]?.GetValue<string>();
        var parsedRating   = AgeRatingParser.Parse(jellyfinRating);
        var isUnrated      = parsedRating == AgeRating.Unrated;

        // --- Determine the effective rating ---

        AgeRating effectiveRating;

        if (!isUnrated)
        {
            // The item carries its own Jellyfin rating — use it directly.
            effectiveRating = parsedRating;
        }
        else if (!string.IsNullOrEmpty(jellyfinId) &&
                 _cache.GetRating(jellyfinId) is AgeRating cached)
        {
            // We have a cached (TMDB-resolved or manual) rating.
            effectiveRating = cached;
        }
        else if (SeriesChildTypes.Contains(itemType))
        {
            // Episode / Season with no individual rating — try the parent Series.
            effectiveRating = await ResolveSeriesRatingAsync(item, authHeader);
        }
        else
        {
            effectiveRating = AgeRating.Unrated;
        }

        // --- Enqueue a TMDB lookup if still unresolved ---

        if (effectiveRating == AgeRating.Unrated
            && !string.IsNullOrEmpty(jellyfinId)
            && _cache.ShouldLookup(jellyfinId))
        {
            // For episodes/seasons, look up the parent Series ID on TMDB.
            // For everything else, look up by the item's own TMDB ID.
            var tmdbIdStr = item["ProviderIds"]?["Tmdb"]?.GetValue<string>();
            var itemName  = item["Name"]?.GetValue<string>() ?? "Unknown";

            _queue.TryEnqueue(new TmdbLookupRequest(
                jellyfinId, tmdbIdStr, itemType, itemName, authHeader));
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
    // Series rating inheritance
    // -------------------------------------------------------------------------

    /// <summary>
    /// For an Episode or Season, tries to resolve the rating via:
    ///   1. The SeriesId field on the item itself (cache lookup).
    ///   2. Falling back to the item's own Jellyfin OfficialRating (already
    ///      checked as Unrated at this point, so this is a no-op guard).
    /// </summary>
    private async Task<AgeRating> ResolveSeriesRatingAsync(JsonNode item, string authHeader)
    {
        // Jellyfin typically populates SeriesId on Episode items.
        var seriesId = item["SeriesId"]?.GetValue<string>();

        if (!string.IsNullOrEmpty(seriesId))
        {
            // Check the cache first (fast path — no network call)
            if (_cache.GetRating(seriesId) is AgeRating cachedSeriesRating)
                return cachedSeriesRating;

            // Not cached — try to fetch the Series item from Jellyfin and cache it
            var seriesName   = item["SeriesName"]?.GetValue<string>() ?? "Unknown Series";
            var seriesRating = await FetchSeriesRatingFromJellyfinAsync(seriesId, authHeader);

            if (seriesRating != AgeRating.Unrated)
            {
                // Cache the Jellyfin-supplied rating so subsequent episode/season
                // requests for the same series are served from memory.
                await _cache.StoreRatingAsync(seriesId, seriesRating, seriesName, "Series");
                return seriesRating;
            }

            // Jellyfin had no rating either — enqueue a TMDB lookup for the Series
            if (_cache.ShouldLookup(seriesId))
            {
                _queue.TryEnqueue(new TmdbLookupRequest(
                    seriesId, null, "Series", seriesName, authHeader));
            }
        }

        return AgeRating.Unrated;
    }

    private async Task<AgeRating> FetchSeriesRatingFromJellyfinAsync(
        string seriesId, string authHeader)
    {
        if (string.IsNullOrWhiteSpace(authHeader))
            return AgeRating.Unrated;

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", authHeader);

            var url      = $"{_jellyfinUrl.TrimEnd('/')}/Items/{seriesId}";
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
                return AgeRating.Unrated;

            var jsonString     = await response.Content.ReadAsStringAsync();
            var json           = JsonNode.Parse(jsonString);
            var officialRating = json?["OfficialRating"]?.GetValue<string>();

            return AgeRatingParser.Parse(officialRating);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to retrieve Series metadata from Jellyfin for {Id}", seriesId);
            return AgeRating.Unrated;
        }
    }
}
