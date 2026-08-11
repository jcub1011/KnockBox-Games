# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

KnockBox is a game-hosting platform for multiplayer web games. Drop an HTML5/WASM game into
`games/` — as a single `.kbg` package or a plain folder — and it becomes playable with no server
code and no restart. The server owns discovery, lobbies, anonymous identity, and message routing;
**games own all logic and state** (host-authoritative). Games talk to the server over WebSocket via
the `web/knockbox.js` SDK. See `docs/INFRASTRUCTURE.md` (architecture),
`docs/GAME_DEVELOPER_GUIDE.md` (authoring), and `docs/KBG_FORMAT.md` (the package format).

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
- `docker` — image build + smoke test (boots the container, checks shell/SDK serving, hot-reload
  discovery, and that the admin portal binds its own port, claims a password once and stays 404 on the
  public origins — the only place a real listener is exercised). Build context is the repo root;
  `web/` must be present.

Deployment: the `games/` directory is mounted **read-only** from a stable host path
**outside** the image, so it survives image updates (see `docs/HOSTING.md`). That read-only-ness is
why both derived caches (`GamesCompressedRoot`, `GamesUnpackedRoot`) live outside it on their own
writable mounts. On bind mounts, file-watch events don't propagate, so the image sets
`KnockBox__GamesPollSeconds=10` as a polling fallback for hot-reload — which is also the only signal
that notices a dropped `.kbg` there.

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
- `tools/pack-game/` (`knockbox-pack-game`) — engine-agnostic CLI (`knockbox-pack`) that packages a
  game into a drop-in `<id>.kbg` file (`--dir` still emits the plain folder layout). `kbg.mjs` is the
  dependency-free stored-ZIP writer + Brotli pipeline; its compress-or-store decision deliberately
  mirrors `GameAssetPrecompressor.ShouldCompress`, so keep the two in sync.

