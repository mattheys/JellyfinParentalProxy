# Jellyfin Parental Proxy

## Architecture

- **ReverseProxy** project: YARP-based reverse proxy with middleware (`ParentalFilterMiddleware`)
- **WebAdmin** project: Blazor Server UI (MudBlazor) for managing overrides
- **Domain** project: Shared models (`AgeRating`, `ProxyOptions`) and interfaces (`IRatingCache`, `ITmdbLookupQueue`)

## Key Files

### Entry Points
- `WebAdmin/Program.cs`: Starts both proxy and UI servers on ports 5000 and 5001 respectively
- `ReverseProxy/ParentalFilterMiddleware.cs`: Core filtering logic for JSON responses
- `ReverseProxy/ReverseProxy.csproj`: References Domain and contains middleware registration

### Core Functionality
- `ReverseProxy/ReverseProxy.csproj`: References Domain and uses Yarp.ReverseProxy
- `ReverseProxy/TmdbService.cs`: Wraps TMDB API calls for movie and TV ratings
- `ReverseProxy/TmdbLookupWorker.cs`: Background service processing TMDB lookup queue
- `ReverseProxy/RatingCache.cs`: SQLite-backed in-memory cache for ratings
- `Domain/AgeRating.cs`: Enum with ordered age ratings

## Running the Application

### Docker
```bash
docker run -d \
  -p 5000:5000 \
  -p 5001:5001 \
  -e JELLYFIN_URL=http://192.168.1.10:8096 \
  -e MAX_RATING=PG \
  -e TMDB_API_KEY=your_key_here \
  -e TMDB_REGION=GB \
  -v jellyfin-proxy-data:/data \
  --name jellyfin-proxy \
  jellyfinparentalproxy
```

Point Jellyfin clients at `:5000`. Open UI at `http://your-host:5001/ratings`.

## Configuration

All settings are in `appsettings.json` or via environment variables (env vars take precedence).

| JSON key | Env var | Default | Description |
|---|---|---|---|
| `JellyfinUrl` | `JELLYFIN_URL` | `http://127.0.0.1:8096` | Upstream Jellyfin address |
| `MaxRating` | `MAX_RATING` | `PG-13` | Highest rating allowed through |
| `TmdbApiKey` | `TMDB_API_KEY` | _(empty = disabled)_ | TMDB v3 read API key |
| `TmdbRegion` | `TMDB_REGION` | `US` | ISO-3166-1 country for rating lookup |
| `TmdbRetryHours` | `TMDB_RETRY_HOURS` | `24` | Hours before retrying a failed TMDB lookup |
| `TmdbWorkerCount` | `TMDB_WORKER_COUNT` | `2` | Concurrent background TMDB workers |
| `TmdbQueueCapacity` | `TMDB_QUEUE_CAPACITY` | `500` | Max queued TMDB lookups |
| `CachePath` | `CACHE_PATH` | `rating_cache.db` | Path to SQLite file |

## Features

- **Automatic Filtering**: Removes items exceeding `MaxRating` from Jellyfin responses
- **TMDB Integration**: Resolves ratings for unrated items via TMDB API
- **Manual Overrides**: Admin UI allows setting permanent overrides
- **Series/Episode Inheritance**: Episodes/Seasons inherit rating from parent Series

## Cache Behavior

- SQLite-backed with in-memory dictionary for fast lookups
- Manual overrides take precedence over automatic ratings
- Failed TMDB lookups are retried after `TmdbRetryHours`
- Automatic migration of schema on startup