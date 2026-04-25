using System.Collections.Concurrent;
using Dapper;
using Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReverseProxy;

/// <summary>
/// Thread-safe, SQLite-backed rating cache.
///
/// Reads are served entirely from an in-memory <see cref="ConcurrentDictionary"/>
/// for zero-latency hot-path access.  Writes go to both the dictionary and the
/// database immediately, so the SQLite file is always authoritative on disk.
/// </summary>
public sealed class RatingCache
{
    // -------------------------------------------------------------------------
    // Internal model
    // -------------------------------------------------------------------------

    private sealed record CacheRow(
        string JellyfinId,
        string Rating,               // AgeRating enum name
        long?  LastTmdbAttemptUnix   // null = confirmed rating; set = pending / failed
    );

    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------

    private readonly string _connectionString;
    private readonly TimeSpan _retryCooldown;
    private readonly ILogger<RatingCache> _log;

    /// <summary>In-memory mirror of the SQLite table for lock-free reads.</summary>
    private readonly ConcurrentDictionary<string, CacheRow> _map = new(StringComparer.OrdinalIgnoreCase);

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

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS RatingCache (
                JellyfinId          TEXT    NOT NULL PRIMARY KEY,
                Rating              TEXT    NOT NULL,
                LastTmdbAttemptUnix INTEGER
            );
            """);

        // Load all existing rows into the in-memory dictionary.
        var rows = await conn.QueryAsync<CacheRow>(
            "SELECT JellyfinId, Rating, LastTmdbAttemptUnix FROM RatingCache");

        foreach (var row in rows)
            _map[row.JellyfinId] = row;

        _log.LogInformation("Rating cache initialised: {Count} entries loaded from SQLite", _map.Count);
    }

    // -------------------------------------------------------------------------
    // Read API (lock-free)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the cached <see cref="AgeRating"/> for the given Jellyfin item,
    /// or <c>null</c> if the item has never been seen before.
    /// </summary>
    public AgeRating? GetRating(string jellyfinId)
    {
        if (_map.TryGetValue(jellyfinId, out var row))
            return Enum.TryParse<AgeRating>(row.Rating, out var r) ? r : AgeRating.Unrated;

        return null;
    }

    /// <summary>
    /// Returns <c>true</c> when a background TMDB lookup should be fired for
    /// this item right now.
    /// </summary>
    public bool ShouldLookup(string jellyfinId)
    {
        if (!_map.TryGetValue(jellyfinId, out var row))
            return true; // Never seen

        if (row.LastTmdbAttemptUnix is null)
            return false; // Confirmed rating — no retry needed

        // Retry once the cooldown has expired
        var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - row.LastTmdbAttemptUnix.Value;
        return elapsed >= (long)_retryCooldown.TotalSeconds;
    }

    // -------------------------------------------------------------------------
    // Write API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Stores a successfully resolved rating.  Sets <c>LastTmdbAttemptUnix</c>
    /// to <c>null</c> so we never retry unnecessarily.
    /// </summary>
    public async Task StoreRatingAsync(string jellyfinId, AgeRating rating)
    {
        var row = new CacheRow(jellyfinId, rating.ToString(), null);
        _map[jellyfinId] = row;
        await UpsertAsync(row);
        _log.LogInformation("Cached rating {Rating} for Jellyfin item {Id}", rating, jellyfinId);
    }

    /// <summary>
    /// Records that a TMDB lookup was attempted but yielded no rating.
    /// Stamps the current time so the retry cooldown is respected.
    /// </summary>
    public async Task RecordFailedLookupAsync(string jellyfinId)
    {
        var row = new CacheRow(
            jellyfinId,
            AgeRating.Unrated.ToString(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        _map[jellyfinId] = row;
        await UpsertAsync(row);
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
                INSERT INTO RatingCache (JellyfinId, Rating, LastTmdbAttemptUnix)
                VALUES (@JellyfinId, @Rating, @LastTmdbAttemptUnix)
                ON CONFLICT(JellyfinId) DO UPDATE SET
                    Rating              = excluded.Rating,
                    LastTmdbAttemptUnix = excluded.LastTmdbAttemptUnix;
                """, row);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to upsert cache entry for {Id}", row.JellyfinId);
        }
    }
}
