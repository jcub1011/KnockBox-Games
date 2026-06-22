# KnockBox — Godot 4 client addon

A small Godot 4 addon that lets a Godot **Web export** play on the KnockBox platform by
speaking the platform's data-socket protocol directly — the "native protocol" engine route
from [`docs/GAME_DEVELOPER_GUIDE.md` §10](../../docs/GAME_DEVELOPER_GUIDE.md). It is the
GDScript analogue of the reference JS SDK (`web/knockbox.js`): it reads a lobby-scoped
ticket from the page URL fragment, opens its own WebSocket, attaches, and exposes
send/receive over Godot **signals**.

You never see a lobby id, the player's identity, or the shell — the server routes
everything from your connection. Just send and receive.

## Install

1. Copy the `addons/knockbox/` folder into your Godot project (so you have
   `res://addons/knockbox/`).
2. **Project → Project Settings → Plugins** → enable **KnockBox**. This registers a
   `KnockBox` autoload singleton, available from any script.
3. Export your game for **Web** and drop the build into the platform's `games/<your-id>/`
   next to a `GAME.json`. For a single-threaded export leave `"crossOriginIsolated": false`;
   set it `true` only for a threaded export (see Game Developer Guide §10).

## API

`KnockBox` is the autoload. Properties below are populated once `session_ready` fires.

### Signals

| Signal | Fires when | Arguments |
|---|---|---|
| `session_ready(player_id, players, is_host)` | The data socket attached and the server handed you identity + roster. **Start here.** | `String`, `Array`, `bool` |
| `message_received(from_id, payload)` | A relayed message arrives for you. | `String`, `Variant` |
| `player_joined(player)` | A player joins the lobby. | `Dictionary` `{ id, displayName }` |
| `player_left(player_id)` | A player leaves/disconnects. | `String` |
| `closed(terminal)` | The socket closed. `terminal == true` means the ticket/membership is gone (no reconnect). | `bool` |

### Properties

| Property | Type | Meaning |
|---|---|---|
| `KnockBox.player_id` | `String` | Your player's id this session. |
| `KnockBox.players` | `Array` | Everyone in the lobby, each `{ "id", "displayName" }`. **Order is stable and shared** — index 0 is the host. |
| `KnockBox.is_host` | `bool` | True if *you* are the authoritative host. |

> Roster dictionaries keep the **wire keys** (`id`, `displayName`), matching the JS SDK and
> the server contract.

### Sending

| Method | Sends `payload` to |
|---|---|
| `KnockBox.send_to_host(payload)` | The authoritative host (use for **intent**). |
| `KnockBox.send_to_all(payload)` | Everyone, including you (use by the host for **state**). |
| `KnockBox.send_to(player_id, payload)` | One specific player (use for **hidden info**). |
| `KnockBox.set_lobby_open(open)` | **Host-only.** Open (listed + joinable) or close the lobby to new joins. The game owns this; the server never changes it. |

`payload` is any JSON-serializable value you define. The server stamps the sender; you
receive it as `message_received(from_id, payload)`.

## Example (host-authoritative)

KnockBox uses host-client authority: one player (`is_host == true`) owns the state;
everyone else holds a render copy. Guests send **intent** to the host; the host validates,
updates, and broadcasts **state** to all. See Game Developer Guide §5.

```gdscript
extends Node

func _ready() -> void:
    KnockBox.session_ready.connect(_on_ready)
    KnockBox.message_received.connect(_on_message)

func _on_ready(player_id: String, players: Array, is_host: bool) -> void:
    if is_host:
        _broadcast_state()                       # seed everyone
    else:
        KnockBox.send_to_host({"kind": "sync"})  # ask for current state on (re)entry

# e.g. from a button press:
func _on_cell_pressed(cell: int) -> void:
    KnockBox.send_to_host({"kind": "move", "cell": cell})

func _on_message(from_id: String, payload) -> void:
    if payload.get("kind") == "state":
        _adopt_state(payload)                    # everyone renders from authoritative state
        return
    if not KnockBox.is_host:
        return                                   # only the host acts on intents
    if payload.get("kind") == "move":
        _apply_move(from_id, payload["cell"])    # validate + mutate
    _broadcast_state()                           # always re-broadcast (even on illegal move)

func _broadcast_state() -> void:
    KnockBox.send_to_all({"kind": "state", "board": _board, "next": _next, "winner": _winner})
```

Keep state messages **self-contained** (the full snapshot) so late joins and reconnects
just work — the SDK re-attaches with the same ticket on a transient drop and fires
`session_ready` again.

## Less boilerplate: `KBNet` and `KBAuthority`

The example above wires the `KnockBox` autoload directly. Two optional layers remove the
repetitive parts (register `KBNet` as a second autoload named `Net`):

- **`KBNet`** (`kb_net.gd`) mirrors the `KnockBox` API but **runs a single-player loopback in the
  editor/native** — press Play and you have a solo host session with no server or ticket. On web it
  forwards the real transport unchanged. Adds a `reconnected` flag and a `resumed` signal. Develop
  against `Net` and the same code runs solo in-editor and live on the web.

- **`KBAuthority`** (`kb_authority.gd`) implements the entire host-authoritative loop (guest sync,
  host broadcast, late-join + reconnect re-sync) against a small **model** you write:

  ```
  apply_intent(from_id, action) -> Variant   # host only: mutate, return a patch to broadcast (or null)
  apply_patch(patch) -> void                 # every client applies a delta
  snapshot() -> Dictionary                   # full state for sync/join/reconnect
  apply_snapshot(state) -> void              # every client adopts a snapshot
  ```

  ```gdscript
  var authority := KBAuthority.new()
  add_child(authority)
  authority.setup(Net, my_model)               # Net = the KBNet autoload
  authority.state_changed.connect(_render)     # re-render from the model
  authority.send_intent({"kind": "move", "cell": cell})
  ```

  The whole host-authoritative example above collapses to: write the model, call `setup`, render on
  `state_changed`. See **Game Developer Guide §10b** for a full GDScript Tic-Tac-Toe.

## Testing outside a web export

There is no URL fragment in the editor or a native build. With the `Net` (`KBNet`) autoload you
don't need one — it runs a **solo loopback** automatically, so just press Play. To instead connect
a native/editor build to a **real** server, supply credentials:

```gdscript
Net.connect_with(my_ticket, "ws://localhost:5115/ws")   # or, on the raw transport:
KnockBox.set_launch_params(my_ticket, "ws://localhost:5115/ws")
```

Obtain `my_ticket` by driving the shell to an `EnterGame` and calling `RequestTicket`
(see the control-plane flow in `docs/INFRASTRUCTURE.md`).

### Headless unit tests

The pure helpers in `kb_core.gd` have a headless test mirroring the JS suite. From
`clients/godot/`:

```sh
godot --headless --script res://test_kb_core.gd
```

It exits non-zero on any failure.
