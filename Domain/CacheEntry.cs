namespace Domain;

/// <summary>
/// A single entry in the rating cache, returned for UI display and API consumers.
/// </summary>
public sealed record CacheEntry(
    string  JellyfinId,
    string? ItemName,
    string? ItemType,
    AgeRating Rating,
    bool    IsManualOverride,
    bool    IsPendingLookup,
    DateTimeOffset? LastAttemptUtc
);
