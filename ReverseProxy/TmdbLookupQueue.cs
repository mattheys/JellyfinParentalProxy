using System.Threading.Channels;
using Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReverseProxy;

/// <summary>
/// Bounded, non-blocking FIFO queue for background TMDB rating lookups.
///
/// Uses <see cref="System.Threading.Channels"/> so that:
/// - Producers (middleware) never block — <see cref="TryEnqueue"/> drops items
///   when the channel is full rather than stalling a proxied HTTP request.
/// - Consumers (<see cref="TmdbLookupWorker"/>) wait efficiently without
///   spinning.
/// </summary>
public sealed class TmdbLookupQueue : ITmdbLookupQueue
{
    private readonly Channel<TmdbLookupRequest> _channel;
    private readonly ILogger<TmdbLookupQueue>   _log;

    public TmdbLookupQueue(IOptions<ProxyOptions> options, ILogger<TmdbLookupQueue> log)
    {
        _log = log;
        var capacity = Math.Max(1, options.Value.TmdbQueueCapacity);

        _channel = Channel.CreateBounded<TmdbLookupRequest>(new BoundedChannelOptions(capacity)
        {
            FullMode       = BoundedChannelFullMode.DropOldest,
            SingleWriter   = false,   // Many request threads may produce concurrently
            SingleReader   = false,   // Multiple worker tasks read concurrently
        });
    }

    /// <inheritdoc/>
    public bool TryEnqueue(TmdbLookupRequest request)
    {
        if (_channel.Writer.TryWrite(request))
            return true;

        _log.LogDebug(
            "TMDB queue full — dropped lookup for '{Name}' ({Id})",
            request.ItemName, request.JellyfinId);
        return false;
    }

    /// <inheritdoc/>
    public ValueTask<TmdbLookupRequest> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);
}
