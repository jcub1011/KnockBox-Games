# KnockBox Games — Server-Authoritative Mode (Design)

**Status: Phases 0–2 implemented** on `feature/server-authoratative-state` (2026-07-10); Phase 3
(docs) in progress; Phase 4 (WASM backend) remains (§13). This document is the design for an
optional, per-game server-authoritative mode. Most file/line references now describe the code
*as-built*; a few (WASM, §3c/§13 Phase 4) still describe where later work lands.

> Architecture background: **[INFRASTRUCTURE.md](./INFRASTRUCTURE.md)**. Game authoring:
> **[GAME_DEVELOPER_GUIDE.md](./GAME_DEVELOPER_GUIDE.md)** (§5 documents the host-authoritative
> contract that stays the default; §5b documents this server-authoritative mode for opted-in games).

---

## 1. Problem, goals, non-goals

Today one session is authoritative on one client — the lobby creator's browser (the **host**,
INFRASTRUCTURE.md §1 principle 4). That has three structural costs:

1. **Latency**: every guest input round-trips through the host before anyone sees the result, so a
   high-latency host penalizes the whole lobby.
2. **Fragility**: `Lobby.HostId` is immutable (`Lobby/Lobby.cs`), there is no host migration, and
   the guide is explicit — *"if the host leaves for good, the session effectively ends."*
3. **Trust**: the host browser can cheat; the server relay is blind
   (`WebSocketHandler.HandleGameMessage` never inspects payloads).

**Goal**: let a game *opt in* (per game, via `GAME.json`) to having the **server** run its
authoritative logic. The game ships an **authority module** — pure game rules with no rendering —
alongside its web build; the server executes it, sandboxed, one instance per lobby. Client intents
route to the server; the server broadcasts state. Wins: uniform (near-zero) authority latency, the
session survives the creator leaving, and rules are enforced where clients can't tamper with them.

**Explicit non-goals (v1)**:
- Client-side prediction/rollback (Rune-style). Games keep the "render what the authority sent"
  model; the doc's contract deliberately leaves room to add prediction later because the module is
  pure and could also run client-side. (Prediction would also require deterministic modules —
  v1 leaves `Math.random` available; a seeded `kb.random` would come with that work.)
- Automatic owner migration *policy*. Owner succession is the game's decision: the platform ships
  the primitive (`kb.setOwner`, §3) and the game's authority module decides when and to whom.
  A lobby whose module never transfers ownership simply runs owner-less (documented).
- Durable state. Server-authority lobbies remain in-memory and die on restart, like everything else.
- Changing anything for games that don't opt in. Host-authoritative stays the default and is
  untouched.

