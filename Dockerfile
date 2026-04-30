# ============================================================
#  Build stage
# ============================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and all project files first so layer-caching works
COPY JellyfinProxy.sln .
COPY Domain/Domain.csproj           Domain/
COPY ReverseProxy/ReverseProxy.csproj ReverseProxy/
COPY WebAdmin/WebAdmin.csproj       WebAdmin/

RUN dotnet restore

# Copy source and publish
COPY . .
RUN dotnet publish WebAdmin/WebAdmin.csproj -c Release -o /app/publish

# ============================================================
#  Runtime stage
# ============================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Persist the SQLite rating cache outside the container
RUN mkdir -p /data

COPY --from=build /app/publish .

# ---- Defaults (all overridable via -e / environment) -------
ENV ASPNETCORE_ENVIRONMENT=Production
ENV JELLYFIN_URL=http://host.docker.internal:8096
ENV MAX_RATING=PG-13
ENV TMDB_API_KEY=
ENV TMDB_REGION=US
ENV TMDB_RETRY_HOURS=24
ENV TMDB_WORKER_COUNT=2
ENV TMDB_QUEUE_CAPACITY=500
ENV CACHE_PATH=/data/rating_cache.db

# 5000 = Jellyfin reverse proxy  (point clients here instead of Jellyfin directly)
# 5001 = WebAdmin UI
EXPOSE 5000 5001

ENTRYPOINT ["dotnet", "WebAdmin.dll"]
