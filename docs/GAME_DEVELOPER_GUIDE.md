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

This folder is also exactly what a `.kbg` package contains — a `.kbg` is this tree plus a small
header, compressed into one file (see [Packaging your game](#packaging-your-game) below). Either form
can be dropped into a server's games directory.

### `GAME.json`

```jsonc
{
  "id": "your-game-id",        // unique key; MUST match the folder name
  "name": "Your Game",         // shown in the lobby browser
  "version": "1.0.0",          // optional; semver. Set it if you publish to the marketplace
  "entry": "index.html",       // the HTML file loaded in the iframe
  "thumbnail": "thumb.svg",    // optional; served from your folder
  "minPlayers": 1,             // optional; how many the game needs. Display only — nothing is gated
  "maxPlayers": 2,             // joins are rejected beyond this
  "tags": ["party", "word"],   // optional; searchable chips on your game's tile
  "description": "…",          // optional; searchable
  "crossOriginIsolated": false // set true ONLY for threaded engine exports (see §11)
}
```

| Field | Required | Notes |
|---|---|---|
| `id` | ✅ | Catalog key **and** URL segment. Your files are served at `/games/{id}/…`, so the folder name must equal `id`. |
| `name` | ✅ | Display name. |
| `version` | — | Your build's version, conventionally semver (`"1.2.3"`, `"1.2.3-beta.1"`). The platform never validates it and it never affects whether your game loads. It matters if you publish to the official marketplace: it is what an operator's server compares to decide whether their copy is out of date, and the catalog entry is generated from it. `knockbox-pack` copies it into the `.kbg` header too, so the two can't disagree. See [MARKETPLACE.md](./MARKETPLACE.md). |
| `entry` | ✅ | HTML file the iframe loads, relative to your folder. |
| `thumbnail` | — | Path (relative to your folder) to an image for the game card. |
| `minPlayers` | — | How many players your game needs, shown on your tile and used by the home page's **Players** filter. Defaults to `1`. Nothing is gated on it — your game still loads for one player (see below), so this is a recommendation to the person browsing, not a lobby rule. `knockbox pack` **rejects** a value outside `1..maxPlayers` so you fix the typo here; a server that meets one anyway clamps it and warns, rather than dropping the game from an operator's catalog. |
| `maxPlayers` | ✅ | The platform refuses joins past this count. |
| `tags` | — | Category/genre labels (`["party", "word-game"]`). Rendered as chips on your tile and matched by the search box. Never validated; blank and non-text entries are dropped rather than drawn. |
| `description` | — | One short line about your game. Not shown on the tile, but matched by the search box, so it is worth filling in. |
| `createdAt` / `updatedAt` | — | ISO 8601 timestamps (`"2026-01-15T10:00:00Z"`) backing the home page's **Newest** and **Recently Updated** sorts. When you omit them the server derives them from your `GAME.json` file's own timestamps — which for a `.kbg` means *when this build was installed on that server*, and a `.kbg` update resets it, since the game folder is re-extracted. Set `createdAt` yourself if you want your game to hold a stable position under "Newest" across releases. |
| `crossOriginIsolated` | — | `true` makes the platform serve your game with COOP/COEP so a **threaded** Godot/Unity export can use `SharedArrayBuffer`. Leave `false` for hand-written games and single-threaded exports. |

Your game **loads as soon as a player creates or joins a lobby** — there is no minimum-player gate.
Show your own "waiting for players" UI and decide when play begins. You control who may join with
`setLobbyOpen(true/false)` (§4); a lobby is **open** (listed + joinable) by default.

The catalog **hot-reloads**: drop in, edit, or remove a game folder and the change is picked up
within a second or two — **no server restart**.

### Packaging your game

To **ship** a game, package it into a single `.kbg` file. An administrator copies that one file into
their server's games directory and the server installs it — no unzipping, no CLI on their host, no
restart. The packer also **validates your manifest against the same rules the server enforces**, so a
bad `id`, a missing `entry`, or a thumbnail typo fails immediately instead of being silently skipped
at runtime.

```sh
# Vite/Phaser: build, then package dist/ (lands in this platform's games/, so it installs at once)
node tools/pack-game/pack-game.mjs --build "npm run build" --in dist --manifest export/GAME.json

# Godot/Unity: export from the editor first, then package the export folder
node tools/pack-game/pack-game.mjs --in build/web --manifest GAME.json --version 1.4.0

# Hand-written: the files are already the build
node tools/pack-game/pack-game.mjs --in . --manifest GAME.json
```

The package is named after your `id`, and the manifest/thumbnail may live outside the build (e.g. an
`export/` folder). Pass `--out ~/builds/` to write it somewhere that doesn't touch the platform, or
`--dir dist-game` to get the plain folder layout instead when you want to inspect exactly what was
packaged. See [`tools/pack-game/README.md`](../tools/pack-game/README.md) for all options and
[`KBG_FORMAT.md`](./KBG_FORMAT.md) for the format itself.

Packing runs Brotli at maximum quality, which takes ~50 seconds for a 38 MB WASM export. That cost is
paid once here instead of on every server cold start — the server copies your compressed assets
straight into its HTTP cache. While iterating, use `--quality 4`; save the default for releases.

> **Both installation methods stay supported.** A plain `games/<id>/` folder works exactly as before,
> and is still the easiest thing to edit while developing (§11). `.kbg` is for *distribution*: one
> file to hand over, checksum, and version. If a folder and a package supply the same `id`, the
> folder wins and the server logs a warning.

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
| `KnockBox.onPlayerLeft(cb)` | A player leaves for good (or their reconnect grace elapsed). | their `playerId` |
| `KnockBox.onPlayerDisconnected(cb)` | A player's tab dropped (refresh/close/network blip) but they're held in the lobby for the reconnect grace window. They stay in `players` — show a "reconnecting…" state. | their `playerId` |
| `KnockBox.onPlayerConnected(cb)` | A previously-disconnected player reconnected within the grace window. | their `playerId` |

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

### Recording Play Log entries

Where `KnockBox.log.*` writes to the **server's** log (for operators), `KnockBox.logPlay(metadata)`
writes to the **player's** home page. Each call records one entry in that player's **Play Log** — the
"Recently Played" panel on the home screen — so a player can glance back at how their last games went.
Call it at a natural milestone, e.g. when a match ends:

```js
KnockBox.logPlay({ placement: '1', playerCount: '4', result: 'win', score: '4200' });
```

`metadata` is an arbitrary `{ key: value }` bag; values are coerced to strings. The shell shows a
**recognized set of standard keys** as dedicated chips and tucks everything else into a collapsible
details table, so prefer these names when they fit:

| Standard key | Rendered as | Example |
|---|---|---|
| `placement` | an ordinal chip | `"1"` → **1st** |
| `playerCount` | a "{n} players" chip | `"4"` → **4 players** |
| `score` | a chip | `"4200"` |
| `result` | a chip | `"win"` |

You do **not** supply the game, the time, or whether you were host — the **server stamps** those
(`gameId`, a UTC `timestamp`, and `isHost`) as trusted, unforgeable context, and the shell shows them
as the entry's game name, time, and a "Host" badge. Any keys you send named like those are still just
ordinary metadata.

Entries are stored **in the player's own browser** (most-recent 50, per browser — like the saved
display name), never on the server, and are visible only to that player. Like `log.*`, `logPlay` is
best-effort and queued until the socket attaches, shares the data-plane rate limit, and is never a
place to keep game state. Metadata is untrusted: each key/value is length-capped and stripped of
control characters, the entry count is bounded, and the shell renders every value as text (never
markup).

---

## 5. The host-authoritative model (the contract)

KnockBox uses **host-client authority**: one player (the lobby creator, `isHost === true`) owns the
game state. Everyone else holds a render copy only. (Prefer the **server** to own the rules — for
cheat-resistance or so the session survives the creator leaving? See **§5b** to opt in per game.)

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

## 5a. Working with replicated state (real-time, prediction, per-player state)

§5 is the contract. This section is the **failure mode** that contract doesn't spell out, and it bites
games of every genre — action, real-time, social, turn-based. Read it if your game has *anything*
beyond "click → host updates → everyone re-renders": timers, animation, movement, prediction, or
per-player state.

> **Skip this if your game isn't host-authoritative.** It assumes one client owns the truth and the
> rest hold render copies. (You can also build non-authoritative games on the raw `KnockBox` sends.)

**The core idea.** In single-player, three things are one object you mutate in one loop:

1. the **authoritative state** (the truth),
2. what you **render** (a copy of the truth), and
3. the **per-frame loop** that advances time/animation.

On KnockBox they are *separate things across a network*: the truth lives on the host; every other
client holds a **replicated render copy** updated only when a message arrives; and each client runs
its own frame loop. Bugs come from code that silently assumed these were still one object. The cruel
part: **this class of bug doesn't throw and passes single-player** — you only see it once there are
real peers and latency.

Three rules that prevent almost all of it:

**1 — A replicated copy does not update itself between messages.** Anything continuous — a countdown,
an animation, interpolated/predicted positions, dead-reckoning, smoothing — must be advanced by *your
own per-frame loop on every client*. Owning authority does **not** make the host's render copy
refresh for free: the host needs the same frame loop as everyone else. (An FPS interpolating remote
players and a party game ticking a timer hit this identically. With `KBAuthority`, note `state-changed`
fires on messages, **not per frame** — so it can't drive a smooth display by itself.)

**2 — Effects on shared state are decided by the authority, not by a local event.** A reaction wired
to a *local* event (a click, a tween finishing, a displayed timer reaching zero) can't deterministically
order against, or arrive before, the host's own update across the wire — so it loses, silently. Route
anything that changes shared state through an **intent** the host resolves. If the host needs client
input to make a time- or event-based decision, the client must send that input *before* the decision
point. (How you keep client and server in step is genre's choice — buffered inputs, client prediction +
server reconciliation, interpolation delay — KnockBox mandates none of them.)

**3 — Model per-participant state explicitly.** "This player did X" is not "everyone did X." Votes,
ready-checks, "locked in", per-player resources — store them keyed by player id and fire a group
transition only when the condition holds for *all* relevant participants (often with a timeout
fallback). Single-player has one participant, so this conflation is invisible until peers exist.

**A design check for any new feature:** *if the authority ran on another machine and ticked separately
from this client, would this still work?* If the feature reacts to a local event, mutates the render
copy, or assumes the host sees changes the instant it makes them — it won't.

**One small illustration (a value that changes over time).** Say a turn has a countdown. Do it like
this — and note it's *one* illustration of rules 1 & 2, not a prescribed pattern:

```jsonc
// Authority OWNS the clock and decides what expiry means. The snapshot carries the deadline,
// not a per-frame number — every client derives the displayed value itself.
// host → all:  { "kind":"state", "turn":"<pid>", "deadlineMs": 1718500000000, ...rest }

// Every client (HOST INCLUDED) computes the displayed remaining each frame from the last snapshot:
//   const remaining = Math.max(0, state.deadlineMs - now());   // in your render/update loop
// Nothing here mutates shared state; it's pure display.

// If expiry needs client input (e.g. "submit whatever I've typed"), the client streams that input
// AHEAD of the deadline as an intent, so the host can act on it when ITS clock expires:
//   guest → host:  { "kind":"draft", "text": "<in-progress>" }
// The host — not the client's display — detects expiry and resolves the turn, then broadcasts.
```

The wrong version (works solo, fails live): let each client watch its *own* displayed timer and, when
it hits zero, send the move. Over the wire the host's clock already expired and moved on, so the
client's late intent is rejected or applies to the wrong turn — and nothing errors.

See §11 for how to catch all of this before you ship.

---

## 5b. Server-authoritative mode (opt-in)

§5 runs your rules in the **host's browser**. A game can instead opt in to having the **server** run
its authoritative logic, sandboxed, one instance per lobby. You get three wins over host authority:
uniform (near-zero) authority latency, **the session survives the creator leaving**, and rules are
enforced where clients can't tamper with them. Host-authoritative (§5) stays the default and is
untouched — this is per-game, and you choose it.

> Full design & server internals: **[SERVER_AUTHORITY_DESIGN.md](./SERVER_AUTHORITY_DESIGN.md)**.
> This section is the game-author's view.

### Opt in

Add one field to your [`GAME.json`](#gamejson) (§2):

```jsonc
{
  "id": "tictactoe-server",
  "name": "Tic-Tac-Toe (server)",
  "entry": "index.html",
  "maxPlayers": 2,
  "serverAuthority": "authority.js"   // ← ships an authority module; the server runs it
}
```

`serverAuthority` names a **single-file ES module** in your game folder. It is validated exactly
like `entry` (must exist, no path traversal, ≤ 1 MB) — and if it fails validation the game is
**skipped**, never silently downgraded to host mode. The module is **server-side code**: the game
origin never serves it (a `GET /games/<id>/authority.js` returns 404), so it's safe to hold secrets
there (see *Hidden information*, below).

### The module contract

Your module exports a `createAuthority(kb)` factory and an optional `config`. The returned object is
a bundle of **pure functions over JSON** — no rendering, no DOM, no network:

```js
// games/<id>/authority.js
export function createAuthority(kb) {
  let state = null;
  return {
    init(players) { state = { /* … */ }; },              // required — establish initial state
    applyIntent(fromId, action) { /* … */ return patch; }, // required — validate & mutate; return patch or null
    snapshot(forPlayerId) { return state; },              // required — full state for sync/late-join
    // optional roster hooks & tick below
  };
}
export const config = {};   // optional — { perRecipient?, tickHz? }
```

| Export | Required | Signature | Notes |
|---|---|---|---|
| `init` | ✅ | `init(players)` | Called once at lobby start with the roster `[{id, displayName}]`. |
| `applyIntent` | ✅ | `applyIntent(fromId, action) → patch \| null` | Validate against authoritative state; mutate; return a small **absolute-valued** patch to broadcast, or `null` to reject (nothing is sent, clients re-sync). |
| `snapshot` | ✅ | `snapshot(forPlayerId?) → state` | Full self-contained state for sync / late-join / reconnect. `forPlayerId` is passed in per-recipient mode. |
| `onPlayerJoined` | — | `onPlayerJoined(player) → patch \| null` | Roster hooks. State is re-broadcast after any of them, so a return patch is optional. |
| `onPlayerLeft` | — | `onPlayerLeft(playerId) → patch \| null` | |
| `onPlayerDisconnected` / `onPlayerConnected` | — | `(playerId) → patch \| null` | Soft presence during the reconnect grace window (e.g. pause a timer). |
| `tick` | — | `tick(dtMs) → patch \| null` | Exporting it opts into a server-driven tick; rate from `config.tickHz` (clamped). Absent → no timer at all. |
| `config` | — | `{ perRecipient?, tickHz? }` | `perRecipient:true` re-projects `snapshot(playerId)` per player (hidden info); `tickHz` requests a tick rate. |

This is a **superset of the `KBAuthority` model contract** (§5a / the Phaser client): a plain object
with `applyIntent`/`snapshot` you already use client-side ports over with a few lines. `applyPatch`/
`applySnapshot` from that contract are simply unused server-side — the server *is* the authority and
never adopts external state.

### The wire: intents in, state out

Clients speak the same `_kb` envelope `KBAuthority` uses. You rarely hand-write it — `KBAuthority`
(or the sample's `game.js`) does — but for a raw-SDK game:

| Direction | Payload |
|---|---|
| client → `sendToHost` | `{ "_kb": "intent", "action": { … } }` |
| client → `sendToHost` | `{ "_kb": "sync" }` (request full state — do this on `onReady`) |
| server → all | `{ "_kb": "delta", "patch": { … } }` |
| server → one/all | `{ "_kb": "state", "state": { … } }` |

`sendToHost` still means "send to the authority" — **the send path doesn't change.** The authority's
broadcasts arrive stamped `from: "server"`. In server mode the relay **drops** any client-sent
`_kb` `delta`/`state` (only the authority may publish state) and any non-`_kb` payload sent to
`host` — so don't route game state that way. Plain client-to-client chatter (`sendToAll` cursors,
emotes) is untouched.

### The `kb` capability object

`createAuthority(kb)` receives a small frozen `kb`:

| Capability | Purpose |
|---|---|
| `kb.setLobbyOpen(open)` | Join gate — e.g. close joins once a round starts. |
| `kb.setOwner(playerId)` | **Owner-migration primitive** (see below). |
| `kb.now()` | Milliseconds since epoch, **server clock**. There is **no `Date`** in the sandbox — `kb.now()` is the only time source. |
| `kb.log.info/warn/error/debug(msg)` | Server-side logging under `KnockBox.Authority`. |
| `kb.words.*` | Shared, immutable word dictionaries (validate a word, pick one by index, or take a whole prefix range). See **Word dictionaries** below. |
| `kb.budgetRemainingMs()` | Milliseconds left in **this call's** wall-clock budget. See **Staying inside your budget** below. |

### Word dictionaries (`kb.words`)

Word games need a large dictionary (hundreds of thousands of entries) to validate and pick words.
Inlining it in `authority.js` is impossible (it blows the module size cap) and would be duplicated
into every lobby's sandbox. Instead **declare dictionaries in `GAME.json`** and the server loads each
one **once** into a shared, memory-efficient structure that every lobby of the game queries — the
dictionary never enters your sandbox's memory, so a huge list costs one copy for the whole process.

```jsonc
{
  "id": "word-rush",
  "serverAuthority": "authority.js",
  "authorityWords": {
    "en": { "file": "words.txt", "caseInsensitive": true }
  }
}
```

The file is line-delimited (one word per line; blanks trimmed), ASCII-only, and lives in the game
folder — validated like `serverAuthority` (must exist, no path traversal, size ≤
`AuthorityMaxWordFileBytes`) and **never served on the game origin** (it's server-side data, and for
hidden-information games the answer list is secret). `authorityWords` **requires** `serverAuthority`.
It travels inside a `.kbg` package like every other file (`knockbox-pack` packs it and checks the
same rules), so a packaged word game works exactly like a folder-dropped one: the server extracts
the dictionary into its own cache and still refuses to serve it.

Your module queries it through `kb.words`, keyed by the dictionary key you chose (`"en"` above):

| Call | Returns |
|---|---|
| `kb.words.has(key, word)` | `boolean` — is `word` in the dictionary (case per `caseInsensitive`; non-ASCII → false) |
| `kb.words.count(key)` | `number` — total words (the valid index range for `pick` is `[0, count)`) |
| `kb.words.pick(key, index)` | `string \| null` — the word at a global index, or `null` if out of range |
| `kb.words.countOfLength(key, len)` | `number` — words of a given length |
| `kb.words.pickOfLength(key, len, index)` | `string \| null` — the `index`-th word of that length |
| `kb.words.rangeOfPrefix(key, len, prefix)` | `[start, end)` — the index range of the words of `len` starting with `prefix`, or `null` for a bad key/prefix |
| `kb.words.pickRange(key, len, start, count)` | `string[]` — that many words of `len` from `start`, in one call (clamped to the bucket and to `AuthorityMaxWordsPerCall`, default 512) |

Use `count` to size the dictionary before indexing — e.g. draw a random word with
`kb.words.pick(key, Math.floor(Math.random() * kb.words.count(key)))`. An unknown key or an
out-of-range index is safe (`false`/`0`/`null`), never a crash. Words are ordered length-bucket by
length (ascending), ordinal within a length, so `pick` is stable and identical in local emulation.

**Reach for `rangeOfPrefix` before you write a loop.** Because words are ordinal within a length,
every word starting with a given prefix occupies one contiguous run, and `rangeOfPrefix` hands you its
bounds. Nearly every word game wants exactly that — "words of length 6 starting with the succession
letter" — and the tempting alternative is a binary search written in your module over `pickOfLength`.
Do not: that search runs **inside the sandbox**, so each probe is an interpreted loop iteration plus a
string marshalled across the boundary, and it is the single most expensive thing word games do here.
Resolving all 26 starting letters across 14 lengths costs ~3,300 boundary crossings hand-rolled and
~360 through `rangeOfPrefix`. Then take the words with `pickRange` rather than a `pickOfLength` per
index:

```js
// Every 6-letter word starting with "s", in two calls instead of hundreds.
const [start, end] = kb.words.rangeOfPrefix('en', 6, 's');
const candidates = kb.words.pickRange('en', 6, start, end - start);   // capped at 512

// A random one, without materialising anything:
const pick = kb.words.pickOfLength('en', 6, start + Math.floor(Math.random() * (end - start)));
```

`pickRange` returns fewer than you asked for when the range runs out or the cap bites — the array's
own `length` is the honest answer, so iterate it rather than the count you requested.

### Staying inside your budget

**Every call into your module is bounded by a wall clock** (`AuthorityCallTimeoutMs`, 250 ms by
default) — `init`, `applyIntent`, each `tick`, every roster hook. Blow it and the call is killed;
blow it repeatedly and the lobby is closed with everyone in it.

**Your browser will not warn you about this, and that is the trap.** In solo/local mode your module
runs in the browser's JIT-compiled JavaScript engine over an in-memory array. On the server it runs
*interpreted*, in Jint, and every `kb.words` call crosses a boundary into the host. The same turn that
felt instant in a tab can be one to two orders of magnitude slower here. A game that plays perfectly
solo can still close its lobbies in server mode on its very first match.

Two things to do about it:

1. **Bound your own loops.** Anything open-ended — scanning candidates, searching, simulating — needs
   a ceiling that does not depend on the dictionary's size. Prefer `rangeOfPrefix` over scanning, and
   cap how many candidates you will examine per call.
2. **Ask how much budget is left.** `kb.budgetRemainingMs()` returns the milliseconds remaining in the
   current call (0 outside one). Use it to stop cleanly with a partial-but-consistent result instead of
   being killed part-way through:

   ```js
   const found = [];
   for (let i = start; i < end; i++) {
     if (found.length >= 5) break;
     if (kb.budgetRemainingMs() < 40) break;   // leave room to finish the turn properly
     const w = kb.words.pickOfLength('en', 6, i);
     if (isPlayable(w)) found.push(w);
   }
   ```

   Locally it reports a flat 250 ms and never counts down — there is no interpreter budget in a tab to
   count. It lets you *write* budget-aware code locally; it cannot tell you whether you fit. Only a
   real server run can.

**Measure it before you ship: `--authority-bench`.** The server itself will run your module under the
real engine and tell you how close you are, without starting a listener or a lobby:

```bash
# Ticks only — enough for a game that does work every frame.
dotnet run --project KnockBox.Server -- --authority-bench ./games/my-game

# Most games idle until a match starts, so drive the intents that get you into the interesting states.
dotnet run --project KnockBox.Server -- --authority-bench ./games/my-game --script bench.json
```

`bench.json` is an array of steps, each an optional intent and a number of ticks to run after it:

```json
[{ "intent": { "kind": "startMatch", "settings": {} }, "from": "p0", "ticks": 3000 }]
```

It prints p50/p90/p99/max per export, the percentage of budget the worst call used, and how many
`kb.words` queries each export made — the last one tells you whether your cost is boundary crossings
or interpreted work, which have different fixes. It **exits non-zero** when a call blows the budget,
so it works as a CI gate, and warns when you are under 2x headroom: a real host is busier than your
laptop, a GC pause counts against the budget, and the worst turn a player reaches is rarely the worst
turn a bench happened to generate.

**Watch the server log.** When one call reaches `AuthoritySlowCallWarnFraction` of its budget (half, by
default) the server logs a warning naming your game, the export, and the percentage — on each new
worst. If you see that during development, act on it: it is the only warning you get before players
start being disconnected. If your game genuinely needs more room, an operator can raise it for that
game alone with `KnockBox:AuthorityCallTimeoutMsByGame:<your-game-id>`.

A `tick` that overruns is dropped and the lobby survives (up to `AuthorityMaxConsecutiveOverruns`, 3
by default, in a row). An overrun in `applyIntent` or a roster hook is fatal on the first occurrence —
there is no safe way to half-apply someone's move.

### Owner ≠ authority

In server mode **every client is `isHost: false`** — no browser is the host. A separate concept, the
**owner** (initially the lobby creator), holds the lobby powers `setLobbyOpen` / `kickPlayer`, and is
surfaced to clients as `KnockBox.ownerId` / `KnockBox.isOwner`. **Gate owner-only UI (kick buttons,
open/close toggles) on `isOwner`, not `isHost`.** When the owner leaves, the game continues; your
module decides succession by calling `kb.setOwner(nextId)` (typically from `onPlayerLeft`). A module
that never calls it simply runs owner-less — allowed. Clients get an `onOwnerChanged(ownerId)`
callback when it moves.

### Limits & error semantics

Each module call is budgeted (memory / wall-clock / statement count — see the `Authority*` knobs in
INFRASTRUCTURE.md §9). Two failure classes:
- **Contained** — your `applyIntent`/hook *throws*: the intent is dropped, the current `snapshot()`
  is re-broadcast so clients re-converge, and the lobby stays alive. In development the error message
  is relayed to the browser console as `{ "_kb": "error", … }`; production leaks nothing. Five
  consecutive contained failures escalate to fatal.
- **Fatal** — a constraint violation (timeout / memory / statement overflow) or a load/`init`
  failure: the engine is untrustworthy, so the lobby is **closed loudly** — members get a
  `LobbyClosed` control event (the shell returns them home) and the game sockets are dropped. A
  load/`init` failure at creation just fails lobby creation with an error to the creator.

### Hidden information (server mode makes this real)

Because the module runs server-side and is never served to clients, secret state genuinely stays
secret. Set `config.perRecipient = true` and return a per-player projection from
`snapshot(forPlayerId)`. The clean structure is a **split-file pattern**: a shared rules module
(loadable client-side too) composed by a thin, server-only `authority.js` that holds the secret
projection. Contrast §9's host-authoritative hidden-info approach, which trusts the host browser.

### Test it locally (no server needed for the inner loop)

Three tiers — see §11 for the mechanics; server-authority specifics:

1. **Pure module tests (Vitest).** Import `createAuthority(fakeKb)`, feed intents, assert
   patches/snapshots. `fakeKb` is ~10 lines:
   ```js
   let ownerId = 'p1';
   const words = new Set(['apple', 'brave', 'crane']); // stub kb.words for the test
   const fakeKb = {
     now: () => 0,
     log: { info(){}, warn(){}, error(){}, debug(){} },
     setLobbyOpen(_open) {},
     setOwner(id) { ownerId = id; },
     words: {
       has: (_key, w) => words.has(String(w).toLowerCase()),
       count: () => words.size,
       pick: (_key, i) => [...words][i] ?? null,
       countOfLength: (_key, len) => [...words].filter((w) => w.length === len).length,
       pickOfLength: (_key, len, i) => [...words].filter((w) => w.length === len)[i] ?? null,
     },
   };
   const a = createAuthority(fakeKb);
   a.init([{ id: 'p1', displayName: 'A' }, { id: 'p2', displayName: 'B' }]);
   expect(a.applyIntent('p1', { kind: 'move', cell: 0 })).toMatchObject({ /* … */ });
   ```
2. **Local emulation (Phaser `knockbox-local.js`).** Pass `authority: createAuthority` (or a
   `'./authority.js'` URL) to run your real module as a virtual `from:"server"` actor over the local
   `tab`/`process` transports, with default-on fidelity checks (JSON round-trip boundary, `Date`
   poisoning, single-file import scan) that catch server-only failures early. For `kb.words`, supply
   the data with a `words` option — `words: { en: ['apple', 'brave', …] }`, or
   `words: { en: { file: './words.txt' } }` to fetch it — with the same `pick` ordering as the
   server. With the `'./authority.js'` URL form, the sibling `GAME.json`'s `authorityWords` are
   auto-discovered and fetched, so no `words` option is needed. (Those files fetch in dev because your
   own static server serves them; the real KnockBox server denies them on the game origin.)
3. **A real server (optional, full fidelity).** Drop the game into `games/` of a local instance
   (desktop exe or `dotnet run`) for the real Jint sandbox and constraint limits.

The canonical worked example is **`games/tictactoe-server/`** — a faithful port of `games/tictactoe`:
`authority.js` (the rules + owner succession + join gate) and a render-only `game.js`.

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
- A tab refresh, tab close, or network blip drops a player's shell socket, but the server now holds
  them in the lobby for a grace window (default 60s) instead of removing them immediately. You learn
  about this through `onPlayerDisconnected(playerId)` — the player **stays in `KnockBox.players`** the
  whole time, so treat it as "reconnecting…", not a departure. If they return in time you get
  `onPlayerConnected(playerId)`; if the window elapses you get the usual `onPlayerLeft(playerId)`.
- Decide what a `playerLeft` means for your game (pause, forfeit, end). A `playerDisconnected` is
  usually a softer signal — pause or show a spinner rather than forfeit. Host migration is not
  provided — if the host leaves for good, the session effectively ends.

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

For Godot, a maintained GDScript addon removes the boilerplate of the routes above.

**Install it from inside the editor** — **Project → AssetLib**, search **KnockBox**, Install — then
enable it under **Project → Project Settings → Plugins**. No terminal and nothing to install first.
(Prefer a download? Grab `knockbox-godot-<version>.zip` from the
[releases page](https://github.com/jcub1011/KnockBox-Games/releases) and unzip it at your project
root. With Node available, `npx knockbox addon add godot` also works.)

Once enabled, **Project → Tools** gains two actions: *check for addon updates*, and *reinstall addon*
— which restores every file if you have edited one by accident. So **don't fork it**: fixes land
upstream, and the updater brings them to you rather than you copying them forward. If you do need a
local change, `knockbox addon check` will keep telling you about it, which is the point. Full details
in [`docs/ADDONS.md`](ADDONS.md).

It has three layers; use as much as you want:

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

## 10c. Phaser — use the KnockBox client

For [Phaser 3](https://phaser.io) there is a maintained client that speaks the same JSON protocol as
the vanilla and Godot ones — so a Phaser game can even share a lobby with games built on those.

**Install it:**

```bash
npx knockbox addon add phaser        # lands in addons/knockbox/, records the version
```

Nothing to install first: `npx` fetches the CLI on demand, and Node is already a prerequisite of any
Phaser/Vite toolchain. (No Node? Unzip `knockbox-phaser-<version>.zip` from the
[releases page](https://github.com/jcub1011/KnockBox-Games/releases) at your project root — same
result.) Commit the installed files **and** `knockbox.json`: that file is what records the version,
lets `knockbox addon check` verify the files are unmodified, and gets stamped into your `.kbg`.

Four files land in `addons/knockbox/`, all UMD (browser global, `import`, CommonJS or AMD — **no build
step required**), plus TypeScript definitions:

| File | Purpose |
| --- | --- |
| `knockbox-plugin.js` | The Phaser **global plugin** — the main send/receive API. |
| `kb-core.js` | Pure protocol helpers. No dependencies. |
| `kb-authority.js` | Optional host-authoritative state-sync helper (§5). |
| `knockbox-local.js` | Local testing with **no server** — multi-tab + automated loopback. |
| `knockbox-phaser.d.ts` | TypeScript definitions for all of the above. |

Register it as a **global plugin** with `start: true` and a `mapping`, so every scene reaches it as
`this.<mapping>`:

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

The plugin connects automatically on start: it reads the ticket + endpoint the shell put in the URL
fragment, opens its own WebSocket, authenticates, then fires `ready`. Full API — signals, sending,
`KBAuthority`, and the server-less local peer — is in
[`clients/phaser/README.md`](../clients/phaser/README.md).

Then package as usual (§9): `knockbox pack --in dist --manifest GAME.json --build "npm run build"`.

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

**Test with peers, not just solo (see §5a).** A single tab/solo session never exercises a real
replicated copy, so the bugs in §5a stay hidden. Two ways to surface them:

- **Manual:** open two tabs (above) and actually play across them — watch that *continuous* state
  (timers, motion, animation) advances every frame on **both** the host and the guest, not only when
  a message lands.
- **Automated:** drive several peers in one process with the local-testing client and assert they
  converge. The web SDK ships `KnockBoxLocalPeer` (`mode:'process'`) plus `_resetLocalHubs()` to clear
  the in-process relay between tests:

  ```js
  import { KnockBoxLocalPeer, _resetLocalHubs } from './knockbox-local.js';
  const host  = new KnockBoxLocalPeer({ mode:'process', channel:'t', playerId:'h' });
  const guest = new KnockBoxLocalPeer({ mode:'process', channel:'t', playerId:'g' });
  // wire each to your model/KBAuthority, start both, send intents, then assert host & guest agree.
  ```

  If you use `KBAuthority` in per-recipient mode, its **dev checks** (on by default under the local
  client) deep-freeze `currentView`, so a stray mutation of the rendered copy throws right here
  instead of silently diverging in production — see the Phaser client README.

**Server-authoritative games (§5b)** iterate with the same three tiers, but the module runs as a
virtual `from:"server"` actor: pure `createAuthority(fakeKb)` tests, then the `authority:` option on
`knockbox-local.js`, then optionally a real local server. See §5b for the specifics.

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
- **A replicated copy doesn't tick itself.** Continuous state (timers, motion, animation, prediction)
  must be advanced by your own per-frame loop on *every* client — the host included (§5a).
- **Decide shared-state effects on the authority, not on a local event.** A local reaction can't beat
  the host's update over the wire; route it through an intent, and send any input the host will need
  *before* the deadline (§5a).
- **Model per-participant state per id.** One player's action isn't the whole group's; fire a group
  transition only when it holds for everyone relevant (§5a).
- **This class of bug passes single-player and never throws** — test with real peers (§11), not just
  solo.
