namespace Domain.Models;

/// <summary>
/// All configurable options for the Jellyfin Parental Proxy.
/// Values are populated from appsettings.json / environment variables.
/// </summary>
public sealed class ProxyOptions
{
    public string JellyfinUrl { get; set; } = "http://127.0.0.1:8096";

    public string MaxRating { get; set; } = "PG-13";

    public string? TmdbApiKey { get; set; }

    public string TmdbRegion { get; set; } = "US";

    public int TmdbRetryHours { get; set; } = 24;

    public string DatabasePath { get; set; } = "rating_cache.db";

    public int LogBufferSize { get; set; } = 500;

    /// <summary>
    /// Maximum number of concurrent TMDB lookup workers.
    /// Prevents the background queue from flooding the proxy under heavy load.
    /// </summary>
    public int TmdbWorkerCount { get; set; } = 2;

    /// <summary>
    /// Maximum number of pending TMDB lookup items in the queue.
    /// Items beyond this limit are dropped until the queue drains.
    /// </summary>
    public int TmdbQueueCapacity { get; set; } = 500;
}
