using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Domain.Interfaces;

namespace DatabaseService;

public class DatabaseService : IDatabaseService
{
    private readonly string _connectionString;
    
    public DatabaseService(string databasePath)
    {
        _connectionString = $"Data Source={databasePath};Cache=Shared;";
    }

    public async Task InitializeAsync()
    {
        await CreateSettingsTableAsync();
    }

    public async Task<T> GetAsync<T>(string key)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        using var command = new SqliteCommand("SELECT Value FROM Settings WHERE Key = @key", connection);
        command.Parameters.AddWithValue("@key", key);
        
        var result = await command.ExecuteScalarAsync();
        if (result == null || result == DBNull.Value)
        {
            return default!;
        }
        
        return (T)Convert.ChangeType(result, typeof(T));
    }

    public async Task SetAsync<T>(string key, T value)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        // First try to update existing
        using var updateCommand = new SqliteCommand("UPDATE Settings SET Value = @value WHERE Key = @key", connection);
        updateCommand.Parameters.AddWithValue("@value", value?.ToString() ?? string.Empty);
        updateCommand.Parameters.AddWithValue("@key", key);
        
        var rowsAffected = await updateCommand.ExecuteNonQueryAsync();
        
        // If no rows were updated, insert a new one
        if (rowsAffected == 0)
        {
            using var insertCommand = new SqliteCommand("INSERT INTO Settings (Key, Value) VALUES (@key, @value)", connection);
            insertCommand.Parameters.AddWithValue("@key", key);
            insertCommand.Parameters.AddWithValue("@value", value?.ToString() ?? string.Empty);
            await insertCommand.ExecuteNonQueryAsync();
        }
    }

    public async Task<bool> TableExistsAsync(string tableName)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        using var command = new SqliteCommand(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=@tableName", 
            connection);
        command.Parameters.AddWithValue("@tableName", tableName);
        
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result) > 0;
    }

    public async Task CreateSettingsTableAsync()
    {
        if (await TableExistsAsync("Settings"))
        {
            return;
        }
        
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        using var command = new SqliteCommand(@"
            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT NOT NULL PRIMARY KEY,
                Value TEXT NOT NULL,
                LastModified TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            )", connection);
            
        await command.ExecuteNonQueryAsync();
    }
}