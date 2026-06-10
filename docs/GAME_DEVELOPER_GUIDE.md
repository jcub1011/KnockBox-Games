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
URL, a lobby id, or the player's identity token — the library reads a one-time **ticket** from its
page URL, authenticates with it, and the **server resolves all routing from your connection**. You
just send and receive messages.

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
  "minPlayers": 2,             // a lobby starts once this many players join
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
| `minPlayers` | ✅ | The platform fires "game starting" when the lobby reaches this count. |
| `maxPlayers` | ✅ | The platform refuses joins past this count. |
| `crossOriginIsolated` | — | `true` makes the platform serve your game with COOP/COEP so a **threaded** Godot/Unity export can use `SharedArrayBuffer`. Leave `false` for hand-written games and single-threaded exports. |

The catalog **hot-reloads**: drop in, edit, or remove a game folder and the change is picked up
within a second or two — **no server restart**.

---

## 3. Load the SDK

The SDK is served by the platform at a fixed, absolute path. Reference it from your entry page:

```html
<!doctype html>
<html>
  <head><meta charset="utf-8" /><title>Your Game</title></head>
  <body>
    <!-- your UI -->
    <script src="/knockbox.js"></script>  <!-- absolute path; provided by the platform -->
    <script src="game.js"></script>        <!-- your code (relative path) -->
  </body>
</html>
```

`window.KnockBox` is available after `/knockbox.js` loads. On load it reads a one-time ticket from
its own page URL (the platform put it there) and opens the data socket automatically — **don't strip
the query string** from your entry URL.

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
  Read the ticket and endpoint from your page URL (`?kbTicket=…&kbEndpoint=…`) and:

  ```jsonc
  → { "type": "Attach", "ticket": "<kbTicket>" }            // your first frame
  ← { "type": "Ready",  "playerId": "…", "players": [ { "id": "…", "displayName": "…" } ], "isHost": true }
  → { "type": "Game", "to": "host"|"all"|"<playerId>", "payload": { … } }   // send
  ← { "type": "Game", "to": …, "payload": { … }, "from": "<senderId>" }     // receive
  ← { "type": "GamePlayerJoined", "player": { … } }
  ← { "type": "GamePlayerLeft",   "playerId": "…" }
  ```

  Connect to `kbEndpoint` (the data socket). Reconnect with the same ticket if the socket drops.

**Threaded exports** (Godot 4 with threads, Unity with threads) need `SharedArrayBuffer`, which
requires cross-origin isolation. Set `"crossOriginIsolated": true` in `GAME.json` and the platform
serves your game with `Cross-Origin-Opener-Policy`/`Cross-Origin-Embedder-Policy`. (Fully isolating
a cross-origin iframe also requires the shell to be served cross-origin-isolated — see
INFRASTRUCTURE.md §8.) **Single-threaded exports need none of this** — leave the flag `false`.

---

## 11. Test your game locally

1. Put your folder in `games/your-game-id/` next to the sample.
2. Run the server: `dotnet run --project KnockBox.Server --launch-profile http` (shell at
   `http://localhost:5114`, games at `http://localhost:5115`). Your game appears in the startup log
   and in the browser within a second or two — no restart needed when you add/edit it.
3. Open `http://localhost:5114/` in **two browser tabs** — each tab is a separate player (identity
   is per-tab). Create a lobby in one, join it from the other, and play.

Static files are read per request, so editing your game and reloading the tabs is enough.

---

## 12. Rules & gotchas

- **Folder name must equal `id`.** Your assets are served at `/games/{id}/…`.
- **Load the SDK from `/knockbox.js`** (absolute). Load your own files with relative paths. Don't
  strip the `?kbTicket=…` query string from your entry URL — the SDK needs it to attach.
- **The server never inspects payloads.** All validation and rules are yours, on the host.
- **Don't trust guests.** Only the host should mutate state; guests render what the host sends.
- **You never name a lobby.** Send to `host` / everyone / a player id; the server routes by your
  connection.
- **No server persistence.** Design state messages to be self-contained so reconnect/late-join just
  works.
