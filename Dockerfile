# ============================================================
#  Build stage
# ============================================================
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY JellyfinProxy.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

# ============================================================
#  Runtime stage
# ============================================================
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Create a directory for the SQLite database
RUN mkdir -p /data

COPY --from=build /app/publish .

# ---- Environment variable defaults -------------------------
ENV ASPNETCORE_URLS=http://+:8080
ENV JELLYFIN_URL=http://host.docker.internal:8096
ENV MAX_RATING=PG-13
ENV TMDB_API_KEY=
ENV TMDB_REGION=US
ENV TMDB_RETRY_HOURS=24
ENV CACHE_PATH=/data/rating_cache.db

EXPOSE 8080

ENTRYPOINT ["dotnet", "JellyfinProxy.dll"]
