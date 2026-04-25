namespace Domain;

/// <summary>
/// Abstracts the rating cache so that both the proxy middleware and the
/// WebAdmin UI can share the same contract without taking a hard dependency
/// on the ReverseProxy assembly.
/// </summary>
public interface IRatingCache
{
    // ── Read ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the cached <see cref="AgeRating"/> for the given Jellyfin item,
    /// or <c>null</c> if the item has never been seen before.
    /// </summary>
    AgeRating? GetRating(string jellyfinId);

    /// <summary>
    /// Returns <c>true</c> when a background TMDB lookup should be fired for
    /// this item (never seen, or cooldown has expired).
    /// </summary>
    bool ShouldLookup(string jellyfinId);

    /// <summary>
    /// Returns all cache entries for display in the admin UI.
    /// </summary>
    IReadOnlyList<CacheEntry> GetAllEntries();

    // ── Write ────────────────────────────────────────────────────────────────

    /// <summary>Stores a successfully resolved rating from TMDB.</summary>
    Task StoreRatingAsync(string jellyfinId, AgeRating rating, string? itemName = null, string? itemType = null);

    /// <summary>Records that a TMDB lookup was attempted but yielded no result.</summary>
    Task RecordFailedLookupAsync(string jellyfinId);

    /// <summary>
    /// Stores a manually-set rating override from the admin UI.
    /// Manual overrides are never overwritten by automatic TMDB lookups.
    /// </summary>
    Task StoreManualOverrideAsync(string jellyfinId, AgeRating rating, string? itemName = null, string? itemType = null);

    /// <summary>Removes a manual override so automatic lookup can resume.</summary>
    Task RemoveOverrideAsync(string jellyfinId);
}
