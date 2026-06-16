# KnockBox Games — Game Developer Guide

How to build a multiplayer game (hand-written HTML5, or a Godot / Unity / engine web export) that
runs on the KnockBox platform and talks to other players over the server's networking.

> For how the platform works under the hood, see **[INFRASTRUCTURE.md](./INFRASTRUCTURE.md)**.

---

## 1. What a KnockBox game is

A game is a **folder of static files** — an HTML5 build plus a small manifest. You drop it into the
platform's `games/` directory and it becomes playable; there is **no server-side code to write and
nothing to compile into the server**.

Your game runs inside an `<iframe>` served from the platform's **game origin** (a separate origin
from the shell, for isolation). It uses the **`KnockBox` client library**, which opens its **own
WebSocket** to the server and exchanges messages with the other players. You never see the socket
URL, a lobby id, or the player's identity token — the library reads a lobby-scoped **ticket** from
its page URL fragment, authenticates with it, and the **server resolves all routing from your
connection**. You just send and receive messages.

Key consequences:

- **The server is a blind relay.** It forwards your messages between the players in your lobby but
  never reads or validates them. Game rules are your responsibility, and they run on the **host**
  (see §5).
- **You never name a lobby.** You send to roles (`host`, everyone, a specific player); the server
  knows which lobby your connection belongs to.

---

## 2. Anatomy of a game folder

```
games/
└─ your-game-id/          # folder name MUST equal the manifest "id"
   ├─ GAME.json           # manifest (required)
   ├─ index.html          # your entry page (name set by "entry")
   ├─ game.js             # your code (any structure you like)
   ├─ thumb.svg           # thumbnail shown in the game list (optional)
   └─ … any other assets (images, wasm, data) …
```

### `GAME.json`

```jsonc
{
  "id": "your-game-id",        // unique key; MUST match the folder name
  "name": "Your Game",         // shown in the lobby browser
  "entry": "index.html",       // the HTML file loaded in the iframe
  "thumbnail": "thumb.svg",    // optional; served from your folder
  "maxPlayers": 2,             // joins are rejected beyond this
  "crossOriginIsolated": false // set true ONLY for threaded engine exports (see §11)
}
```

| Field | Required | Notes |
|---|---|---|
| `id` | ✅ | Catalog key **and** URL segment. Your files are served at `/games/{id}/…`, so the folder name must equal `id`. |
| `name` | ✅ | Display name. |
| `entry` | ✅ | HTML file the iframe loads, relative to your folder. |
| `thumbnail` | — | Path (relative to your folder) to an image for the game card. |
| `maxPlayers` | ✅ | The platform refuses joins past this count. |
| `crossOriginIsolated` | — | `true` makes the platform serve your game with COOP/COEP so a **threaded** Godot/Unity export can use `SharedArrayBuffer`. Leave `false` for hand-written games and single-threaded exports. |

Your game **loads as soon as a player creates or joins a lobby** — there is no minimum-player gate.
Show your own "waiting for players" UI and decide when play begins. You control who may join with
`setLobbyOpen(true/false)` (§4); a lobby is **open** (listed + joinable) by default.

The catalog **hot-reloads**: drop in, edit, or remove a game folder and the change is picked up
within a second or two — **no server restart**.

### Packaging your game

You can hand-assemble the folder above, but the repo ships a packer that does it for you and
**validates your manifest against the same rules the server enforces** — so a bad `id`, a missing
`entry`, or a thumbnail typo fails immediately instead of being silently skipped at runtime.

```sh
# Vite/Phaser: build, then package dist/ straight into games/ (hot-reloads)
node tools/pack-game/pack-game.mjs --build "npm run build" --in dist --manifest export/GAME.json

# Godot/Unity: export from the editor first, then package the export folder
node tools/pack-game/pack-game.mjs --in build/web --manifest GAME.json

# Hand-written: the files are already the build
node tools/pack-game/pack-game.mjs --in . --manifest GAME.json
```

The output folder is named after your `id`, and the manifest/thumbnail may live outside the build
(e.g. an `export/` folder). See [`tools/pack-game/README.md`](../tools/pack-game/README.md) for all
options. Pass `--out dist-game` for a local inspect build that doesn't touch the platform.

---

