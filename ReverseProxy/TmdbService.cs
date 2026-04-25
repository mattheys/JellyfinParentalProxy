using System.Text.Json;
using System.Text.Json.Nodes;
using Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReverseProxy;

/// <summary>
/// Thin async wrapper around the TMDB v3 REST API.
/// Returns <c>null</c> when no rating can be resolved.
/// </summary>
public sealed class TmdbService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ProxyOptions _opts;
    private readonly ILogger<TmdbService> _log;

    public TmdbService(
        IHttpClientFactory httpFactory,
        IOptions<ProxyOptions> options,
        ILogger<TmdbService> log)
    {
        _httpFactory = httpFactory;
        _opts        = options.Value;
        _log         = log;
    }

    public bool IsEnabled => !string.IsNullOrEmpty(_opts.TmdbApiKey);

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>Resolve a rating for a movie.</summary>
    public Task<AgeRating?> GetMovieRatingAsync(long tmdbId) =>
        GetRatingAsync(
            $"movie/{tmdbId}/release_dates?api_key={_opts.TmdbApiKey}",
            ParseMovieRating);

    /// <summary>Resolve a rating for a TV series.</summary>
    public Task<AgeRating?> GetTvRatingAsync(long tmdbId) =>
        GetRatingAsync(
            $"tv/{tmdbId}/content_ratings?api_key={_opts.TmdbApiKey}",
            ParseTvRating);

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<AgeRating?> GetRatingAsync(string relativeUrl, Func<JsonNode, string?, AgeRating?> parser)
    {
        try
        {
            var client   = _httpFactory.CreateClient("tmdb");
            var response = await client.GetAsync(relativeUrl);
            response.EnsureSuccessStatusCode();

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            if (json is null) return null;

            return parser(json, _opts.TmdbRegion);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TMDB request failed: {Url}", relativeUrl);
            return null;
        }
    }

    /// <summary>
    /// Parses /movie/{id}/release_dates.
    /// Prefers theatrical releases (type=3) in the requested region, then US.
    /// </summary>
    private static AgeRating? ParseMovieRating(JsonNode root, string? region)
    {
        var results = root["results"]?.AsArray();
        if (results is null) return null;

        var cert = FindMovieCert(results, region ?? "US")
                ?? FindMovieCert(results, "US");

        return cert is not null ? AgeRatingParser.Parse(cert) : null;
    }

    private static string? FindMovieCert(JsonArray results, string iso)
    {
        var country = results.FirstOrDefault(r =>
            r?["iso_3166_1"]?.GetValue<string>() == iso);

        if (country is null) return null;

        var dates = country["release_dates"]?.AsArray();
        if (dates is null) return null;

        // Prefer type=3 (Theatrical), then any that has a non-empty cert.
        var best = dates.FirstOrDefault(d => d?["type"]?.GetValue<int>() == 3)
                ?? dates.FirstOrDefault(d =>
                    !string.IsNullOrEmpty(d?["certification"]?.GetValue<string>()));

        var cert = best?["certification"]?.GetValue<string>();
        return string.IsNullOrEmpty(cert) ? null : cert;
    }

    /// <summary>
    /// Parses /tv/{id}/content_ratings.
    /// Prefers the requested region, then US.
    /// </summary>
    private static AgeRating? ParseTvRating(JsonNode root, string? region)
    {
        var results = root["results"]?.AsArray();
        if (results is null) return null;

        var match = results.FirstOrDefault(r =>
                        r?["iso_3166_1"]?.GetValue<string>() == (region ?? "US"))
                 ?? results.FirstOrDefault(r =>
                        r?["iso_3166_1"]?.GetValue<string>() == "US");

        var rating = match?["rating"]?.GetValue<string>();
        return string.IsNullOrEmpty(rating) ? null : AgeRatingParser.Parse(rating);
    }
}
