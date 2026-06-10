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
   browser. Others send intent; the host validates and broadcasts state. (Real cheat-resistance
   would need server-side logic, intentionally out of scope.)

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
| **GameCatalog** | `Games/GameCatalog.cs` | Scans `games/*/GAME.json`, validates each entry file, registers manifests by `Id`. **Hot-reloads** via a debounced `FileSystemWatcher`; rebuilds into a local dictionary and **atomically swaps** it so readers never see a half-built catalog. |
| **TokenService** | `Security/TokenService.cs` | HMAC-signs/verifies the **identity token** (anti-spoof, per-tab playerId) and the **game ticket** (scoped `playerId+lobbyId+gameId` credential for the data socket). Per-process secret (or `KnockBox:TokenSecret`). |
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
6. **Shell origin** (the default port): serve `web/` at root and `/games/{id}/…` (for thumbnails).

---

## 4. The single WebSocket transport, two roles

Everything flows over **one** endpoint, **`/ws`**, served on both origins. A connection's role is
chosen by its **first frame**. Messages are UTF-8 **JSON envelopes** discriminated by a `"type"`
field (`System.Text.Json` polymorphism; camelCase on the wire). Request/response ops carry a
client-generated `cid`.

### Control role (the shell) — first frame `Hello`

```jsonc
→ { "type": "Hello",   "displayName": "Alice", "token": "<id.sig|null>" }
← { "type": "Welcome", "playerId": "<id>", "token": "<id.sig>", "gameOrigin": "http://host:5115" }
```
The server honours a claimed id **only if its signed `token` verifies**; otherwise it mints a fresh
anonymous id. The token is per-tab (sessionStorage) and **never leaves the shell origin**.

```jsonc
→ { "type": "ListGames",  "cid": "c1" }   ← { "type": "GameList", "cid": "c1", "games": [ … ] }
→ { "type": "CreateLobby","cid": "c2", "gameId": "tictactoe" }  ← { "type": "LobbyCreated", "cid":"c2", "lobbyId":"AB12" }
→ { "type": "ListLobbies","cid": "c3" }   ← { "type": "LobbyList", "cid":"c3", "lobbies":[ { "lobbyId":"AB12","gameId":"tictactoe","players":1 } ] }
→ { "type": "JoinLobby",  "cid": "c4", "lobbyId": "AB12" }      ← { "type": "Joined", "cid":"c4", "lobbyId":"AB12" }
→ { "type": "Rejoin",     "cid": "c5", "lobbyId": "AB12" }      ← { "type": "RejoinFailed", "cid":"c5" }   // if gone
→ { "type": "RequestGameTicket", "cid": "c6", "lobbyId": "AB12" } ← { "type": "GameTicket", "cid":"c6", "ticket":"<scoped>" }
→ { "type": "LeaveLobby", "lobbyId": "AB12" }   // no response
```
Push events (no `cid`): `PlayerJoined`, `PlayerLeft`, `GameStarting{lobbyId,gameId,hostId,players}`.

### Data role (a game iframe's own socket) — first frame `Attach`

```jsonc
→ { "type": "Attach", "ticket": "<from RequestGameTicket>" }
← { "type": "Ready",  "playerId": "<id>", "players": [ … ], "isHost": true }

→ { "type": "Game", "to": "host"|"all"|"<playerId>", "payload": { … } }      // game sends
← { "type": "Game", "to": …, "payload": { … }, "from": "<senderId>" }        // server stamps From
← { "type": "GamePlayerJoined", "player": { … } }   ← { "type": "GamePlayerLeft", "playerId": "…" }
```
The server validates the ticket signature **and live lobby membership**, binds the connection to
`(playerId, lobbyId)`, and resolves all routing from that binding — **the game never sends a lobby
id.** `to` routing: `"all"` → every member (incl. sender), `"host"` → the lobby's host, `"<id>"` →
that member only. A message from a non-member is dropped silently.

