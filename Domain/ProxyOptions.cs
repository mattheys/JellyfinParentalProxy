namespace ReverseProxy;

public sealed class ProxyOptions
{
    public string JellyfinUrl { get; set; } = "http://127.0.0.1:8096";

    public string MaxRating { get; set; } = "PG-13";

    public string? TmdbApiKey { get; set; }

    public string TmdbRegion { get; set; } = "US";

    public int TmdbRetryHours { get; set; } = 24;

    public string CachePath { get; set; } = "rating_cache.db";

    public int LogBufferSize { get; set; } = 500;
}
