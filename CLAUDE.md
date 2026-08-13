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
into publish/Docker output. Unit-tested under `web/__tests__/`: `web/kb-core.js` (pure protocol
logic, Node env) plus `shell.js` and `knockbox.js` (jsdom, against the **real** `index.html` —
`helpers.js` injects it, so element ids stay in sync with production markup). `index.html` loads
`/shell.js?v=N` — **bump `N` whenever you change `shell.js`**, or browsers serve the stale module
against new markup.

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
in prod), an operator dashboard served from `web/admin/` at that origin's **root**, claimed in a `MapWhen`
branch ahead of the game and shell pipelines. Every `/admin*` path 404s on the two public origins. The API
under `/admin/api/*` lives in `Hosting/AdminApi.cs` (`MapAdminApi`), not in `Program.cs`: one `WriteJson`
helper, one `RequireSession` wrapper and a route table, so an endpoint's handler is only the part specific
to it. Mutating routes additionally go through `WriteGuard` (JSON content type + `Sec-Fetch-Site`), which is
defence in depth behind `SameSite=Strict`, not the primary control — the port is. Operator guide:
`docs/ADMIN.md`.

**Portal tabs** (five; the frontend is `web/admin/{index.html,admin.js,admin-core.js,admin.css}`, where
`admin-core.js` is the pure/tested half exactly as `kb-core.js` is to `shell.js`, and `admin.js` exports
`bootstrap()` so it can be driven under jsdom). Only the **visible tab polls** — five panels each polling
would multiply the request rate for four nobody is looking at, and the games tab can trigger a disk walk.
- **Overview** — platform counters, `DeploymentDiagnostics` issues (repeated here because the warning page
  only replaces the *shell's* home page), maintenance toggle, and per-game relay cost.
- **Active Lobbies** — the directory, with single/bulk close, stale purge and per-member kick.
- **Game Catalog** — availability, disk footprint, delete, rescan, lifecycle badges.
- **Marketplace & Packages** — catalogs, install/update/rollback/uninstall, upload, the operations list.
- **System Logs** — the live stream plus raw file download.

**The marketplace tab's poll rate is split, and that split is the design.** `POLL_MS.marketplace = 3000`
hits **only** the in-memory job feed; the catalog — which reaches the network with a 30-second timeout — is
read on tab entry, on Refresh, and when a job reaches a terminal status (which is what flips a card from
"Update to 1.3.0" to "Up to date" the moment it's true). That is also why it is a fifth tab rather than
part of Game Catalog: one panel means one timer, and 20 s (a disk walk) and 3 s (a progress bar) have no
common answer. `web/__tests__/admin-marketplace.test.js` asserts exactly this.

**Two jsdom traps that file documents**, both from vitest reusing one `window` per file: every
previously-imported copy of `admin.js` still holds its `hashchange` listener, so assigning
`location.hash` in `beforeEach` makes stale modules re-render and re-fetch into the fresh DOM (use
`history.replaceState`); and their poll intervals keep firing against the next test's fetch stub unless
`stopPolling()` — exported for exactly this — is called in `afterEach`.

**Upload is `XMLHttpRequest`, not `fetch`**, and it is the only such path in the codebase. `fetch` has no
upload-progress event, and a half-gigabyte upload with no progress reads as hung — so the operator clicks
again and starts a second one. Two consequences: the 401 funnel `request()` owns had to be extracted into
`handleUnauthorized()` so the XHR path shares it, and `WriteGuard` grew a media-kind parameter scoped to
that single route (its content-type check is skipped when `ContentLength` is null, so the upload route
requires `application/octet-stream` *positively* rather than on that technicality).

**Server counters are cumulative; rates are the client's job.** `RelayMetrics` and `Connection`'s
frame/byte/drop counters only ever increase, and `system/status` reports `cpuSecondsTotal` rather than a
percentage: a rate needs two samples, and producing one server-side would mean sleeping inside a request or
keeping per-viewer state. `admin-core.js` `ratePerSecond` differences them, and returns **null** rather
than a negative when a counter goes backwards — that means the server restarted, and drawing it as a spike
would mislead at exactly the wrong moment.

**Per-game relay cost is measured, because games are not server-side-free.** Every socket holds a bounded
outbound `Channel` plus a writer task, and a `to:"all"` fan-out serializes once then sends per recipient.
`Networking/RelayMetrics.cs` counts frames/bytes in and out per game (fan-out counted per recipient, from
the actual send loop, so a member with no attached game socket costs nothing), and `Connection` counts
frames the `DropOldest` policy discarded — which used to be visible only in a log line nobody watches.

**Operator policy is the one persisted thing.** `Admin/AdminSettingsStore.cs` writes game availability
(`Available`/`Disabled`/`Staged`) and maintenance mode to `AdminSettingsPath` (default: beside
`AdminPasswordPath`, i.e. the persisted `/app/data` volume). Everything else here is deliberately ephemeral,
but an admin who disables a game means it to stay disabled across the next image update. Reads are lock-free
(a `volatile` immutable snapshot swapped atomically, the same discipline as `GameCatalog`) because the
lobby-create path calls them; a change **takes effect in memory even when the write fails**, and the setter
returns that as a warning rather than rejecting the change. The relay sees it through the narrow
`Admin/IPlatformPolicy.cs` — `WebSocketHandler` has no business knowing about settings files, and
`PlatformPolicy.OpenPlatform` keeps the flow tests free of one.

**What policy does and doesn't touch:** it gates **creation and listing only**. Existing lobbies play on
through both a disable and maintenance mode (spec §3.1), and join is deliberately ungated. `Staged` =
hidden from the catalog but still startable, which is why `ListGamesMessage` grew an optional `Include`:
the shell allowlists every launch against the catalog it was given, so a staged game reached via its
`/?game=<id>` link has to come back in that list or the shell rejects its own `EnterGame` as unknown. A
**disabled** game is never re-admitted by `Include` — that would only move the refusal to the create round
trip, after the launch overlay was already up. Staged is **visibility, not access control**: there are no
player accounts, so nothing can be authorized and the link is a weak secret at best.

**Closing a live lobby has exactly one implementation.** `Lobby/LobbyCloser.cs` (detach the authority actor,
remove the lobby, one `LobbyClosedMessage` fanned out, abort the game sockets) was extracted from
`ServerAuthorityManager.HandleFatal`, which now calls it — two copies would drift, and whichever gained the
next step would leave the other half-closing lobbies. Distinct from `CloseLobbyIfDark`, which broadcasts
nothing because nobody is left to tell. The authority hook is a settable `OnClosing` rather than a ctor
dependency (the manager needs the closer, so it can't also be an argument to it), but `HandleFatal` still
stops its **own** actor first: a class must not depend on external wiring to reach its own invariant.

**The live log view reads a ring buffer, not the log file.** `Admin/AdminLogBuffer.cs` is a hand-written
bounded `ILogEventSink` (no `Serilog.Sinks.*` package — same AOT reasoning that rejected
`ReadFrom.Configuration`), wired via `WriteTo.Sink` and constructed before `UseSerilog` since the host isn't
built yet. Level and `SourceContext` stay structured, so filtering is exact instead of a guess at parsing
rendered text; a monotonic sequence per event makes ordinary polling a stream (`?after=<seq>`), with no SSE
and no second socket role. It renders with `MessageTemplateTextFormatter("{Message:lj}")` **on purpose** —
Serilog's own `RenderMessage()` quotes string properties, so the portal and the log file would disagree
about the same event.

**Deleting a game is all-or-nothing, and usually impossible in production.** `Admin/AdminOperations.cs`
probes every parent directory for writability *before* closing a single lobby, because the bad outcome isn't
failure — it's removing the unpacked copy while leaving the `.kbg`, so the installer reinstalls the game and
the operator watches a deletion undo itself after their lobbies were torn down for nothing. `games/` is
mounted `:ro` in the shipped compose file and the server only ever reads it, so the API answers **409** (a
deployment limit, not a fault) and the portal disables the button with the blocking path named. Disk usage
counts the game folder **plus** its compressed cache **plus** its source `.kbg`; reporting only the first
understates a large WASM game by roughly its own cache. A missing `web/admin` is reported through `DeploymentDiagnostics` **and** answered with an
explanatory 503 at the origin, because the warning page only replaces the *shell* home page.
Auth is one PBKDF2-hashed password (min 12 chars, file created mode `600`) in `AdminPasswordPath`
(`Security/AdminAuthService.cs`) — **claim-on-first-use**: while no password is set, whoever reaches the
origin sets it, which is why compose binds 8082 to loopback. Claiming writes with `FileMode.CreateNew`, so
concurrent setups yield one winner and one 409 — a check-then-write let both "succeed" and left the loser
holding a cookie signed under a key that no longer existed. The session cookie's HMAC key is derived from a
per-process secret **plus a fingerprint of the stored hash**, so any change to that file revokes all
sessions (a reset actually locks an intruder out); its `Secure` flag follows the request scheme **or** an
`https://` `AdminOrigin`, since behind a TLS proxy without `ForwardedHeaders` the request here is plain
HTTP. The file *is* the credential — write access to it is
total control, and rollback is deliberately not defended against (it needs state the attacker doesn't
control); filesystem permissions are the boundary. Password attempts are rate-limited **before** hashing —
at 600k PBKDF2 iterations (~0.4s of a core) an unthrottled endpoint is an unauthenticated CPU-exhaustion
lever, not just a guessing oracle — by **two** buckets: per IP
(`AdminLoginAttemptsPerMinute`, `Networking/IpRateLimiter.cs`) for fair share, plus a server-wide one
(`AdminLoginAttemptsPerMinuteGlobal`, `TokenBucket`) that bounds CPU regardless. The second exists because
the first keys on an address `X-Forwarded-For` can invent unless `KnownProxies` names the proxy.

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
reader pair a pre-swap manifest with a post-swap path. `TryGetDirectory`/`GameLocations` expose the
resolved path (never put it on `GameManifest`, which goes over the wire); `Count` reads the game count
without `Games`' snapshot allocation. A games dir
that is missing OR present-but-unreadable (e.g. a Docker mount the UID-1654 user can't read) does
**not** crash startup: `Discover()` catches the access error and exposes `GameCatalog.ScanError`,
which `Hosting/DeploymentDiagnostics.cs` surfaces (with other file-access problems found at
bootstrap) by replacing the shell home page with `Hosting/DeploymentWarningPage.cs` — see the
home-page warning middleware in `Program.cs` and `docs/HOSTING.md`.

GAME.json fields: `id`, `name`, `entry` (entry HTML), `thumbnail`, `maxPlayers`, `version` (optional,
never validated — the marketplace's installed-side version, see below),
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

### The official marketplace (`KnockBox.Server/Marketplace/`)
Where admins get games from: a catalog index (`.plugins/CATALOG.json` in the separate
`jcub1011/KnockBox-Games-Marketplace` repo) listing one entry per published game, each pointing at a
`.kbg` on a GitHub release. `MarketplaceClient` is the **only outbound HTTP in the server** — a plain
singleton `HttpClient` over a `SocketsHttpHandler` with `PooledConnectionLifetime`, deliberately not
`IHttpClientFactory`, so no new package has to clear the AOT gate. Spec + trust model:
`docs/MARKETPLACE.md`.

**The install engine (`Games/PackageManager.cs`, `Games/PackageJobRegistry.cs`).** `DownloadAsync` still
installs nothing — `PackageManager` places what it hands back, and the same `PlaceAsync` serves a
download, an upload and a rollback, so none of them re-decides what "valid" means. Every path re-runs
`GamePackageReader.Read`, **including rollback**: a file that has sat on disk for months is not more
trustworthy than one off the network.

Packages the portal installs land in **`GamesManagedRoot`** (default sibling `games-managed`), a writable
*package* root the installer also scans — `games/` is `:ro` in production, so the portal cannot write
there at all. Extraction still goes to `GamesUnpackedRoot`, so the catalog's root list and asset serving
are unchanged, and `games/` still wins a contested id. Layout in `Games/ManagedPackageLayout.cs`. Unlike
the two derived caches this root is **not regenerable**: a marketplace package can be re-fetched, an
uploaded one exists nowhere else.

`PlaceAsync`'s order is load-bearing: **copy** the current package to `.backups/` (a *move* would leave
the id with no package, and a reconcile pass landing in that window starts the two-pass uninstall
countdown on a healthy game), then one `File.Move(..., overwrite: true)`, then stamp the mtime **strictly
past** the previous value — the installer keys freshness on `(mtime, length)`, so two same-length versions
inside one filesystem tick would otherwise look identical and the second would never extract (a rollback
is the likeliest way to hit that). `GamePackageInstaller.Adopt` then vouches for the file so it installs
on the next pass rather than waiting out the two-pass settle check, which exists for copy-in and has no
bearing on an atomic rename.

**Jobs, not blocking requests** — a download plus extraction outlives any request and a drain is
open-ended, so every operation answers `202` + `jobId` and the portal polls a cursor change feed. This is
the **third** use of that house pattern (after `AdminLogBuffer`): still no SSE and no second socket role.
A job is cancellable until `Applying` and refused after — a half-swapped game directory is the one outcome
worth refusing to create, the same reasoning `DeleteGame` applies to a half-delete. Retention never evicts
an active job, so the cap is deliberately soft.

**Sources** (`Marketplace/MarketplaceSourceRegistry.cs`): one `MarketplaceClient` per source, since each
holds exactly one catalog+ETag pair and so cannot be parameterised by URL; all share one `HttpClient`
(`CreateHttpClient()` reads nothing source-specific — it used to take an options argument it never looked
at). Per-source options are `global with { CatalogUrl, DownloadBaseUrl }`; the caps and timeouts stay
shared because those are policy about *this server*. Registrations are validated with
`MarketplaceClient.IsAllowedUrl` — the downloader's own rule, exposed rather than copied. One unreachable
source is an error string, never a failed aggregate (`GameCatalog.ScanError` discipline).

`Marketplace/MarketplaceProjection.cs` merges catalogs against installed state and is
`PluginUpdateEvaluator`'s **first production caller**. It synthesizes `installedOnly` there rather than in
the enum, because every `PluginUpdateStatus` is a statement about a catalog *entry* and that one is a
statement about the absence of one.

**Update policy** is per game and persisted (`manual`/`auto`/`drain`/`force`, recorded by *absence* when
manual, like `Available`); `Admin/GameUpdateCoordinator.cs` is the schedule. With nothing enrolled a pass
**makes no outbound request at all**, so a default deployment doesn't quietly start phoning home.

`Admin/GameLifecycleGate.cs` holds the transient `Draining`/`Updating` states and implements
`IPlatformPolicy` by composing `AdminSettingsStore` and ANDing its own answer. They are **never
persisted**: lobbies are in-memory, so after a restart every game has zero lobbies and a persisted drain
would be stale by construction — a server killed mid-update would come back with a game permanently
unlaunchable. `IPlatformPolicy` gained `UnavailableReason` so a draining game stays *listed* and refuses
with something a player can act on; `WebSocketHandler` passes that string through exactly as it already
does `MaintenanceMessage`, and still knows nothing about packages.

**`GamePackageLocations.Find` replaced three copies of `Path.Combine(GamesRoot, id + ".kbg")`.** The
installer accepts any `*.kbg` name and takes the id from the header inside, so that derivation was already
wrong for a hand-named package: it reported `packageBacked: false`, left the bytes uncounted, and — worst —
`DeleteGame` removed the unpacked folder while leaving the archive, so the installer put the game straight
back. The marker inside the extracted folder (`Games/PackageMarker.cs`, 4-field; a 3-field legacy marker
reads as `games`, which is exactly what it meant) is the authority.

**The catalog's commit history is the trust root, not the release** — a release asset can be
re-uploaded in place, so what the catalog commits to is a **`sha256`, which is required** (schema and
server) and enforced on every download. URLs are **derived**
(`{DownloadBaseUrl}/{repo}/releases/download/{tag}/{asset}`), never carried in the schema, so a
tampered entry has nothing to point elsewhere; `repo`/`tag`/`asset` are pattern-checked *before* any
request leaves the process. No GitHub API is used — deriving the URL dodges the 60/hr unauthenticated
limit, at the cost of making a malformed `asset` a hard error (it named `GAME.json` once; the schema
now requires `.kbg`). A download must also re-pass **the same `GamePackageReader.Read`** the installer
uses, and match the entry's id *and* version — never a second, weaker copy of that validation.
Catalog DTOs are all-nullable like `GamePackageHeader` (untrusted input must never throw on parse);
checking splits three ways — `Parse` (is it a catalog?), `ValidateEntry` (is it safe to act on?),
`PluginUpdateEvaluator` (is it meaningful?).

`GameManifest.Version` (new, optional, never validated) is a game's self-declared build label and the
installed side of the update check; `SemVer` in `PluginVersion.cs` implements real semver 2.0.0
precedence because string comparison inverts both `0.9.0 < 0.10.0` and `1.0.0-rc.1 < 1.0.0`.
`Incompatible` (min/maxAppVersion vs `Hosting/KnockBoxVersion.cs`, read off the assembly so csproj
`<Version>` is the one source of truth) **outranks** `UpdateAvailable` — never offer an update that
can't run; and an unparseable bound counts as incompatible, since a constraint we can't read isn't no
constraint. `InstalledVersionUnknown` is deliberately distinct from `UpdateAvailable`: every
hand-made game has no version, and nagging about all of them is noise.

### Serving game assets & pre-compression
Game builds are served with stock `UseStaticFiles` (ETag + `must-revalidate`, so unchanged
assets — esp. the large `.wasm` — return `304`). To avoid re-compressing the same static bytes
on every cold request, `Games/GameAssetPrecompressor.cs` keeps a derived cache of max-effort
(`CompressionLevel.SmallestSize`) `.br`/`.gz` variants under `GamesCompressedRoot`
(default sibling `games-compressed/`, **writable, outside the read-only `games/` mount**).
`GameAssetPrecompressor` is **root-agnostic** — `ReconcileAll` takes an id→manifest+directory map (from
`GameCatalog.GameLocations`), because a game's files may sit under either root. Both former uses of
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
like `entry`; the game origin never serves the module — `Hosting/GameOriginAssetGate.cs` → 404). That gate
compares **canonical** paths on both sides via `Hosting/GameAssetPath.cs` (the one parser for
`/games/{id}/{relative}`, also used by the thumbnail allowlist, the COOP/COEP header hook and the
pre-compressed negotiation): raw string equality denied `…/authority.js` but waved through
`…//authority.js`, which `PhysicalFileProvider` then resolved to the very same file.
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
`partitionPlayLogMetadata`, `ordinal`; launch copy: `launchMessage`). Close code **1008** is
terminal (no reconnect); other closes back off exponentially.

**Launch overlay.** Clicking a game tile is covered by a "Starting {GameName}…" overlay
(`#launch-overlay`) from the click until the game iframe's `load` event — two socket round trips
plus, on a first play, a multi-megabyte asset download, all of which used to look like a frozen
page. Determinate progress is deliberately *not* attempted: the iframe is cross-origin (no
resource-timing) and `GameManifest` carries no asset sizes. It is raised in `createLobby`/
`joinByCode` (name known synchronously from the catalog) and in `enterGame` (which is the *only*
path on a rejoin — `tryRejoin` ignores its reply), and retired by the `load` handler,
`showError`, `showLobbyView`, or a hard `LAUNCH_MAX_MS` ceiling so a missed event can't hide a
running game. Two counters guard it: `launchSeq` drops a stale `load`, and `launchAbortSeq` makes a
deliberate bail-out reject the in-flight reply (and leave the lobby the server made anyway).

It is presented as a **continuity transition, not a card**: the clicked tile itself is flown from its
grid rect to the centre (a FLIP — `launchFlipFrom`/`rotationFromMatrix` in `kb-core.js` do the math,
`flyLaunchTile` sets one inline start transform and clears it after a forced reflow), straightening
out of its `nth-child` rotation, while `#lobby-view` dissolves and the dot ticker rises. Nothing large
arrives, so a warm launch reads as a lift rather than a flash. The destination size is derived from the
source (1.25×, viewport-capped) — a *fixed* size is a shrink on a wide window, since the grid columns
are elastic. Three consequences worth knowing before touching it:
- **`z-index: 100`, above** `.game-header`'s `99` (which competes in the root stacking context). The
  header must not slide in over the flying tile at a moment network timing decides, so the launch owns
  the screen and `#launch-cancel` is the only way out of a stall.
- **There is no scrim.** The overlay is transparent so `body::before/::after` keep running in phase —
  a scrim of our own could not be phase-matched to the scrolling stripes. That means the game view has
  to be revealed (it must be in the layout to download) but veiled: `#game-view.launch-veil`, plus
  `body.in-game` withheld, both released as the overlay fades. `visibility`, never `display:none`,
  which would entitle the iframe to defer loading.
- **Nothing of the launch is ever drawn over a running game.** On the iframe's `load` —
  `hideLaunchOverlay(true)`, the only caller that passes it — the tile *hands over*: `startGameMorph`
  drops the overlay outright and in the same frame plants `#game-view` in the exact rect the tile had
  reached (mid-flight or settled, rounded corners and all), then expands it to fullscreen like a video
  (`LAUNCH_MORPH_MS`/`LAUNCH_MORPH_EASING`). That expand is a **Web Animations call, not a CSS
  transition** — a transition has to be armed by writing a start value and clearing it, which made it
  a hostage of style-recalc ordering and was seen sticking at the start matrix with `playState`
  `running` and `transition-duration: 0s`, freezing the game at tile size. Its safety timer is held
  outside `launchTimers` so ending the morph cancels it; a stray one strips the class off whatever
  launch is running by then. The scale is deliberately
  **non-uniform** — matching the tile's rect on both axes is what sells it, and a uniform scale would
  start the game at nearly full height on a portrait phone. `body.in-game` is withheld until the morph
  ends, because that's the first moment the game covers the screen and the background can swap
  unseen. Every other ending (an error, a bail-out, the `LAUNCH_MAX_MS` ceiling, a launch that never
  had a tile) takes the `LAUNCH_EXIT_MS` fade instead. Fading a loading screen away over a game that
  has already arrived was tried and rejected as clunky — don't reintroduce it.
- Both durations mirror `home.css`; change them together. `clearGameMorph` runs from `showLobbyView`
  and `showLaunchOverlay` as well as on completion — a stranded inline transform on `#game-view`
  breaks the next session. And `#lobby-view.is-launching` is cleared in two places on purpose: leave
  it stuck and the home page renders at `opacity: 0`.

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
`AdminPasswordPath`/`AdminSessionTtlHours`/`AdminSettingsPath` (admin portal; both paths must be writable
and, in a container, on a persisted volume outside the image — the settings file is the only operator state
that survives a restart), `AdminStaleLobbyMinutes`/`AdminLogBufferSize`/`AdminDiskUsageCacheSeconds`
(dashboard behaviour; see `docs/ADMIN.md`), `GamesPollSeconds` (hot-reload
fallback), `Precompress`/`GamesCompressedRoot`/`PrecompressGzip`/`PrecompressMinBytes`/`PrecompressReconcileSeconds`
(pre-compressed game-asset cache), `Packages`/`GamesUnpackedRoot`/`MaxPackageBytes`/`MaxPackageEntries`/`MaxPackageRatio`
(`.kbg` install; the root must be writable and outside `games/`),
`GamesManagedRoot`/`ManagedPackages`/`PackageBackupCount`/`MaxConcurrentInstalls`/`PackageJobRetention`
(portal installs; the managed root must be writable, outside `games/`, and — unlike the caches — backed up),
`MarketplacePollMinutes`/`MarketplaceMaxSources` (the scheduled check and extra catalogs),
`Marketplace{Enabled,CatalogUrl,DownloadBaseUrl,MaxCatalogBytes,MaxDownloadBytes,CatalogTimeoutSeconds,DownloadTimeoutSeconds}`
(official marketplace; `Enabled=false` ⇒ no outbound HttpClient at all), `LogRetentionDays` (daily log files kept under `LogsRoot`, default 31),
`ForwardedHeaders`/`KnownProxies`/`AllowedOrigins` (behind a reverse proxy),
`*TokenTtlHours`, `DisconnectGraceSeconds` (reconnect grace before a dropped member is removed,
default 60; `0` = immediate), the rate-limit knobs (`*MessagesPerSecond/Burst`,
`MaxConnectionsPerIp`, `LobbyCreatesPerMinute`, `AdminLoginAttemptsPerMinute`/`…Global`), and the server-authority knobs (`AuthorityEnabled`
master switch, `AuthorityMax{MemoryBytes,Statements,ScriptBytes,WordFileBytes,Lobbies}`,
`AuthorityCallTimeoutMs`, `AuthorityRecursionLimit`, `AuthorityTickHzMax`, `AuthorityQueueCapacity`).
