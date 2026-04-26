namespace Domain.Models;

/// <summary>
/// A work item placed onto the TMDB lookup queue.
/// </summary>
public sealed record TmdbLookupRequest(
    string  JellyfinId,
    string? TmdbIdStr,
    string  ItemType,
    string  ItemName,
    string  AuthHeader
);
