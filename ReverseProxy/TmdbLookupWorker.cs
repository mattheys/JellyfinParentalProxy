using Domain.Interfaces;
using Domain.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReverseProxy;

/// <summary>
/// Hosted service that drains the <see cref="ITmdbLookupQueue"/> using a
/// configurable number of concurrent workers (<see cref="ProxyOptions.TmdbWorkerCount"/>).
///
/// Replaces the fire-and-forget <c>Task.Run</c> calls that previously flooded
/// the proxy under heavy load.
/// </summary>
public sealed class TmdbLookupWorker : BackgroundService
{
    private readonly ITmdbLookupQueue             _queue;
    private readonly IRatingCache                 _cache;
    private readonly TmdbService                  _tmdb;
    private readonly IHttpClientFactory           _httpClientFactory;
    private readonly string                       _jellyfinUrl;
    private readonly int                          _workerCount;
    private readonly ILogger<TmdbLookupWorker>    _log;

    public TmdbLookupWorker(
        ITmdbLookupQueue           queue,
        IRatingCache               cache,
        TmdbService                tmdb,
        IHttpClientFactory         httpClientFactory,
        IOptions<ProxyOptions>     options,
        ILogger<TmdbLookupWorker>  log)
    {
        _queue             = queue;
        _cache             = cache;
        _tmdb              = tmdb;
        _httpClientFactory = httpClientFactory;
        _jellyfinUrl       = options.Value.JellyfinUrl;
        _workerCount       = Math.Max(1, options.Value.TmdbWorkerCount);
        _log               = log;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("TMDB lookup worker started with {Count} concurrent worker(s)", _workerCount);

        var workers = Enumerable
            .Range(0, _workerCount)
            .Select(i => RunWorkerAsync(i, stoppingToken));

        return Task.WhenAll(workers);
    }

    // -------------------------------------------------------------------------
    // Worker loop
    // -------------------------------------------------------------------------

    private async Task RunWorkerAsync(int workerId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TmdbLookupRequest request;
            try
            {
                request = await _queue.DequeueAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await ProcessRequestAsync(request);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "[Worker {Id}] Unhandled error processing TMDB lookup for '{Name}' ({JellyfinId})",
                    workerId, request.ItemName, request.JellyfinId);
            }
        }

        _log.LogDebug("[Worker {Id}] TMDB lookup worker stopped", workerId);
    }

    // -------------------------------------------------------------------------
    // Lookup logic (moved from ParentalFilterMiddleware)
    // -------------------------------------------------------------------------

    private async Task ProcessRequestAsync(TmdbLookupRequest req)
    {
        long tmdbId = 0;
        
        if (!long.TryParse(req.TmdbIdStr, out tmdbId))
            tmdbId = await FetchTmdbIdFromJellyfinAsync(req.JellyfinId, req.AuthHeader);

        if (tmdbId == 0)
        {
            _log.LogDebug(
                "Could not resolve TMDB ID for {Type} '{Name}' ({JellyfinId}). Marking as failed.",
                req.ItemType, req.ItemName, req.JellyfinId);
            await _cache.RecordFailedLookupAsync(req.JellyfinId);
            return;
        }

        _log.LogDebug("TMDB lookup: {Type} '{Name}' (tmdb_id={TmdbId})", req.ItemType, req.ItemName, tmdbId);

        AgeRating? rating = req.ItemType switch
        {
            "Movie" or "Trailer"                    => await _tmdb.GetMovieRatingAsync(tmdbId),
            "Series" or "Episode" or "Program"      => await _tmdb.GetTvRatingAsync(tmdbId),
            _                                       => null,
        };

        if (rating.HasValue)
        {
            _log.LogInformation(
                "TMDB resolved {Type} '{Name}' → {Rating}", req.ItemType, req.ItemName, rating.Value);

            await _cache.StoreRatingAsync(req.JellyfinId, rating.Value, req.ItemName, req.ItemType);
        }
        else
        {
            _log.LogWarning(
                "TMDB found no rating for {Type} '{Name}' (tmdb_id={TmdbId})",
                req.ItemType, req.ItemName, tmdbId);

            await _cache.RecordFailedLookupAsync(req.JellyfinId);
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

            var url      = $"{_jellyfinUrl.TrimEnd('/')}/Items/{jellyfinId}";
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
                return 0;

            var jsonString = await response.Content.ReadAsStringAsync();
            var json       = System.Text.Json.Nodes.JsonNode.Parse(jsonString);

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
