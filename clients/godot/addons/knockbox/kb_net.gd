## KBNet — the transport façade a game talks to.
##
## On a web export it forwards the `KnockBox` autoload (the real netcode, unchanged). In
## the editor / native it runs a built-in single-player LOOPBACK: a synthetic solo host
## whose sends route straight back to itself, so the whole game runs with no server and no
## ticket — press Play and you're "in a session". Mirrors KnockBox's signals and send API
## and adds a `reconnected` flag, so game code is identical in both modes.
##
## Use it as an autoload (add `Net="*res://addons/knockbox/kb_net.gd"` to project settings)
## or instance it yourself. For native testing against a REAL server, call
## `connect_with(ticket, endpoint)` instead of using the loopback default.
class_name KBNet
extends Node

signal session_ready(player_id: String, players: Array, is_host: bool)
signal message_received(from_id: String, payload)
signal player_joined(player: Dictionary)
signal player_left(player_id: String)
signal closed(terminal: bool)
signal resumed

var player_id: String = ""
var players: Array = []
var is_host: bool = false
var reconnected: bool = false
var loopback: bool = false

# The `KnockBox` autoload, resolved at runtime by node path rather than its global
# identifier — so this script compiles even where that autoload name isn't registered
# (tooling, `--script` runs) and isn't hard-coupled to the singleton name.
var _kb: Node = null

# Set when joined to an in-process KBLocalRelay for multiplayer editor/testing (see connect_local).
var _relay = null


func _ready() -> void:
	if OS.has_feature("web"):
		_bind_knockbox()
	else:
		loopback = true
		# Defer so listeners connect before the synthetic session fires.
		_start_loopback.call_deferred()


func _bind_knockbox() -> void:
	loopback = false
	if _kb != null:
		return
	_kb = get_node_or_null("/root/KnockBox")
	if _kb == null:
		push_error("[KBNet] KnockBox autoload not found — is the addon enabled?")
		return
	_kb.session_ready.connect(_on_ready)
	_kb.message_received.connect(func(f, p): message_received.emit(f, p))
	_kb.player_joined.connect(_on_joined)
	_kb.player_left.connect(_on_left)
	_kb.closed.connect(func(t): closed.emit(t))
	_kb.resumed.connect(func(): resumed.emit())


## Opt into the REAL server in a native/editor run (instead of the loopback): supply a
## lobby ticket and optional ws(s):// endpoint. Call this at startup, before the deferred
## loopback would otherwise fire.
func connect_with(ticket: String, endpoint := "") -> void:
	_bind_knockbox()
	if _kb != null:
		_kb.set_launch_params(ticket, endpoint)


## Join an in-process KBLocalRelay (see kb_local_relay.gd) for solo multiplayer testing: multiple
## KBNet peers share one relay and message each other with no server. Call at startup (it suppresses
## the default solo loopback). Connect this peer's signals BEFORE calling so session_ready isn't missed.
func connect_local(relay, p_player_id: String, display_name := "", is_host_hint := false) -> void:
	loopback = true
	_relay = relay
	player_id = p_player_id
	relay.register(self, p_player_id, display_name, is_host_hint)


func _on_ready(pid: String, pl: Array, ih: bool) -> void:
	player_id = pid
	players = pl
	is_host = ih
	reconnected = _kb.reconnected
	session_ready.emit(pid, pl, ih)
	if reconnected:
		resumed.emit()


func _on_joined(player: Dictionary) -> void:
	players = _kb.players  # the addon reconciled its roster before emitting
	player_joined.emit(player)


func _on_left(pid: String) -> void:
	players = _kb.players
	player_left.emit(pid)


# ── Send API (mirrors the addon) ──

func send_to_host(payload) -> void:
	if loopback:
		if _relay != null:
			_relay.deliver("host", player_id, payload)
		else:
			_loop(payload)
	else:
		_kb.send_to_host(payload)


func send_to_all(payload) -> void:
	if loopback:
		if _relay != null:
			_relay.deliver("all", player_id, payload)
		else:
			_loop(payload)
	else:
		_kb.send_to_all(payload)


func send_to(target_id: String, payload) -> void:
	if loopback:
		if _relay != null:
			_relay.deliver(target_id, player_id, payload)
		elif target_id == player_id:
			_loop(payload)
	else:
		_kb.send_to(target_id, payload)


## Host-only: set whether the lobby accepts new joins. No-op in the editor loopback (no server).
func set_lobby_open(open: bool) -> void:
	if not loopback and _kb != null:
		_kb.set_lobby_open(open)


## Host-only: remove a player from the lobby. Forwards to the relay in a local multiplayer loopback
## (see connect_local); no-op in the solo loopback.
func kick_player(player_id_: String) -> void:
	if loopback:
		if _relay != null:
			_relay.kick(player_id, player_id_)
	elif _kb != null:
		_kb.kick_player(player_id_)


# In loopback every send comes from, and is delivered to, the lone local player. Deferred
# to avoid re-entrancy while a message is still being handled.
func _loop(payload) -> void:
	message_received.emit.call_deferred(player_id, payload)


func _start_loopback() -> void:
	if not loopback or _relay != null:
		return  # connect_with() opted into the real server, or connect_local() joined a relay
	player_id = "local-player"
	players = [{"id": "local-player", "displayName": "You"}]
	is_host = true
	reconnected = false
	session_ready.emit(player_id, players, is_host)


# ── KBLocalRelay callbacks (invoked by the relay; see kb_local_relay.gd) ──
func _lr_ready(roster: Array, is_host_: bool) -> void:
	players = roster
	is_host = is_host_
	reconnected = false
	session_ready.emit(player_id, players, is_host)


func _lr_deliver(from_id: String, payload) -> void:
	message_received.emit(from_id, payload)


func _lr_joined(roster: Array, player: Dictionary) -> void:
	players = roster
	player_joined.emit(player)


func _lr_left(roster: Array, left_id: String) -> void:
	players = roster
	player_left.emit(left_id)


func _lr_closed() -> void:
	closed.emit(true)
