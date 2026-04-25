using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReverseProxy;

/// <summary>
/// In the SQLite implementation, writes are committed immediately in
/// <see cref="RatingCache.UpsertAsync"/>, so there is nothing to flush
/// periodically.  This hosted service exists as a convenient place for any
/// future housekeeping (e.g. vacuuming old entries) and to satisfy the DI
/// registration in Program.cs.
/// </summary>
public sealed class CacheFlushService : BackgroundService
{
    private readonly ILogger<CacheFlushService> _log;

    public CacheFlushService(ILogger<CacheFlushService> log) => _log = log;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogDebug("CacheFlushService started (no-op for SQLite backend)");

        // Sleep indefinitely — add periodic housekeeping here if needed.
        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }
}