`← { "type": "Error", "cid": "<cid|null>", "reason": "…" }` reports control-role failures.

---

## 5. Lifecycle flows

### Identity (control)
Client opens `/ws`, sends `Hello` with its stored token (or null). Server verifies/mints the id,
replies `Welcome` with the (re)issued token and the game origin. The shell persists the token
per-tab.

### Create / join a lobby (control)
`CreateLobby` makes a lobby (creator = host). `JoinLobby` adds the player, seeds them the roster,
announces them to others. Once `MinPlayers` is reached the server marks the lobby `Started` and
broadcasts `GameStarting` to all members.

### Entering the game (control → data)
On `GameStarting` the shell calls `RequestGameTicket`, receives a scoped ticket, and embeds the
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
- Closing the **control** socket removes the player from its lobby, broadcasts `PlayerLeft` (and
  `GamePlayerLeft` to game sockets), and deletes the lobby if empty.
- The **data** socket reconnects on a *transient* drop with capped exponential backoff (1s→30s) and
  re-`Attach`es with the same ticket (re-validated against live membership). A **terminal** close
  (code `1008`: invalid ticket / membership ended) stops reconnection — no retry storm after a game
  ends. Because the server keeps no game state, the game client re-syncs on reconnect (a guest asks
  the host for current state).

---

## 6. The two browser origins

For isolation, the **shell** and **games** are served from different origins (a second port in dev,
a subdomain in prod):

- **Shell origin** — `web/shell.js` + `index.html`. Owns the single **control** socket, identity
  (per-tab token in `sessionStorage`), the lobby browser, and the waiting room. When a game starts
  it requests a ticket and embeds the game iframe on the game origin. It does **not** bridge
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

---

## 7. Static file serving

| URL (origin) | Source | Notes |
|---|---|---|
| `/`, `/shell.js`, `/knockbox.js` (shell origin) | `web/` | Platform shell + SDK. |
| `/games/{id}/thumb…` (shell origin) | `games/{id}/…` | Thumbnails for the lobby browser. |
| `/games/{id}/…`, `/knockbox.js` (game origin) | `games/{id}/…`, `web/` | The game build + SDK; COOP/COEP added when the manifest sets `crossOriginIsolated`. |

Files are read from disk per request, and the catalog hot-reloads, so adding/editing a game needs no
rebuild and no restart — only C# changes do.

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
- **Server-authoritative game logic / anti-cheat; host migration; persistent match history.**

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
# → shell at http://localhost:5114, games at http://localhost:5115
```

On startup you should see `Discovered game 'tictactoe' (Tic-Tac-Toe)` and
`Watching … for game changes (hot-reload enabled)`. Open `http://localhost:5114/` in two tabs (each
tab is a separate player), create a lobby in one, and join it from the other. Drop a new game folder
into `games/` and it appears within a second or two — no restart.

### Configuration (`KnockBox:*`)

| Key | Default | Purpose |
|---|---|---|
| `TokenSecret` | random per process | HMAC secret. Set it to keep identity tokens valid across restarts. |
| `IdentityTokenTtlHours` | `720` (30d) | Identity-token lifetime (anti-spoof, per-tab id). |
| `GameTicketTtlHours` | `12` | Game-ticket lifetime. Long enough for a play session + reconnects; live lobby membership is the primary check. |
| `GamesPort` | `5115` | Dev: the port the game origin is served on. |
| `GamesHost` | — | Prod: the games subdomain (e.g. `games.knockbox.example`); routes by `Host` header behind a proxy where every request shares one port. |
| `GamesOrigin` | — | Prod: explicit origin the shell embeds games from (overrides `GamesHost`/`GamesPort`). |
| `AllowedOrigins` | `[]` (allow all) | `/ws` Origin allowlist (defense-in-depth; the token/ticket is the real auth). An empty `Origin` is always allowed — native engine clients send none. |
| `IsolateShell` | `false` | Serve the shell cross-origin isolated (COOP/COEP) for threaded engine exports — see §8. |
