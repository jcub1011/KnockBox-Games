# KnockBox Games — Infrastructure

How the platform is put together: what the server does, how clients talk to it, and how a
multiplayer game session flows end to end.

> For building a game, see **[GAME_DEVELOPER_GUIDE.md](./GAME_DEVELOPER_GUIDE.md)**.

---

## 1. Philosophy

KnockBox hosts multiplayer **HTML5 games** supplied as drop-in content folders. Three principles
shape the whole design:

1. **Games are content, not code.** A game is a folder containing an HTML5 build plus a
   `GAME.json` manifest. The server discovers it at startup and serves it. The server **never runs
   game logic** and has no compile-time knowledge of any game.
2. **The server is a coordinator, not an authority.** Its entire job is **discover, serve, relay**:
   find games, serve their files, track in-memory lobbies, and forward opaque messages between the
   players in a lobby. It never inspects the contents of a game message.
3. **One game session is authoritative on one client — the host.** Game rules run in the lobby
   creator's browser (the *host*). Other players send intent; the host validates and broadcasts the
   resulting state. (This is *host-client* authority. Real cheat-resistance would require server-side
   logic, which is intentionally out of scope here.)

The server holds **no durable state**. A restart drops all in-progress lobbies by design; all
persistent data (identity, etc.) lives in the browser.

---

## 2. Solution structure

```
KnockBox-Games.sln(x)
├─ KnockBox.Contracts/     # Class library: shared WebSocket DTOs + GAME.json shape
├─ KnockBox.Server/        # ASP.NET Core (.NET 10) Web API host — no DB, no EF
├─ web/                    # Platform shell (owns the socket) + knockbox.js game SDK
├─ games/                  # Runtime drop folder: one subfolder per game
│  └─ tictactoe/           # Sample game (GAME.json, index.html, game.js, thumb.svg)
└─ docs/
```

There is **no database, ORM, or migration layer**. The server is a plain Web API host (chosen over
Blazor Server because game clients are JS/WASM in iframes and engine exports can only speak raw
WebSockets).

### Projects

| Project | Purpose |
|---|---|
| **KnockBox.Contracts** | Plain C# records: the WebSocket envelope hierarchy (`Message` and its derived types) and `GameManifest` (the `GAME.json` shape). Serialized with `System.Text.Json`. |
| **KnockBox.Server** | The host: game discovery, static file serving, the `/ws` endpoint, lobby tracking, and message relay. |

---

## 3. Server components

All are registered as singletons in `Program.cs`.

| Component | File | Responsibility |
|---|---|---|
| **GameCatalog** | `Games/GameCatalog.cs` | At startup, scans `games/*/GAME.json`, validates each entry file exists, and registers manifests keyed by `Id`. Logs every discovered game. |
| **LobbyManager** | `Lobby/LobbyManager.cs` | Tracks active lobbies in a `ConcurrentDictionary`. Creates lobbies with short 4-char codes; the creator becomes the lobby **host**. |
| **Lobby** | `Lobby/Lobby.cs` | Membership for one lobby: players, `HostId`, min/max, and a `Started` flag. Thread-safe add/remove. |
| **Connection** | `Networking/Connection.cs` | Wraps one client `WebSocket`. Outbound frames go through a single-reader channel drained by one writer task (a `WebSocket` forbids concurrent sends), preserving order without locks. |
| **ConnectionManager** | `Networking/ConnectionManager.cs` | Registry of live connections keyed by `playerId`, plus the JSON (de)serialization helpers. |
| **WebSocketHandler** | `Networking/WebSocketHandler.cs` | Owns a connection's lifecycle: identity handshake, message routing, lobby ops, and relay fan-out. |

### Startup pipeline (`Program.cs`)

1. Resolve the repo root by walking up from the content root until `KnockBox-Games.slnx` is found,
   then locate `web/` and `games/` beside it.
