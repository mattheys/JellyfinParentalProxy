using System.Collections.Concurrent;
using Dapper;
using Domain.Interfaces;
using Domain.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReverseProxy;

/// <summary>
/// Thread-safe, SQLite-backed rating cache.
///
/// Reads are served entirely from an in-memory <see cref="ConcurrentDictionary"/>
/// for zero-latency hot-path access.  Writes go to both the dictionary and the
/// database immediately so the SQLite file is always authoritative on disk.
///
/// Manual overrides (set from the WebAdmin UI) take priority over automatic
/// TMDB lookups and are never overwritten by background workers.
/// </summary>
public sealed class RatingCache : IRatingCache
{
    // -------------------------------------------------------------------------
    // Internal model
    // -------------------------------------------------------------------------

    private sealed record CacheRow(
        string JellyfinId,
        string Rating,
        string? ItemName,
        string? ItemType,
        int IsManualOverride,      // SQLite has no bool; 0/1
        long? LastTmdbAttemptUnix
    )
    { public CacheRow() : this(string.Empty, string.Empty, null, null, 0, null) { } }

    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------

    private readonly string   _connectionString;
    private readonly TimeSpan _retryCooldown;
    private readonly ILogger<RatingCache> _log;

    /// <summary>In-memory mirror of the SQLite table for lock-free reads.</summary>
    private readonly ConcurrentDictionary<string, CacheRow> _map =
        new(StringComparer.OrdinalIgnoreCase);

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    public RatingCache(IOptions<ProxyOptions> options, ILogger<RatingCache> log)
    {
        var opts = options.Value;
        _connectionString = $"Data Source={opts.CachePath};Cache=Shared;";
        _retryCooldown    = TimeSpan.FromHours(opts.TmdbRetryHours);
        _log              = log;
    }

    // -------------------------------------------------------------------------
    // Initialisation (called once at startup)
    // -------------------------------------------------------------------------

