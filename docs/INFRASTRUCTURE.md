# KnockBox Games — Infrastructure

How the platform is put together: what the server does, how the shell and games talk to it, and how
a multiplayer game session flows end to end.

> For building a game, see **[GAME_DEVELOPER_GUIDE.md](./GAME_DEVELOPER_GUIDE.md)**.

---

## 1. Philosophy

KnockBox hosts multiplayer **web games** (hand-written HTML5 or Godot/Unity/engine web exports)
supplied as drop-in content folders. Four principles shape the design:

1. **Games are content, not code.** A game is a folder containing a web build plus a `GAME.json`
   manifest. The server discovers it (and re-discovers on change) and serves it. The server **never
   runs game logic** and has no compile-time knowledge of any game.
2. **The server is a coordinator, not an authority.** Its entire job is **discover, serve, relay**:
   find games, serve their files, track in-memory lobbies, identify players, and forward opaque
   messages between the players in a lobby. It never inspects the contents of a game message.
3. **Games just send and receive over a websocket.** A game opens its own data socket (via the SDK)
   and exchanges role-addressed messages (`host` / everyone / a player). It never names a lobby —
   the server resolves routing from the connection, which it bound to a lobby at attach time.
4. **One session is authoritative on one client — the host.** Game rules run in the lobby creator's
   browser. Others send intent; the host validates and broadcasts state. This is the default. A game
   may instead **opt in** to server-authoritative mode (`GAME.json` `serverAuthority`), where the
   server runs the game's sandboxed authority module — cheat-resistance and creator-departure
   survival *for opted-in games*. See [SERVER_AUTHORITY_DESIGN.md](./SERVER_AUTHORITY_DESIGN.md) and
   GAME_DEVELOPER_GUIDE §5b; principle 2 still holds for every game that doesn't opt in.

The server holds **no durable state**: a restart drops all in-progress lobbies by design. Anonymous,
per-tab player identity lives in the browser and is made unforgeable with a signed token (§4).

---

## 2. Solution structure

```
KnockBox-Games.sln(x)
├─ KnockBox.Contracts/     # Class library: shared WebSocket DTOs + GAME.json shape
├─ KnockBox.Server/        # ASP.NET Core (.NET 10) host — no DB, no EF
│  ├─ Games/               #   GameCatalog (discovery + hot-reload)
│  ├─ Lobbies/             #   Lobby, LobbyManager
│  ├─ Networking/          #   Connection, ConnectionManager, WebSocketHandler
│  └─ Security/            #   TokenService (HMAC identity token + game ticket)
├─ web/                    # Platform shell (owns the control socket) + knockbox.js game SDK
├─ games/                  # Runtime drop folder: one subfolder per game (hot-reloaded)
│  └─ tictactoe/           # Sample game (GAME.json, index.html, game.js, thumb.svg)
└─ docs/
```

There is **no database, ORM, or migration layer**. The server is a plain Web API host (chosen over
Blazor Server because game clients are JS/WASM in iframes and engine exports can only speak raw
WebSockets).

---

## 3. Server components

All are registered as singletons in `Program.cs`.

