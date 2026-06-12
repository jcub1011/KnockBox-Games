# KnockBox server image. Build context must be the REPO ROOT (web/ is baked into the publish
# output):
#   docker build -f KnockBox.Server/Dockerfile -t knockbox .
# or simply `docker compose up --build` (the compose file sets the context).
#
# Games are NOT in the image: bind-mount any stable host directory onto /games (read-only is
# fine — the server only reads it). It survives image updates and container recreation, and can
# be shared by several instances. See docs/HOSTING.md.

# Runtime base: ports, mount points, and the non-root user. Native AOT produces a self-contained
# native binary, so the runtime image needs only the native deps (libc, zlib, …) — not the managed
# .NET runtime. runtime-deps is the smallest base that satisfies that.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS base
WORKDIR /app
# /games is the (read-only) mount point — the server only reads it, so it needs no ownership change.
# /app/logs must be writable by the non-root app user, so hand it to $APP_UID while still root.
RUN mkdir -p /games /app/logs && chown $APP_UID /app/logs
USER $APP_UID
# Two plain-HTTP ports keep the two-origin model working with no proxy: 8080 = shell, 8081 = games
# origin. Map them 1:1 on the host (or set KnockBox__GamesOrigin) — the server advertises its
# INTERNAL games port to browsers. TLS belongs to a fronting proxy (set KnockBox__ForwardedHeaders).
ENV KnockBox__GamesRoot=/games \
    KnockBox__LogsRoot=/app/logs \
    KnockBox__GamesPort=8081 \
    KnockBox__GamesPollSeconds=10 \
    ASPNETCORE_HTTP_PORTS="8080;8081"
EXPOSE 8080
EXPOSE 8081

# Build + publish. The solution file is deliberately NOT copied: its absence inside the image is
# what makes the server's path resolution fall through to the app directory (see ContentPaths).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
# Native AOT compiles to a native object file and links it: the SDK image doesn't ship the toolchain,
# so install clang + zlib headers for the ILC backend.
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY KnockBox.Contracts/KnockBox.Contracts.csproj KnockBox.Contracts/
COPY KnockBox.Server/KnockBox.Server.csproj KnockBox.Server/
RUN dotnet restore KnockBox.Server/KnockBox.Server.csproj
COPY KnockBox.Contracts/ KnockBox.Contracts/
COPY KnockBox.Server/ KnockBox.Server/
# web/ must be present before publish — the csproj bakes it into the output as content.
COPY web/ web/
# AOT publish needs an explicit RID; it produces a native apphost, so UseAppHost stays on (the old
# framework-dependent flag is gone). Output is the self-contained native binary + the baked web/.
RUN dotnet publish KnockBox.Server/KnockBox.Server.csproj -c $BUILD_CONFIGURATION -r linux-x64 -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
# The published native binary, not `dotnet <dll>` (there is no managed entry assembly under AOT).
ENTRYPOINT ["./KnockBox.Server"]
