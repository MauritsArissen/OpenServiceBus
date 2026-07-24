# syntax=docker/dockerfile:1
# Multi-stage build for OpenServiceBus. Alpine-based to keep the image small.
# Runs the broker AND the Explorer UI side-by-side, SQLite-backed by default
# (persistent across container restarts) with the .db file at /data/broker.db.
# Mount /data as a volume for durability.
#
# Exposed ports:
#   5672 - AMQP listener (Service Bus SDK + AMQPNetLite clients)
#   5300 - REST management API + /health
#   5400 - Explorer browser UI (proxies to the management API + drives AMQP via the SDK)
#
# Optional environment variables:
#   OPENSERVICEBUS__STORAGE__MODE        InMemory | Sqlite (default: Sqlite in this image)
#   OPENSERVICEBUS__STORAGE__DATASOURCE  Path to the SQLite .db file (default: /data/broker.db)
#   OPENSERVICEBUS__WEBSOCKETS__ENABLED  true to start the AMQP-over-WebSocket bridge on :5673
#   OPENSERVICEBUS_CONFIG                Path to a Microsoft-emulator-compatible config.json
#                                        for bootstrapping queues/topics/subs at startup.

# ─── Build stage ──────────────────────────────────────────────────────────────
# ─── Explorer SPA stage ───────────────────────────────────────────────────────
# Builds the React/shadcn UI into wwwroot. Kept separate so the .NET SDK image
# needs no Node, and so this layer caches on ui/ changes alone.
FROM node:22-alpine AS spa
WORKDIR /ui
COPY src/OpenServiceBus.Explorer/ui/package.json src/OpenServiceBus.Explorer/ui/package-lock.json ./
RUN npm ci
COPY src/OpenServiceBus.Explorer/ui/ ./
RUN npm run build   # emits ../wwwroot -> /src/OpenServiceBus.Explorer/wwwroot inside the image

# ─── .NET build stage ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Copy only what NuGet restore needs first, so the layer cache survives source-only edits.
COPY Directory.Build.props Directory.Packages.props OpenServiceBus.slnx ./
COPY src/OpenServiceBus.Core/OpenServiceBus.Core.csproj                       src/OpenServiceBus.Core/
COPY src/OpenServiceBus.InMemoryStorage/OpenServiceBus.InMemoryStorage.csproj src/OpenServiceBus.InMemoryStorage/
COPY src/OpenServiceBus.SqliteStorage/OpenServiceBus.SqliteStorage.csproj     src/OpenServiceBus.SqliteStorage/
COPY src/OpenServiceBus.Amqp/OpenServiceBus.Amqp.csproj                       src/OpenServiceBus.Amqp/
COPY src/OpenServiceBus.Management/OpenServiceBus.Management.csproj           src/OpenServiceBus.Management/
COPY src/OpenServiceBus.Host/OpenServiceBus.Host.csproj                       src/OpenServiceBus.Host/
COPY src/OpenServiceBus.Explorer/OpenServiceBus.Explorer.csproj               src/OpenServiceBus.Explorer/

RUN dotnet restore src/OpenServiceBus.Host/OpenServiceBus.Host.csproj \
    && dotnet restore src/OpenServiceBus.Explorer/OpenServiceBus.Explorer.csproj

# Copy the rest of the source and the prebuilt Explorer SPA. With wwwroot already
# present and BuildExplorerUi=false, the csproj skips its npm build (no Node here).
COPY src/ src/
COPY --from=spa /wwwroot src/OpenServiceBus.Explorer/wwwroot
RUN dotnet publish src/OpenServiceBus.Host/OpenServiceBus.Host.csproj \
    --configuration Release \
    --no-restore \
    --output /app/host \
    && dotnet publish src/OpenServiceBus.Explorer/OpenServiceBus.Explorer.csproj \
    --configuration Release \
    --no-restore \
    -p:BuildExplorerUi=false \
    --output /app/explorer

# ─── Runtime stage ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime

ARG VERSION=""

# Run as a non-root user - the broker doesn't need root and OCI scanners flag images that do.
RUN addgroup -S osb && adduser -S osb -G osb \
    && mkdir -p /data \
    && chown -R osb:osb /data

WORKDIR /app
COPY --from=build /app/host     ./host
COPY --from=build /app/explorer ./explorer

# Tiny POSIX-safe entrypoint that runs both apps in parallel, forwards SIGTERM/SIGINT to
# each, and exits the container the moment either process dies. Keeps both apps as plain
# .NET binaries (no supervisor dep) while still giving us a clean `docker stop` behaviour.
COPY --chown=osb:osb docker/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh && chown -R osb:osb /app

USER osb

# Defaults: SQLite persisted at /data/broker.db; Kestrel bound to the right ports per app.
# The Host's appsettings.json owns the management binding (5300); the Explorer is overridden
# via ASPNETCORE_URLS_EXPLORER below to bind 5400.
ENV OPENSERVICEBUS__STORAGE__MODE=Sqlite \
    OPENSERVICEBUS__STORAGE__DATASOURCE=/data/broker.db \
    ASPNETCORE_URLS_HOST=http://+:5300 \
    ASPNETCORE_URLS_EXPLORER=http://+:5400 \
    OSB_EXPLORER_VERSION=${VERSION} \
    DOTNET_RUNNING_IN_CONTAINER=true

VOLUME ["/data"]
EXPOSE 5300 5400 5672 5673

HEALTHCHECK --interval=15s --timeout=3s --start-period=5s --retries=3 \
    CMD wget -q -O /dev/null http://127.0.0.1:5300/health || exit 1

ENTRYPOINT ["/app/entrypoint.sh"]