## 3. Load the SDK

The SDK is served by the platform at a fixed, absolute path. Reference it from your entry page:

```html
<!doctype html>
<html>
  <head><meta charset="utf-8" /><title>Your Game</title></head>
  <body>
    <!-- your UI -->
    <script type="module" src="/knockbox.js"></script>  <!-- absolute path; provided by the platform -->
    <script type="module" src="game.js"></script>        <!-- your code (relative path) -->
  </body>
</html>
```

Load both as `type="module"` so the SDK runs before your code (modules execute in document order).
`window.KnockBox` is available once `/knockbox.js` has run. On load it reads its ticket from the page
URL **fragment** (`#kbTicket=…`, which the platform put there) and opens the data socket
automatically — **don't strip the fragment** from your entry URL.

---

## 4. The `KnockBox` API

### Properties (populated once `onReady` fires)

| Property | Type | Meaning |
|---|---|---|
| `KnockBox.playerId` | `string` | Your player's id in this session. |
| `KnockBox.players` | `{ id, displayName }[]` | Everyone in the lobby. **Order is stable and shared by all clients** — index 0 is the host/creator. Use it to assign seats/roles. |
| `KnockBox.isHost` | `boolean` | True if *you* are the authoritative host. |

### Lifecycle callbacks

| Method | Fires when | Argument |
|---|---|---|
| `KnockBox.onReady(cb)` | The data socket attached and the server handed you identity + roster. Start here. | `{ playerId, players, isHost }` |
| `KnockBox.onMessage(cb)` | A relayed message arrives for you. | `{ from, payload }` |
| `KnockBox.onPlayerJoined(cb)` | A player joins the lobby. | the new `player` |
| `KnockBox.onPlayerLeft(cb)` | A player leaves/disconnects. | their `playerId` |

### Sending

| Method | Sends your `payload` to |
|---|---|
| `KnockBox.sendToHost(payload)` | The authoritative host (use for **intent**: "I want to do X"). |
| `KnockBox.sendToAll(payload)` | Everyone in the lobby, including yourself (use by the host for **state**). |
| `KnockBox.sendTo(playerId, payload)` | One specific player (use for **hidden information**). |

`payload` is any JSON-serializable value you define. The server stamps the sender; you receive it as
`{ from, payload }`. There is **no lobby parameter** — routing is resolved from your connection.

### Controlling who can join

| Method | Effect |
|---|---|
| `KnockBox.setLobbyOpen(open)` | **Host-only.** `open: true` → the lobby is listed in the browser and accepts new joins; `false` → hidden and joins are rejected (`"Lobby is closed"`). Existing members and reconnects are unaffected. |

A lobby is **open** when created. The platform never opens or closes it for you — *your game* decides
(e.g. close once the match is full or has begun, reopen if someone leaves). Calls from non-host players
are ignored.

### Logging to the server

`console.log` only reaches the player's own browser — an operator running a deployed instance never
sees it. To surface a diagnostic in the **server's** log, use the console-like `KnockBox.log`:

```js
KnockBox.log.info('match started');
KnockBox.log.warn('player sent an unexpected action');
KnockBox.log.error('failed to apply patch');
```

| Method | Level (Microsoft.Extensions.Logging.LogLevel) |
|---|---|
| `KnockBox.log.trace(msg)` | `Trace` |
| `KnockBox.log.debug(msg)` | `Debug` |
| `KnockBox.log.info(msg)` | `Information` |
| `KnockBox.log.warn(msg)` | `Warning` |
| `KnockBox.log.error(msg)` | `Error` |
| `KnockBox.log.critical(msg)` | `Critical` |

Lines land under the `KnockBox.GameLog` category with your game id, lobby, and player id stamped on by
the server. The message itself is never trusted — it's capped in length and control characters
(including newlines) are stripped, so it can't forge extra log lines. Logging is **best-effort**: a
line emitted before the socket attaches (or while reconnecting) is queued and flushed once connected,
but the queue is bounded and dropped on a permanent close — never use logging for game state. Log
frames also count against the **same per-connection rate limit as your game messages**, so a very
chatty logger competes with gameplay sends for that budget. By default the server logs at
`Information` and above, so `trace`/`debug` lines are filtered unless an operator lowers the level.

