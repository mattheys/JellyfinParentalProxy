using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain;
using Domain.Interfaces;
using Domain.Models;
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
///   4. For Episodes and Seasons, always uses the parent Series rating.
/// </summary>
public sealed class ParentalFilterMiddleware
{
    // Jellyfin item types that carry an age rating
    private static readonly HashSet<string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Movie", "Series", "Episode", "Season", "Trailer", "Program" };

    // Item types that should inherit from their parent Series when unrated
    private static readonly HashSet<string> SeriesChildTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Episode", "Season" };

    private static readonly string[] StreamingRoutePrefixes =
        ["/Videos", "/Audio", "/LiveTv", "/hls", "/Sync"];

    private readonly RequestDelegate                    _next;
    private readonly IConfigurationService              _configurationService;
    private readonly string                             _defaultMaxRating;
    private readonly bool                               _defaultRewritePlaybackUrlsToDownstream;
    private readonly IRatingCache                       _cache;
    private readonly ITmdbLookupQueue                   _queue;
    private readonly IHttpClientFactory                 _httpClientFactory;
    private readonly string                             _jellyfinUrl;
    private readonly IBypassService                     _bypassService;
    private readonly ILogger<ParentalFilterMiddleware>  _log;

    public ParentalFilterMiddleware(
        RequestDelegate                    next,
        IOptions<ProxyOptions>             options,
        IConfigurationService              configurationService,
        IRatingCache                       cache,
        ITmdbLookupQueue                   queue,
        IBypassService                     bypassService,
        IHttpClientFactory                 httpClientFactory,
        ILogger<ParentalFilterMiddleware>  log)
    {
        _next              = next;
        _configurationService = configurationService;
        _defaultMaxRating  = options.Value.MaxRating;
        _defaultRewritePlaybackUrlsToDownstream = options.Value.RewritePlaybackUrlsToDownstream;
        _cache             = cache;
        _queue             = queue;
        _httpClientFactory = httpClientFactory;
        _jellyfinUrl       = options.Value.JellyfinUrl;
        _bypassService     = bypassService;
        _log               = log;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_bypassService.GetBypassState)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        var isPlaybackInfoRequest = IsPlaybackInfoRequest(path);
        var shouldRewritePlaybackUrls = isPlaybackInfoRequest && ShouldRewritePlaybackUrlsToDownstream();

        if (IsStreamingRequest(path)
            || (!ShouldInspectForFiltering(path) && !shouldRewritePlaybackUrls))
        {
            await _next(context);
            return;
        }

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
            JsonNode result = json;
            var wasModified = false;

            if (ShouldInspectForFiltering(path))
            {
                var authHeader = context.Request.Headers.Authorization.ToString();
                var filtered = await FilterJsonAsync(result, authHeader);
                result = filtered.Result;
                wasModified = filtered.WasModified;
            }

            if (shouldRewritePlaybackUrls)
                wasModified = RewritePlaybackUrls(result) || wasModified;

