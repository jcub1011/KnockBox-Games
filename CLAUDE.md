# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

KnockBox is a game-hosting platform for multiplayer web games. Drop an HTML5/WASM game
folder into `games/` and it becomes playable with no server code and no restart. The server
owns discovery, lobbies, anonymous identity, and message routing; **games own all logic and
state** (host-authoritative). Games talk to the server over WebSocket via the `web/knockbox.js`
SDK. See `docs/INFRASTRUCTURE.md` (architecture) and `docs/GAME_DEVELOPER_GUIDE.md` (authoring).

## Commands

Solution file is `KnockBox-Games.slnx` (modern `.slnx`, not legacy `.sln`). All projects target `net10.0`.

- Build: `dotnet build KnockBox-Games.slnx`
- Run (dev): `dotnet run --project KnockBox.Server --launch-profile http`
  — shell at http://localhost:5114, games origin at http://localhost:5115
- All .NET tests (xUnit): `dotnet test KnockBox-Games.slnx --nologo`
- Single .NET test: `dotnet test KnockBox.Server.Tests --filter "Name~SomeTestName"`
  (or `--filter "FullyQualifiedName~Namespace.Class.Method"`)
- Web tests (Vitest, from `web/`): `npm ci && npm test` (watch: `npm run test:watch`)
- Phaser client tests (from `clients/phaser/`): `npm ci && npm run lint && npm test`
- pack-game tool tests (from `tools/pack-game/`): `npm ci && npm test`
- Desktop publish (self-contained win-x64 exe): `dotnet publish KnockBox.Server -p:PublishProfile=win-x64-desktop`

The `web/` frontend is plain ES modules — **no build step**; it is served directly and baked
into publish/Docker output. Only `web/kb-core.js` (pure protocol logic) is unit-tested.

## Docker / CI

Docker does not build locally on this machine — verify container changes via GitHub Actions
(`gh run watch`). CI (`.github/workflows/ci.yml`) runs six jobs:
- `dotnet` — .NET build & tests.
- `aot` — Native AOT publish with `/warnaserror`; any new trim/AOT `ILxxxx` warning fails the
  build (mirrors the Dockerfile build stage, needs clang + zlib). Keeps the server AOT-clean.
- `web` — shell + SDK Vitest tests.
- `clients-phaser` — Phaser client lint + tests.
- `pack-game` — packer tool tests.
- `docker` — image build + smoke test (boots the container, checks shell/SDK serving and
  hot-reload discovery). Build context is the repo root; `web/` must be present.

Deployment: the `games/` directory is mounted **read-only** from a stable host path
**outside** the image, so it survives image updates (see `docs/HOSTING.md`). On bind mounts,
file-watch events don't propagate, so the image sets `KnockBox__GamesPollSeconds=10` as a
polling fallback for hot-reload.

## Architecture

### Projects
- `KnockBox.Contracts` — shared wire DTOs: `Messages.cs` (polymorphic, `type`-discriminated,
  camelCase on wire), `GameManifest.cs` (the GAME.json shape), `Player.cs`, `Protocol.cs`
  (wire version, currently 1).
- `KnockBox.Server` — ASP.NET Core host. **No database, no EF** — all state is in-memory
  singletons (`Program.cs` wires `GameCatalog`, `TokenService`, `LobbyManager`,
  `ConnectionManager`, `WebSocketHandler`, `ServerLimits`, `TimeProvider`).
- `KnockBox.Server.Tests` / `KnockBox.Contracts.Tests` — xUnit.

Outside the .NET solution, two Node subprojects (each its own npm package, Vitest-tested):
- `clients/phaser/` (`knockbox-phaser`) — networking client for Phaser. Ships `kb-core.js`
  (pure protocol logic, same concept as `web/kb-core.js`), `knockbox-local.js` (server-less
  local peer), and `kb-authority.js` (host-authoritative helper).
- `tools/pack-game/` (`knockbox-pack-game`) — engine-agnostic CLI (`knockbox-pack`) that
  assembles a drop-in `games/<id>/` folder.

