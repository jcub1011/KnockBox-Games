# KnockBox networking for Phaser

A tiny client that lets a [Phaser](https://phaser.io) game send and receive messages between players
in the same KnockBox lobby — without touching WebSockets, tickets, lobby ids, or routing. It speaks
the same JSON protocol as the [vanilla-JS](../../web/knockbox.js) and [Godot](../godot) clients, so a
Phaser game can even share a lobby with games built on those engines.

The server is a **blind relay** with no game-side state: it never reads your payloads. One client —
the lobby creator (`isHost`) — owns the truth; everyone else sends intent and renders what the host
broadcasts. The optional [`KBAuthority`](#host-authoritative-helper-kbauthority) helper wires that
pattern up for you.

## Files

| File | Purpose |
| --- | --- |
| `kb-core.js` | Pure protocol helpers (version, backoff, fragment parsing, roster reducers). No dependencies. |
| `knockbox-plugin.js` | The Phaser **global plugin** — the main send/receive API. Depends on `kb-core.js` + Phaser. |
| `kb-authority.js` | Optional host-authoritative state-sync helper. Depends on Phaser (optional). |
| `knockbox-local.js` | **Local-testing** drop-in (no server): multi-tab + automated loopback. See [Local testing](#local-testing-no-server). |
| `knockbox-phaser.d.ts` | TypeScript definitions for all of the above. |

All JS files are UMD modules: load them as browser globals via `<script>`, with `import`, or
under CommonJS/AMD. No build step required.

## Install

Register the plugin as a **global plugin** in your `Phaser.Game` config with `start: true` and a
`mapping`, so every scene can reach it as `this.<mapping>`:

```js
import KnockBoxPlugin from './addons/knockbox/knockbox-plugin.js';

new Phaser.Game({
  type: Phaser.AUTO,
  scene: [MainScene],
  plugins: {
    global: [
      { key: 'KnockBox', plugin: KnockBoxPlugin, start: true, mapping: 'knockbox' },
    ],
  },
});
```

Or with plain `<script>` tags (Phaser must load first):

```html
<script src="phaser.js"></script>
<script src="./addons/knockbox/kb-core.js"></script>
<script src="./addons/knockbox/knockbox-plugin.js"></script>
<!-- then use window.KnockBoxPlugin in your game config -->
```

The plugin connects automatically on start: it reads the lobby-scoped ticket + endpoint the shell put
in the URL fragment (`#kbTicket=…&kbEndpoint=…`), opens its own WebSocket, authenticates, and then
fires `ready`.

## Use

In any scene (via the `mapping` above):

```js
class MainScene extends Phaser.Scene {
  create() {
    const net = this.knockbox;

    net.events.on('ready', ({ playerId, players, isHost }) => {
      // playerId = you; players[0] = host; isHost = you own the truth
    });
    net.events.on('message', ({ from, payload }) => {
      // a relayed game message from `from` (which may be yourself, for sendToAll)
    });
    net.events.on('player-joined', (player)   => { /* roster grew */ });
    net.events.on('player-left',   (playerId) => { /* roster shrank */ });

    // Send:
    net.sendToAll({ kind: 'move', x: 100, y: 200 }); // everyone incl. self (state)
    net.sendToHost({ kind: 'tap' });                 // the authoritative host (intent)
    net.sendTo(somePlayerId, { secret: 42 });        // one specific player (hidden info)
  }
}
```

After `ready` fires, `net.playerId`, `net.players`, and `net.isHost` are populated.

### Events

| Event | Argument | Fires when |
| --- | --- | --- |
| `ready` | `{ playerId, players, isHost }` | Authenticated and the roster is known. Re-fires after a reconnect. |
| `message` | `{ from, payload }` | A game message was relayed to you (incl. your own `sendToAll`). |
| `player-joined` | `player` | Someone joined the lobby. |
| `player-left` | `playerId` | Someone left/disconnected. |
| `resumed` | — | `ready` fired again after a reconnect (`net.reconnected` is then `true`). |
| `closed` | `{ terminal }` | The socket closed. `terminal: true` means it won't reconnect (bad ticket / ended membership). |

### Methods

| Method | Effect |
| --- | --- |
| `sendToHost(payload)` | Send to the authoritative host. |
| `sendToAll(payload)` | Send to everyone including yourself. |
| `sendTo(playerId, payload)` | Send to one specific player. |
| `setLobbyOpen(open)` | **Host only.** Open/close the lobby to new joins. |
| `kickPlayer(playerId)` | **Host only.** Remove a player (barred from rejoining). |
| `log.info(msg)` (also `trace`/`debug`/`warn`/`error`/`critical`) | Log a line to the **server** (not the player's console). Best-effort; see below. |
| `setLaunchParams(ticket, endpoint?)` | Supply credentials manually for local/editor testing. |

`this.knockbox.log.*` ships a diagnostic to the server's log (each method maps to a
`Microsoft.Extensions.Logging.LogLevel`: `info`→`Information`, `warn`→`Warning`, …). The server
stamps your game/lobby/player context and logs under the `KnockBox.GameLog` category at `Information`
and above by default. It's best-effort — a line sent before attach is queued with your other frames,
but logging must never carry game state. Locally (`knockbox-local.js`) `log.*` prints to the dev
console for parity.

Sends issued before the socket is ready are queued and flushed once authenticated, so an eager send
in your `ready` handler is never dropped. Transient drops reconnect automatically with capped
exponential backoff; a terminal close (code 1008) stops retrying and emits `closed { terminal: true }`.

## Host-authoritative helper (`KBAuthority`)

For most games, the right shape is: guests send **intents**, the host validates and mutates the
single source of truth, then broadcasts the result so everyone converges. `KBAuthority` implements
that loop so you only write your game's rules in a small **model** object.

```js
import KBAuthority from './addons/knockbox/kb-authority.js';

// Your model owns the rules. The host runs applyIntent; everyone runs applyPatch/applySnapshot.
const model = {
  state: { score: 0 },
  applyIntent(fromId, action) {           // host only — return a patch to broadcast, or null
    if (action.kind === 'point') { this.state.score++; return { score: this.state.score }; }
    return null;                          // reject / no-op
  },
  applyPatch(patch)   { Object.assign(this.state, patch); },  // every client
  snapshot()          { return this.state; },                 // full state for joins/sync
  applySnapshot(s)    { this.state = s; },                    // every client
};

const authority = new KBAuthority(this.knockbox, model);
authority.events.on('state-changed', () => this.render(model.state));

authority.sendIntent({ kind: 'point' });  // call from any client
```

Guests automatically request a snapshot on join/reconnect; the host answers and re-broadcasts on
roster changes, so late joiners converge.

> **Return absolute patches, not relative ones.** A late joiner requests a snapshot, but a delta
> broadcast to everyone can race ahead of that point-to-point snapshot over a real socket. Convergence
> relies on re-applying state being safe, so `applyIntent` should return absolute values
> (`{ score: 5 }`) rather than relative ones (`{ delta: +1 }`), which would double-count or land on
> stale state. The counter above is correct because it returns the new absolute score.

### Hidden-information games (per-recipient mode)

For games where each player must see a *different* view (secret roles, hands, votes-before-reveal),
pass `{ perRecipient: true }`. The host then projects a per-player `snapshot(forPlayerId)` to each
client; render from `authority.currentView` instead of the shared model. There are no deltas and
guests need no model in this mode.

```js
const authority = new KBAuthority(this.knockbox, model, { perRecipient: true });
authority.events.on('state-changed', () => this.render(authority.currentView));
```

### Pitfalls with replicated state (read if you have timers, motion, or per-player state)

A guest's model / `currentView` is a **render copy of the host's truth** — it's overwritten by the
next snapshot. The mistakes below pass single-player and **never throw**, so they only surface live.
The platform guide's **§5a** covers them in full; the essentials for this client:

- **`state-changed` is not per-frame.** It fires on snapshots/deltas/roster changes, never every
  frame. Anything continuous (a countdown, a tween, interpolated positions) must be advanced by your
  own update loop on **every** client — the host included. Carry the inputs to that loop in the
  snapshot (e.g. a `deadlineMs`), not a per-frame number.
- **Don't mutate the replicated copy to drive behavior.** Change shared state by sending an intent the
  host resolves; never react to a local event (a click, a tween end, your displayed timer hitting
  zero) and expect it to beat the host's own update over the wire.
- **Key per-player state by id.** One player's "ready"/"locked in"/vote isn't the group's.

**Dev guard (per-recipient mode).** In per-recipient mode the helper owns the rendered object —
`currentView` — and `KBAuthority` deep-freezes it so an accidental write **throws** instead of
silently diverging. It's **on by default under `knockbox-local.js`** (local dev) and **off in
production**; override with `{ devChecks: true | false }` (e.g. `false` for a high-frequency game that
shouldn't freeze large per-frame state even while testing). Scope is deliberately narrow — it freezes
only `currentView`, never the host's model; in broadcast mode the game owns its own state object, so
the convention and the TypeScript view type below carry the message there instead.

If your game uses TypeScript, parameterize the view type — `new KBAuthority<MyView>(net, model)` — and
`currentView` is typed `DeepReadonly<MyView>`, turning a stray write into a compile error.

## Local testing (no server)

`knockbox-local.js` provides **`KnockBoxLocalPlugin`** — a drop-in for `KnockBoxPlugin` with the
**identical public API** (same `events`, properties, methods), so your game code and `KBAuthority`
are unchanged. It needs no server and no ticket. Pick a transport with `mode`:

| `mode` | What it does | Use for |
| --- | --- | --- |
| `'tab'` (default) | `BroadcastChannel`: every same-origin browser **tab** is a separate player in one shared lobby. | Manual multiplayer testing. |
| `'process'` | In-process hub: many peers in **one JS realm** message each other (deterministic). | Automated tests. |
| `'solo'` | Single-player host that echoes its own sends. | Running a scene by itself. |

### Multi-tab: each tab is a player

Swap the one config line (only the `plugin:` class changes — keep the same `mapping`):

```js
import { KnockBoxLocalPlugin } from './addons/knockbox/knockbox-local.js';

plugins: {
  global: [{
    key: 'KnockBox', plugin: KnockBoxLocalPlugin, start: true, mapping: 'knockbox',
    data: { mode: 'tab' },   // optional: channel, displayName, playerId, settleMs
  }],
}
```

Then **serve the game over HTTP** (BroadcastChannel needs a real origin — `file://` won't do) and
open it in several tabs. Each tab joins as its own player; `this.knockbox.events`/`sendToAll`/etc.
behave exactly as with the real server.

```sh
# from the game folder, any static server on one origin, e.g.:
npx serve .        # or: python -m http.server 8000
# open http://localhost:8000 in 2–3 tabs
```

The **first tab is the host** (`isHost`, `players[0]`). When the host tab closes, the lobby ends:
remaining tabs get `player-left` for the host, then a `closed` event (matching the real server — no
host migration). `setLobbyOpen` is a no-op locally (there's no server-side join gate); `kickPlayer`
works.

### Automated tests (headless, no Phaser)

Use **`KnockBoxLocalPeer`** — the same API without the Phaser dependency. Spin up several peers in
`mode:'process'`; they route to each other synchronously. Composes with `KBAuthority` unchanged.

```js
const { KnockBoxLocalPeer, _resetLocalHubs } = require('./knockbox-local.js');

_resetLocalHubs();   // isolate hub state between tests
const host  = new KnockBoxLocalPeer({ mode: 'process', channel: 't', playerId: 'host' });
const guest = new KnockBoxLocalPeer({ mode: 'process', channel: 't', playerId: 'guest' });

guest.events.on('message', ({ from, payload }) => { /* assert */ });
host.start();
guest.start();           // host is players[0]; guest sees the full roster
guest.sendToHost({ kind: 'tap' });   // reaches only the host
host.sendToAll({ state: 1 });        // reaches everyone incl. host
```

Peers `start()` asynchronously (deferred), so `await` a macrotask/microtask before asserting.

### Real server from a standalone page

To test against an actual running server without the shell, use the **real** `KnockBoxPlugin` and
supply a ticket + endpoint via `data` (a ticket comes from the shell's `RequestGameTicket`):

```js
plugins: {
  global: [{
    key: 'KnockBox', plugin: KnockBoxPlugin, start: true, mapping: 'knockbox',
    data: { ticket: '<scoped-ticket>', endpoint: 'ws://localhost:5115/ws' },
  }],
}
```

## Protocol

This client implements the KnockBox **data-role** protocol (the game-iframe side). See
[`docs/INFRASTRUCTURE.md`](../../docs/INFRASTRUCTURE.md) and
[`docs/GAME_DEVELOPER_GUIDE.md`](../../docs/GAME_DEVELOPER_GUIDE.md) for the full architecture.