**Prior art**: [Rune](https://developers.rune.ai/docs/how-it-works/server-side-logic) runs each
game's pure `logic.js` on clients *and* its servers; Colyseus is server-authoritative rooms with
schema state. KnockBox's own `clients/phaser/kb-authority.js` already factors game rules into a
pure model contract — this design builds on that contract (the server ABI is a superset, §3) so
existing KBAuthority games port their model file to the server with a few-line adapter.

---

## 2. Design at a glance

```
                      ┌───────────────────────────── KnockBox.Server ─────────────────────────────┐
 GuestA ── /ws data ──┤  WebSocketHandler.HandleGameMessage                                        │
 GuestB ── /ws data ──┤     to:"host" ──► ServerAuthorityManager ──► ServerAuthority (per lobby)   │
 Owner  ── /ws data ──┤                                               │  bounded Channel + 1 task  │
                      │                                               │  IAuthorityRuntime         │
                      │   fan-out ◄── GameMessage{from:"server"} ◄────┤   ├─ JsAuthorityRuntime    │
                      │   (SendRawToGame per member)                  │   │    (Jint, authority.js)│
                      │                                               │   └─ WasmAuthorityRuntime  │
                      └───────────────────────────────────────────────┴──      (later, .wasm)  ────┘
```

- `GAME.json` gains `"serverAuthority": "authority.js"` (or `"authority.wasm"`).
- On lobby creation for such a game, a per-lobby **actor** loads the module into a sandboxed
  runtime. All module calls happen on one drain task fed by a bounded channel (the
  `Networking/Connection.cs` pattern).
- The relay diverts `to:"host"` frames to the actor instead of the creator's socket. The actor
  broadcasts results back through the existing `GameMessage` fan-out with the reserved sender id
  **`"server"`**.
- `Ready` tells every client `isHost:false` plus `authority:"server"`, so client code written to
  the existing contract simply behaves as a guest. **Lobby-owner powers (kick, open/close) start
  with the creator** — "owner" and "authority" become separate concepts — and the authority module
  can transfer them (`kb.setOwner`, §3).
- The module ABI is runtime-agnostic (JSON in, JSON out), with two backends: **Jint** (JavaScript,
  v1) and **WASM** (any language — C#, Rust, Go, Zig… — later phase).

---

## 3. The authority module ABI (runtime-agnostic)

An authority module is a bundle of **pure functions over JSON values**. It extends the model
contract `clients/phaser/kb-authority.js` already defines — there, a plain model object with
`applyIntent`/`applyPatch`/`snapshot`/`applySnapshot` (plus a `perRecipient` option) passed to
`new KBAuthority(net, model)` — so the mental model is shared between host-authoritative and
server-authoritative games. The server ABI is a **superset**: `init`, the roster hooks, `tick`,
`config`, and the `createAuthority(kb)` factory wrapper are new, server-only additions. Porting an
existing KBAuthority model is a few lines:

```js
// authority.js — wrapping an existing KBAuthority model object (bundled single-file, §14.2)
export function createAuthority(kb) {
  const model = { /* your existing applyIntent/snapshot/applyPatch/applySnapshot object */ };
  return {
    init(players) { /* establish initial state (game code did this before creating KBAuthority) */ },
    applyIntent: (fromId, action) => model.applyIntent(fromId, action),
    snapshot: (forPlayerId) => model.snapshot(forPlayerId),
  };
}
```

| Export | Required | Signature (conceptual) | Notes |
|---|---|---|---|
| `init` | yes | `init(players)` | Called once at lobby start with the initial roster `[{id, displayName}]`. Establish state here. |
| `applyIntent` | yes | `applyIntent(fromId, action) -> patch \| null` | Identical to KBAuthority: validate against authoritative state using `fromId`, mutate, return a small **absolute-valued** patch to broadcast — or `null` to reject (nothing is sent). |
| `snapshot` | yes | `snapshot(forPlayerId?) -> state` | Full self-contained state for sync/late-join/reconnect. `forPlayerId` is passed in per-recipient (hidden-information) mode. |
| `onPlayerJoined` | no | `onPlayerJoined(player) -> patch \| null` | Roster hooks. After any of these the server re-broadcasts state anyway (KBAuthority's roster rule), so returning a patch is optional. |
| `onPlayerLeft` | no | `onPlayerLeft(playerId) -> patch \| null` | |
| `onPlayerDisconnected` / `onPlayerConnected` | no | `(playerId) -> patch \| null` | Soft presence (reconnect grace window) — e.g. pause a timer. |
| `tick` | no | `tick(dtMs) -> patch \| null` | Exporting it opts into a server-driven tick (§6). Absent → no timer exists at all. |
| `config` | no | `{ perRecipient?: boolean, tickHz?: number }` | Static behavior knobs. `tickHz` is clamped by the server (§8). |

The module also receives a tiny frozen capability object at creation:

| Capability | Purpose |
|---|---|
| `kb.setLobbyOpen(open)` | Join gate (e.g. close joins once a round starts). |
| `kb.setOwner(playerId)` | **Owner migration primitive.** Reassigns the lobby owner (kick/open powers + `isOwner` on clients). The server validates the target is a current member, updates `Lobby.HostId`, and pushes an `OwnerChanged` event (§5f). *Policy* is the game's: typically called from `onPlayerLeft` when the departed player was the owner. A module that never calls it leaves the lobby owner-less after the creator departs — allowed and documented. |
| `kb.now()` | Milliseconds since epoch, server clock (backed by `TimeProvider` so tests can fake it). Modules must not reach for their own clock — engine setup **deletes the `Date` global** (Jint ships the full ECMAScript `Date` by default, so this is an active removal, verified in the Phase 0 spike), making `kb.now()` the only time source. |
| `kb.log.info/warn/error/debug(msg)` | Serilog under a `KnockBox.Authority` category (the `KnockBox.GameLog` precedent), stamped with gameId/lobbyId. |
| `kb.words.has/count/pick/countOfLength/pickOfLength` | Read-only queries over the game's declared word dictionaries (`GAME.json` `authorityWords`). Each dictionary is loaded **once** by `AuthorityWordService` into a shared, immutable, length-bucketed structure (`WordPoolSet`) that every lobby engine of the game shares — and deduped across games by **content hash** (SHA-256), so byte-identical dictionaries shipped under different names collapse to a single copy — the dictionary never enters the JS heap or the per-invocation memory budget; only the boolean/number/string result of a query crosses the boundary. Backed by `ClrFunction` (the same no-reflection path as the rest of `kb`), so it stays AOT-clean. Guarded: an unknown key / out-of-range index returns `false`/`0`/`null`, never a fatal throw. This is the answer to games needing a large dictionary (a word list) without the naive per-lobby copy or a raised memory cap. |

`kb.setOwner` and `kb.setLobbyOpen` are **deferred effects**: a call during a module invocation
only records the request; the actor applies it (validation, `HostId` update, event broadcasts)
after the invocation returns. This keeps host code — locks, sends, Serilog sinks — out of the
constrained call (a slow sink must not burn the module's CPU/memory budget and get misclassified
as a fatal module failure, §7) and keeps ordering sane: `OwnerChanged` always follows the delta of
the intent that triggered it. `kb.log` writes are buffered the same way; `kb.now()` is a pure read
and stays inline.

`applyPatch`/`applySnapshot` from the KBAuthority contract are deliberately **not** called
server-side — the server *is* the authority and never adopts external state. A shared model file
simply carries them unused; that is what makes the same file loadable by KBAuthority client-side
(the local authority emulation, §12a, or a future prediction mode).

**Why a functional contract rather than an injected send/broadcast API**: the actor owns the whole
KBAuthority host loop (intent → patch → broadcast; sync → snapshot; roster change → re-broadcast;
per-recipient → re-project per player). Keeping the module output-only makes it deterministic-ish,
trivially unit-testable (feed intents, assert patches — in Vitest for `.js`, natively for `.wasm`
source languages), and byte-compatible with the client-side contract.

### 3a. Server-side seam: `IAuthorityRuntime`

One small interface isolates "execute untrusted module code" from everything else (actor, wire,
lifecycle, limits — all shared):

```csharp
// KnockBox.Server/Games/IAuthorityRuntime.cs (new)
public interface IAuthorityRuntime : IDisposable
{
    // Load + instantiate the module (calls createAuthority/init equivalent). Throws AuthorityLoadException.
    void Initialize(string playersJson);
    // Hook names present on the instantiated authority object (init/applyIntent/…/tick), so the
    // actor knows which optional hooks and tick exist. (In JS these are properties of
    // createAuthority's return value — only createAuthority and config are true module exports.)
    IReadOnlySet<string> Exports { get; }
    AuthorityConfig Config { get; }                    // parsed `config` export (perRecipient, tickHz)
    // Invoke an exported function with JSON-string args; returns the result as a JSON string
    // ("null" for null). Throws AuthorityScriptException (contained) or
    // AuthorityConstraintException (memory/timeout/statements — fatal, engine untrustworthy).
    string Invoke(string export, params string[] jsonArgs);
}
```

The boundary is **strings of JSON** in both directions. That is what the wire already carries
(`GameMessage.Payload` is opaque JSON), it avoids marshaling CLR object graphs into either runtime
(the AOT-risky area), and it makes the two backends interchangeable.

### 3b. `JsAuthorityRuntime` — Jint (v1, ships first)

[Jint](https://github.com/sebastienros/jint) is a pure-C# JavaScript interpreter: no native
dependency (works in the single-exe desktop publish), no `Reflection.Emit`, and an official Native
AOT sample (`Jint.AotExample`: `PublishAot=true` + `<TrimmerRootAssembly Include="Jint" />`).

The module is an **ES module** (so the very same file is `import`able client-side):

```js
// games/<id>/authority.js
export function createAuthority(kb) {
  let state = null;
  return {
    init(players) { state = { board: Array(9).fill(null), next: players[0].id /* … */ }; },
    applyIntent(fromId, action) { /* validate, mutate, return absolute patch or null */ },
    snapshot() { return state; },
  };
}
export const config = { tickHz: 0 };
```

Engine setup, one engine per lobby:

```csharp
var engine = new Engine(o => o
    .Strict()
    .LimitMemory(opts.MaxMemoryBytes)        // default 32 MB
    .TimeoutInterval(opts.CallTimeout)       // default 250 ms, re-armed per invocation
    .MaxStatements(opts.MaxStatements)       // default 1,000,000 per invocation
    .LimitRecursion(opts.RecursionLimit));   // default 64
engine.Modules.Add("authority", File.ReadAllText(path));   // path pre-validated by GameCatalog
var ns = engine.Modules.Import("authority");
```

Sandbox properties, by construction:
- **No CLR access**: `AllowClr` is never enabled; scripts cannot touch .NET types.
- **No filesystem/module escape**: no module loader is configured, so an `import './other.js'`
  inside the module fails. (Multi-file modules are a possible later addition via a custom loader
  path-restricted to the game folder, exactly like the `entry` check.)
- **No ambient I/O, no ambient time**: Jint provides no `fetch`/`setTimeout`, but it *does* ship
  the full ECMAScript `Date` built-in (and `Math.random`). Engine setup **deletes the `Date`
  global** so `kb.now()` is the only clock (spike-verified); `Math.random` stays available in v1 —
  nondeterminism is acceptable while nothing replays module calls (§1 non-goals). We inject only
  the `kb` object (§3), built with `JsValue`-typed callbacks — Jint's no-reflection-marshaling
  path (`ClrFunction`/`JsCallDelegate`) — never typed-delegate `SetValue` overloads.
- **Bounded CPU/memory per call**: the four constraints above. Jint re-arms all constraints
  automatically at the start of every invocation (inside `Engine.ExecuteWithConstraints`, which
  `Invoke`/`Call`/`Evaluate` route through — there is no public whole-engine reset API); a
  dedicated unit test pins that behavior (a timeout-limited engine invoked twice must budget each
  call separately), with a per-constraint `engine.Constraints.Find<T>()?.Reset()` loop as the
  fallback if a Jint upgrade ever changes it (§14.3). Violation throws and is treated as
  fatal (§7).

Interop mechanics: inbound payloads are parsed with Jint's own `JsonParser` (`string → JsValue`),
results serialized with its `JsonSerializer` (which returns a JS string as a `JsValue`;
`.AsString()` yields the CLR string). Nothing but strings and `JsValue` ever crosses the boundary.

### 3c. `WasmAuthorityRuntime` — any language (designed now, built later)

For developers not writing JavaScript (Godot C#, Rust, Go, Zig, AssemblyScript…), the same ABI is
expressible as a WASM module: `"serverAuthority": "authority.wasm"`. The backend is selected by
file extension.

- **Calling convention**: bytes-in/bytes-out — each export takes UTF-8 JSON in linear memory and
  returns UTF-8 JSON, i.e. exactly `IAuthorityRuntime.Invoke`. This is the
  [Extism](https://extism.org/docs/concepts/plug-in-system/) plugin convention; the
  [Extism .NET host SDK](https://github.com/extism/dotnet-sdk) provides the memory-shuttling
  plumbing plus host functions (for `kb.*`) out of the box, with plugin PDKs for a dozen source
  languages. Alternative: raw [wasmtime-dotnet](https://github.com/bytecodealliance/wasmtime-dotnet)
  with a hand-rolled convention — more control, more plumbing. **Recommendation: Extism**, unless
  the extra native lib is a problem, because the ABI, host functions, and language PDKs are the
  entire hard part.
- **Sandboxing**: WASM is sandboxed by construction (no ambient capabilities). CPU limiting via
  Wasmtime fuel or epoch interruption ≈ `MaxStatements`/`TimeoutInterval`; memory via store limits
  ≈ `LimitMemory`. The same `AuthorityOptions` knobs (§8) map onto both runtimes.
- **Deployment cost (why it's a later phase)**: unlike Jint, this adds a native library per RID —
  it must be verified in the Docker linux-x64 image *and* the win-x64 self-contained desktop
  publish, and checked against the `aot` CI gate (P/Invoke itself is AOT-safe; note that parts of
  wasmtime-dotnet's convenience API historically used `dynamic`, which is not — stick to the typed
  API). None of this blocks the shared design: the actor, wire protocol, lifecycle, limits, and
  tests are runtime-agnostic, so the WASM backend is additive.

---

## 4. Manifest opt-in (`GAME.json`)

```jsonc
{
  "id": "tictactoe-server",
  "name": "Tic-Tac-Toe (server)",
  "entry": "index.html",
  "maxPlayers": 2,
  "serverAuthority": "authority.js"     // ← the opt-in; ".wasm" selects the WASM backend
}
```

- `KnockBox.Contracts/GameManifest.cs` gains `string? ServerAuthority = null`. Additive and
  source-gen-safe (no `KnockBoxProtocolContext` change — the manifest is already registered).
  Flat rather than nested, matching every existing field; `CrossOriginIsolated` is the
  opt-in-with-default precedent. Behavior tuning (tick rate, per-recipient) lives in the module's
  `config` export, not the manifest: the manifest describes *hosting*, the module describes
  *behavior*.
- **Validation** in `GameCatalog.Discover()` mirrors the existing `entry` checks (path-traversal:
  resolved path must stay inside the game folder; file must exist) and adds a new size check —
  size ≤ `AuthorityMaxScriptBytes` (there is no existing asset-size precedent; this is the first). A manifest that declares `serverAuthority` but fails validation
  **skips the whole game** (same policy as a bad `entry`) — silently downgrading a game that asked
  for server-side enforcement back to a cheatable host mode would betray the opt-in.
- **`tools/pack-game` mirrors the new rules** (its stated purpose — the packer's `validate()`
  already mirrors `GameCatalog.Discover()` for `entry`/`thumbnail`, with a keep-in-sync comment):
  `serverAuthority` file exists, no path traversal, size within the default cap, plus two checks
  the catalog can't do cheaply — a **static scan for top-level `import`/`export … from`
  statements** (the single-file rule, §14.2) and a load check (dynamic-import the module in Node,
  assert `createAuthority` is a function and `config`, if present, is well-formed). It runs the
  developer's own code inside their own packer — acceptable.

---

## 5. Wire protocol changes

All changes are **additive**; the protocol version stays `1`. (JS clients ignore unknown JSON
fields; old servers simply omit the new ones; behavior changes only for games that opt in, and
opting in is a developer action targeting this contract.)

### 5a. Routing: `to:"host"` reaches the authority

In `WebSocketHandler.HandleGameMessage`, the `"host"` case gains one branch:

```csharp
case "host":
    if (lobby.IsServerAuthority)                                      // mode, not actor presence
    {
        if (authorities.TryGet(conn.LobbyId, out var auth))
            auth.PostIntent(conn.PlayerId, m.Payload.GetRawText());   // raw JSON crosses the channel
        // else: actor gone (fatal-failure teardown race) — drop; never fall through to a client
    }
    else
        connections.SendRawToGame(lobby.HostId, bytes);               // unchanged host-auth path
    break;
```

The branch keys on the **lobby's mode** (stamped at creation from the manifest), never on actor
presence: if the actor is missing in a server-authority lobby, the frame is dropped — intents must
never fall through to the creator's socket in a mode that promised server-side enforcement.

`sendToHost` keeps meaning what it always meant — *"send to the authority"* — so the SDK send path
does not change at all. The payload crosses the actor boundary as raw JSON text (`GetRawText()`),
which is both the natural input for the runtime's JSON parser and free of `JsonElement` lifetime
concerns.

### 5b. The authority's outbound identity: `from:"server"`

The actor holds no `Connection`. It serializes `GameMessage(To, Payload, From: "server")` once via
`KnockBoxProtocolContext` and fans out with `ConnectionManager.SendRawToGame` per lobby member —
the same mechanics `HandleGameMessage` uses today. `"server"` cannot collide with a real player id
(ids are 32-char server-minted GUID strings). A malicious client addressing `to:"server"` falls
through to the default relay case, fails the `lobby.Contains` membership check, and is dropped —
no new attack surface.

### 5c. `Ready` grows `authority` and `ownerId`

```csharp
public sealed record ReadyMessage(
    string PlayerId, IReadOnlyList<Player> Players, bool IsHost,
    int Proto = KnockBoxProtocol.Version,
    string Authority = "host",        // "host" | "server"
    string? OwnerId = null) : IMessage;
```

- **Server-authority lobby**: every client gets `IsHost:false`, `Authority:"server"`,
  `OwnerId: lobby.HostId`. Because *no* client is ever told it is host, client code written to the
  existing contract (KBAuthority or raw SDK per guide §5) runs its guest branch: auto-request sync
  on ready, send intents to `"host"`, adopt broadcast state. **An already-shipped KBAuthority game
  client works unchanged.**
- **Host-authority lobby**: unchanged semantics, plus `Authority:"host"` and `OwnerId` for
  uniformity.
- **Owner ≠ authority.** `Lobby.HostId` starts as the creator and continues to gate the two
  server-enforced owner powers, `SetLobbyOpen` and `KickPlayer`. It becomes **mutable** (a
  lock-guarded setter) so the authority module can reassign it via `kb.setOwner` (§3) — the one
  structural change to `Lobby`. `OwnerId` exists so SDKs can expose `isOwner` and games can gate
  owner UI (kick buttons, open/close toggle) on it instead of `isHost`.

### 5d. Payload protocol: the `_kb` envelope, verbatim

The actor speaks exactly the envelope `kb-authority.js` already defines (shared with the Godot
addon), so nothing new is invented:

| Direction | Payload |
|---|---|
| client → `to:"host"` | `{ "_kb": "intent", "action": { … } }` |
| client → `to:"host"` | `{ "_kb": "sync" }` |
| server → all | `{ "_kb": "delta", "patch": { … } }` |
| server → one / all | `{ "_kb": "state", "state": { … } }` |

A non-`_kb` payload addressed to `"host"` in a server-authority lobby is dropped with a debug log:
there is no host player to deliver it to, and the envelope *is* the documented contract for
server-mode games.

The relay also enforces the envelope in the **other** direction: in a server-authority lobby, a
client-sent `_kb` frame of kind `delta` or `state` (addressed to `"all"` or to a player) is
dropped with a debug log — only the authority may publish state. Client-side hardening (§10)
protects updated kb-authority clients, but raw-SDK and not-yet-updated Godot clients would
otherwise remain forgeable, and enforcing rules where clients can't tamper is this mode's entire
premise. Non-`_kb` client-to-client chatter (`to:"all"` cursors, emotes, chat) is untouched.

### 5e. New control-plane event: `LobbyClosed`

```csharp
public sealed record LobbyClosedMessage(string LobbyId, string Reason) : IMessage;  // + [JsonDerivedType]
```

Pushed to each member's **control** socket when the server closes a *live* lobby (the authority
fatal-failure path, §7). Today's `CloseLobbyIfDark` never needed this because it only fires when
nobody is connected. The shell handles it by returning home with the reason; game sockets are
aborted.

### 5f. New events: `OwnerChanged` / `GameOwnerChanged`

```csharp
public sealed record OwnerChangedMessage(string LobbyId, string OwnerId) : IMessage;   // control plane
public sealed record GameOwnerChangedMessage(string OwnerId) : IMessage;               // data-plane mirror
```

Pushed to all members when `kb.setOwner` succeeds, following the existing dual-plane pattern
(`PlayerLeft` / `GamePlayerLeft`): the control event keeps the shell's roster UI honest, the
data-plane mirror lets SDKs update `ownerId`/`isOwner` live and fire an `onOwnerChanged` callback.
The server validates the target is a current lobby member before applying; an invalid target is a
contained module error (logged, ignored). Host-authoritative lobbies are untouched — nothing else
can move `HostId` in v1 (`kb.setOwner` is the only writer).

---

## 6. The server actor

Two new files under `KnockBox.Server/Games/`:

**`ServerAuthorityManager`** — DI singleton (registered with the others in `Program.cs`), a
`ConcurrentDictionary<string /*lobbyId*/, ServerAuthority>`:
- `bool TryStart(Lobby lobby, GameManifest manifest, out string? error)` — reads + loads the
  module, constructs the actor, starts its drain task. Enforces `AuthorityMaxLobbies`.
- `bool TryGet(string lobbyId, out ServerAuthority auth)` / `void Stop(string lobbyId)` /
  `void StopAll()` (hooked to `ApplicationStopping`, the existing timer-disposal pattern).

**`ServerAuthority`** — the per-lobby actor:

- **Inbound**: a bounded `Channel<AuthorityWork>` (`AuthorityQueueCapacity`) drained by **one**
  task — mandatory because neither a Jint `Engine` nor a WASM store is thread-safe, so every
  module call happens on the drain task. Overflow policy is **two-tier**: `IntentWork` uses
  `TryWrite` and is dropped with a warning when full (the data-plane policy — a lost intent is
  recoverable, the client resyncs), and `TickWork` is coalesced (never enqueued while one is
  already pending). **Roster work is never dropped**: `PlayerJoined/Left/Disconnected/Connected`
  are one-shot events whose loss would permanently desynchronize the module's roster view from
  real membership (owner succession that never fires; per-recipient projections computed for a
  stale roster) — the same reason the control plane uses `CloseOnFull` — so roster items are
  posted with `WriteAsync` (their rate is inherently low and already bounded by control-plane
  limits).
- **Work items** (sealed record hierarchy): `IntentWork(fromId, payloadJson)` — covers both
  `intent` and `sync`, discriminated by `_kb`; `PlayerJoinedWork(player)`; `PlayerLeftWork(id)`;
  `PlayerDisconnectedWork(id)`; `PlayerConnectedWork(id)`; `TickWork`.
- **The loop** (the KBAuthority host loop, server-side): `intent` → `applyIntent`; non-null patch
  → broadcast `delta` (or re-project per-player `state` in per-recipient mode). `sync` →
  `snapshot(fromId)` → `state` to the requester. Roster work → optional hook, then re-broadcast
  state to all (KBAuthority's "roster change → re-push" rule, which also covers late-join).
- **Tick**: a `Timer` posting `TickWork` exists **only** when the module exports `tick`; rate =
  `min(config.tickHz, AuthorityTickHzMax)`; `dtMs` computed from `TimeProvider` deltas on the
  drain task. A `null` tick result sends nothing.
- **Outbound**: builds `GameMessage{…, From:"server"}`, serializes once, fans out via
  `ConnectionManager.SendRawToGame` over the lobby's player snapshot. Payloads over
  `MaxMessageBytes` (512 KB — today enforced inbound-only; this outbound check is new code) are
  dropped with an error log.

**Lifecycle** — every path:

| Event | Site (today) | Action |
|---|---|---|
| Lobby created, manifest opts in | `HandleCreateLobby` | `TryStart`; on failure remove the just-created lobby and reply with an `Error` to the creator — loud, never a half-alive lobby, never silent downgrade to host mode. |
| Member joins / leaves / kicked / reaped | the seven sites that already broadcast the `GamePlayer*` roster mirrors (join, kick, reaper, leave, leave-others, disconnect, reconnect) | post the matching roster work item — one shared private helper so each site is a one-liner. |
| Lobby goes dark | `CloseLobbyIfDark` | `authorities.Stop(lobbyId)` immediately after `lobbies.Remove` (single chokepoint). |
| Authority fatal failure | new path (§7) | manager removes the lobby, broadcasts `LobbyClosed`, aborts members' game sockets, stops the actor. |
| Server shutdown | `ApplicationStopping` | `StopAll()` — dispose engines and timers. |

---

## 7. Error policy

| Failure | Classification | Response |
|---|---|---|
| Module throws inside one call (`applyIntent`, a hook, `tick`) | **Contained** — engine state is still consistent (the interpreter unwound one call) | Log under `KnockBox.Authority` with gameId/lobbyId/fromId context; drop the intent; **re-broadcast `snapshot()`** so all clients converge (guide §5: on an illegal intent, re-broadcast the *unchanged* state so the offending client re-syncs); increment a consecutive-failure counter. |
| 5 consecutive contained failures | Escalate | → fatal. |
| Constraint violation — memory limit, call timeout, statement/fuel overflow | **Fatal** — the engine may be mid-mutation; state is untrustworthy | Log error; broadcast `LobbyClosedMessage(lobbyId, "authority-failed")` on control sockets; abort members' game sockets; `lobbies.Remove`; dispose the actor. |
| Module load / `init` failure at lobby creation | Fatal at birth | `TryStart` fails → lobby creation fails with a clear `Error` to the creator. |

The bias: a buggy-but-recoverable module keeps the lobby alive and converged; anything that could
corrupt authoritative state kills the lobby loudly rather than limping.

Developer-experience note: in the Development environment, a contained failure is additionally
relayed to lobby members as a `{ "_kb": "error", "message": … }` debug frame, so a game developer
sees their `applyIntent` exception in the browser console instead of digging through server logs.
Production sends nothing (no internals leak to clients).

---

## 8. Configuration

New `AuthorityOptions` record (`ServerLimits.FromConfiguration` pattern), all under the
`KnockBox:` prefix (env: `KnockBox__Key`). Document in INFRASTRUCTURE.md §9 when built.

| Key | Default | Meaning |
|---|---|---|
| `AuthorityEnabled` | `true` | Master switch. When `false`, creating a lobby for a `serverAuthority` game fails with a clear error (no silent host-mode downgrade). |
| `AuthorityMaxMemoryBytes` | 33554432 (32 MB) | Per-engine memory limit (Jint `LimitMemory` / WASM store limit). |
| `AuthorityCallTimeoutMs` | 250 | Wall-clock budget per module invocation. Wall-clock is a blunt fatal trigger (a GC pause or thread-pool stall inside the call counts against it), so the default leaves headroom; `AuthorityMaxStatements` is the deterministic runaway guard. |
| `AuthorityMaxStatements` | 1000000 | Statement/fuel budget per invocation. |
| `AuthorityRecursionLimit` | 64 | Call-depth limit (Jint). |
| `AuthorityTickHzMax` | 20 | Clamp on a module's requested `config.tickHz`. |
| `AuthorityMaxScriptBytes` | 1048576 (1 MB) | Max module file size (checked at discovery and load). |
| `AuthorityMaxWordFileBytes` | 33554432 (32 MB) | Max size of a single `authorityWords` dictionary file (checked at discovery). Larger than the module cap because dictionaries are the big blobs; they live on the shared CLR heap, not in a per-invocation budget. |
| `AuthorityQueueCapacity` | 256 | Actor inbound channel bound (two-tier: intents drop-oldest, ticks coalesce, roster work never dropped — §6). |
| `AuthorityMaxLobbies` | 100 | Cap on concurrent server-authority lobbies (`0` = unlimited). |

CPU fairness note: each call is budgeted, ticks are clamped, and `AuthorityMaxLobbies` bounds the
aggregate — that is the v1 answer to a hot module; a shared scheduler is future work.

Memory honesty note: Jint's `LimitMemory` is a **per-invocation allocation budget** (per-thread
`GC.GetAllocatedBytesForCurrentThread`, re-baselined each call; the check self-skips if a call
migrates threads). It does **not** cap what an engine *retains* across calls — a leaky module can
grow its heap by up to the budget every invocation. The v1 threat model makes this acceptable:
authority modules are **operator-installed** (dropped into `games/` by whoever runs the server),
so the sandbox is defense-in-depth against buggy or compromised games, not against arbitrary
hostile uploads. Partial backstops that exist anyway: an oversized `snapshot()` fails the actor's
outbound size check, and `AuthorityMaxLobbies` bounds the blast radius. Sizing note: 100 lobbies ×
32 MB is a ~3.2 GB theoretical per-call ceiling — lower both knobs on small hosts.

Steady-state footprint (as-built): each lobby holds one long-lived engine, so RSS scales with
concurrent authority lobbies. Two mitigations keep the per-lobby cost down:
- **Shared parsed module** (`Games/AuthorityModuleCache.cs`): a game's `authority.js` is parsed once
  via `Engine.PrepareModule` (Jint documents the result as reusable + thread-safe) and the shared
  prepared module is registered on each lobby engine with `ModuleBuilder.AddModule`, keyed by file
  path with mtime/length freshness. So N lobbies of one game share a single parsed AST instead of
  re-reading and re-parsing per lobby. The cache is pruned on `GameCatalog.Discovered` (like the word
  service) so a removed game's parsed AST doesn't linger for the process lifetime. The per-engine
  **realm baseline** (ECMAScript intrinsics) still can't be shared for isolated untrusted state and
  dominates when many lobbies run.
- **GC footprint** (`KnockBox.Server.csproj`): Server GC stays (relay throughput) with **DATAS**
  (heap-count adaptation, on by default since .NET 8) doing the footprint work — it grows/shrinks
  heaps with load, so no fixed `System.GC.HeapCount` is set (that would disable DATAS). Only
  `System.GC.ConserveMemory=5` is embedded at publish (honored under AOT), plus a container `mem_limit`
  so DATAS/GC size to the cgroup budget. `KnockBox:MemoryLogSeconds` logs working set / heap /
  lobby+actor counts to measure and verify all of the above.

---

## 9. Key flows

**Intent (happy path)**

```
GuestB ──Game{to:"host", {_kb:'intent',action}}──► relay: authority lobby? yes
    ──PostIntent(B, rawJson)──► actor drain task:
         parse → applyIntent("B", action) → patch
         patch != null → GameMessage{to:"all", {_kb:'delta',patch}, from:"server"}
    ──SendRawToGame(each member)──► every client (incl. B and the owner) applies the patch, renders
```

**Late join**

```
C joins (control) → roster broadcasts + GamePlayerJoined  → actor: onPlayerJoined?(C); re-broadcast state
C attaches (data) → Ready{isHost:false, authority:"server", ownerId}
C (KBAuthority guest branch) → {_kb:'sync'} to "host" → actor → snapshot(C) → {_kb:'state'} to C
   (idempotent absolute state: converges even if C also saw the join-time broadcast)
```

**Reconnect within grace**

```
C's control socket drops → grace flag → GamePlayerDisconnected to peers → actor: onPlayerDisconnected?(C)
C returns → rejoin → AnnounceConnected → actor: onPlayerConnected?(C)
C's data socket re-attaches → Ready → sync → snapshot(C)   (same as late join; nothing special)
```

**Creator leaves for good — the headline win**

```
Owner leaves (explicit or grace elapses) → removed from roster → PlayerLeft broadcasts
    → actor: onPlayerLeft(owner); re-broadcast state
Lobby still has connected members → CloseLobbyIfDark is a no-op → THE GAME CONTINUES
    module may promote a successor in onPlayerLeft → kb.setOwner(nextId)
        → HostId reassigned → OwnerChanged/GameOwnerChanged to all → clients' isOwner updates
    (a module that doesn't: lobby runs owner-less; kb.setLobbyOpen still available to the module)
Last connected member leaves → CloseLobbyIfDark → lobbies.Remove → actor stopped, engine disposed
```

**Module failure** — per §7: contained throw → drop + resync; constraint violation → lobby closed
with `LobbyClosed{reason:"authority-failed"}`.

---

## 10. SDK impact (small, all additive)

- **`web/kb-core.js`**: pure `normalizeReady(msg)` helper → `{ playerId, players, isHost,
  authority, ownerId, isOwner }` with old-server fallbacks (`authority ?? 'host'`,
  `ownerId ?? (isHost ? playerId : null)`). Lives in kb-core because it's pure and Vitest-tested.
- **`web/knockbox.js`**: use `normalizeReady` in the `Ready` case; expose `authority`, `ownerId`,
  `isOwner` as properties and in the `onReady` payload. **No send-path changes** — `sendToHost`
  already routes to the authority.
- **`clients/phaser/knockbox-plugin.js`**: mirror the same three fields/events.
- **`clients/phaser/knockbox-local.js`**: the `authority:` option — the local authority emulation
  (§12a) with its default-on fidelity checks (JSON round-trip boundary, `Date` poisoning,
  URL-form import scan). Local `ready` surfaces `authority`/`ownerId` for parity with the
  server-mode `Ready`.
- **Both SDKs**: handle `GameOwnerChanged` — update `ownerId`/`isOwner` and fire
  `onOwnerChanged(ownerId)` (web) / emit `'owner-changed'` (Phaser) so owner-gated UI re-renders;
  the shell handles the control-plane `OwnerChanged` for its roster display.
- **`clients/phaser/kb-authority.js`**: correct **unchanged** in server mode (everyone is a guest:
  auto-sync on ready, adopt deltas/snapshots, host branches never fire). One hardening while
  here: when `net.authority === 'server'`, ignore `_kb` `delta`/`state` frames whose
  `from !== 'server'` — closing the today-possible forgery where any guest can broadcast fake
  state. Godot addon gets the same treatment as a parity follow-up.
- **Raw-SDK edge cases** (to document in the guide):
  - `isHost` is `false` on every client in server mode — don't branch on it; the authority
    branch of your game *is* the authority module now.
  - Gate owner UI (kick, open/close) on the new `isOwner`, not `isHost`. `setLobbyOpen`/
    `kickPlayer` remain server-enforced for the owner only.
  - `sendTo(ownerId)` is an ordinary direct message to the creator's client — the routing target
    `"host"` and the owner player are different things in server mode.
  - Only `_kb`-envelope payloads sent to `"host"` are consumed; anything else is dropped.

---

## 11. Hot reload & deployment

The actor reads and compiles the module **once at lobby creation** and holds it for the lobby's
lifetime — a `GameCatalog` rescan (file change, poll tick) never touches live actors; new lobbies
pick up the new file via the normal `catalog.TryGet` at creation. A manifest that drops
`serverAuthority` mid-flight leaves running lobbies in server mode until they close (lobbies are
short-lived; acceptable, documented). `authority.js`/`.wasm` is just another asset in the game
folder: the read-only `games/` Docker mount, the precompressor, and hosting layout need no
changes — with one exception. **Required: the game origin must not serve the authority module
file.** It is server-side code, not a client asset — and for hidden-information games (§14.5) its
secrecy is the whole point. Today the game origin serves *everything* in the game folder
(`GAME.json` included: `ServeUnknownFileTypes = true`; only the shell origin gates `/games/*` to
the declared thumbnail), so there is no existing exclusion to reuse. Phase 1 adds a deny rule on
the game-origin pipeline for the manifest's `serverAuthority` path, with a test
(`GET /games/<id>/authority.js` → 404), and the precompressor skips the file (no point warming
variants of an asset that is never served). Excluding `GAME.json` the same way is an optional
tidy-up — a separate decision, since nothing secret lives in it today.

The same deny + precompressor-skip applies to every `authorityWords` dictionary file
(`GameOriginAssetGate.IsDeniedAuthorityAsset` covers both the module and the word files, plus their
`.br`/`.gz` variants): the words are server-side data the client never needs, and for a
hidden-information word game the answer list is exactly the secret. The local dev loop (§12a) has no
such deny — the developer's own static server serves the file so `knockbox-local.js` can fetch it to
emulate `kb.words`.

---

## 12. Testing strategy & local developer loop

### 12a. Local developer loop (no server required)

A game developer must be able to iterate on an authority module **without pulling this repo or
deploying a server** — and the local flow must exercise real message paths, so logic doesn't only
work because everything shared one JS object. Three tiers, cheapest first; only the first two are
needed for iteration.

**Tier 1 — pure module tests (Vitest).** The fastest loop, and why the ABI is output-only (§3):
import `createAuthority(fakeKb)`, feed intents, assert patches/snapshots. `fakeKb` is ~10 lines
(controllable `now`, recording `log`/`setOwner`/`setLobbyOpen`) — shown in the guide (Phase 3).

**Tier 2 — local authority emulation (`knockbox-local.js`).** The headline: the existing local
peer gains an `authority:` option that runs the developer's **actual `authority.js`** as a virtual
server actor over the existing transports — `tab` (multi-tab play: every frame crosses a real
`BroadcastChannel` between browsing contexts) and `process` (headless automated tests;
`KnockBoxLocalPeer` is Phaser-free).

```js
// dev config — same plugin swap as today, plus the module:
import { createAuthority } from './authority.js';
data: { mode: 'tab', authority: createAuthority }      // or authority: './authority.js' (URL form)
```

- The elected local host peer becomes the **actor host**: it instantiates the module and runs the
  §6 loop — `init(roster)`; `intent` → `applyIntent` → `delta` broadcast (per-recipient →
  re-projection); `sync` → `snapshot(fromId)` → `state`; roster events → hooks + re-broadcast;
  a `config.tickHz` tick timer.
- **Every** peer — including the actor host's own game — gets `ready` with `isHost:false`,
  `authority:'server'`, `ownerId` = the elected peer, and actor deliveries are stamped
  `from:'server'` — so client code runs the byte-identical server-mode path.
- **Relay rules are mirrored** (§5a/§5d): client-sent `_kb` `delta`/`state` and non-`_kb`
  payloads to `"host"` are dropped with a console warning — a game can't accidentally depend on
  something the real relay forbids.
- **Emulated `kb`**: `now()` → `Date.now`; `log.*` → console; `setLobbyOpen` → logged no-op;
  `setOwner` → updates `ownerId` and emits the owner-changed event locally.
- **Fidelity checks, default on** (the `devChecks` precedent):
  - *JSON round-trip boundary* — every value crossing into or out of the module goes through
    `JSON.stringify` → `parse`, mirroring the server's strings-of-JSON boundary (§3a). Functions,
    `undefined`, cycles, and class instances **throw locally** instead of breaking only on the
    server — applied on all transports, so even `process` mode gets serialization realism despite
    its in-memory hub.
  - *`Date` poisoning* — around each (synchronous) module invocation, `globalThis.Date` is
    swapped for a throwing stub and restored, catching modules that reach for ambient time (the
    server deletes `Date`, §3b). `Math.random` is not poisoned — allowed in v1 (§1).
  - The URL form of `authority:` fetches the module source first and runs the packer's
    import-scan (§4) before dynamic-importing — catching the single-file rule in the browser,
    where a relative `import` would otherwise happily resolve.
- Scope: lives in the Phaser package. **Plain web-SDK games** (tictactoe-style) accept the gap in
  v1 — they use Tier 1 + Tier 3 (their documented workflow today, guide §11); a web-SDK loopback
  and a Godot `kb_local_relay.gd` authority mode are later parity follow-ups.

**Tier 3 — a real server (optional, full fidelity).** Real Jint sandbox, constraint limits, real
WebSockets, ticket/`Ready` flow: drop the game into `games/` of a local instance — the published
self-contained desktop exe, or `dotnet run` for repo contributors. Explicitly **not** required
for iteration.

What only Tier 3 catches, and why that's acceptable:

| Gap in Tiers 1–2 | Mitigation |
|---|---|
| Jint interpreter quirks vs. the browser's JS engine | Rare in pure-JSON logic code; surfaces loudly (module load/`init` failure fails lobby creation, §7). |
| Constraint limits (timeout / memory / statements) not emulated | §8 knobs are generous for honest modules; violation is a loud `LobbyClosed`, not silent corruption. |
| Single-file rule (browser resolves relative `import`s the server rejects) | pack-game static scan (§4) + the URL-form scan above + `TryStart` failing loudly — never a silent downgrade (§4). |

### 12b. Test suites

**xUnit (`KnockBox.Server.Tests`, reusing the fake-socket/`MutableTimeProvider` helpers):**
1. Catalog: valid `serverAuthority` accepted; traversal (`../x.js`), missing file, oversize file
   each skip the game.
2. Actor unit tests (inline module source, real Jint): intent → delta with `from:"server"` to all
   members; rejected intent → no send; sync → state to requester only; per-recipient → distinct
   projections; roster join → re-broadcast; `tick` exported → periodic patches, absent → no timer.
3. Error policy: throwing `applyIntent` → intent dropped, snapshot re-broadcast, lobby alive;
   infinite loop (timeout) and memory bomb → lobby closed, actor disposed.
4. Flow tests through the real `WebSocketHandler`: Ready carries `authority:"server"`,
   `isHost:false` even for the creator, `ownerId` set; `to:"host"` reaches the module, not the
   creator's socket; **owner leaves → lobby survives and intents still answered**; last member
   leaves → actor disposed; `AuthorityEnabled=false` → creation errors; owner powers
   (`SetLobbyOpen`/`KickPlayer`) still honored from the creator and refused from guests;
   `kb.setOwner` → `HostId` reassigned, `OwnerChanged`/`GameOwnerChanged` broadcast, owner powers
   honored from the new owner and refused from the old one, non-member target rejected (contained
   error, no change); client-sent `_kb` `delta`/`state` to `"all"` dropped (forgery enforcement,
   §5d); `GET` of the authority file on the game origin → 404 (§11).

**Vitest:** `normalizeReady` (defaults, server mode, old-server fallback); kb-authority server
mode (sync on ready, host branch never fires, forged non-`"server"` state frames ignored); the
sample game's `authority.js` tested pure (feed intents, assert patches) — doubling as the doc
example. Local authority emulation (§12a, over the `process` transport): full actor loop —
intent → delta stamped `from:'server'` to all peers, every peer `ready` with `isHost:false` /
`authority:'server'` / `ownerId`; JSON-boundary check throws on a function-carrying patch; `Date`
poison throws inside `applyIntent`; forged client `_kb` `state` dropped; `setOwner` emulation
updates `ownerId` and fires the owner-changed event. pack-game: `serverAuthority`
traversal/missing/oversize rejected; import-scan rejects a module with a relative `import`; load
check rejects a module without `createAuthority`.

**Manual E2E:** dev server, two browsers, sample game: play; owner closes the tab mid-game (game
continues for the guest and a fresh joiner); reconnect within grace; `AuthorityEnabled=false` kill
switch.

**Sample game:** `games/tictactoe-server/` — `GAME.json` with `serverAuthority`, `authority.js`
(the model), and a render-only client. Port of the existing tictactoe sample.

---

## 13. Implementation phasing (each independently shippable)

| Phase | Contents | Why this order |
|---|---|---|
| **0 — AOT spike** | Add Jint + `TrimmerRootAssembly`, one trivial sandboxed-engine xUnit test, run the `aot` CI publish (`/warnaserror`) locally and on a branch. The spike test also pins the sandbox facts: constraints re-arm across two invocations (§3b), `Date` is gone after removal (§3b), and a relative `import` fails with no loader (§14.2). | Kills the project's biggest unknown before any design code. Fallback if ILxxxx warnings appear: targeted suppression with an evidence comment (the Serilog IL2104 precedent) — sound because interop is `JsValue`-only with CLR access disabled. Keeping all Jint usage inside `JsAuthorityRuntime` also preserves a clean swap point. |
| **1 — server core** | `GameManifest.ServerAuthority` + catalog validation; `AuthorityOptions`; `IAuthorityRuntime` + `JsAuthorityRuntime`; actor + manager + lifecycle wiring; relay divert + client-sent `_kb` `delta`/`state` drop (§5d); game-origin deny rule for the authority file + 404 test (§11); `Ready` fields; `kb.setOwner` + mutable `HostId` + `OwnerChanged`/`GameOwnerChanged`; xUnit suites. | Raw-SDK games speaking `_kb` work end-to-end with **zero client-file changes**. |
| **2 — SDK + sample + failure UX** | `normalizeReady` + SDK surfacing (`authority`/`ownerId`/`isOwner`); kb-authority hardening + server-mode tests; local authority emulation in `knockbox-local.js` (`authority:` option + fidelity checks, §12a) + Vitest suite; pack-game `serverAuthority` validation + import scan (§4); `LobbyClosedMessage` + shell handling; `games/tictactoe-server`; Vitest suites. | Developer-facing polish once the core is proven — the local loop ships with the first SDK release so game devs never need a server to iterate. |
| **3 — docs** | GAME_DEVELOPER_GUIDE §5b (manifest, module contract, `_kb` protocol, `kb` object incl. the `setOwner` succession pattern, limits, error semantics; the **split-file pattern** for hidden-information games — shared rules module + thin server-only `authority.js` holding secret projection; the three-tier local-testing workflow incl. the `fakeKb` snippet, §12a; revisit §8's "host migration is not provided"); INFRASTRUCTURE (§1 principle 4 caveat, §9 knobs, soften the "intentionally not built" list — anti-cheat and host-departure are now addressed *for opted-in games*); CLAUDE.md blurb. Godot parity follow-ups tracked together: kb_authority.gd forgery hardening (§10) + a `kb_local_relay.gd` authority mode (§12a). | |
| **4 — WASM backend (later)** | `WasmAuthorityRuntime` (Extism or wasmtime-dotnet), `.wasm` manifest support, RID packaging for Docker + desktop publish, AOT verification, a non-JS sample. | Additive behind `IAuthorityRuntime`; nothing in phases 0–3 needs rework. |

---

## 14. Decisions & remaining risks

The originally-open questions were reviewed and decided (2026-07-10):

1. **Jint AOT fallback — decided: targeted suppression.** If the Phase 0 spike surfaces Jint
   trim/AOT warnings, suppress only the specific ILxxxx codes with an evidence comment proving the
   flagged path is unreachable (CLR interop off, `JsValue`-only boundary) — the Serilog IL2104
   precedent. *Remaining risk*: the spike itself is still to be run; it stays the first task.
2. **Module imports — decided: single-file only in v1.** No module loader is configured, so any
   `import` inside `authority.js` fails (that *is* the sandbox). Devs with multi-file logic bundle
   (esbuild/rollup). A game-folder-restricted loader can be added later without breaking anything.
   *Verify in the spike*: an `import` really does fail with no loader configured.
3. **Constraint re-arming — decided: rely on Jint's automatic per-invocation re-arm, pinned by a
   test.** There is no public whole-engine reset (`Engine.ResetConstraints()` is private, called
   inside `ExecuteWithConstraints`); the public surface is per-constraint
   `Constraints.Find<T>()?.Reset()`. A security boundary shouldn't depend on library semantics
   staying stable across upgrades, so a unit test asserts the re-arm (two sequential budgeted
   invocations each get a fresh budget) and fails loudly on any Jint upgrade that changes it — the
   per-constraint reset loop is the ready fallback.
4. **Owner migration — decided: platform primitive, game policy.** Succession is the game
   developer's problem; the platform ships the tool: `kb.setOwner(playerId)` (§3), mutable
   `HostId`, and the `OwnerChanged`/`GameOwnerChanged` events (§5f). No server-side auto-promotion
   fallback and no owner-initiated transfer command in v1 — a module that never calls `setOwner`
   runs owner-less after the creator departs, which is allowed and documented.
5. **Hidden-information games — decided: document the split-file pattern.** The guide (Phase 3)
   shows a shared rules module composed by a thin server-only `authority.js` that holds the secret
   projection; secrecy is real because the authority module is never served to clients (§11).
6. **WASM packaging — decided: both targets, verified in Phase 4.** The WASM backend must work in
   the Docker linux-x64 image *and* the win-x64 single-exe desktop publish (native lib bundled via
   self-extract). Feature parity between the two deployment modes is a platform goal; Phase 4
   verifies the mechanics before shipping.
7. **Local testing — decided: three tiers, emulation in `knockbox-local.js`, real server
   optional (§12a).** A developer must be able to iterate with **no server at all** — pulling the
   repo or deploying an instance is not acceptable for the inner loop. Tier 1 is pure Vitest
   module tests; Tier 2 runs the real `authority.js` as a virtual `from:"server"` actor over the
   existing local transports with default-on fidelity checks (JSON round-trip boundary, `Date`
   poisoning, import scan); Tier 3 (a real local instance — desktop exe or `dotnet run`) is
   optional full fidelity. Remaining gaps (Jint quirks, constraint limits) are accepted:
   pack-game static checks plus loud server-side failures (never silent downgrade) keep them from
   biting silently. Plain web-SDK games accept the Tier 2 gap in v1; a web loopback and Godot
   authority-mode relay are parity follow-ups.
