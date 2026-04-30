# Jellyfin Parental Proxy

Jellyfin Parental Proxy helps you keep age-restricted content out of your family Jellyfin experience.

You run it in front of your existing Jellyfin server, then point your devices to the proxy instead of connecting to Jellyfin directly.

## What this does

- Hides movies and shows above the rating level you choose
- Keeps a local cache so ratings do not need to be looked up every time
- Lets you manually set or correct ratings from a simple web page
- Works with your existing Jellyfin server (your library stays where it is)

## Services and ports

When running the container, two services are exposed:

| Port | Service | What it is for |
|---|---|---|
| `5000` | Proxy endpoint | Connect Jellyfin apps/TVs/clients to this port |
| `5001` | Admin web page | Manage rating cache and manual overrides at `/ratings` |

Example URLs after startup:

- `http://your-host:5000` (use this in Jellyfin clients)
- `http://your-host:5001/ratings` (open this in a web browser)

## Quick start (Docker)

Public image:

- `ghcr.io/mattheys/jellyfinparentalproxy:latest`

Run it:

`TMDB_API_KEY` is required. The container will not start without it.

```bash
docker run -d \
  --name jellyfin-proxy \
  -p 5000:5000 \
  -p 5001:5001 \
  -e JELLYFIN_URL=http://192.168.1.10:8096 \
  -e MAX_RATING=PG \
  -e TMDB_API_KEY=your_tmdb_api_key \
  -e TMDB_REGION=US \
  -v jellyfin-proxy-data:/data \
  ghcr.io/mattheys/jellyfinparentalproxy:latest
```

After it starts:

1. Change your Jellyfin apps/devices to use `http://your-host:5000`
2. Open `http://your-host:5001/ratings` to review or override ratings

## Docker Compose example

```yaml
services:
  jellyfin-parental-proxy:
    image: ghcr.io/mattheys/jellyfinparentalproxy:latest
    container_name: jellyfin-proxy
    restart: unless-stopped
    ports:
      - "5000:5000"
      - "5001:5001"
    environment:
      JELLYFIN_URL: http://192.168.1.10:8096
      MAX_RATING: PG
      TMDB_API_KEY: your_tmdb_api_key
      TMDB_REGION: US
    volumes:
      - jellyfin-proxy-data:/data

volumes:
  jellyfin-proxy-data:
```

Start it:

```bash
docker compose up -d
```

## Common settings

You can set these in Docker environment variables:

| Setting | Default | Plain-language meaning |
|---|---|---|
| `JELLYFIN_URL` | `http://127.0.0.1:8096` | Address of your existing Jellyfin server |
| `MAX_RATING` | `PG-13` | Highest rating you want to allow |
| `TMDB_API_KEY` | _(required)_ | Required TMDB API key; container will not start without it |
| `TMDB_REGION` | `US` | Country/region used when looking up ratings |
| `TMDB_RETRY_HOURS` | `24` | Wait time before retrying failed lookups |
| `TMDB_WORKER_COUNT` | `2` | How many rating lookups can run at once |
| `TMDB_QUEUE_CAPACITY` | `500` | Max number of queued lookups |
| `CACHE_PATH` | `rating_cache.db` | Where the local cache is stored |

## Managing manual overrides

Go to `http://your-host:5001/ratings`.

- Use the edit button to set a manual rating
- Use reset to remove a manual override and return to automatic behavior
- Manual overrides always win over automatic lookup results
