using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReverseProxy;

/// <summary>
/// In the SQLite implementation, writes are committed immediately in
/// <see cref="RatingCache.UpsertAsync"/>, so there is nothing to flush
/// periodically.  This hosted service is kept as a convenient place for future
/// housekeeping (e.g. vacuuming old entries).
/// </summary>
public sealed class CacheFlushService : BackgroundService
{
    private readonly ILogger<CacheFlushService> _log;

    public CacheFlushService(ILogger<CacheFlushService> log) => _log = log;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogDebug("CacheFlushService started (no-op for SQLite backend)");
        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }
}
