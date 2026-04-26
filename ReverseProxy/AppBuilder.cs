using Domain.Interfaces;
using Domain.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

        // Cache — singleton, implements IRatingCache (used by both proxy and WebAdmin)
        _ = builder.Services.AddSingleton<IRatingCache, RatingCache>();

        // TMDB helpers
        _ = builder.Services.AddSingleton<TmdbService>();

        // Bounded lookup queue — replaces fire-and-forget Task.Run
        _ = builder.Services.AddSingleton<ITmdbLookupQueue, TmdbLookupQueue>();

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
        //    o.SingleLine      = true;
        //    o.TimestampFormat = "HH:mm:ss ";
        //});
        //_ = builder.Logging.SetMinimumLevel(LogLevel.Trace);

        var app = builder.Build();

        // Initialise SQLite (creates tables / runs migrations)
        var cache = app.Services.GetRequiredService<RatingCache>();
        await cache.InitialiseAsync();

        // Content-filtering middleware BEFORE YARP
        _ = app.UseMiddleware<ParentalFilterMiddleware>();
        _ = app.MapReverseProxy();

        var opts = app.Services.GetRequiredService<IOptions<ProxyOptions>>().Value;
        app.Logger.LogInformation("Jellyfin Parental Proxy started");
        app.Logger.LogInformation("Max allowed rating : {Rating}", opts.MaxRating);
        app.Logger.LogInformation("TMDB integration   : {Enabled}",
            !string.IsNullOrEmpty(opts.TmdbApiKey) ? "enabled" : "disabled (no TMDB_API_KEY)");
        app.Logger.LogInformation("TMDB workers       : {Count}", opts.TmdbWorkerCount);

        return app;
    }
}