            if (wasModified)
            {
                var outBytes = JsonSerializer.SerializeToUtf8Bytes(result);
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

    private static bool IsStreamingRequest(string path)
    {
        foreach (var prefix in StreamingRoutePrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return path.Contains("/stream", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldInspectForFiltering(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (IsPlaybackInfoRequest(path))
            return false;

        return path.Contains("/Items", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Shows", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Movies", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Series", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Users", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Genres", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Studios", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Artists", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlaybackInfoRequest(string path) =>
        path.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase);

    private bool ShouldRewritePlaybackUrlsToDownstream()
    {
        var configured = _configurationService.GetValue(
            nameof(ProxyOptions.RewritePlaybackUrlsToDownstream),
            _defaultRewritePlaybackUrlsToDownstream ? "true" : "false");

        return bool.TryParse(configured, out var enabled)
            ? enabled
            : _defaultRewritePlaybackUrlsToDownstream;
    }

    private bool RewritePlaybackUrls(JsonNode json)
    {
        if (json is not JsonObject obj || obj["MediaSources"] is not JsonArray mediaSources)
            return false;

        var modified = false;
        foreach (var mediaSource in mediaSources)
        {
            if (mediaSource is not JsonObject source)
                continue;

            modified = RewritePlaybackUrlField(source, "DirectStreamUrl") || modified;
            modified = RewritePlaybackUrlField(source, "TranscodingUrl") || modified;
        }

        return modified;
    }

    private bool RewritePlaybackUrlField(JsonObject source, string fieldName)
    {
        var rawValue = source[fieldName]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(rawValue))
            return false;

        if (Uri.TryCreate(rawValue, UriKind.Absolute, out _))
            return false;

        if (!Uri.TryCreate($"{_jellyfinUrl.TrimEnd('/')}/", UriKind.Absolute, out var baseUri))
            return false;

        if (!Uri.TryCreate(baseUri, rawValue, out var absolute))
            return false;

        source[fieldName] = absolute.ToString();
        return true;
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

        var jellyfinId = item["Id"]?.GetValue<string>() ?? string.Empty;
        var itemName = item["Name"]?.GetValue<string>() ?? "Unknown";
        var jellyfinRating = item["OfficialRating"]?.GetValue<string>();
        var parsedRating = AgeRatingParser.Parse(jellyfinRating);

        // --- Determine the effective rating ---

        AgeRating effectiveRating = AgeRating.Unrated;
        string? parentSeriesId = null;
        string? parentSeriesName = null;

        if (SeriesChildTypes.Contains(itemType))
        {
            parentSeriesId = item["SeriesId"]?.GetValue<string>()
                ?? item["ParentId"]?.GetValue<string>();
            parentSeriesName = item["SeriesName"]?.GetValue<string>() ?? "Unknown Series";

            if (parsedRating != AgeRating.Unrated)
            {
                effectiveRating = parsedRating;
            }
            else
            {
                var seriesResolution = await ResolveSeriesRatingAsync(item, authHeader);
                effectiveRating = seriesResolution.Rating;
                parentSeriesId = seriesResolution.SeriesId;
                parentSeriesName = seriesResolution.SeriesName;
            }

            if (effectiveRating != AgeRating.Unrated && !string.IsNullOrEmpty(jellyfinId))
            {
                await _cache.StoreRatingAsync(
                    jellyfinId,
                    effectiveRating,
                    itemName,
                    itemType,
                    parentSeriesId);
            }
        }
        else if (parsedRating != AgeRating.Unrated)
        {
            effectiveRating = parsedRating;
            if (!string.IsNullOrEmpty(jellyfinId))
                await _cache.StoreRatingAsync(jellyfinId, parsedRating, itemName, itemType);
        }
        else if (!string.IsNullOrEmpty(jellyfinId) &&
                 _cache.GetRating(jellyfinId) is AgeRating cached)
        {
            effectiveRating = cached;
        }

        // --- Enqueue a TMDB lookup if still unresolved ---

        if (effectiveRating == AgeRating.Unrated)
        {
            if (SeriesChildTypes.Contains(itemType)
                && !string.IsNullOrEmpty(parentSeriesId)
                && _cache.ShouldLookup(parentSeriesId))
            {
                var seriesTmdbId = item["SeriesProviderIds"]?["Tmdb"]?.GetValue<string>()
                    ?? item["ProviderIds"]?["Tmdb"]?.GetValue<string>();
                var lookupName = parentSeriesName ?? "Unknown Series";

                _queue.TryEnqueue(new TmdbLookupRequest(
                    parentSeriesId,
                    seriesTmdbId,
                    "Series",
                    lookupName,
                    authHeader));
            }
            else if (!SeriesChildTypes.Contains(itemType)
                && !string.IsNullOrEmpty(jellyfinId)
                && _cache.ShouldLookup(jellyfinId))
            {
                var tmdbIdStr = item["ProviderIds"]?["Tmdb"]?.GetValue<string>();

                _queue.TryEnqueue(new TmdbLookupRequest(
                    jellyfinId,
                    tmdbIdStr,
                    itemType,
                    itemName,
                    authHeader));
            }
        }

        var maxAllowed = GetCurrentMaxAllowed();
        var allowed = effectiveRating <= maxAllowed;

        if (!allowed)
        {
            _log.LogDebug(
                "Blocked {Type} '{Name}' (id={Id}) — effective rating {Rating}",
                itemType,
                itemName,
                jellyfinId,
                effectiveRating);
        }

        return allowed;
    }

    private AgeRating GetCurrentMaxAllowed()
    {
        var configuredMaxRating = _configurationService.GetValue(nameof(ProxyOptions.MaxRating), _defaultMaxRating);
        return AgeRatingParser.Parse(configuredMaxRating);
    }

    // -------------------------------------------------------------------------
    // Series rating inheritance
    // -------------------------------------------------------------------------

    /// <summary>
    /// For an Episode or Season, resolves the rating via:
    ///   1. The SeriesId field on the item itself (cache lookup).
    ///   2. Jellyfin series metadata.
    /// </summary>
    private async Task<(AgeRating Rating, string? SeriesId, string? SeriesName)> ResolveSeriesRatingAsync(
        JsonNode item,
        string authHeader)
    {
        var seriesId = item["SeriesId"]?.GetValue<string>()
            ?? item["ParentId"]?.GetValue<string>();
        var seriesName = item["SeriesName"]?.GetValue<string>() ?? "Unknown Series";

        if (string.IsNullOrEmpty(seriesId))
            return (AgeRating.Unrated, null, seriesName);

        // Check the cache first (fast path — no network call)
        if (_cache.GetRating(seriesId) is AgeRating cachedSeriesRating)
            return (cachedSeriesRating, seriesId, seriesName);

        // Not cached — try to fetch the Series item from Jellyfin and cache it
        var seriesRating = await FetchSeriesRatingFromJellyfinAsync(seriesId, authHeader);

        if (seriesRating != AgeRating.Unrated)
        {
            await _cache.StoreRatingAsync(seriesId, seriesRating, seriesName, "Series");
            return (seriesRating, seriesId, seriesName);
        }

        return (AgeRating.Unrated, seriesId, seriesName);
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