| Component | File | Responsibility |
|---|---|---|
| **GameCatalog** | `Games/GameCatalog.cs` | Scans `<root>/*/GAME.json` across its roots (`games/` first, then the unpacked-package cache), validates each entry file, registers manifests by `Id`. First root to claim an id wins; duplicates are warned about. **Hot-reloads** via a debounced `FileSystemWatcher`; rebuilds into a local dictionary of manifest **and** serving directory, then **atomically swaps** it so readers never see a half-built catalog or a mismatched manifest/path pair. |
| **MarketplaceClient** | `Marketplace/MarketplaceClient.cs` | The server's only outbound HTTP. Fetches the official catalog (conditional `If-None-Match`) and downloads a game's `.kbg` from its GitHub release, verifying the SHA-256 the catalog published and re-validating the archive through `GamePackageReader` before handing back a temp file. Derives every URL from the catalog's `repo`/`tag`/`asset` — the catalog carries no URL. Installs nothing. `PluginUpdateEvaluator` answers "is my copy current?" as pure logic. See [`MARKETPLACE.md`](./MARKETPLACE.md). |
| **GamePackageInstaller** | `Games/GamePackageInstaller.cs` | Installs `.kbg` game packages by extracting them into `GamesUnpackedRoot` (the games mount is read-only, so they can't be expanded in place). Scans **two** package roots — `games/` for hand-placed files, then `GamesManagedRoot` for portal-installed ones, first to claim an id wins. Owns no watcher or timer: it rides `GameCatalog.Discovered` and asks for rediscovery via `ScheduleRescan`. Waits for a package to present the same size+mtime on two consecutive passes before reading it (skipped via `Adopt` for a file this server renamed into place atomically), and needs two passes without the file before uninstalling. See [`KBG_FORMAT.md`](./KBG_FORMAT.md). |
| **PackageManager** | `Games/PackageManager.cs` | The install engine behind the admin portal: receive an upload or a marketplace download, validate it through the same `GamePackageReader.Read` everything else uses, honour the apply mode against running lobbies, retain the previous version, and place the new one atomically. Also rollback and backup pruning. Works with the marketplace switched off. |
| **PackageJobRegistry** | `Games/PackageJobRegistry.cs` | The cursor-polled change feed of package operations, so a download that outlives a request has somewhere to live. Same `?after=<seq>` shape as `AdminLogBuffer`. Retains finished jobs; never evicts a running one; refuses cancellation once files are being swapped. |
| **MarketplaceSourceRegistry** | `Marketplace/MarketplaceSourceRegistry.cs` | The official catalog plus any the operator registered, one `MarketplaceClient` each over one shared `HttpClient`. An unreachable source reports an error rather than failing the aggregate. |
| **GameLifecycleGate** | `Admin/GameLifecycleGate.cs` | The transient `Draining`/`Updating` states, composed over `AdminSettingsStore` and exposed through `IPlatformPolicy`. Never persisted — lobbies are in-memory, so a persisted drain would be stale after any restart. |
| **GameUpdateCoordinator** | `Admin/GameUpdateCoordinator.cs` | The scheduled check: which games the operator enrolled in automatic updates, and starting a job for each that needs one. Makes no request when nothing is enrolled. |
| **GamePackageReader** | `Games/GamePackageReader.cs` | Validates and extracts a package. Treats it as **untrusted input**: full validation before any byte is written, hand-rolled entry iteration (never `ZipFile.ExtractToDirectory`), strict path rules, and byte/entry/ratio caps enforced against bytes actually written rather than the sizes the package declares. |
| **TokenService** | `Security/TokenService.cs` | HMAC-signs/verifies the **identity token** (anti-spoof, per-tab playerId) and the **game ticket** (scoped `playerId+lobbyId+gameId` credential for the data socket). The secret is always random per process — identities are ephemeral by design, so restart-invalidated tokens are intended. |
| **LobbyManager** | `Lobbies/LobbyManager.cs` | Tracks active lobbies in a `ConcurrentDictionary`. Short 4-char codes; the creator becomes the **host**. |
| **Lobby** | `Lobbies/Lobby.cs` | Membership for one lobby. Thread-safe add/remove; `Players` returns a snapshot under lock so broadcasts can't race join/leave. |
| **Connection** | `Networking/Connection.cs` | Wraps one `WebSocket`. Outbound frames go through a **bounded** single-reader channel drained by one writer task (a `WebSocket` forbids concurrent sends), preserving order without locks and bounding memory for a stuck socket. |
| **ConnectionManager** | `Networking/ConnectionManager.cs` | Two registries keyed by `playerId`: **control** connections (shell) and **game** connections (data sockets). A player has both during a game. JSON (de)serialization helpers. |
| **WebSocketHandler** | `Networking/WebSocketHandler.cs` | A connection's lifecycle. Dispatches on the **first frame**: `Hello` → control role; `Attach` → data role. |

### Startup pipeline (`Program.cs`)

1. Resolve the repo root by walking up to `KnockBox-Games.slnx`; locate `web/` and `games/`.
2. Register singletons; `GameCatalog.Discover()` then `StartWatching()`.
3. `UseWebSockets()`.
4. Map `GET /ws` (both ports) with an Origin allowlist → `WebSocketHandler.HandleAsync`.
5. **Game origin** (the games port): serve `/games/{id}/…` and `/knockbox.js`, applying per-game
   COOP/COEP for `crossOriginIsolated` games. `/ws` is excluded so the shared socket endpoint is
   reachable here too.
6. **Shell origin** (the default port): serve `web/` at root and, under `/games/{id}/…`, **only each
   game's declared thumbnail** — never the full build, so untrusted game code can't run on the shell
   origin and read the identity token.

---

## 4. The single WebSocket transport, two roles

Everything flows over **one** endpoint, **`/ws`**, served on both origins. A connection's role is
chosen by its **first frame**. Messages are UTF-8 **JSON envelopes** discriminated by a `"type"`
field (`System.Text.Json` polymorphism; camelCase on the wire). Request/response ops carry a
client-generated `cid`.

**Naming convention** (keep new types consistent): **commands** (client→server) are imperative verbs
(`CreateLobby`, `JoinLobby`, `RejoinLobby`, `RequestTicket`, `KickPlayer`); **responses**
(cid-correlated) are noun + past participle (`LobbyCreated`, `LobbyJoined`, `RejoinRejected`,
`GameCatalog`, `Ticket`); **events** (push, no cid) are past-tense and plane-tagged. The `Game` prefix
is **reserved for the data plane** — the relay payload (`Game`) and the roster mirrors (`GamePlayer*`,
the data-plane twins of the control `Player*` events, which omit `lobbyId`).

The first frame also carries a **protocol version** (`"proto"`, see `KnockBoxProtocol.Version` —
currently `1`). SDKs get copied into games and can outlive server upgrades, so the server accepts
anything up to its own version (a missing field is a pre-versioning client, treated as `1`) and
terminally rejects (`1008`) anything newer — a too-new SDK fails loudly instead of being silently
misrouted. `Welcome`/`Ready` echo the server's version back.

### Control role (the shell) — first frame `Hello`

```jsonc
→ { "type": "Hello",   "displayName": "Alice", "token": "<id.sig|null>" }
← { "type": "Welcome", "playerId": "<id>", "token": "<id.sig>", "gameOrigin": "http://host:5115" }
```
The server honours a claimed id **only if its signed `token` verifies**; otherwise it mints a fresh
anonymous id. The token is per-tab (sessionStorage) and **never leaves the shell origin**.

```jsonc
→ { "type": "ListGames",  "cid": "c1" }   ← { "type": "GameCatalog", "cid": "c1", "games": [ … ] }
→ { "type": "CreateLobby","cid": "c2", "gameId": "tictactoe" }  ← { "type": "LobbyCreated", "cid":"c2", "lobbyId":"AB12" }
→ { "type": "JoinLobby",  "cid": "c4", "lobbyId": "AB12" }      ← { "type": "LobbyJoined", "cid":"c4", "lobbyId":"AB12" }
→ { "type": "RejoinLobby","cid": "c5", "lobbyId": "AB12" }      ← { "type": "RejoinRejected", "cid":"c5" }   // if gone; success replies LobbyJoined
→ { "type": "RequestTicket", "cid": "c6", "lobbyId": "AB12" }   ← { "type": "Ticket", "cid":"c6", "ticket":"<scoped>" }
→ { "type": "LeaveLobby", "lobbyId": "AB12" }   // no response
```
Push events (no `cid`): `PlayerJoined`, `PlayerLeft`, and the reconnect-grace pair
`PlayerDisconnected{lobbyId,playerId}` / `PlayerConnected{lobbyId,playerId}` (a member's shell
socket dropped but they're held in the lobby for the grace window, then returned within it — they
stay on the roster the whole time). `EnterGame{lobbyId,gameId,hostId,players}` is sent to a
single player when they enter a lobby (create/join/rejoin) — it means "load the game now", not a
min-players threshold.

### Data role (a game iframe's own socket) — first frame `Attach`

```jsonc
→ { "type": "Attach", "ticket": "<from RequestTicket>" }
← { "type": "Ready",  "playerId": "<id>", "players": [ … ], "isHost": true }

→ { "type": "Game", "to": "host"|"all"|"<playerId>", "payload": { … } }      // game sends
← { "type": "Game", "to": …, "payload": { … }, "from": "<senderId>" }        // server stamps From
→ { "type": "SetLobbyOpen", "open": true|false }    // host-only: set the lobby's join policy
→ { "type": "Log", "level": "Information", "message": "…" }   // → server log sink (KnockBox.GameLog)
→ { "type": "PlayLog", "metadata": { "placement": "1", … } } // → forwarded to this player's CONTROL socket
← { "type": "GamePlayerJoined", "player": { … } }   ← { "type": "GamePlayerLeft", "playerId": "…" }
← { "type": "GamePlayerDisconnected", "playerId": "…" }   ← { "type": "GamePlayerConnected", "playerId": "…" }  // reconnect grace
```
The server validates the ticket signature **and live lobby membership**, binds the connection to
`(playerId, lobbyId)`, and resolves all routing from that binding — **the game never sends a lobby
id.** `to` routing: `"all"` → every member (incl. sender), `"host"` → the lobby's host, `"<id>"` →
that member only. A message from a non-member is dropped silently.

`PlayLog` is the one data-role frame the server **routes back to a control socket**: a game calls
`KnockBox.logPlay(metadata)`, and the server sanitizes the untrusted metadata, stamps trusted context
(`gameId`, a UTC `timestamp`, `isHost`), and sends the enriched `PlayLog` to **that same player's**
control socket. The shell persists the most-recent 50 in the browser and shows them in the home-page
Play Log. (`Log`, by contrast, only lands in the server's log sink — it is never relayed.)

`← { "type": "Error", "cid": "<cid|null>", "reason": "…" }` reports control-role failures.

---

## 5. Lifecycle flows

### Identity (control)
Client opens `/ws`, sends `Hello` with its stored token (or null). Server verifies/mints the id,
replies `Welcome` with the (re)issued token and the game origin. The shell persists the token
per-tab.

### Create / join a lobby (control)
`CreateLobby` makes a lobby (creator = host, **open** by default). `JoinLobby` adds the player, seeds
them the roster, and announces them to others. The server has **no "started" concept** — each player
who creates, joins, or rejoins is sent `EnterGame` (load-the-game) for themselves, so the game
runs from the moment anyone enters. There is **no lobby-listing endpoint** — players join only by
entering a lobby code, so private lobbies stay discoverable only to those who have the code. The host
controls joinability with `SetLobbyOpen`: an **open** lobby accepts joins by code; a **closed** one
rejects new joins (existing members and reconnects still get back in).

### Entering the game (control → data)
On `EnterGame` the shell calls `RequestTicket`, receives a scoped ticket, and embeds the
game iframe **on the game origin** at `…/games/{id}/{entry}#kbTicket=…&kbEndpoint=wss://host:5115/ws`.
The credentials ride in the URL **fragment** (`#…`), not a query string, so they are never sent in a
`Referer` header or written to server/proxy logs — untrusted game code that loads an external
resource can't leak its own ticket. The game's `knockbox.js` reads the ticket from `location.hash`,
opens its **own** data socket, sends `Attach`, and gets `Ready`.

### In-game relay (host-authoritative)
```
guest intent ─Game{to:host}→ server ─→ host game socket
host validates & updates state
host ─Game{to:all}→ server ─→ every member's game socket renders
```
The server is a blind pipe routing by the bound connection; the host's browser is the source of
truth.

### Disconnect & reconnect
- Closing the **control** socket does **not** immediately remove the player. With a reconnect grace
  window configured (`DisconnectGraceSeconds`, default 60), the player is flagged *disconnected* but
  kept in the lobby (so the lobby stays alive and their game ticket stays valid); the server
  broadcasts `PlayerDisconnected`/`GamePlayerDisconnected`. A reconnect within the window (a fresh
  shell `Hello` + `RejoinLobby`) clears the flag and broadcasts `PlayerConnected`/`GamePlayerConnected`
  with no roster churn. A background reaper (sweeping every ~5s) removes any member whose grace
  elapses — broadcasting `PlayerLeft`/`GamePlayerLeft` and deleting the lobby if it empties; the
  reaper re-checks for a live control socket first, so a player who reconnected is never evicted.
  Setting the grace to `0` restores the old behaviour: a control-socket close leaves immediately.
- A lobby is **closed immediately** the moment no member still holds a live control socket (it's
  empty, or every remaining member is disconnected) — the grace only helps when someone is still
  there to reconnect to, so a "dark" lobby isn't held. So a lone host who refreshes loses the lobby
  (and recreates it), while a multiplayer refresh stays protected by the still-connected peers.
  Explicit leaves (Leave / home button → `LeaveLobby`) are always immediate and unaffected by grace.
- This is why a **tab refresh** is now survivable: the identity token (per-tab `sessionStorage`) and
  saved lobby code persist across the reload, the shell auto-rejoins, and the grace window keeps the
  lobby and membership alive in the gap.
- The **data** socket reconnects on a *transient* drop with capped exponential backoff (1s→30s) and
  re-`Attach`es with the same ticket (re-validated against live membership). A **terminal** close
  (code `1008`: invalid ticket / membership ended) stops reconnection — no retry storm after a game
  ends. Because the server keeps no game state, the game client re-syncs on reconnect (a guest asks
  the host for current state).

---

## 6. The browser origins

For isolation, the **shell** and **games** are served from different origins (a second port in dev,
a subdomain in prod). A third, operator-only **admin** origin sits alongside them — see below:

- **Shell origin** — `web/shell.js` + `index.html`. Owns the single **control** socket, identity
  (per-tab token in `sessionStorage`), the lobby browser, and the waiting room. When a game starts
  it requests a ticket and embeds the game iframe on the game origin, covering the wait with a
  "Starting {GameName}…" launch overlay — up from the click, taken down on the iframe's `load`
  event (the only signal a cross-origin frame gives the parent), which flies the clicked tile itself
  to the centre rather than dropping a card over the page, and on `load` hands that tile over to the
  game, expanding it from the tile's rect to fullscreen. It does **not** bridge
  gameplay — there is no `postMessage` relay; the game talks to the server directly.
- **Game origin** — serves each game's build under `/games/{id}/…` plus `knockbox.js`. The SDK opens
  the game's own data socket using the ticket from its URL.

Because the game is a separate origin, it **cannot** read the shell's `sessionStorage` (the identity
token), DOM, or socket — yet it keeps a real origin, so engine storage (IndexedDB) and per-origin
COOP/COEP work normally. Identity (shell) and gameplay (game) are cleanly separated; the game only
ever holds a lobby-scoped ticket.

```
┌── shell origin ──────────────┐        ┌── game origin ───────────────┐
│ shell.js ─(control /ws)─► server      │ iframe + knockbox.js          │
│   requests ticket, embeds ──┼──────────►  ─(data /ws, Attach ticket)─► server
└──────────────────────────────┘        └──────────────────────────────┘
```

### The admin origin (operators only)

A **third** origin serves the operator dashboard: `AdminPort` (`5116` dev, `8082` in the image), or
`AdminHost`/`AdminOrigin` as a subdomain in prod. `Hosting/OriginRouting.cs` `IsAdminOrigin` decides it the
same way `IsGameOrigin` decides the game origin — by `Connection.LocalPort`, or by `Host` header when
`AdminHost` is set — and `Program.cs` claims those requests in a `MapWhen` branch **before** the game and
shell pipelines.

- **Files:** `web/admin/` is served at the branch's **root**, so the portal is `/` on the admin origin
  (`web/admin/index.html`), with its API under `/admin/api/*`. Players' files, `/games/*` builds and
  thumbnails are not served here at all.
- **Invisible from the public origins:** every `/admin*` path returns **404** on the shell and game
  origins — including `web/admin`'s own assets, which the shell's `web/`-rooted file provider would
  otherwise happily serve at `/admin/…`.
- **Auth:** a single password (minimum 12 characters), hashed with PBKDF2-HMAC-SHA256 (600k iterations,
  16-byte salt) into `AdminPasswordPath`, which is created **owner-read/write only** on Unix so a shell on
  the box can't copy the hash and crack it offline. There are no accounts. On first run the portal is
  **unclaimed** and whoever reaches it sets the password, so the admin origin must not be publicly
  reachable before that happens — see [HOSTING.md](./HOSTING.md). Claiming it is an atomic
  create-if-absent, so two simultaneous setup requests produce one winner and one `409` rather than two
  "successes" of which only one holds a usable session. Attempts are rate-limited *before* any hashing,
  since the hash is expensive by design: per IP (`AdminLoginAttemptsPerMinute`) for fair share, plus a
  server-wide bucket (`AdminLoginAttemptsPerMinuteGlobal`) that bounds CPU even when the per-IP key is a
  header the caller wrote.
- **Sessions** are an HMAC-signed cookie (`HttpOnly`, `SameSite=Strict`, `Secure` whenever the request is
  HTTPS **or** `AdminOrigin` is an `https://` URL — behind a TLS-terminating proxy without
  `ForwardedHeaders` the request Kestrel sees is plain HTTP, and deriving the flag from it alone would
  hand out a non-Secure session token in exactly the deployment HOSTING.md recommends). The signing key is
  derived from a per-process secret **and a fingerprint of the stored password
  hash**, which gives two properties: a restart logs admins out, and *any* change to the secret file
  immediately revokes every outstanding session. So resetting a compromised password actually locks the
  intruder out instead of leaving their session live until the next restart.

  The secret file **is** the credential, so write access to it is total control — whoever can replace it can
  just delete it and claim a new password. That is inherent to a file-backed credential with no external
  state (as with `/etc/shadow`), and detecting a rollback would require monotonic state the attacker doesn't
  also control; filesystem permissions are the boundary. The guarantee that *is* enforced is that sessions
  are valid for exactly the secret currently on disk, so a swap can never leave two sets of sessions live at
  once (pinned by `AdminAuthServiceTests`).
- **Not on the games/shell path:** because the branch is selected before them, an admin request never
  touches the precompressed-asset negotiation, the `.kbg` gate, or COOP/COEP handling.
- **Reads and controls.** Beyond the four auth routes, `/admin/api/*` serves `system/status`, `metrics`,
  `metrics/history`, `lobbies`, `games`, `logs`, `logs/files` and `logs/files/{name}` (raw download),
  `limits`, `room-codes`, `announcement` and `webhooks`, plus POSTs for closing a lobby, bulk-closing (all or
  per game), purging stale lobbies, kicking a member, setting a game's availability, deleting a game,
  rescanning the catalog, toggling maintenance mode, editing the runtime limits and lobby caps, replacing the
  room-code blocklist, posting or clearing the player announcement, and registering, removing or testing a
  webhook endpoint. Every one is behind
  `RequireSession`; the mutating ones additionally pass `WriteGuard`, which requires a JSON content type and
  rejects a cross-site `Sec-Fetch-Site`. That is defence in depth behind `SameSite=Strict` and the isolated
  port, not a substitute for either — a header a client may simply omit cannot be a security boundary, so a
  request without it (curl, the CI smoke test) is allowed through.
- **Policy is the only persisted state.** Game availability, maintenance mode, runtime limit overrides, the
  room-code blocklist, the live announcement and the webhook endpoints live in `AdminSettingsPath`
  (default: beside `AdminPasswordPath`). They gate lobby **creation and listing only** — a lobby already
  running survives both a disable and maintenance mode, and joining is never gated. The relay reads them
  through the narrow `Admin/IPlatformPolicy.cs`, lock-free, because `HandleCreateLobby` asks on every
  request. A change applies in memory even if it can't be written; the portal reports that as "active now,
  lost on restart" rather than silently doing nothing.
- **Limits are read live, so an edit reaches open sockets.** `Networking/LimitsProvider.cs` publishes the
  configured baseline merged with the operator's overrides as one volatile `ServerLimits`, and `TokenBucket` /
  `IpConnectionGate` read it through a delegate rather than capturing numbers at construction. That is the
  whole point of the control: the connections a flood arrives on are already connected. The handshake timeout,
  the reconnect grace window and both admin-login caps stay startup-only and the portal says so — the first
  two because the reaper's timer is derived from grace at startup, the last two because they bound PBKDF2 CPU
  for an unauthenticated caller.
- **One outbound egress besides the marketplace.** `Webhooks/WebhookDispatcher.cs` posts platform events to
  operator-registered endpoints, reusing `MarketplaceClient.CreateHttpClient()` and its `IsAllowedUrl` rule
  (https, or http on loopback) rather than configuring a second handler or copying the rule. A bounded
  drop-oldest queue sits in front of it so no request path ever waits on an outbound POST, and the error sink
  that feeds it excludes the dispatcher's own log category — otherwise a failed delivery logs an error that
  becomes another delivery, forever.
- **The live log view is a ring buffer, not a file tail.** `Admin/AdminLogBuffer.cs` is a bounded
  `ILogEventSink` added via `WriteTo.Sink`, so level and `SourceContext` stay structured fields and
  filtering is exact. Each event carries a monotonic sequence, which turns ordinary polling into a stream
  (`?after=<seq>`). The rolling files under `LogsRoot` remain the history and the thing you download.
  `Games/PackageJobRegistry.cs` and `Admin/MetricHistory.cs` use the same cursor shape, which is why the
  portal needs neither SSE nor a second socket role for any of its three live feeds.

Full operator guide, including what each tab shows and why Delete usually can't work in production:
[ADMIN.md](./ADMIN.md).

Note that `/ws` is mapped ahead of all three branches so the one socket endpoint is reachable on every
origin — that is deliberate for the game origin (the data socket connects back to it) and simply
inherited by the admin origin, which never opens one.

---

## 7. Static file serving

| URL (origin) | Source | Notes |
|---|---|---|
| `/`, `/shell.js`, `/knockbox.js` (shell origin) | `web/` | Platform shell + SDK. |
| `/games/{id}/<thumbnail>` (shell origin) | `games/{id}/<thumbnail>` | **Only** the manifest's declared thumbnail for the lobby browser; every other `/games/*` path 404s here (the full build is reachable only on the game origin). |
| `/games/{id}/…`, `/knockbox.js` (game origin) | `games/{id}/…`, `web/` | The game build + SDK; COOP/COEP added when the manifest sets `crossOriginIsolated`. |
| `/games/*.kbg` (any origin) | — | Always **404**. The package's contents are public (they are the game), but serving a multi-megabyte uncacheable archive at a guessable URL is a needless bandwidth amplifier. |
| `/`, `/admin.js`, `/admin-core.js`, `/admin.css` (admin origin) | `web/admin/` | The operator dashboard, served at the admin origin's root. Its API lives under `/admin/api/*`. `admin-core.js` is the pure, DOM-free half (formatting, filtering, rate arithmetic), the same split `kb-core.js` has from `shell.js`. |
| `/admin*` (shell **or** game origin) | — | Always **404**, so the portal is unreachable from any origin a player can browse. |

Game assets resolve through a `CompositeFileProvider` over `games/` then `GamesUnpackedRoot`, in the
same order the catalog searches — so a request's manifest and its assets always come from the same
place. Each member is a `PhysicalFileProvider`, so ETags, range requests and `sendfile` behave exactly
as with a single root.

Files are read from disk per request, and the catalog hot-reloads, so adding/editing a game (or
copying in a `.kbg`) needs no rebuild and no restart — only C# changes do.

---

## 8. Statelessness, concurrency, and deferred work

**State** is in memory only: the game catalog, active lobbies, live connections. A crash drops
everything; clients fall back to the lobby browser.

**Concurrency** is multithreaded and partitioned: each socket runs an independent async task (no
global lock), shared maps are `ConcurrentDictionary`, per-lobby state is lock-guarded with snapshot
reads, and each connection's outbound is a bounded single-reader channel. Separate lobbies never
contend, so it scales to many concurrent lobbies within one process.

Intentionally **not** built (future work):

- Real accounts/login (identity is anonymous; the signed token prevents spoofing, not sybils).
- Multi-server scale-out (today all state is single-process; would need sticky lobby routing + a
  backplane). Binary wire format (protobuf) for high-tick games.
- Server-authoritative game logic / anti-cheat; host migration; persistent match history.

### Cross-origin isolation for threaded engine exports

A cross-origin iframe only gets `SharedArrayBuffer` (needed by threaded Godot/Unity exports) when
**all three** hold:

1. the game's assets are served COOP/COEP+CORP — automatic when its manifest sets
   `crossOriginIsolated: true`;
2. the iframe carries `allow="cross-origin-isolated"` — the shell adds this automatically for such
   games;
3. the **shell page itself** is cross-origin isolated — set `KnockBox:IsolateShell = true`, which
   serves the shell with `COOP: same-origin` + `COEP: credentialless`.

`IsolateShell` is **off by default**: single-threaded exports need none of this, and isolating the
shell constrains what else it can embed. Turn it on only when hosting threaded engine games.

---

## 9. Running locally

```bash
# From the repo root:
dotnet run --project KnockBox.Server --launch-profile http
# → shell at http://localhost:5114, games at http://localhost:5115,
#   admin portal at http://localhost:5116
```

On startup you should see `Discovered game 'tictactoe' (Tic-Tac-Toe)` and
`Watching … for game changes (hot-reload enabled)`. Open `http://localhost:5114/` in two tabs (each
tab is a separate player), create a lobby in one, and join it from the other. Drop a new game folder
into `games/` and it appears within a second or two — no restart.

### Configuration (`KnockBox:*`)

| Key | Default | Purpose |
|---|---|---|
| `IdentityTokenTtlHours` | `720` (30d) | Identity-token lifetime (anti-spoof, per-tab id). |
| `GameTicketTtlHours` | `12` | Game-ticket lifetime. Long enough for a play session + reconnects; live lobby membership is the primary check. |
| `WebRoot` / `GamesRoot` / `LogsRoot` | auto | Where the shell / games / logs live. Precedence per root: explicit config → repo discovery (dev) → the app's own directory (published exe / container). Relative paths resolve against the content root. See `Hosting/ContentPaths.cs`. |
| `LogRetentionDays` | `31` | Daily rolling log files kept under `LogsRoot`. |
| `GamesPollSeconds` | `0` (off) | Polling fallback for games hot-reload where `FileSystemWatcher` doesn't fire (Docker bind mounts). The Docker image sets `10`. |
| `Precompress` | `true` | Pre-compress each game's assets once into `GamesCompressedRoot` and serve those variants via `Accept-Encoding` negotiation, instead of compressing every full-body response on the fly. `false` ⇒ the on-the-fly `ResponseCompression` fallback only. |
| `GamesCompressedRoot` | auto (sibling `games-compressed`) | Where the pre-compressed `.br`/`.gz` cache lives. Same precedence as `GamesRoot`. **Must be writable and stay outside the read-only `games/` mount** — it is a regenerable cache, rebuilt from `games/` on boot and on change, so ephemeral storage is fine. In Docker, mount a named volume or host path here (`KNOCKBOX_COMPRESSED_DIR`) to persist it across image updates and skip the cold-boot re-compression — a bind-mounted host path must be writable by the container's UID `1654`. See [HOSTING.md](./HOSTING.md). |
| `PrecompressGzip` | `false` | Also emit `.gz` alongside `.br`. Off by default: a `.kbg` already carries its Brotli blobs (seeding one is a byte copy) but a `.gz` must be produced here at max effort, which is the slowest step of a cold boot — and a client without Brotli (~3%) still gets gzip on the fly. `true` ⇒ the variants are built and kept; switching back to `false` prunes them on the next reconcile. |
| `PrecompressMinBytes` | `1024` | Don't pre-compress files smaller than this (compression overhead outweighs the win). |
| `PrecompressReconcileSeconds` | `60` | Periodic cache-reconcile interval. The discovery event already covers manifest add/remove/edit; this also catches **asset-only** edits under bind-mount polling (which fingerprints `GAME.json` only) and recovers from any missed event. `0` = off (rely on the discovery event). |
| `Packages` | `true` | Install `.kbg` game packages copied into `GamesRoot`. `false` ⇒ packages are ignored entirely and only plain game folders work. See [KBG_FORMAT.md](./KBG_FORMAT.md). |
| `GamesUnpackedRoot` | auto (sibling `games-unpacked`) | Where games extracted from `.kbg` packages live. Same precedence as `GamesRoot`. **Must be writable and stay outside the read-only `games/` mount** (the server refuses a configuration where the two overlap) — it is regenerable, so ephemeral storage is fine, but persisting it (`KNOCKBOX_UNPACKED_DIR`) avoids re-extracting the library on every container recreation. A bind-mounted host path must be writable by the container's UID `1654`; if it isn't, packages can't install and the home page says so. See [HOSTING.md](./HOSTING.md). |
| `GamesManagedRoot` | auto (sibling `games-managed`) | Where `.kbg` packages the **admin portal** installed live, plus the previous versions retained for rollback. Scanned by the installer alongside `GamesRoot`, which wins a contested id. **Must be writable and outside both `games/` and `GamesUnpackedRoot`** (the server refuses an overlapping configuration). Unlike the two caches it is **not regenerable** — a marketplace package can be re-fetched, an uploaded one exists nowhere else — so back it up like the admin volume. |
| `ManagedPackages` | `true` | Master switch for portal installs. Off ⇒ the managed root is never created and every install is refused with a reason the portal shows; packages copied into `games/` by hand still work. Implied off when `Packages=false`, since nothing would extract what was installed. |
| `PackageBackupCount` | `1` | Previous versions of each managed package retained for one-click rollback. `0` keeps none, and makes an update a bare atomic move with no copy. Counted in the game's disk figure. |
| `MaxConcurrentInstalls` | `1` | Downloads/extractions in flight at once. Bounds bandwidth and peak disk — two simultaneous half-gigabyte downloads on a small VPS is not a feature — not the number of jobs. |
| `PackageJobRetention` | `50` | Finished operations kept for the portal's list, so an operator who looked away can still find the outcome. Never evicts a running job, so the cap is soft. |
| `MarketplaceUpdateCadence` | `daily` | How often registered catalogs are checked for updates to games **enrolled** in automatic updates: `off`, `hourly`, `daily` or `weekly`. Nothing is enrolled by default, and a pass with an empty enrolment makes no request at all. A check also runs ~30 s after every start. Editable at runtime from the portal's Platform tab, which overrides this and persists. |
| `MarketplaceUpdateHourUtc` | `3` | Hour (0-23, **UTC**) the daily and weekly checks run at. UTC so the schedule doesn't move with the host's zone or with daylight saving. Each due time carries up to 5 minutes of jitter so a fleet doesn't hit the catalog host at the same second. |
| `MarketplaceUpdateDayOfWeek` | `sunday` | Day the weekly check runs on. Ignored by the other cadences, but kept, so switching cadence back and forth doesn't lose it. |
| `MarketplaceMaxSources` | `8` | Extra marketplaces an operator may register beyond the built-in official one. |
| `MaxPackageBytes` | `536870912` (512 MiB) | Cap on a package's total uncompressed size, enforced while extracting rather than from the sizes the package declares (those are attacker-controlled). `0` = no limit. Also the portal upload cap, enforced against bytes actually received rather than `Content-Length`. |
| `MaxPackageEntries` | `20000` | Cap on the number of files in a package. `0` = no limit. |
| `MaxPackageRatio` | `200` | Cap on a package's uncompressed ÷ archive size. A real game lands well under 10:1; hundreds-to-one is a decompression bomb. `0` = no limit. |
| `MarketplaceEnabled` | `true` | Fetch and download games from the official marketplace. `false` ⇒ nothing marketplace-related is registered and the server holds no outbound `HttpClient` at all — the posture an air-gapped deployment wants. See [MARKETPLACE.md](./MARKETPLACE.md). |
| `MarketplaceCatalogUrl` | official catalog | Where the catalog index is fetched from. Override to run your own marketplace. Must be `https` (loopback `http` is allowed for tests/mirrors). |
| `MarketplaceDownloadBaseUrl` | `https://github.com` | Origin that release download URLs are built on. Package URLs are always **derived** from the catalog's `repo`/`tag`/`asset` against this origin — the catalog never carries a URL, so a tampered entry cannot aim the server at another host. |
| `MarketplaceMaxCatalogBytes` | `4194304` (4 MiB) | Cap on the catalog response body, enforced while reading. |
| `MarketplaceMaxDownloadBytes` | `536870912` (512 MiB) | Cap on a downloaded package, enforced against bytes actually received rather than any declared length. Mirrors `MaxPackageBytes` — a package too large to install isn't worth downloading. `0` = no limit. |
| `MarketplaceCatalogTimeoutSeconds` | `30` | Overall timeout for fetching the catalog. |
| `MarketplaceDownloadTimeoutSeconds` | `600` | Overall timeout for one package download. Generous: packages reach hundreds of megabytes. |
| `GamesPort` | `5115` | Dev: the port the game origin is served on. |
| `GamesHost` | — | Prod: the games subdomain (e.g. `games.knockbox.example`); routes by `Host` header behind a proxy where every request shares one port. |
| `GamesOrigin` | — | Prod: explicit origin the shell embeds games from (overrides `GamesHost`/`GamesPort`). |
| `AdminPort` | `5116` | Dev: the port the **admin portal** origin is served on (`8082` in the Docker image). Whatever you set here must also be a port the host actually binds — see the warning below the table. |
| `AdminHost` | — | Prod: the admin subdomain (e.g. `admin.knockbox.example`), routed by `Host` header exactly like `GamesHost`. Set this **only** behind a proxy you trust to set `Host` (with `ForwardedHeaders`): once set, any request carrying that `Host` reaches the admin app, including one arriving on the public port. |
| `AdminOrigin` | — | Prod: explicit admin origin (overrides `AdminHost`/`AdminPort`). |
| `AdminPasswordPath` | `admin.secret` next to the binary | Where the admin password hash is stored. Must be **writable** and, in a container, on a **persisted volume outside the image** — otherwise the password is lost on every image update and the portal reverts to unclaimed. The Docker image sets `/app/data/admin.secret`. Deleting this file is the password-reset path. |
| `AdminSessionTtlHours` | `8` | Admin session-cookie lifetime. Sessions are also invalidated by a restart (the signing key is per-process, like the player token secret). |
| `AdminSettingsPath` | `admin-settings.json` next to `AdminPasswordPath` | Persisted operator policy: per-game availability (`available`/`disabled`/`staged`), maintenance mode, runtime limit overrides, the room-code blocklist, the live player announcement and the webhook endpoints. The **only** state this server keeps across a restart, because re-applying policy by hand after every deploy is how a platform ships a game it meant to keep hidden. Same requirements as the password file — writable, and on a persisted volume in a container. Unreadable ⇒ platform defaults plus a `DeploymentDiagnostics` warning, never a crash. Delete it to reset all policy. |
| `AdminStaleLobbyMinutes` | `30` | How long a lobby may go without a relayed frame, a join or a leave before the portal calls it **stale** and "Purge Stale" collects it. Independent of `DisconnectGraceSeconds`, which is about one player's socket dropping; this is about a whole session nobody is playing any more. `0` judges staleness only by "nobody in it is connected". |
| `AdminLogBufferSize` | `2000` | Log events held in memory for the portal's live log view (`Admin/AdminLogBuffer.cs`). Bounded ring — older entries exist only in the rolling files under `LogsRoot`. |
| `AdminDiskUsageCacheSeconds` | `60` | How long per-game disk measurements are reused before a background refresh. The measurement walks directories, and the dashboard polls, so a request must never wait on one. `0` measures on every read. |
| `ForwardedHeaders` | `false` | Trust `X-Forwarded-For/Proto/Host` from a fronting reverse proxy so the game origin resolves to `https`/`wss` and per-IP limits see real client IPs. Opt-in: only enable behind a trusted proxy, and name that proxy in `KnownProxies`. |
| `KnownProxies` | `[]` (trust any forwarder) | Addresses allowed to set `X-Forwarded-*`: IPs (`10.0.0.7`, `::1`) and/or CIDR ranges (`10.0.0.0/8`). Only consulted when `ForwardedHeaders` is on. **Leaving it empty means any caller can choose the IP every per-IP limit keys on** — including the admin login throttle, whose per-IP bucket a rotating `X-Forwarded-For` then defeats entirely; startup logs a warning saying so. Unparseable entries are logged as errors and ignored (the proxy they name is *not* trusted). |
| `AllowedOrigins` | `[]` (allow all) | `/ws` Origin allowlist (defense-in-depth; the token/ticket is the real auth). An empty `Origin` is always allowed — native engine clients send none. |
| `IsolateShell` | `false` | Serve the shell cross-origin isolated (COOP/COEP) for threaded engine exports — see §8. |
| `HandshakeTimeoutSeconds` | `10` | A `/ws` socket must send its first frame (`Hello`/`Attach`) within this deadline or it is closed (anti socket-squatting). `0` disables. |
| `MaxConnectionsPerIp` | `32` | Concurrent `/ws` sockets per client IP (a player holds 2 per tab: control + game). `0` disables. |
| `GameMessagesPerSecond` / `GameMessagesBurst` | `30` / `60` | Per-connection token bucket on inbound data-role frames (each relayed frame fans out O(lobby size)). Sustained violation → `Error{rate_limited}` + terminal close `1008`. `0` disables. |
| `ControlMessagesPerSecond` / `ControlMessagesBurst` | `5` / `10` | Same, for control-role (shell) frames. |
| `LobbyCreatesPerMinute` | `10` | Per-player lobby-creation bucket; a violation rejects the create with `rate_limited` but keeps the connection. `0` disables. |
| `AdminLoginAttemptsPerMinute` | `10` | Per-IP bucket on `/admin/api/auth/{login,setup}` (`Networking/IpRateLimiter.cs`), checked **before** any hashing. Unlike the limits above this guards **CPU**, not bandwidth: each attempt runs a 600k-iteration PBKDF2 (~0.4 s of one core), so without it an unauthenticated caller can both guess passwords and saturate every core — starving the WebSocket relay. Over the limit ⇒ `429` + `Retry-After`, at ~7 ms instead of ~420 ms. `0` disables. Fair-share only: it is exactly as trustworthy as the IP it keys on, so behind a proxy it needs `ForwardedHeaders` **and** `KnownProxies` — without the latter a client rotating `X-Forwarded-For` gets a fresh bucket per request. The CPU ceiling is `AdminLoginAttemptsPerMinuteGlobal`. |
| `AdminLoginAttemptsPerMinuteGlobal` | `60` | Cap on admin password attempts across **all** callers, checked after the per-IP bucket and still before any hashing. This is the one that bounds CPU no matter what a caller claims its address to be: 60/min ≈ one hash per second ≈ 40% of one core at worst. `0` disables. |
| `DisconnectGraceSeconds` | `60` | How long a member is held in their lobby after their **control** socket drops, so a tab refresh / brief network loss doesn't kick them out (see §Disconnect & reconnect). `0` disables grace (immediate removal on drop). |
| `AuthorityEnabled` | `true` | Master switch for server-authoritative mode (games with `GAME.json` `serverAuthority`, see SERVER_AUTHORITY_DESIGN.md). `false` ⇒ creating a lobby for such a game fails with a clear error — never a silent downgrade to host mode. |
| `AuthorityMaxMemoryBytes` | `33554432` (32 MB) | Per-engine memory budget for the sandboxed authority runtime (Jint `LimitMemory`; a per-invocation allocation budget, see design §8). |
| `AuthorityCallTimeoutMs` | `250` | Wall-clock budget per module invocation. A blunt fatal trigger (a GC pause counts against it), so it leaves headroom — `AuthorityMaxStatements` is the deterministic runaway guard. |
| `AuthorityMaxStatements` | `1000000` | Statement budget per invocation (deterministic infinite-loop guard). Overflow is fatal — the lobby is closed. |
| `AuthorityRecursionLimit` | `64` | Call-depth limit for the authority engine. |
| `AuthorityTickHzMax` | `20` | Clamp on a module's requested `config.tickHz` (a module exporting `tick` opts into a server-driven timer). |
| `AuthorityMaxScriptBytes` | `1048576` (1 MB) | Max authority-module file size; checked at discovery (oversize ⇒ the game is skipped) and at load. |
| `AuthorityMaxWordFileBytes` | `33554432` (32 MB) | Max size of a single `authorityWords` dictionary file; checked at discovery (oversize ⇒ the game is skipped). Dictionaries load once into a shared CLR structure (not a per-lobby budget), so this cap is generous. |
| `AuthorityQueueCapacity` | `256` | Per-lobby actor inbound-channel bound. Two-tier overflow: intents drop-oldest, ticks coalesce, roster events are never dropped (design §6). |
| `AuthorityMaxLobbies` | `100` | Cap on concurrent server-authority lobbies; creation past it fails. `0` = unlimited. Bounds aggregate CPU/memory blast radius. |
| `MaxLobbies` | `0` (unlimited) | Cap on simultaneous lobbies across every game. Also editable at runtime from the admin portal, which persists an override — this is the value a deployment starts from. Enforced in `HandleCreateLobby` before the player is moved out of any lobby they were already in, so a refusal never costs them their current game. |
| `MaxLobbiesPerGame` | `0` (unlimited) | Same, per game, so one popular title can't consume every remaining slot. |
| `MetricSampleSeconds` | `15` | How often the server samples counters into `Admin/MetricHistory.cs` for the dashboard's graphs. Sampled server-side so the history belongs to the SERVER, not to one open browser tab. `0` = no history and no graphs. |
| `MetricHistoryPoints` | `240` | Samples retained (240 x 15 s = one hour). Bounded ring; memory is a fixed handful of numbers per sample plus one small row per game seen in it. |
| `WebhooksEnabled` | `true` | Outbound webhooks (`Webhooks/`). `false` ⇒ no dispatcher, no drain task and **no HttpClient at all**, and the webhook admin routes answer `409` naming this key — the same air-gapped posture as `MarketplaceEnabled`. With it on but no endpoints registered, nothing ever leaves the process either. |
| `MaxWebhooks` | `8` | Endpoints an operator may register. |
| `WebhookTimeoutSeconds` | `10` | Per-delivery deadline (a linked `CancellationTokenSource`, like the marketplace's). Short on purpose: a slow endpoint must not hold the drain task while the bounded queue behind it fills and starts dropping alerts. One attempt, no retry. |
| `WebhookErrorsPerMinute` | `6` | Token-bucket cap on error-log events turned into deliveries; the count suppressed rides the next delivery. An error storm is when this feature fires most and is worth least per message. **`0` sends no error alerts** — off, not unlimited, unlike the connection rate limits: this knob gates traffic into someone else's chat channel. |
| `WebhookMemoryThresholdMb` | `0` (off) | Working set that counts as a resource breach. Edge-triggered: crossing alerts once, and coming back under alerts once. |
| `WebhookCpuPercentThreshold` | `0` (off) | Process CPU (percent of one core-equivalent, measured between metric samples) that counts as a breach. |
| `MemoryLogSeconds` | `0` (off) | Interval for a periodic memory-diagnostics log line (working set, managed heap, GC-committed bytes, gen0/1/2 collection counts, live lobby & authority-actor counts). Use it to correlate footprint with concurrent server-authority lobbies and confirm memory falls back after lobbies close. |

> **Setting ports explicitly replaces the defaults — it does not add to them.** With no port
> configuration at all, the server binds all three origins itself (`5114`, `GamesPort`, `AdminPort`), so a
> bare published exe works out of the box. The moment **any** explicit setting appears — `ASPNETCORE_URLS`,
> `--urls`, `ASPNETCORE_HTTP_PORTS`, or a `Kestrel:Endpoints` section — the server stops choosing and that
> list is the whole truth. `GamesPort`/`AdminPort` then only tell the **router** which port belongs to
> which origin; they don't cause anything to be bound. An origin missing from the list is routed but never
> listened on, and answers `connection refused`. So every port must appear in every place that sets ports:
> `launchSettings.json`'s `applicationUrl`, the Dockerfile's `ASPNETCORE_HTTP_PORTS`, your own env.
> The server warns at startup (`Admin portal is UNREACHABLE: nothing is listening on admin port …`)
> when the admin origin is routed to an unbound port; `OriginPortBindingTests` pins the repo's own files.

### Memory footprint (server-authority games)

Each server-authority lobby runs one sandboxed **Jint engine** for the lobby's lifetime; footprint
scales with concurrent authority lobbies. Two things keep it in check:

- **Shared parsed module** — a game's `authority.js` is parsed once and the reusable parsed module is
  shared across every lobby engine of that game (`Games/AuthorityModuleCache.cs`), so N lobbies of
  one game don't hold N copies of the parsed AST. (The per-engine ECMAScript realm baseline is still
  per lobby — it can't be shared for isolated untrusted state.) `AuthorityMaxMemoryBytes` bounds only
  a single *invocation's* allocation, not what an engine retains.
- **GC tuning** — the server runs **Server GC** (throughput for the WebSocket relay) with **DATAS**
  (Dynamic Adaptation To Application Sizes, on by default since .NET 8) doing the footprint work:
  DATAS grows and shrinks the heap count with actual load, which is exactly the "RSS climbs and stays
  on many-core hosts" case. So the publish (`KnockBox.Server.csproj`) sets `System.GC.ConserveMemory=5`
  (release/decommit sooner, composes with DATAS) and deliberately **no** `System.GC.HeapCount` —
  pinning a heap count would *disable* DATAS. In Docker, set a container **memory limit** (`mem_limit`
  in `docker-compose.yml`) so DATAS/GC size and collect against the cgroup budget — the single biggest
  lever on steady-state RSS. Only pin `DOTNET_GCHeapCount` as a runtime override if you have a specific
  reason (**foot-gun:** it is **hex**, and setting it turns DATAS off).

Deployment (Docker, desktop publish, reverse proxies) is covered in **[HOSTING.md](./HOSTING.md)**.
