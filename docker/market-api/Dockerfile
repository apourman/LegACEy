# The Market API alone, for the local stack (docker/docker-compose.local.yml). Separate from upstream ACE's game-server Dockerfile.
# Build context: Source/ (see Dockerfile.dockerignore next to this file).

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# restore as its own layer: only the projects the API builds from
COPY ACE.Common/ACE.Common.csproj ACE.Common/
COPY ACE.Entity/ACE.Entity.csproj ACE.Entity/
COPY ACE.Database/ACE.Database.csproj ACE.Database/
COPY ACE.DatLoader/ACE.DatLoader.csproj ACE.DatLoader/
COPY ACE.MarketApi/ACE.MarketApi.csproj ACE.MarketApi/
RUN dotnet restore ACE.MarketApi/ACE.MarketApi.csproj

COPY ACE.Common/ ACE.Common/
COPY ACE.Entity/ ACE.Entity/
COPY ACE.Database/ ACE.Database/
COPY ACE.DatLoader/ ACE.DatLoader/
COPY ACE.MarketApi/ ACE.MarketApi/
RUN dotnet publish ACE.MarketApi/ACE.MarketApi.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
ARG DEBIAN_FRONTEND=noninteractive

# curl for the health check
RUN apt-get update \
    && apt-get install --no-install-recommends -y curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

# the named volume for the icon cache starts out owned by the non-root user
RUN mkdir -p /data/icon-cache && chown -R app:app /data

USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

HEALTHCHECK --interval=10s --timeout=3s --start-period=30s --retries=3 \
    CMD curl -fsS -o /dev/null http://127.0.0.1:8080/health || exit 1

# the database login and the service key come from the environment (Market__DatabaseUsername, Market__DatabasePassword, Market__ServiceKey),
# never from a file in the image or in git
ENTRYPOINT ["dotnet", "ACE.MarketApi.dll"]