    public async Task InitialiseAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // Migrate: add new columns if they don't exist yet (idempotent)
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS RatingCache (
                JellyfinId          TEXT    NOT NULL PRIMARY KEY,
                Rating              TEXT    NOT NULL,
                ItemName            TEXT,
                ItemType            TEXT,
                IsManualOverride    INTEGER NOT NULL DEFAULT 0,
                LastTmdbAttemptUnix INTEGER
            );
            """);

        var rows = await conn.QueryAsync<CacheRow>(
            "SELECT JellyfinId, Rating, ItemName, ItemType, IsManualOverride, LastTmdbAttemptUnix FROM RatingCache");

        foreach (var row in rows)
            _map[row.JellyfinId] = row;

        _log.LogInformation("Rating cache initialised: {Count} entries loaded from SQLite", _map.Count);
    }

    // -------------------------------------------------------------------------
    // IRatingCache — Read
    // -------------------------------------------------------------------------

    public AgeRating? GetRating(string jellyfinId)
    {
        if (_map.TryGetValue(jellyfinId, out var row))
            return Enum.TryParse<AgeRating>(row.Rating, out var r) ? r : AgeRating.Unrated;
        return null;
    }

    public bool ShouldLookup(string jellyfinId)
    {
        if (!_map.TryGetValue(jellyfinId, out var row))
            return true; // Never seen

        if (row.IsManualOverride == 1)
            return false; // Manual overrides are authoritative — never re-query

        if (row.LastTmdbAttemptUnix is null)
            return false; // Confirmed TMDB rating — no retry needed

        var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - row.LastTmdbAttemptUnix.Value;
        return elapsed >= (long)_retryCooldown.TotalSeconds;
    }

    public IReadOnlyList<CacheEntry> GetAllEntries()
    {
        return _map.Values.ToList()
            .Select(row => new CacheEntry(
                JellyfinId:      row.JellyfinId,
                ItemName:        row.ItemName,
                ItemType:        row.ItemType,
                Rating:          Enum.TryParse<AgeRating>(row.Rating, out var r) ? r : AgeRating.Unrated,
                IsManualOverride:row.IsManualOverride == 1,
                IsPendingLookup: row.LastTmdbAttemptUnix.HasValue && row.IsManualOverride == 0,
                LastAttemptUtc:  row.LastTmdbAttemptUnix.HasValue
                                    ? DateTimeOffset.FromUnixTimeSeconds(row.LastTmdbAttemptUnix.Value)
                                    : null))
            .OrderBy(e => e.ItemName ?? e.JellyfinId)
            .ToList();
    }

    // -------------------------------------------------------------------------
    // IRatingCache — Write
    // -------------------------------------------------------------------------

    public async Task StoreRatingAsync(string jellyfinId, AgeRating rating,
        string? itemName = null, string? itemType = null)
    {
        // Don't overwrite a manual override with an automatic one
        if (_map.TryGetValue(jellyfinId, out var existing) && existing.IsManualOverride == 1)
        {
            _log.LogDebug("Skipping automatic rating update for {Id} — manual override is set", jellyfinId);
            return;
        }

        var row = new CacheRow(jellyfinId, rating.ToString(),
            itemName ?? existing?.ItemName,
            itemType ?? existing?.ItemType,
            IsManualOverride: 0,
            LastTmdbAttemptUnix: null);

        _map[jellyfinId] = row;
        await UpsertAsync(row);
        _log.LogInformation("Cached rating {Rating} for Jellyfin item {Id}", rating, jellyfinId);
    }

    public async Task RecordFailedLookupAsync(string jellyfinId)
    {
        _map.TryGetValue(jellyfinId, out var existing);

        var row = new CacheRow(
            jellyfinId,
            AgeRating.Unrated.ToString(),
            existing?.ItemName,
            existing?.ItemType,
            IsManualOverride: 0,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        _map[jellyfinId] = row;
        await UpsertAsync(row);
    }

    public async Task StoreManualOverrideAsync(string jellyfinId, AgeRating rating,
        string? itemName = null, string? itemType = null)
    {
        _map.TryGetValue(jellyfinId, out var existing);

        var row = new CacheRow(
            jellyfinId,
            rating.ToString(),
            itemName ?? existing?.ItemName,
            itemType ?? existing?.ItemType,
            IsManualOverride: 1,
            LastTmdbAttemptUnix: null);

        _map[jellyfinId] = row;
        await UpsertAsync(row);
        _log.LogInformation("Manual override set: {Rating} for Jellyfin item {Id}", rating, jellyfinId);
    }

    public async Task RemoveOverrideAsync(string jellyfinId)
    {
        if (_map.TryGetValue(jellyfinId, out var existing))
        {
            // Reset to unrated + failed timestamp so it will be re-queried
            var row = new CacheRow(
                jellyfinId,
                AgeRating.Unrated.ToString(),
                existing.ItemName,
                existing.ItemType,
                IsManualOverride: 0,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            _map[jellyfinId] = row;
            await UpsertAsync(row);
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task UpsertAsync(CacheRow row)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            await conn.ExecuteAsync("""
                INSERT INTO RatingCache
                    (JellyfinId, Rating, ItemName, ItemType, IsManualOverride, LastTmdbAttemptUnix)
                VALUES
                    (@JellyfinId, @Rating, @ItemName, @ItemType, @IsManualOverride, @LastTmdbAttemptUnix)
                ON CONFLICT(JellyfinId) DO UPDATE SET
                    Rating              = excluded.Rating,
                    ItemName            = COALESCE(excluded.ItemName, RatingCache.ItemName),
                    ItemType            = COALESCE(excluded.ItemType, RatingCache.ItemType),
                    IsManualOverride    = excluded.IsManualOverride,
                    LastTmdbAttemptUnix = excluded.LastTmdbAttemptUnix;
                """, row);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to upsert cache entry for {Id}", row.JellyfinId);
        }
    }
}
