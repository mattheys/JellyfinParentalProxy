# JellyfinParentalProxy

A YARP-based reverse proxy that filters Jellyfin media by age rating, with a MudBlazor admin UI for managing overrides.

---

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│  Docker container / host process                        │
│                                                         │
│  ┌────────────────────┐    ┌─────────────────────────┐  │
│  │  ReverseProxy      │    │  WebAdmin (Blazor SSR)  │  │
│  │  :5000             │    │  :5001                  │  │
│  │                    │    │                         │  │
│  │  YARP → Jellyfin   │    │  Rating Cache browser   │  │
│  │  ParentalFilter    │    │  Manual override UI     │  │
│  │  TmdbLookupQueue   │    │                         │  │
│  └────────┬───────────┘    └────────────┬────────────┘  │
│           │  shared IRatingCache singleton              │
│           └───────────────┬─────────────┘              │
│                    ┌──────┴──────┐                      │
│                    │  SQLite DB  │  /data/rating_cache  │
│                    └─────────────┘                      │
└─────────────────────────────────────────────────────────┘
```

### Projects

| Project        | Purpose |
|---------------|---------|
| `Domain`       | Shared models, enums, and interfaces (`IRatingCache`, `ITmdbLookupQueue`, `ProxyOptions`, `AgeRating`, etc.) |
| `ReverseProxy` | YARP proxy, `ParentalFilterMiddleware`, `RatingCache`, `TmdbService`, `TmdbLookupQueue`, `TmdbLookupWorker` |
| `WebAdmin`     | Blazor Server UI (MudBlazor). Entry point — starts both apps. |

---

## Configuration

All settings are readable from `appsettings.json` **or** environment variables (env vars take precedence).

| JSON key              | Env var               | Default              | Description |
|----------------------|-----------------------|----------------------|-------------|
| `JellyfinUrl`         | `JELLYFIN_URL`        | `http://127.0.0.1:8096` | Upstream Jellyfin address |
| `MaxRating`           | `MAX_RATING`          | `PG-13`              | Highest rating allowed through |
| `TmdbApiKey`          | `TMDB_API_KEY`        | _(empty = disabled)_ | TMDB v3 read API key |
| `TmdbRegion`          | `TMDB_REGION`         | `US`                 | ISO-3166-1 country for rating lookup |
| `TmdbRetryHours`      | `TMDB_RETRY_HOURS`    | `24`                 | Hours before retrying a failed TMDB lookup |
| `TmdbWorkerCount`     | `TMDB_WORKER_COUNT`   | `2`                  | Concurrent background TMDB workers |
| `TmdbQueueCapacity`   | `TMDB_QUEUE_CAPACITY` | `500`                | Max queued TMDB lookups (oldest dropped when full) |
| `CachePath`           | `CACHE_PATH`          | `rating_cache.db`    | Path to SQLite file |

### Configuration Service

A database-backed configuration service has been added that stores settings in SQLite. This service can be used to dynamically store and load configuration values during runtime, with the persistence of settings in a SQLite database.

The configuration service implements the `IConfigurationService` interface and provides the following functionality:
- `GetValue(key, defaultValue)` - Gets a configuration value by key with fallback to default
- `SetValueAsync(key, value)` - Sets a configuration value to be persisted
- `DeleteValueAsync(key)` - Removes a configuration value
- `GetAllValuesAsync()` - Gets all configuration values

The service is available through dependency injection with the `IConfigurationService` interface.

---

## Running with Docker

Prebuilt images are published to GitHub Container Registry (GHCR):

- `ghcr.io/mattheys/jellyfinparentalproxy:latest`

If your package visibility is private, log in first with a token that has `read:packages`:

```bash
echo "$GHCR_TOKEN" | docker login ghcr.io -u YOUR_GITHUB_USERNAME --password-stdin
```

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
  ghcr.io/mattheys/jellyfinparentalproxy:latest
```

Point your Jellyfin clients at `:5000` instead of Jellyfin directly.  
Open the admin UI at `http://your-host:5001/ratings`.

### Running with Docker Compose

The included `docker-compose.yml` now pulls from GHCR by default.

```bash
docker compose up -d
```

Optional overrides:

- `IMAGE_NAME=ghcr.io/mattheys/jellyfinparentalproxy`
- `IMAGE_TAG=latest`

Example:

```bash
IMAGE_TAG=v1.0.0 docker compose up -d
```

### Publishing images to GHCR (maintainers)

This repository includes `.github/workflows/docker-publish.yml`.

- Push to `main` publishes `latest` and a `sha-` tag
- Push a version tag (for example `v1.2.3`) publishes a matching image tag

You can then pull the image from `ghcr.io/<owner>/jellyfinparentalproxy:<tag>`.

---

## Rating Cache & Manual Overrides

The **Rating Cache** page (`/ratings`) shows every item the proxy has seen:

- **TMDB** — rating was resolved automatically via the TMDB API
- **Pending** — a TMDB lookup was attempted but failed (will retry after `TmdbRetryHours`)
- **Manual** — you set this rating yourself; it takes priority over TMDB and is never overwritten automatically

To override a rating: click the ✏️ icon, pick a rating, and click **Apply Override**.  
To remove an override and allow automatic re-lookup: click the 🔄 icon.

Episodes and seasons inherit their series rating and are not manually overrideable directly.

---

## Episode / Season rating inheritance

When an Episode or Season has no individual rating (neither in Jellyfin nor the TMDB cache), the proxy:

1. Reads `SeriesId` from the item payload
2. Checks the rating cache for the parent Series
3. If not cached, fetches the Series `OfficialRating` from Jellyfin directly and caches it
4. If Jellyfin also has no rating, enqueues a TMDB lookup for the **Series** (so once resolved, all episodes benefit)

---

## Upgrading from earlier versions

The SQLite schema is migrated automatically at startup — new columns (`ItemName`, `ItemType`, `IsManualOverride`) are added with `ALTER TABLE … ADD COLUMN` only if they don't exist, so existing data is preserved.

The old `ProxyOptions` class that lived in the `ReverseProxy` namespace has moved to `Domain`. If you have any external code referencing `ReverseProxy.ProxyOptions`, update the `using` to `Domain`.