### One `/ws` endpoint, two roles (the core idea)
`/ws` is served on **both** the shell origin and the game origin. The **first frame** selects the role:
- **Control role** (`HelloMessage`, the shell's socket): identity handshake, lobby ops
  (list/create/join/leave), and `RequestTicket`. Handled by `RunControlAsync` in
  `KnockBox.Server/Networking/WebSocketHandler.cs`.
- **Data role** (`AttachMessage`, the game iframe's own socket): authenticates with a
  lobby-scoped ticket, then relays `Game{to, payload}` messages where `to` ∈
  `{"host","all","<playerId>"}`; the server stamps `from` and fans out. Handled by `RunDataAsync`.

### Three origins
Shell origin (5114 dev) serves the shell UI + SDK; game origin (5115 dev, a subdomain in
prod) serves `/games/{id}/…` builds. Games run in **cross-origin iframes** so untrusted game
code cannot read the shell's identity token. `Hosting/OriginRouting.cs` resolves which origin
a request is on; `Hosting/ContentPaths.cs` resolves the web/games/logs locations.

Third: the **admin origin** (`AdminPort`, 5116 dev / 8082 image; `AdminHost`/`AdminOrigin` as a subdomain
in prod), an operator dashboard served from `web/admin/` at that origin's **root**, API under
`/admin/api/*`, claimed in a `MapWhen` branch ahead of the game and shell pipelines. Every `/admin*` path
404s on the two public origins. Auth is one PBKDF2-hashed password in `AdminPasswordPath`
(`Security/AdminAuthService.cs`) plus an HMAC session cookie whose key is per-process — **claim-on-first-use**:
while no password is set, whoever reaches the origin sets it, which is why compose binds 8082 to loopback.

**Port-binding trap (this shipped broken once):** `Program.cs` binds all three origins itself *only* when
nothing else set ports. Any explicit `ASPNETCORE_URLS` / `ASPNETCORE_HTTP_PORTS` / `Kestrel:Endpoints`
**replaces** that list instead of adding to it, and `GamesPort`/`AdminPort` only tell the *router* which
port maps to which origin — they bind nothing. So every origin must be listed in `launchSettings.json`
`applicationUrl`, the Dockerfile's `ASPNETCORE_HTTP_PORTS`, and any env you set, or it is routed but never
listened on and answers `connection refused`. A startup check logs the address each origin actually bound
and warns when the admin port isn't among them; `OriginPortBindingTests` asserts the repo's own files stay
in sync. NOTE: `launchSettings.json` is parsed as **strict JSON** — a `//` comment there makes the whole
profile silently fail to apply (falling back to Production + the built-in ports).

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
`Games/GameCatalog.cs` scans `<root>/*/GAME.json` at startup and on change (debounced
`FileSystemWatcher`, plus the polling fallback above). It is **multi-root**: `games/` first, then
`GamesUnpackedRoot` (where `.kbg` packages are extracted), and the first root to claim an id wins with
a warning on duplicates. Only `roots[0]` is watched/polled — the installer owns the other root and
triggers rediscovery itself. The folder name **must equal** the manifest `id`, and `entry` is
path-traversal–checked to stay inside the game folder. **One** dictionary of `GameEntry(manifest,
directory)` is swapped atomically — two parallel dictionaries could not be swapped together, letting a
reader pair a pre-swap manifest with a post-swap path. `TryGetDirectory`/`GameDirectories` expose the
resolved path (never put it on `GameManifest`, which goes over the wire). A games dir
that is missing OR present-but-unreadable (e.g. a Docker mount the UID-1654 user can't read) does
**not** crash startup: `Discover()` catches the access error and exposes `GameCatalog.ScanError`,
which `Hosting/DeploymentDiagnostics.cs` surfaces (with other file-access problems found at
bootstrap) by replacing the shell home page with `Hosting/DeploymentWarningPage.cs` — see the
home-page warning middleware in `Program.cs` and `docs/HOSTING.md`.

GAME.json fields: `id`, `name`, `entry` (entry HTML), `thumbnail`, `maxPlayers`,
`crossOriginIsolated` (optional, for threaded engine exports), and `themeColor` /
`themeTextColor` (optional CSS colors the shell tints the in-game header chrome with;
shell-validated, so invalid values are ignored — no CSS injection).

### `.kbg` game packages
A game can be installed as a single `.kbg` file instead of a folder: copying it into `games/` is the
whole procedure, no CLI and no restart. Spec: `docs/KBG_FORMAT.md`. It is a ZIP with every entry
**stored**, a `KBG.json` header (`formatVersion`, `id`, `files[]`), and per-file **Brotli** payloads
under `<path>.br`. Brotli rather than deflate/LZMA because it is built into both .NET and Node (zero
new dependencies, keeping the `aot` job clean) and lands within ~3% of LZMA; per-file rather than solid
so `GameAssetPrecompressor.SeedFromPackage` can copy the blobs straight into `games-compressed/`,
skipping the ~49s-per-asset max-effort Brotli the server otherwise pays on every cold boot.

`Games/GamePackageInstaller.cs` extracts into `GamesUnpackedRoot` (the `games/` mount is read-only in
production). It owns **no watcher and no timer**: it hangs off `GameCatalog.Discovered` and calls
`catalog.ScheduleRescan()` — never `Discover()`, which has no mutual exclusion and could let an older
scan win the publish. `ComputeFingerprint` therefore also stats `*.kbg`; without that, packages would
never install under Docker, where the poll is the only signal that fires. A package must present the
same (mtime, length) on **two** consecutive passes before it is read (so a half-copied archive never
is), and must be absent for two passes before its game is uninstalled (so delete-then-copy doesn't
drop a live game). `Reconcile()` returns `Pending` for both of those deferrals — the caller must
rescan, or that work stalls until an unrelated file event arrives.

`Games/GamePackageReader.cs` treats packages as untrusted: full validation before any byte is written,
manual entry iteration (**never** `ZipFile.ExtractToDirectory` — no caps, can't pre-validate, and on
.NET 7+ it restores the entry's Unix file mode), strict path rules, and byte caps counted **while
copying** because declared sizes are attacker-controlled. Tests: `GamePackageReaderTests` (validation),
`GamePackageInstallerTests` (lifecycle), `PackageFixture` (builds deliberately malformed packages).

### Serving game assets & pre-compression
Game builds are served with stock `UseStaticFiles` (ETag + `must-revalidate`, so unchanged
assets — esp. the large `.wasm` — return `304`). To avoid re-compressing the same static bytes
on every cold request, `Games/GameAssetPrecompressor.cs` keeps a derived cache of max-effort
(`CompressionLevel.SmallestSize`) `.br`/`.gz` variants under `GamesCompressedRoot`
(default sibling `games-compressed/`, **writable, outside the read-only `games/` mount**).
`GameAssetPrecompressor` is **root-agnostic** — `ReconcileAll` takes an id→directory map (from
`GameCatalog.GameDirectories`), because a game's files may sit under either root. Both former uses of
`gamesRoot` had to change together: fixing the compression side alone would leave the prune side
deleting every package-backed game's cache each pass and recompressing it at max effort.
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

### Server-authoritative mode (`Games/ServerAuthority*.cs`, `Games/*AuthorityRuntime*.cs`)
Optional, per-game. A game opts in with `GAME.json` `serverAuthority: "authority.js"` (validated
like `entry`; the game origin never serves the module — `Hosting/GameOriginAssetGate.cs` → 404).
On lobby creation `ServerAuthorityManager` loads the module into a per-lobby sandboxed **Jint**
engine (`JsAuthorityRuntime` behind `IAuthorityRuntime`; `Date` deleted, no CLR, memory/timeout/
statement/recursion budgets — AOT-clean via `TrimmerRootAssembly`) wrapped in a `ServerAuthority`
actor: one drain task over a bounded `Channel` (two-tier overflow — intents drop, ticks coalesce,
roster never dropped). `WebSocketHandler.HandleGameMessage` diverts `to:"host"` to the actor
instead of a client and enforces the `_kb` envelope both ways (§5d — clients can't publish
`delta`/`state`); the actor broadcasts state stamped `from:"server"`. The module ABI is the
`kb-authority.js` model contract as a superset (`init`/`applyIntent`/`snapshot` + roster hooks +
`tick`/`config`), runtime-agnostic (JSON strings in/out) so a WASM backend is additive (Phase 4,
not built). **Owner ≠ authority**: every client is `isHost:false`; `Lobby.HostId` (now mutable,
lock-guarded) is the owner holding kick/open powers, reassignable by the module via `kb.setOwner`
(`OwnerChanged`/`GameOwnerChanged` events, so the session survives the creator leaving). Errors:
a module throw is contained (drop + re-broadcast snapshot; 5 in a row → fatal), a constraint
violation is fatal (`LobbyClosed`, sockets aborted). See `docs/SERVER_AUTHORITY_DESIGN.md` and
GAME_DEVELOPER_GUIDE §5b. Knobs: `Authority*` (§Configuration).

**Shared word dictionaries (`kb.words`)**: a game declares immutable word lists in `GAME.json`
(`authorityWords: { "<key>": { file, caseInsensitive } }`, validated like `serverAuthority` + a size
cap, and requiring `serverAuthority`). `Games/Words/AuthorityWordService.cs` (DI singleton) loads each
file **once** into a shared, length-bucketed packed-ASCII structure (`WordPool`/`WordPoolSet`, adapted
from the sibling `KnockBox.WordService` repo) shared by every lobby engine of the game and deduped
across games by content hash (byte-identical files share one structure regardless of name/path) — so a large dictionary costs one copy, never a per-lobby copy or a
raised memory cap. The module queries it via `kb.words.has/count/pick/countOfLength/pickOfLength`
(`ClrFunction`s over the shared pool — the dictionary never enters the JS heap; guarded, so unknown
key / out-of-range → `false`/`0`/`null`). The word files are denied on the game origin
(`GameOriginAssetGate`, server-side/secret) and skipped by the precompressor; `knockbox-local.js`
emulates `kb.words` with server-identical `pick` ordering. GAME_DEVELOPER_GUIDE §5b walks a
worked example; the docker CI job synthesizes one to prove the files 404 on the game origin.

**Server authority + `.kbg` packages**: the two compose. An authority game packs like any other
(`knockbox-pack` ships `serverAuthority` / `authorityWords` files inside the archive) and installs
into `GamesUnpackedRoot`, so its files are **not** under `GamesRoot/<id>`. Everything server-side
therefore resolves a game's folder through the catalog — `GameCatalog.TryGetDirectory` /
`GameLocations`, never `gamesRoot/<id>`: `ServerAuthorityManager` takes a
`Func<string, string?> gameDirectory` resolver, and the `Discovered` event carries
`GameLocation(Manifest, Directory)` so the precompressor, word-pool prune and module-cache prune
all see both. The origin gate is path-based and so is unaffected by which root won; the installer
skips seeding compressed variants of the never-served files.

### Web SDK (`web/knockbox.js`)
Games load `<script type="module" src="/knockbox.js">`. Key API: properties `playerId`,
`players`, `isHost` (plus `authority`/`ownerId`/`isOwner` for server-authority mode, normalized via
`kb-core.js` `normalizeReady`); callbacks `onReady`, `onMessage`, `onPlayerJoined`, `onPlayerLeft`,
`onPlayerDisconnected`, `onPlayerConnected` (the last two: a peer's tab dropped but is held for the
reconnect grace window, then returned — they stay in `players` throughout), `onOwnerChanged`; send
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
`GamesPort`/`GamesHost`/`GamesOrigin` and `AdminPort`/`AdminHost`/`AdminOrigin` (origin routing),
`AdminPasswordPath`/`AdminSessionTtlHours` (admin portal; the path must be writable and, in a container,
on a persisted volume outside the image), `GamesPollSeconds` (hot-reload
fallback), `Precompress`/`GamesCompressedRoot`/`PrecompressGzip`/`PrecompressMinBytes`/`PrecompressReconcileSeconds`
(pre-compressed game-asset cache), `Packages`/`GamesUnpackedRoot`/`MaxPackageBytes`/`MaxPackageEntries`/`MaxPackageRatio`
(`.kbg` install; the root must be writable and outside `games/`), `LogRetentionDays` (daily log files kept under `LogsRoot`, default 31),
`ForwardedHeaders`/`AllowedOrigins` (behind a reverse proxy),
`*TokenTtlHours`, `DisconnectGraceSeconds` (reconnect grace before a dropped member is removed,
default 60; `0` = immediate), the rate-limit knobs (`*MessagesPerSecond/Burst`,
`MaxConnectionsPerIp`, `LobbyCreatesPerMinute`), and the server-authority knobs (`AuthorityEnabled`
master switch, `AuthorityMax{MemoryBytes,Statements,ScriptBytes,WordFileBytes,Lobbies}`,
`AuthorityCallTimeoutMs`, `AuthorityRecursionLimit`, `AuthorityTickHzMax`, `AuthorityQueueCapacity`).
