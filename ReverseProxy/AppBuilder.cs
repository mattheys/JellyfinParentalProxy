using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace ReverseProxy;

public class AppBuilder
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

        // Named client for TMDB — separate from the proxy client
        _ = builder.Services.AddHttpClient("tmdb", client =>
        {
            client.BaseAddress = new Uri("https://api.themoviedb.org/3/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        // ---------------------------------------------------------------------------
        // Application services
        // ---------------------------------------------------------------------------
        _ = builder.Services.AddSingleton<RatingCache>();
        _ = builder.Services.AddSingleton<TmdbService>();
        builder.Services.AddHostedService<CacheFlushService>();

        // ---------------------------------------------------------------------------
        // YARP reverse proxy
        // ---------------------------------------------------------------------------
        _ = builder.Services
            .AddReverseProxy()
            .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

        // ---------------------------------------------------------------------------
        // Logging
        // ---------------------------------------------------------------------------
        _ = builder.Logging.ClearProviders();
        _ = builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });

        var app = builder.Build();

        // Initialise the SQLite database (creates tables if missing)
        var cache = app.Services.GetRequiredService<RatingCache>();
        await cache.InitialiseAsync();

        // Wire up the content-filtering middleware BEFORE YARP
        _ = app.UseMiddleware<ParentalFilterMiddleware>();

        _ = app.MapReverseProxy();

        app.Logger.LogInformation("Jellyfin Parental Proxy started");
        app.Logger.LogInformation("Max allowed rating : {Rating}", app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProxyOptions>>().Value.MaxRating);
        app.Logger.LogInformation("TMDB integration   : {Enabled}", !string.IsNullOrEmpty(app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProxyOptions>>().Value.TmdbApiKey) ? "enabled" : "disabled (no TMDB_API_KEY)");

        return app;

    }
}
