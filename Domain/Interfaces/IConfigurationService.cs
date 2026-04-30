using System.Collections.Concurrent;

namespace Domain.Interfaces;

/// <summary>
/// Database-backed configuration service that stores settings in SQLite.
/// </summary>
public interface IConfigurationService
{
    /// <summary>
    /// Initializes the configuration service and creates the database table if needed.
    /// </summary>
    Task InitializeAsync();
    
    /// <summary>
    /// Gets a configuration value by key, falling back to the default value if not found.
    /// </summary>
    string GetValue(string key, string defaultValue = "");
    
    /// <summary>
    /// Sets a configuration value in the database.
    /// </summary>
    Task SetValueAsync(string key, string value);
    
    /// <summary>
    /// Deletes a configuration value from the database.
    /// </summary>
    Task DeleteValueAsync(string key);
    
    /// <summary>
    /// Gets all configuration values from the database.
    /// </summary>
    Task<ConcurrentDictionary<string, string>> GetAllValuesAsync();
}