### One `/ws` endpoint, two roles (the core idea)
`/ws` is served on **both** the shell origin and the game origin. The **first frame** selects the role:
- **Control role** (`HelloMessage`, the shell's socket): identity handshake, lobby ops
  (list/create/join/leave), and `RequestTicket`. Handled by `RunControlAsync` in
  `KnockBox.Server/Networking/WebSocketHandler.cs`.
- **Data role** (`AttachMessage`, the game iframe's own socket): authenticates with a
  lobby-scoped ticket, then relays `Game{to, payload}` messages where `to` ∈
  `{"host","all","<playerId>"}`; the server stamps `from` and fans out. Handled by `RunDataAsync`.

### Two origins
Shell origin (5114 dev) serves the shell UI + SDK; game origin (5115 dev, a subdomain in
prod) serves `/games/{id}/…` builds. Games run in **cross-origin iframes** so untrusted game
code cannot read the shell's identity token. `Hosting/OriginRouting.cs` resolves which origin
a request is on; `Hosting/ContentPaths.cs` resolves the web/games/logs locations.

### Identity & tickets (ephemeral by design)
`Security/TokenService.cs` issues HMAC-SHA256 signed tokens. The signing secret is **random
per process** and never persisted — restarting the server invalidates all tokens and lobbies;
this is intentional (anonymous, no accounts).
- **Identity token** (`{playerId, exp}`, ~30d TTL): minted on `Hello`, stored in the shell's
  `sessionStorage` (per-tab), proves ownership on reconnect.
- **Game ticket** (`{playerId, lobbyId, gameId, exp}`, ~12h TTL): scoped to one lobby+game,
  handed to the game iframe via the **URL fragment** (never query/Referer/logs). On `Attach`,
  validity is re-checked against **live lobby membership** (primary) plus ticket signature/expiry.

### Game discovery & hot-reload
`Games/GameCatalog.cs` scans `games/*/GAME.json` at startup and on change (debounced
`FileSystemWatcher`, plus the polling fallback above). The folder name **must equal** the
manifest `id`, and `entry` is path-traversal–checked to stay inside the game folder. The
catalog reference is swapped atomically — readers never see a half-built catalog. A games dir
that is missing OR present-but-unreadable (e.g. a Docker mount the UID-1654 user can't read) does
**not** crash startup: `Discover()` catches the access error and exposes `GameCatalog.ScanError`,
which `Hosting/DeploymentDiagnostics.cs` surfaces (with other file-access problems found at
bootstrap) by replacing the shell home page with `Hosting/DeploymentWarningPage.cs` — see the
home-page warning middleware in `Program.cs` and `docs/HOSTING.md`.

GAME.json fields: `id`, `name`, `entry` (entry HTML), `thumbnail`, `maxPlayers`,
`crossOriginIsolated` (optional, for threaded engine exports), and `themeColor` /
`themeTextColor` (optional CSS colors the shell tints the in-game header chrome with;
shell-validated, so invalid values are ignored — no CSS injection).

### Serving game assets & pre-compression
Game builds are served with stock `UseStaticFiles` (ETag + `must-revalidate`, so unchanged
assets — esp. the large `.wasm` — return `304`). To avoid re-compressing the same static bytes
on every cold request, `Games/GameAssetPrecompressor.cs` keeps a derived cache of max-effort
(`CompressionLevel.SmallestSize`) `.br`/`.gz` variants under `GamesCompressedRoot`
(default sibling `games-compressed/`, **writable, outside the read-only `games/` mount**).
Reconciliation is driven by `GameCatalog.Discovered` plus a periodic timer
(`PrecompressReconcileSeconds`) — it (re)compresses changed files (mtime/length freshness),
prunes orphaned variants, and removes directories for deleted games. A negotiation step on the
game origin (`Program.cs`) rewrites a request to the `.br`/`.gz` variant when present and
accepted (`Accept-Encoding`), serving it with `Content-Encoding`/`Vary` and the **decompressed**
content-type; a miss falls through to the raw file. The on-the-fly `ResponseCompression`
(Brotli/Gzip at `Fastest`) stays as the fallback for not-yet-warmed assets and the shell origin —
it skips bodies that already carry `Content-Encoding`, so precompressed responses aren't
re-compressed. Disable the whole cache with `Precompress=false`.

### Lobbies & connections
- `Lobbies/Lobby.cs` / `LobbyManager.cs` — in-memory lobbies keyed by a 4-char human code;
  membership is lock-guarded and `Players` is returned as a snapshot so broadcasts can't race
  membership changes. Kicking bars rejoin for that lobby. A dropped **control** socket doesn't
  remove the player immediately: with `DisconnectGraceSeconds` (default 60) set they're flagged
  disconnected but kept in the lobby (so a tab refresh / blip survives — `PlayerDisconnected` /
  `PlayerConnected` events tell peers), and a periodic reaper in `Program.cs` calling
  `WebSocketHandler.ReapDisconnectedPlayers()` evicts those whose grace elapses. `0` = old
  immediate-leave behavior. A lobby with no connected members left (empty, or all disconnected) is
  closed immediately (`CloseLobbyIfDark`) rather than held — the grace only helps when someone's
  still there. Explicit leaves (`LeaveLobby`) are always immediate.
- `Networking/Connection.cs` — wraps one socket with a bounded single-reader outbound
  `Channel` drained by one writer task (preserves order). Overflow policy differs by role:
  control = `CloseOnFull` (events are precious), data = `DropOldest` (state is ephemeral).
- `Networking/ConnectionManager.cs` — separate registries for control vs. game sockets; one
  player may hold both during a session.

### Abuse protection (`Networking/ServerLimits.cs`, `TokenBucket.cs`, `IpConnectionGate.cs`)
Handshake timeout on `/ws`, per-connection token-bucket rate limits (separate for control vs.
data planes), per-IP connection cap, and a per-player lobby-create throttle. All are
configurable and disabled with `0`.

### Web SDK (`web/knockbox.js`)
Games load `<script type="module" src="/knockbox.js">`. Key API: properties `playerId`,
`players`, `isHost`; callbacks `onReady`, `onMessage`, `onPlayerJoined`, `onPlayerLeft`,
`onPlayerDisconnected`, `onPlayerConnected` (the last two: a peer's tab dropped but is held for the
reconnect grace window, then returned — they stay in `players` throughout); send
methods `sendToHost`, `sendToAll`, `sendTo(playerId, …)`, host-only `setLobbyOpen`,
`log.{info,warn,error,debug,trace,critical}(message)` (console-like logging to the server, relayed
as a `LogMessage` and written under the `KnockBox.GameLog` category), and `logPlay(metadata)` (a
`<string,string>` bag sent as a `PlayLogMessage`; the server stamps gameId/timestamp/isHost and
forwards it to that player's **control** socket, where the shell persists the most-recent 50 in
`localStorage` (`kb.playLog`) and renders them in the home-page Play Log).
`web/shell.js` owns the control socket and lobby UI; `web/kb-core.js` holds pure, tested
protocol helpers (reconnect/backoff, fragment parsing, roster reducers, Play Log: `appendPlayLog`,
`partitionPlayLogMetadata`, `ordinal`). Close code **1008** is terminal (no reconnect); other
closes back off exponentially.

### Logging (server side)
Serilog is the host logger (`builder.Host.UseSerilog` in `Program.cs`): console + a **daily**
rolling file at `LogsRoot/knockbox-YYYYMMDD.log`, retained for `KnockBox:LogRetentionDays`
days (default 31). Game logs relayed via `LogMessage` land under the `KnockBox.GameLog`
category so they're filterable. Crucially, levels/sinks are configured **in code, not** via
`ReadFrom.Configuration`: that package's assembly scanning is not Native-AOT-safe and emits
IL2104/IL3002/IL3053 at publish — `ReadFrom.Services` (DI-only) is used instead. Touch this
setup carefully to keep the `aot` CI job green.

## Configuration

All knobs use the `KnockBox:` prefix (env: `KnockBox__Key`, `__` for nesting). Full reference
in `docs/INFRASTRUCTURE.md` §9. Frequently relevant: `GamesRoot`/`WebRoot`/`LogsRoot`,
`GamesPort`/`GamesHost`/`GamesOrigin` (origin routing), `GamesPollSeconds` (hot-reload
fallback), `Precompress`/`GamesCompressedRoot`/`PrecompressGzip`/`PrecompressMinBytes`/`PrecompressReconcileSeconds`
(pre-compressed game-asset cache), `LogRetentionDays` (daily log files kept under `LogsRoot`, default 31),
`ForwardedHeaders`/`AllowedOrigins` (behind a reverse proxy),
`*TokenTtlHours`, `DisconnectGraceSeconds` (reconnect grace before a dropped member is removed,
default 60; `0` = immediate), and the rate-limit knobs (`*MessagesPerSecond/Burst`,
`MaxConnectionsPerIp`, `LobbyCreatesPerMinute`).