---

## 5. The host-authoritative model (the contract)

KnockBox uses **host-client authority**: one player (the lobby creator, `isHost === true`) owns the
game state. Everyone else holds a render copy only.

Follow this loop:

```
1. A guest decides to act        → KnockBox.sendToHost({ ...intent })
2. The host receives the intent  → onMessage → validate against current state
3. If legal, the host updates    → its authoritative state
4. The host publishes the result → KnockBox.sendToAll({ ...state })
5. Everyone (incl. host) renders → onMessage → draw from the received state
```

Rules that keep this correct and consistent:

- **Only the host mutates state.** Guests never apply their own moves locally; they wait for the
  host's broadcast. This guarantees all clients show identical state.
- **Validate on the host.** Reject illegal intents (wrong turn, occupied cell, game over). On an
  illegal intent, re-broadcast the *unchanged* state so the offending client re-syncs.
- **Route a single code path.** Let the host send its *own* actions via `sendToHost` too — they
  loop back through the server to the host and flow through the same `onMessage` handler.
- **Tag your messages.** The server doesn't distinguish "intent" from "state" — that's your job.
  Add a discriminator (e.g. `kind: 'move'` vs `kind: 'state'`) so the host and guests know what
  they received.

---

## 6. Designing your messages

You own the `payload` schema entirely. A simple, robust convention:

```jsonc
// guest → host (intent)
{ "kind": "move", "cell": 4 }

// host → all (authoritative state)
{ "kind": "state", "board": [0,0,1,…], "next": "<playerId>", "winner": null }

// guest → host on (re)entry ("send me the current state")
{ "kind": "sync" }
```

Keep state messages **self-contained** (the full snapshot), so a client can render purely from the
latest one — this makes late joins and reconnects trivial.

---

## 7. Worked example — Tic-Tac-Toe

A condensed version of the bundled sample (`games/tictactoe/game.js`):

```js
let me, players, isHost;
let board = Array(9).fill(0), next = null, winner = null;

KnockBox.onReady((info) => {
  me = info.playerId; players = info.players; isHost = info.isHost;
  buildGrid(); // each cell click → KnockBox.sendToHost({ kind: 'move', cell: i })

  if (isHost) {
    next = players[0].id;          // creator (index 0) is X and moves first
    broadcastState();              // seed everyone
  } else {
    KnockBox.sendToHost({ kind: 'sync' }); // in case we missed the seed
  }
  render();
});

KnockBox.onMessage(({ from, payload }) => {
  if (payload.kind === 'state') {          // everyone: adopt authoritative state
    ({ board, next, winner } = payload);
    return render();
  }
  if (!isHost) return;                     // only the host acts on intents
  if (payload.kind === 'move') applyMove(from, payload.cell); // validate + mutate
  broadcastState();                        // always re-broadcast (even after an illegal move)
});
```

The full file is in `games/tictactoe/` — copy it as a starting point.

---

## 8. Players joining, leaving, and reconnecting

- **Your game loads the moment you enter a lobby** — the host is alone at first and others arrive
  via `onPlayerJoined`. Don't assume a full roster in `onReady`; render a "waiting for players"
  state and begin play when *you* decide (e.g. enough players have joined). Close the lobby with
  `setLobbyOpen(false)` when you don't want more, and reopen it on `onPlayerLeft` if you want a
  replacement.
- Use `KnockBox.players` (from `onReady`) for the initial roster, and `onPlayerJoined` /
  `onPlayerLeft` to keep it current.
- The **server keeps no game state**. If your data socket drops, the SDK reconnects and re-attaches
  with the same session ticket, then fires `onReady` again — but it cannot replay the board. Handle
  this with a **sync** message: on `onReady`, a non-host client asks the host for the current state
  (`sendToHost({kind:'sync'})`) and the host re-broadcasts (`sendToAll`). Because your state
  messages are self-contained, the rejoiner is immediately back in sync.
- Decide what a `playerLeft` means for your game (pause, forfeit, end). Host migration is not
  provided — if the host leaves, the session effectively ends.

---

## 9. Hidden information

For games with secret per-player state (hands, fog of war), do **not** broadcast everything. Have
the host compute each player's view and deliver it individually:

