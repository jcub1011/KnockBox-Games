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
# /app/logs, /app/games-compressed and /app/games-unpacked must be writable by the non-root app user,
# so hand them to $APP_UID while still root. The latter two are regenerable derived caches that must
# stay OUT of the read-only /games mount:
#   • games-compressed — pre-compressed .br/.gz asset variants.
#   • games-unpacked   — games extracted from .kbg packages dropped into /games. Without this the
#                        server can only serve plain game FOLDERS, since it cannot expand a package
#                        into a read-only mount.
# Both default to these container-local dirs (rebuilt from /games on boot), but you can mount a named
# volume or host path onto either to PERSIST it across image updates and skip redoing that work each
# boot (see docker-compose.yml / docs/HOSTING.md). A bind-mounted host path must be writable by
# $APP_UID; a named volume inherits this dir's ownership.
#
# /app/data holds the ADMIN PASSWORD HASH (admin.secret). Unlike the two caches it is NOT regenerable:
# losing it returns the admin portal to its unclaimed state, where the next visitor sets the password.
# It needs its own dir because the service's default (next to the binary, /app) is root-owned while the
# app runs as $APP_UID — the write would fail — and /app lives inside the image, so the password would
# be lost on every image update. Mount a volume here to persist it (see docker-compose.yml).
#
# /app/games-managed holds packages the ADMIN PORTAL installed (marketplace downloads and uploads) plus
# the previous versions retained for rollback. Created and owned here for the same reason as /app/data —
# /app itself is root-owned, so the app user cannot make it at startup — and, like /app/data, it is NOT
# regenerable: a marketplace package can be fetched again, an uploaded one exists nowhere else. Mount a
# volume here (compose does) so portal installs survive an image update.
RUN mkdir -p /games /app/logs /app/games-compressed /app/games-unpacked /app/games-managed /app/data /app/blobs \
    && chown $APP_UID /app/logs /app/games-compressed /app/games-unpacked /app/games-managed /app/data /app/blobs
USER $APP_UID
# Three plain-HTTP ports keep the three-origin model working with no proxy: 8080 = shell, 8081 = games
# origin, 8082 = admin portal. Map the first two 1:1 on the host (or set KnockBox__GamesOrigin) — the
# server advertises its INTERNAL games port to browsers. The admin port should NOT be published to the
# world: until a password is set, the first visitor to reach it claims the portal (docs/HOSTING.md), so
# compose binds it to loopback only. TLS belongs to a fronting proxy (set KnockBox__ForwardedHeaders).
#
# EVERY origin must appear in ASPNETCORE_HTTP_PORTS. Setting it suppresses the UseUrls fallback in
# Program.cs wholesale, so an origin omitted here simply never binds and refuses connections.
ENV KnockBox__GamesRoot=/games \
    KnockBox__LogsRoot=/app/logs \
    KnockBox__GamesCompressedRoot=/app/games-compressed \
    KnockBox__GamesUnpackedRoot=/app/games-unpacked \
    KnockBox__GamesManagedRoot=/app/games-managed \
    KnockBox__GamesPort=8081 \
    KnockBox__GamesPollSeconds=10 \
    KnockBox__AdminPort=8082 \
    KnockBox__AdminPasswordPath=/app/data/admin.secret \
    ASPNETCORE_HTTP_PORTS="8080;8081;8082"
# Set EXPLICITLY rather than inherited from the base image, because the startup check that warns
# "this directory is inside the container and the next image update will destroy it" is gated on it
# (see Hosting/StatePersistence.cs). The .NET base images do set it, but relying on that makes the
# whole warning disappear silently if a future base image stops — and the other signal, /.dockerenv,
# does not exist under Kubernetes or containerd, which is exactly where a missing volume is most
# likely and least visible. A base-image change quietly deleting a persistence safeguard is not
# hypothetical: that is how this project's predecessor lost the chown that made its data volume
# writable. DockerPersistenceTests asserts this line stays here.
ENV DOTNET_RUNNING_IN_CONTAINER=true
# GC footprint: the GC mode and the ConserveMemory knob are baked into the binary at publish (see
# KnockBox.Server.csproj) and honored under Native AOT, so nothing is needed here by default. Server
# GC + DATAS (heap-count adaptation, on by default) does the footprint work; the main lever is a
# container memory limit (compose `mem_limit`, k8s limits) so .NET sizes/collects against the cgroup
# budget. Verify via KnockBox__MemoryLogSeconds. FOOT-GUN: do NOT pin a heap count to "save memory" —
# DOTNET_GCHeapCount is HEX *and* setting it disables DATAS, which is what you actually want here:
#   ENV DOTNET_gcServer=1 DOTNET_GCConserveMemory=5   # leave heap count to DATAS
EXPOSE 8080
EXPOSE 8081
EXPOSE 8082

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
# The addon manifest is EMBEDDED into the assembly (Hosting/KnockBoxSdk.cs reads the one authoritative
# sdkVersion from it), so it has to exist in the build context too. Only the one file: the rest of
# clients/ is the addon SOURCE, which the server never serves and must not bloat the image.
# The `aot` CI job publishes from a full checkout and so cannot catch this — Docker's selective COPY is
# the only place a missing build input shows up.
COPY clients/addons.manifest.json clients/
# AOT publish needs an explicit RID; it produces a native apphost, so UseAppHost stays on (the old
# framework-dependent flag is gone). Output is the self-contained native binary + the baked web/.
RUN dotnet publish KnockBox.Server/KnockBox.Server.csproj -c $BUILD_CONFIGURATION -r linux-x64 -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
# The published native binary, not `dotnet <dll>` (there is no managed entry assembly under AOT).
ENTRYPOINT ["./KnockBox.Server"]
