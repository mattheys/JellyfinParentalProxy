using System.Collections.Concurrent;
using Dapper;
using Domain.Interfaces;
using Domain.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReverseProxy;

/// <summary>
/// Database-backed configuration service that stores settings in SQLite.
/// </summary>
public sealed class ConfigurationService : IConfigurationService
{
    private readonly string _connectionString;
    private readonly ILogger<ConfigurationService> _log;
    private readonly ConcurrentDictionary<string, string> _cache;

    public ConfigurationService(IOptions<ProxyOptions> options, ILogger<ConfigurationService> log)
    {
        var opts = options.Value;
        _connectionString = $"Data Source={opts.DatabasePath};Cache=Shared;";
        _log = log;
        _cache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Initializes the configuration service and creates the database table if needed.
    /// </summary>
    public async Task InitializeAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // Create configuration table if it doesn't exist
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS Configuration (
                Key   TEXT NOT NULL PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """);

        // Load existing configuration into cache
        var rows = await conn.QueryAsync<(string Key, string Value)>("SELECT Key, Value FROM Configuration");
        foreach (var row in rows)
        {
            _cache[row.Key] = row.Value;
        }

        _log.LogInformation("Configuration service initialized with {Count} entries loaded from SQLite", _cache.Count);
    }

    public string GetValue(string key, string defaultValue = "")
    {
        if (_cache.TryGetValue(key, out var value))
            return value;
            
        return defaultValue;
    }

    public async Task SetValueAsync(string key, string value)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            
            await conn.ExecuteAsync("""
                INSERT INTO Configuration (Key, Value)
                VALUES (@Key, @Value)
                ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
                """, new { Key = key, Value = value });

            _cache[key] = value;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save configuration value for key {Key}", key);
            throw;
        }
    }
    
    public async Task DeleteValueAsync(string key)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            
            await conn.ExecuteAsync("DELETE FROM Configuration WHERE Key = @Key", new { Key = key });

            _cache.TryRemove(key, out _);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to delete configuration value for key {Key}", key);
            throw;
        }
    }

    public async Task<ConcurrentDictionary<string, string>> GetAllValuesAsync()
    {
        // Return a copy of the cache to prevent external modification
        var result = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _cache)
        {
            result[kvp.Key] = kvp.Value;
        }
        return result;
    }
}
