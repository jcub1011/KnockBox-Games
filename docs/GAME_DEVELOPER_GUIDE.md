# KnockBox Games — Game Developer Guide

How to build a multiplayer HTML5 game that runs on the KnockBox platform and talks to other
players over the server's networking.

> For how the platform works under the hood, see **[INFRASTRUCTURE.md](./INFRASTRUCTURE.md)**.

---

## 1. What a KnockBox game is

A game is a **folder of static files** — an HTML5 build plus a small manifest. You drop it into the
platform's `games/` directory and it becomes playable; there is **no server-side code to write and
nothing to compile into the server**.

Your game runs inside an `<iframe>` on the platform shell. It does **not** open its own WebSocket or
deal with lobbies, identity, or connection management. Instead it uses the **`KnockBox` JavaScript
API** to exchange messages with the other players in its lobby. The platform shell owns the single
socket and bridges your messages to/from the server.

Key consequence: **the server is a blind relay.** It forwards your messages between players but
never reads or validates them. Game rules are your responsibility, and they run on the **host**
(see §5).

---

## 2. Anatomy of a game folder

```
games/
└─ your-game-id/          # folder name MUST equal the manifest "id"
   ├─ GAME.json           # manifest (required)
   ├─ index.html          # your entry page (name set by "entry")
   ├─ game.js             # your code (any structure you like)
   ├─ thumb.svg           # thumbnail shown in the game list (optional)
   └─ … any other assets (images, wasm, css) …
```

### `GAME.json`

```jsonc
{
  "id": "your-game-id",      // unique key; MUST match the folder name
  "name": "Your Game",       // shown in the lobby browser
  "entry": "index.html",     // the HTML file loaded in the iframe
  "thumbnail": "thumb.svg",  // optional; served from your folder
  "minPlayers": 2,           // a lobby starts once this many players join
  "maxPlayers": 2            // joins are rejected beyond this
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

The server reads `GAME.json` **once at startup**. After adding or changing a game, restart the
server (there is no hot-reload yet).

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

`window.KnockBox` is available to your scripts after `/knockbox.js` loads.

---

## 4. The `KnockBox` API

### Properties (populated once `onReady` fires)

| Property | Type | Meaning |
|---|---|---|
| `KnockBox.playerId` | `string` | Your player's id in this session. |
| `KnockBox.players` | `{ id, displayName }[]` | Everyone in the lobby. **Order is stable and shared by all clients** — index 0 is the host/creator. Use it to assign seats/roles. |
| `KnockBox.isHost` | `boolean` | True if *you* are the authoritative host. |
| `KnockBox.lobbyId` | `string` | The lobby code. |

### Lifecycle callbacks

| Method | Fires when | Argument |
|---|---|---|
| `KnockBox.onReady(cb)` | The platform has handed you identity + roster. Start here. | `{ playerId, players, isHost, lobbyId }` |
| `KnockBox.onMessage(cb)` | A relayed message arrives for you. | `{ from, payload }` |
| `KnockBox.onPlayerJoined(cb)` | A player joins the lobby. | the new `player` |
| `KnockBox.onPlayerLeft(cb)` | A player leaves/disconnects. | their `playerId` |

### Sending

| Method | Sends your `payload` to |
|---|---|
| `KnockBox.sendToHost(payload)` | The authoritative host (use for **intent**: "I want to do X"). |
| `KnockBox.sendToAll(payload)` | Everyone in the lobby, including yourself (use by the host for **state**). |
| `KnockBox.sendTo(playerId, payload)` | One specific player (use for **hidden information**). |

`payload` is any JSON-serializable value you define. The platform wraps it in the transport
envelope and stamps the sender; you receive it as `{ from, payload }`.

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

function applyMove(fromId, cell) {
  if (winner || fromId !== next) return;            // game over / not your turn
  if (cell < 0 || cell > 8 || board[cell] !== 0) return; // out of range / occupied
  board[cell] = markOf(fromId);
  winner = computeWinner();
  if (!winner) next = players.find(p => p.id !== fromId).id;
}

function broadcastState() {
  KnockBox.sendToAll({ kind: 'state', board, next, winner });
}
```

The full file is in `games/tictactoe/` — copy it as a starting point.

---

## 8. Players joining, leaving, and reconnecting

- Use `KnockBox.players` (from `onReady`) for the initial roster, and `onPlayerJoined` /
  `onPlayerLeft` to keep it current.
- The **server keeps no game state**. When a player reconnects, the platform re-enters them into
  the game but cannot replay the board. Handle this yourself with a **sync** message: on
  `onReady`, a non-host client asks the host for the current state (`sendToHost({kind:'sync'})`),
  and the host responds by re-broadcasting (`sendToAll`). Because your state messages are
  self-contained, the rejoiner is immediately back in sync.
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

## 10. Test your game locally

1. Put your folder in `games/your-game-id/` next to the sample.
2. Run the server: `dotnet run --project KnockBox.Server --launch-profile http` (serves
   `http://localhost:5114`). Confirm your game appears in the startup log.
3. Open `http://localhost:5114/` in **two browser tabs** — each tab is a separate player
   (identity is per-tab). Create a lobby in one, join it from the other, and play.

Static files are read per request, so you can edit your game and just reload the tabs; only the
manifest requires a server restart to re-discover.

---

## 11. Rules & gotchas

- **Folder name must equal `id`.** Your assets are served at `/games/{id}/…`.
- **Load the SDK from `/knockbox.js`** (absolute). Load your own files with relative paths.
- **The server never inspects payloads.** All validation and rules are yours, on the host.
- **Don't trust guests.** Only the host should mutate state; guests render what the host sends.
- **Same-origin only.** Your game is served from the platform origin and communicates via the SDK's
  `postMessage` bridge. Don't try to open your own socket to `/ws`.
- **No server persistence.** Design state messages to be self-contained so reconnect/late-join just
  works.
- **Engine exports (Godot/Unity/etc.)** can ship as a game folder too — call the `KnockBox` API
  from the export's JS layer the same way. (Threaded-WASM COOP/COEP serving is not yet configured.)