```js
// host, per player:
for (const p of KnockBox.players) {
  KnockBox.sendTo(p.id, { kind: 'state', you: privateViewFor(p.id), shared: publicState });
}
```

`sendToAll` is for fully public state; `sendTo` is the seam for private state.

---

## 10. Engine exports (Godot, Unity, …)

The platform doesn't care how your iframe was built. Two integration routes:

- **Easiest — reuse `/knockbox.js`.** Include it in your exported `index.html` and call the same
  `KnockBox` API from the engine's JS interop layer (Godot `JavaScriptBridge`, Unity `.jslib`).
- **Native — speak the protocol directly.** The SDK is a thin client over a simple JSON WebSocket
  protocol; an engine can open the socket itself (Godot's `WebSocketPeer`, a Unity jslib socket).
  Read the ticket and endpoint from your page URL **fragment** (`#kbTicket=…&kbEndpoint=…`) and:

  ```jsonc
  → { "type": "Attach", "ticket": "<kbTicket>" }            // your first frame
  ← { "type": "Ready",  "playerId": "…", "players": [ { "id": "…", "displayName": "…" } ], "isHost": true }
  → { "type": "Game", "to": "host"|"all"|"<playerId>", "payload": { … } }   // send
  ← { "type": "Game", "to": …, "payload": { … }, "from": "<senderId>" }     // receive
  ← { "type": "GamePlayerJoined", "player": { … } }
  ← { "type": "GamePlayerLeft",   "playerId": "…" }
  ```

  Connect to `kbEndpoint` (the data socket). On a *transient* drop, reconnect with the same ticket
  (back off between attempts); on close code **`1008`** the ticket/membership is gone — stop retrying.

**Threaded exports** (Godot 4 with threads, Unity with threads) need `SharedArrayBuffer`, which
requires cross-origin isolation. Set `"crossOriginIsolated": true` in `GAME.json` and the platform
serves your game with `Cross-Origin-Opener-Policy`/`Cross-Origin-Embedder-Policy`. Full isolation
also requires the operator to enable `KnockBox:IsolateShell` so the shell page is isolated too — see
INFRASTRUCTURE.md §8. **Single-threaded exports need none of this** — leave the flag `false`.

---

## 10b. Godot — use the KnockBox addon (recommended)

For Godot, a maintained GDScript addon removes the boilerplate of the routes above. It is the
**single source of truth** at `clients/godot/addons/knockbox/` (versioned with `web/knockbox.js`).
Copy that folder into your project's `addons/`, enable the plugin, and **don't fork it** — fixes
land there and you copy them forward. It has three layers; use as much as you want:

1. **`KnockBox` autoload** — the raw transport (a `WebSocketPeer` port of the JS SDK). Signals
   `session_ready(player_id, players, is_host)`, `message_received(from_id, payload)`,
   `player_joined`, `player_left`, `closed(terminal)`, `resumed`; methods `send_to_host`,
   `send_to_all`, `send_to`. On web it auto-attaches from the URL fragment; sends made before the
   socket is open are queued and flushed on connect.

2. **`KBNet`** (`kb_net.gd`) — a façade you register as an autoload named `Net`. On web it forwards
   `KnockBox`; **in the editor it runs a built-in single-player loopback** so you press Play and
   develop with no server and no ticket. Same signals/methods as `KnockBox` (plus a `reconnected`
   flag and `set_lobby_open(open)` for the host's join policy), so your code is identical in both.
   For native testing against a real server, call `Net.connect_with(ticket, endpoint)`.

3. **`KBAuthority`** (`kb_authority.gd`) — *optional* host-authoritative glue. You write a **model**;
   it runs the guest-sync / host-broadcast / late-join / reconnect loop for you (plus `set_open(open)`
   to open/close the lobby). Model contract:

   ```
   apply_intent(from_id, action) -> Variant   # host only: mutate, return a patch to broadcast (or null to reject)
   apply_patch(patch) -> void                 # every client applies a broadcast delta
   snapshot() -> Dictionary                   # full state for sync / late-join / reconnect
   apply_snapshot(state) -> void              # every client adopts a full snapshot
   ```

**Project setup.** Add two autoloads (Project Settings → Autoload), in this order:

```
KnockBox   res://addons/knockbox/knockbox.gd
Net        res://addons/knockbox/kb_net.gd
```

Use the **GL Compatibility** renderer for broad web support.

**Tic-Tac-Toe on `KBAuthority`** (the §7 game, in GDScript — the rules object is all you write):

```gdscript
# board_model.gd — pure rules, no networking.
class_name BoardModel
extends RefCounted
var board := [0, 0, 0, 0, 0, 0, 0, 0, 0]
var next_id := ""
var winner = null            # player id, "draw", or null
var players: Array = []
func apply_intent(from_id, action):                 # host only
    if action.get("kind") != "move" or winner != null: return null
    var cell := int(action.get("cell", -1))
    if from_id != next_id or cell < 0 or cell > 8 or board[cell] != 0: return null
    board[cell] = 1 if from_id == players[0]["id"] else 2
    winner = _winner()
    if winner == null:
        next_id = players[1]["id"] if from_id == players[0]["id"] else players[0]["id"]
    return snapshot()                               # tiny game → broadcast the whole board
func apply_patch(patch): apply_snapshot(patch)
func snapshot(): return {"board": board.duplicate(), "next": next_id, "winner": winner}
func apply_snapshot(s):
    board = (s.get("board", board)).duplicate(); next_id = s.get("next", ""); winner = s.get("winner")
func _winner(): ...   # standard 8-line check; "draw" if full
```

```gdscript
# main.gd
extends Node
var model := BoardModel.new()
var authority: KBAuthority
func _ready():
    Net.session_ready.connect(func(pid, players, is_host):
        model.players = players
        if is_host: model.next_id = players[0]["id"]   # host (X) goes first
        _render())
    authority = KBAuthority.new(); add_child(authority)
    authority.setup(Net, model)
    authority.state_changed.connect(_render)
func _on_cell_pressed(cell): authority.send_intent({"kind": "move", "cell": cell})
func _render(): pass   # draw model.board; enable a cell only when model.next_id == Net.player_id
```

That is the entire multiplayer integration — `KBAuthority` handles sync, late-join and reconnect,
and the host's own moves loop back through the same path. (For a non-authoritative game, skip
`KBAuthority` and use `Net`'s signals/sends directly.)

**Export & ship.**
- Export with the **standard (non-mono) Godot** editor and its Web templates. The .NET/mono Godot
  build **cannot export to Web**, so write game logic in **GDScript**.
- In Export → Web, leave **Thread Support off** (single-threaded) so you don't need
  `crossOriginIsolated`.
- Set the export so the entry file is `index.html`, then drop the output plus a `GAME.json` into
  `games/your-id/`. The reference `DiceSimulator` project is a complete working example of this layout.

---

## 11. Test your game locally

1. Put your folder in `games/your-game-id/` next to the sample.
2. Run the server: `dotnet run --project KnockBox.Server --launch-profile http` (shell at
   `http://localhost:5114`, games at `http://localhost:5115`). Your game appears in the startup log
   and in the browser within a second or two — no restart needed when you add/edit it.
3. Open `http://localhost:5114/` in **two browser tabs** — each tab is a separate player (identity
   is per-tab). Create a lobby in one tab — **your game loads immediately** (you're the host, alone).
   In the other tab the lobby shows in the browser (while it's open); join it and the second player's
   game loads too.

Static files are read per request, so editing your game and reloading the tabs is enough.

**Faster solo loop:** Godot games using `KBNet` can skip the server entirely while iterating — just
**press Play in the editor**. The built-in loopback gives you a solo host session, so UI and host
logic run with no server, ticket, or export.

---

## 12. Rules & gotchas

- **Folder name must equal `id`.** Your assets are served at `/games/{id}/…`.
- **Load the SDK from `/knockbox.js`** (absolute, `type="module"`). Load your own files with relative
  paths. Don't strip the `#kbTicket=…` fragment from your entry URL — the SDK needs it to attach.
- **The server never inspects payloads.** All validation and rules are yours, on the host.
- **Don't trust guests.** Only the host should mutate state; guests render what the host sends.
- **You never name a lobby.** Send to `host` / everyone / a player id; the server routes by your
  connection.
- **No server persistence.** Design state messages to be self-contained so reconnect/late-join just
  works.
