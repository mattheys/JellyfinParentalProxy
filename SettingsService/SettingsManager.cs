using System;
using System.Threading.Tasks;
using Domain.Interfaces;
using Domain.Models;

namespace SettingsService;

public class SettingsManager : ISettingsService
{
    private readonly IDatabaseService _databaseService;
    private readonly ProxyOptions _defaultOptions;
    
    public SettingsManager(IDatabaseService databaseService, ProxyOptions defaultOptions)
    {
        _databaseService = databaseService;
        _defaultOptions = defaultOptions;
    }
    
    public async Task<ProxyOptions> GetSettingsAsync()
    {
        // Initialize database if needed
        await _databaseService.InitializeAsync();
        
        var options = new ProxyOptions
        {
            JellyfinUrl = await _databaseService.GetAsync<string>("JellyfinUrl") ?? _defaultOptions.JellyfinUrl,
            MaxRating = await _databaseService.GetAsync<string>("MaxRating") ?? _defaultOptions.MaxRating,
            TmdbApiKey = await _databaseService.GetAsync<string>("TmdbApiKey"),
            TmdbRegion = await _databaseService.GetAsync<string>("TmdbRegion") ?? _defaultOptions.TmdbRegion,
            TmdbRetryHours = await _databaseService.GetAsync<int>("TmdbRetryHours") != 0 
                ? await _databaseService.GetAsync<int>("TmdbRetryHours") 
                : _defaultOptions.TmdbRetryHours,
            DatabasePath = await _databaseService.GetAsync<string>("DatabasePath") ?? _defaultOptions.DatabasePath,
            TmdbWorkerCount = await _databaseService.GetAsync<int>("TmdbWorkerCount") != 0 
                ? await _databaseService.GetAsync<int>("TmdbWorkerCount") 
                : _defaultOptions.TmdbWorkerCount,
            TmdbQueueCapacity = await _databaseService.GetAsync<int>("TmdbQueueCapacity") != 0 
                ? await _databaseService.GetAsync<int>("TmdbQueueCapacity") 
                : _defaultOptions.TmdbQueueCapacity,
            RewritePlaybackUrlsToDownstream = await _databaseService.GetAsync<bool>("RewritePlaybackUrlsToDownstream")
        };
        
        return options;
    }
    
    public async Task SetSettingsAsync(ProxyOptions options)
    {
        await _databaseService.SetAsync("JellyfinUrl", options.JellyfinUrl);
        await _databaseService.SetAsync("MaxRating", options.MaxRating);
        await _databaseService.SetAsync("TmdbApiKey", options.TmdbApiKey);
        await _databaseService.SetAsync("TmdbRegion", options.TmdbRegion);
        await _databaseService.SetAsync("TmdbRetryHours", options.TmdbRetryHours);
        await _databaseService.SetAsync("DatabasePath", options.DatabasePath);
        await _databaseService.SetAsync("TmdbWorkerCount", options.TmdbWorkerCount);
        await _databaseService.SetAsync("TmdbQueueCapacity", options.TmdbQueueCapacity);
        await _databaseService.SetAsync("RewritePlaybackUrlsToDownstream", options.RewritePlaybackUrlsToDownstream);
    }
    
    public async Task InitializeAsync()
    {
        await _databaseService.InitializeAsync();
    }
}