2. Register singletons; run `GameCatalog.Discover()` once.
3. `app.UseWebSockets()`.
4. Serve `web/` at the site root (shell at `/`, SDK at `/knockbox.js`).
5. Serve `games/` under the `/games` URL path (each game's assets at `/games/{gameId}/…`).
6. Map `GET /ws` → accept the socket → hand to `WebSocketHandler.HandleAsync`.

---

## 4. The single WebSocket transport

Everything — identity, lobby operations, and in-game traffic — flows over **one** persistent
WebSocket per client at **`/ws`**. Messages are UTF-8 **JSON envelopes** discriminated by a
`"type"` field (the C# side uses `System.Text.Json` polymorphism; field names are camelCase on the
wire).

A thin protocol layer provides the three things a higher-level framework would:

1. **Routing** — dispatch on `type`.
2. **Request/response correlation** — requests carry a client-generated `cid`; the matching reply
   echoes it, so the client can `await` a response.
3. **Reconnection** — the client reconnects with backoff and re-identifies (`Hello` + `Rejoin`).

### Message reference

Direction is `→` client-to-server, `←` server-to-client.

**Identity** (first exchange after connect)
```jsonc
→ { "type": "Hello",   "playerId": "<id|null>", "displayName": "Alice" }
← { "type": "Welcome", "playerId": "<id>" }     // server assigns one if null
```

**Catalog**
```jsonc
→ { "type": "ListGames", "cid": "c1" }
← { "type": "GameList",  "cid": "c1", "games": [ { "id": "tictactoe", "name": "Tic-Tac-Toe",
       "entry": "index.html", "thumbnail": "thumb.svg", "minPlayers": 2, "maxPlayers": 2 } ] }
```

**Lobby operations** (request/response, correlated by `cid`)
```jsonc
→ { "type": "CreateLobby", "cid": "c2", "gameId": "tictactoe" }
← { "type": "LobbyCreated","cid": "c2", "lobbyId": "AB12" }      // creator becomes host

→ { "type": "ListLobbies", "cid": "c3" }
← { "type": "LobbyList",   "cid": "c3", "lobbies": [ { "lobbyId": "AB12", "gameId": "tictactoe", "players": 1 } ] }

→ { "type": "JoinLobby", "cid": "c4", "lobbyId": "AB12" }
← { "type": "Joined",    "cid": "c4", "lobbyId": "AB12" }

→ { "type": "LeaveLobby", "lobbyId": "AB12" }                   // no response

→ { "type": "Rejoin",       "cid": "c5", "lobbyId": "AB12" }
← { "type": "RejoinFailed", "cid": "c5" }                       // if the lobby is gone
```

**Lobby push events** (server → client, no `cid`)
```jsonc
← { "type": "PlayerJoined", "lobbyId": "AB12", "player": { "id": "…", "displayName": "Bob" } }
← { "type": "PlayerLeft",   "lobbyId": "AB12", "playerId": "…" }
← { "type": "GameStarting", "lobbyId": "AB12", "gameId": "tictactoe",
       "hostId": "<id>", "players": [ { "id": "…", "displayName": "…" }, … ] }
```

**Relay** (opaque game payload — the server never reads `payload`)
```jsonc
→ { "type": "Relay", "lobbyId": "AB12", "to": "host", "payload": { … } }
← { "type": "Relay", "lobbyId": "AB12", "to": "all",  "payload": { … }, "from": "<senderId>" }
```
`to` routing, resolved by the server:

| `to` | Delivered to |
|---|---|
| `"all"` | Every member of the lobby, **including the sender** |
| `"host"` | The lobby's `hostId` |
| `"<playerId>"` | That specific player, only if they are in the lobby |

The server stamps `from` (the sender's `playerId`) on every outbound relay. A relay from a
non-member is dropped silently.

**Error**
```jsonc
← { "type": "Error", "cid": "<cid|null>", "reason": "Lobby is full" }
```

---

## 5. Lifecycle flows

### Connect & identity
1. Client opens `/ws` and sends `Hello` with its `playerId` (or `null`) and display name.
2. Server creates a `Connection`, registers it, starts the per-socket send loop, and replies
   `Welcome` (assigning a GUID if none was given).

### Create / join a lobby
1. **Create:** `CreateLobby{gameId}` → server makes a `Lobby` (creator = `hostId`), adds the
   creator, replies `LobbyCreated{lobbyId}`.
2. **Join:** `JoinLobby{lobbyId}` → server adds the player (rejecting if full), replies `Joined`,
   sends the joiner a `PlayerJoined` for each existing member, then broadcasts `PlayerJoined` for
   the new player to everyone else.
3. **Start:** once member count reaches the game's `MinPlayers`, the server marks the lobby
   `Started` and broadcasts `GameStarting` (with `hostId` and the full roster) to all members.

### In-game relay (host-authoritative)
```
guest click ─Relay{to:host}→ server ─→ host
host validates & updates state
host ─Relay{to:all}→ server ─→ every member (incl. host) renders
```
The server is a blind pipe; the host's browser is the source of truth. The host's own moves take
the same `to:"host"` loopback path, so there is a single code path for all input.

### Disconnect & reconnect
- On socket close the server removes the player from its lobby, broadcasts `PlayerLeft`, and
  deletes the lobby if it became empty.
- A reconnecting client sends `Hello` then `Rejoin{lobbyId}` (from `sessionStorage`). If the lobby
  still exists the player is re-added and re-sent `GameStarting`; otherwise it gets `RejoinFailed`
  and returns to the lobby browser. Because the server keeps no game state, the **game client**
  re-syncs after rejoining (e.g., a guest asks the host for the current state).

---

## 6. The platform shell (`web/`)

The browser side has two parts, both served from the site root (same origin as `/ws`):

- **`shell.js` / `index.html`** — the platform shell. It owns the single WebSocket, identity
  (per-tab, in `sessionStorage`), the lobby browser, and a waiting room. When a game starts it
  embeds the game in a same-origin `<iframe src="/games/{gameId}/{entry}">` and **bridges** between
  the iframe and the socket: `postMessage` from the game becomes a `Relay` envelope; inbound
  `Relay` payloads are `postMessage`d into the iframe.
- **`knockbox.js`** — the game-facing SDK loaded *inside* each game's iframe. The game calls it
  instead of touching the socket. See the developer guide.

```
┌────────────────────── browser tab ──────────────────────┐
│  shell.js  ──(WebSocket /ws)──►  KnockBox.Server         │
│     ▲  │                                                 │
│  postMessage (same-origin bridge)                        │
│     │  ▼                                                 │
│  <iframe> game  ──uses──►  /knockbox.js                  │
└──────────────────────────────────────────────────────────┘
```

Identity lives in `sessionStorage`, which is **per-tab**, so two tabs in one browser are two
distinct players — convenient for local testing.

---

## 7. Static file serving

| URL | Source | Notes |
|---|---|---|
| `/` , `/shell.js` , `/knockbox.js` | `web/` | Platform shell + SDK. |
| `/games/{gameId}/…` | `games/{gameId}/…` | Each game's HTML5 build and thumbnail. `GAME.json` lives here too but is read server-side, not relied upon by clients. |

Files are read from disk per request, so editing shell/SDK/game files does not require a server
rebuild — only C# changes do.

---

## 8. Statelessness, deferred work, and scaling

The only state the server holds is **in memory**: the game catalog, active lobbies, and live
connections. There is no recovery layer — a crash drops everything, and clients fall back to the
lobby browser.

Intentionally **not** built in this skeleton (future work):

- Protobuf wire format (currently JSON), real authentication (the `playerId` is trusted).
- Server-authoritative game logic / anti-cheat, fixed-rate tick loops, client prediction.
- Multi-server scale-out (e.g., a lobby-per-grain actor model) — today all state is single-process.
- Plugin sandboxing/hot-reload, COOP/COEP headers for threaded-WASM engine exports, persistent
  match history.

---

## 9. Running locally

```bash
# From the repo root:
dotnet run --project KnockBox.Server --launch-profile http
# → serves http://localhost:5114
```

On startup you should see a log line confirming discovery, e.g.
`Discovered game 'tictactoe' (Tic-Tac-Toe)`. Open `http://localhost:5114/` in two tabs (each tab is
a separate player), create a lobby in one, and join it from the other.
