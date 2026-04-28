using Domain;
using Domain.Interfaces;
using SettingsService;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Domain.Models;

namespace ReverseProxy;

public static class AppBuilder
{
    public static async Task<WebApplication> BuildReverseProxy()
    {
        var builder = WebApplication.CreateBuilder();

        // ---------------------------------------------------------------------------
        // Configuration — all driven by environment variables
        // ---------------------------------------------------------------------------
        _ = builder.Configuration.AddEnvironmentVariables();
        _ = builder.Services.Configure<ProxyOptions>(builder.Configuration);
        
        var defaultOptions = new ProxyOptions();
        builder.Configuration.Bind(defaultOptions);

        // ---------------------------------------------------------------------------
        // HTTP clients
        // ---------------------------------------------------------------------------
        _ = builder.Services.AddHttpClient("tmdb", client =>
        {
            client.BaseAddress = new Uri("https://api.themoviedb.org/3/");
            client.Timeout     = TimeSpan.FromSeconds(10);
        });

        // ---------------------------------------------------------------------------
        // Application services
        // ---------------------------------------------------------------------------
        
        // Register ProxyOptions as a service so SettingsManager can resolve it
        _ = builder.Services.AddSingleton(defaultOptions);

        // Database Service
        _ = builder.Services.AddSingleton<IDatabaseService>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ProxyOptions>>().Value;
            return new DatabaseService.DatabaseService(options.DatabasePath);
        });

        // Settings Service
        _ = builder.Services.AddSingleton<ISettingsService, SettingsManager>();
        
        // Cache — singleton, implements IRatingCache (used by both proxy and WebAdmin)
        _ = builder.Services.AddSingleton<RatingCache>();
        _ = builder.Services.AddSingleton<IRatingCache>(sp => sp.GetRequiredService<RatingCache>());

        // Bypass service
        _ = builder.Services.AddSingleton<IBypassService, BypassService>();

        // TMDB helpers
        _ = builder.Services.AddSingleton<TmdbService>();

        // Bounded lookup queue — replaces fire-and-forget Task.Run
        _ = builder.Services.AddSingleton<TmdbLookupQueue>();
        _ = builder.Services.AddSingleton<ITmdbLookupQueue>(sp => sp.GetRequiredService<TmdbLookupQueue>());

        // Background hosted services
        _ = builder.Services.AddHostedService<TmdbLookupWorker>();
        _ = builder.Services.AddHostedService<CacheFlushService>();

        // ---------------------------------------------------------------------------
        // YARP reverse proxy
        // ---------------------------------------------------------------------------
        _ = builder.Services
            .AddReverseProxy()
            .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

        // ---------------------------------------------------------------------------
        // Logging
        // ---------------------------------------------------------------------------
        //_ = builder.Logging.ClearProviders();
        //_ = builder.Logging.AddSimpleConsole(o =>
        //{
        //    o.TimestampFormat = "HH:mm:ss ";
        //});
        //_ = builder.Logging.SetMinimumLevel(LogLevel.Trace);

        var app = builder.Build();

        // Initialize database settings
        var settingsService = app.Services.GetRequiredService<ISettingsService>();
        await settingsService.InitializeAsync();

        // Get actual proxy options from settings service instead
        var actualOptions = await settingsService.GetSettingsAsync();

        // Initialise SQLite (creates tables / runs migrations)
        var cache = app.Services.GetRequiredService<RatingCache>();
        await cache.InitialiseAsync(actualOptions.DatabasePath);

        // Content-filtering middleware BEFORE YARP
        _ = app.UseMiddleware<ParentalFilterMiddleware>();
        _ = app.MapReverseProxy();

        app.Logger.LogInformation("Jellyfin Parental Proxy started");
        app.Logger.LogInformation("Max allowed rating : {Rating}", actualOptions.MaxRating);
        app.Logger.LogInformation("TMDB integration   : {Enabled}",
            !string.IsNullOrEmpty(actualOptions.TmdbApiKey) ? "enabled" : "disabled (no TMDB_API_KEY)");
        app.Logger.LogInformation("TMDB workers       : {Count}", actualOptions.TmdbWorkerCount);

        return app;
    }
}
