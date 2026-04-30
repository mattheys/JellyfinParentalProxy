using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Domain.Interfaces;

public interface IDatabaseService
{
    Task<T> GetAsync<T>(string key);
    Task SetAsync<T>(string key, T value);
    Task InitializeAsync();
    Task<bool> TableExistsAsync(string tableName);
    Task CreateSettingsTableAsync();
}