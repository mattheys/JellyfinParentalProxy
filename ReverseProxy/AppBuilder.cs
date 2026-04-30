using Domain;
using Domain.Interfaces;
using Domain.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

namespace ReverseProxy;

public static class AppBuilder
{
    public static async Task<WebApplication> BuildReverseProxy()
    {
        var builder = WebApplication.CreateBuilder();

        _ = builder.Configuration.AddEnvironmentVariables();

        var envOverrides = LoadEnvironmentOverrides();
        if (envOverrides.Count > 0)
            _ = builder.Configuration.AddInMemoryCollection(envOverrides);

        var bootstrapOptions = new ProxyOptions();
        builder.Configuration.Bind(bootstrapOptions);

        var dbOverrides = await LoadDatabaseOverridesAsync(bootstrapOptions.CachePath);
        if (dbOverrides.Count > 0)
            _ = builder.Configuration.AddInMemoryCollection(dbOverrides);

        if (envOverrides.Count > 0)
            _ = builder.Configuration.AddInMemoryCollection(envOverrides);

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
        _ = builder.Services.AddSingleton<RatingCache>();
        _ = builder.Services.AddSingleton<IRatingCache>(sp => sp.GetRequiredService<RatingCache>());

        // Configuration service
        _ = builder.Services.AddSingleton<ConfigurationService>();
        _ = builder.Services.AddSingleton<IConfigurationService>(sp => sp.GetRequiredService<ConfigurationService>());

        // TMDB helpers
        _ = builder.Services.AddSingleton<TmdbService>();

        // Bounded lookup queue — replaces fire-and-forget Task.Run
        _ = builder.Services.AddSingleton<TmdbLookupQueue>();
        _ = builder.Services.AddSingleton<ITmdbLookupQueue>(sp => sp.GetRequiredService<TmdbLookupQueue>());

        // Background hosted services
        _ = builder.Services.AddHostedService<TmdbLookupWorker>();
        _ = builder.Services.AddHostedService<CacheFlushService>();

        // Bypass service
        _ = builder.Services.AddSingleton<IBypassService, BypassService>();

        // ---------------------------------------------------------------------------
        // YARP reverse proxy (configured in code)
        // ---------------------------------------------------------------------------
        var jellyfinDestination = NormalizeDestinationAddress(
            builder.Configuration[nameof(ProxyOptions.JellyfinUrl)] ?? bootstrapOptions.JellyfinUrl);

        var routes = new[]
        {
            new RouteConfig
            {
                RouteId = "jellyfin-route",
                ClusterId = "jellyfin-cluster",
                Match = new RouteMatch { Path = "{**catch-all}" }
            }
        };

        var clusters = new[]
        {
            new ClusterConfig
            {
                ClusterId = "jellyfin-cluster",
                Destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["primary"] = new DestinationConfig { Address = jellyfinDestination }
                }
            }
        };

        _ = builder.Services
            .AddReverseProxy()
            .LoadFromMemory(routes, clusters);

        var app = builder.Build();

        // Initialise SQLite (creates tables / runs migrations)
        var cache = app.Services.GetRequiredService<RatingCache>();
        await cache.InitialiseAsync();
        
        // Initialise configuration service - this loads database values
        var config = app.Services.GetRequiredService<ConfigurationService>();
        await config.InitializeAsync();

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

    private static async Task<Dictionary<string, string?>> LoadDatabaseOverridesAsync(string cachePath)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath))
            return values;

        try
        {
            await using var conn = new SqliteConnection($"Data Source={cachePath};Cache=Shared;");
            await conn.OpenAsync();

            await using var tableCheck = conn.CreateCommand();
            tableCheck.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'Configuration' LIMIT 1;";

            var hasConfigTable = await tableCheck.ExecuteScalarAsync() is not null;
            if (!hasConfigTable)
                return values;

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Key, Value FROM Configuration;";

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var key = reader.GetString(0);
                var value = reader.IsDBNull(1) ? null : reader.GetString(1);
                values[key] = value;
            }
        }
        catch
        {
            // Ignore bootstrap lookup failures and continue with file/env settings.
        }

        return values;
    }

    private static string NormalizeDestinationAddress(string? jellyfinUrl)
    {
        var fallback = "http://127.0.0.1:8096/";

        if (string.IsNullOrWhiteSpace(jellyfinUrl))
            return fallback;

        var normalized = jellyfinUrl.Trim();
        if (!normalized.EndsWith("/", StringComparison.Ordinal))
            normalized += "/";

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out _))
            return fallback;

        return normalized;
    }

    private static Dictionary<string, string?> LoadEnvironmentOverrides()
    {
        static void MapEnv(Dictionary<string, string?> values, string configKey, string envKey)
        {
            var value = Environment.GetEnvironmentVariable(envKey);
            if (!string.IsNullOrWhiteSpace(value))
                values[configKey] = value;
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        MapEnv(values, nameof(ProxyOptions.JellyfinUrl), "JELLYFIN_URL");
        MapEnv(values, nameof(ProxyOptions.MaxRating), "MAX_RATING");
        MapEnv(values, nameof(ProxyOptions.TmdbApiKey), "TMDB_API_KEY");
        MapEnv(values, nameof(ProxyOptions.TmdbRegion), "TMDB_REGION");
        MapEnv(values, nameof(ProxyOptions.TmdbRetryHours), "TMDB_RETRY_HOURS");
        MapEnv(values, nameof(ProxyOptions.CachePath), "CACHE_PATH");
        MapEnv(values, nameof(ProxyOptions.LogBufferSize), "LOG_BUFFER_SIZE");
        MapEnv(values, nameof(ProxyOptions.TmdbWorkerCount), "TMDB_WORKER_COUNT");
        MapEnv(values, nameof(ProxyOptions.TmdbQueueCapacity), "TMDB_QUEUE_CAPACITY");
        return values;
    }
}
