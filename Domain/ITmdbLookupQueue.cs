namespace Domain;

/// <summary>
/// A bounded, non-blocking queue for background TMDB rating lookups.
/// </summary>
public interface ITmdbLookupQueue
{
    /// <summary>
    /// Tries to enqueue a lookup request.  Returns <c>false</c> if the queue
    /// is full; the caller should not retry immediately.
    /// </summary>
    bool TryEnqueue(TmdbLookupRequest request);

    /// <summary>
    /// Waits asynchronously for the next item and returns it.
    /// Blocks until an item is available or the token is cancelled.
    /// </summary>
    ValueTask<TmdbLookupRequest> DequeueAsync(CancellationToken cancellationToken);
}
