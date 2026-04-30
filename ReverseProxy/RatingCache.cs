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
        long? LastTmdbAttemptUnix,
        string? ParentSeriesId
    )
    { public CacheRow() : this(string.Empty, string.Empty, null, null, 0, null) { } }

    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------

    private string   _connectionString;
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
        _connectionString = $"Data Source={opts.DatabasePath};Cache=Shared;";
        _retryCooldown    = TimeSpan.FromHours(opts.TmdbRetryHours);
        _log              = log;
    }

    // -------------------------------------------------------------------------
    // Initialisation (called once at startup)
    // -------------------------------------------------------------------------

    public async Task InitialiseAsync(string databasePath)
    {
        _connectionString = $"Data Source={databasePath};Cache=Shared;";
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
                LastTmdbAttemptUnix INTEGER,
                ParentSeriesId      TEXT
            );
            """);

        await EnsureColumnExistsAsync(conn, "ParentSeriesId", "TEXT");

        var rows = await conn.QueryAsync<CacheRow>(
            "SELECT JellyfinId, Rating, ItemName, ItemType, IsManualOverride, LastTmdbAttemptUnix, ParentSeriesId FROM RatingCache");

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
                ParentSeriesId:  row.ParentSeriesId,
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

    public async Task StoreRatingAsync(
        string jellyfinId,
        AgeRating rating,
        string? itemName = null,
        string? itemType = null,
        string? parentSeriesId = null)
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
            LastTmdbAttemptUnix: null,
            ParentSeriesId: parentSeriesId ?? existing?.ParentSeriesId);

        _map[jellyfinId] = row;
        await UpsertAsync(row);

        if (string.Equals(row.ItemType, "Series", StringComparison.OrdinalIgnoreCase))
            await PropagateSeriesRatingAsync(row.JellyfinId, rating, isManualOverride: false);

        _log.LogInformation("Cached rating {Rating} for Jellyfin item {Id}", rating, jellyfinId);
    }

    public async Task RecordFailedLookupAsync(
        string jellyfinId,
        string? itemName = null,
        string? itemType = null,
        string? parentSeriesId = null)
    {
        _map.TryGetValue(jellyfinId, out var existing);

        var row = new CacheRow(
            jellyfinId,
            AgeRating.Unrated.ToString(),
            itemName ?? existing?.ItemName,
            itemType ?? existing?.ItemType,
            IsManualOverride: 0,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            parentSeriesId ?? existing?.ParentSeriesId);

        _map[jellyfinId] = row;
        await UpsertAsync(row);
    }

    public async Task StoreManualOverrideAsync(
        string jellyfinId,
        AgeRating rating,
        string? itemName = null,
        string? itemType = null,
        string? parentSeriesId = null)
    {
        _map.TryGetValue(jellyfinId, out var existing);

        var row = new CacheRow(
            jellyfinId,
            rating.ToString(),
            itemName ?? existing?.ItemName,
            itemType ?? existing?.ItemType,
            IsManualOverride: 1,
            LastTmdbAttemptUnix: null,
            ParentSeriesId: parentSeriesId ?? existing?.ParentSeriesId);

        _map[jellyfinId] = row;
        await UpsertAsync(row);

        if (string.Equals(row.ItemType, "Series", StringComparison.OrdinalIgnoreCase))
            await PropagateSeriesRatingAsync(row.JellyfinId, rating, isManualOverride: true);

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
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                existing.ParentSeriesId);

            _map[jellyfinId] = row;
            await UpsertAsync(row);

            if (string.Equals(existing.ItemType, "Series", StringComparison.OrdinalIgnoreCase))
                await ResetSeriesChildrenForLookupAsync(jellyfinId);
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
                    (JellyfinId, Rating, ItemName, ItemType, IsManualOverride, LastTmdbAttemptUnix, ParentSeriesId)
                VALUES
                    (@JellyfinId, @Rating, @ItemName, @ItemType, @IsManualOverride, @LastTmdbAttemptUnix, @ParentSeriesId)
                ON CONFLICT(JellyfinId) DO UPDATE SET
                    Rating              = excluded.Rating,
                    ItemName            = COALESCE(excluded.ItemName, RatingCache.ItemName),
                    ItemType            = COALESCE(excluded.ItemType, RatingCache.ItemType),
                    IsManualOverride    = excluded.IsManualOverride,
                    LastTmdbAttemptUnix = excluded.LastTmdbAttemptUnix,
                    ParentSeriesId      = COALESCE(excluded.ParentSeriesId, RatingCache.ParentSeriesId);
                """, row);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to upsert cache entry for {Id}", row.JellyfinId);
        }
    }

    private async Task PropagateSeriesRatingAsync(string seriesId, AgeRating rating, bool isManualOverride)
    {
        var affectedChildren = _map.Values
            .Where(row => string.Equals(row.ParentSeriesId, seriesId, StringComparison.OrdinalIgnoreCase)
                && (isManualOverride || row.IsManualOverride == 0))
            .Select(row => new CacheRow(
                row.JellyfinId,
                rating.ToString(),
                row.ItemName,
                row.ItemType,
                isManualOverride ? 1 : 0,
                LastTmdbAttemptUnix: null,
                ParentSeriesId: row.ParentSeriesId))
            .ToList();

        foreach (var childRow in affectedChildren)
        {
            _map[childRow.JellyfinId] = childRow;
            await UpsertAsync(childRow);
        }
    }

    private async Task ResetSeriesChildrenForLookupAsync(string seriesId)
    {
        var retryAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var affectedChildren = _map.Values
            .Where(row => string.Equals(row.ParentSeriesId, seriesId, StringComparison.OrdinalIgnoreCase))
            .Select(row => new CacheRow(
                row.JellyfinId,
                AgeRating.Unrated.ToString(),
                row.ItemName,
                row.ItemType,
                IsManualOverride: 0,
                LastTmdbAttemptUnix: retryAt,
                ParentSeriesId: row.ParentSeriesId))
            .ToList();

        foreach (var childRow in affectedChildren)
        {
            _map[childRow.JellyfinId] = childRow;
            await UpsertAsync(childRow);
        }
    }

    private static async Task EnsureColumnExistsAsync(SqliteConnection conn, string columnName, string columnType)
    {
        var exists = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM pragma_table_info('RatingCache') WHERE name = @ColumnName;",
            new { ColumnName = columnName });

        if (exists == 0)
            await conn.ExecuteAsync($"ALTER TABLE RatingCache ADD COLUMN {columnName} {columnType};");
    }
}
