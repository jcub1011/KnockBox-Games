## KnockBox Networking API — the game's "just send/receive over a websocket" client.
##
## A Godot 4 port of the reference vanilla-JS SDK (`web/knockbox.js`). Registered as the
## `KnockBox` autoload singleton by `plugin.gd`. When the game runs as a web export inside
## the KnockBox iframe it reads a lobby-scoped ticket + endpoint from its own URL FRAGMENT
## (the shell put them there; the fragment, unlike a query string, never leaks via Referer
## or server logs), opens its OWN websocket to the server, authenticates with the ticket,
## and exposes a tiny signal-based API. The game never sees a lobby id, the player's
## identity, or the shell — the server resolves all routing from the bound connection.
##
##   KnockBox.session_ready.connect(func(player_id, players, is_host): ...)
##   KnockBox.message_received.connect(func(from_id, payload): ...)
##   KnockBox.player_joined.connect(func(player): ...)
##   KnockBox.player_left.connect(func(player_id): ...)
##   KnockBox.send_to_host(payload)        # -> the authoritative host (intent)
##   KnockBox.send_to_all(payload)         # -> everyone incl. self (state)
##   KnockBox.send_to(player_id, payload)  # -> one specific player
##
## After `session_ready` fires, `player_id` / `players` / `is_host` are populated.
##
## Outside a web export (editor / native), there is no URL fragment, so call
## `set_launch_params(ticket, endpoint)` to attach — handy for local testing.
extends Node

const KBCore := preload("res://addons/knockbox/kb_core.gd")

## The data socket attached and the server handed us identity + roster. Start here.
## (Named `session_ready` to avoid clashing with the built-in `Node.ready` signal.)
signal session_ready(player_id: String, players: Array, is_host: bool)

## A relayed message arrived for us. `payload` is whatever the sender passed.
signal message_received(from_id: String, payload)

## A player joined the lobby; `player` is { "id", "displayName" }.
signal player_joined(player: Dictionary)

## A player left/disconnected.
signal player_left(player_id: String)

## The socket closed. `terminal` is true when the ticket/membership is gone (close
## code 1008) and no further reconnects will be attempted.
signal closed(terminal: bool)

## Re-fired after a transient drop reconnects and the server re-issues `Ready` (in
## addition to `session_ready`). Lets a game re-sync state on a resume without conflating
## it with the first connect. `reconnected` is also true during the `session_ready` that
## accompanies it.
signal resumed

## Populated once `session_ready` fires.
var player_id: String = ""
var players: Array = []  # of { "id": String, "displayName": String }, order is stable; index 0 is host
var is_host: bool = false

## True for the `session_ready`/`resumed` that follows a reconnect (false on first connect).
var reconnected: bool = false

var _socket: WebSocketPeer = null
var _ticket := ""
var _endpoint := ""
var _attached := false  # have we sent Attach on the current socket?
var _attempt := 0       # consecutive failed/transient connects, for backoff
var _stopped := false   # set on a terminal close — don't reconnect
var _has_session := false  # has Ready ever fired? subsequent Readys are reconnects
var _pending: Array = []   # game frames queued before the socket was open, flushed on open


func _ready() -> void:
	# On web, the shell put our credentials in the page URL fragment. Read and attach
	# automatically, mirroring the JS SDK. Off-web there is no fragment — wait for a
	# set_launch_params() call instead.
	if OS.has_feature("web"):
		var hash_str := str(JavaScriptBridge.eval("window.location.hash", true))
		var launch := KBCore.parse_launch_params(hash_str)
		_ticket = launch["ticket"]
		_endpoint = launch["endpoint"]
		if _endpoint == "":
			var protocol := str(JavaScriptBridge.eval("window.location.protocol", true))
			var host := str(JavaScriptBridge.eval("window.location.host", true))
			_endpoint = KBCore.default_endpoint(protocol, host)
		if _ticket == "":
			push_error("[KnockBox] missing kbTicket — cannot attach.")
			set_process(false)
			return
		connect_to_server()
	else:
		# Idle until the game supplies credentials.
		set_process(false)


## Manual override for editor/native testing: supply the ticket (and optionally the
## ws(s):// endpoint) yourself, then we connect. No-op once connecting/connected.
func set_launch_params(ticket: String, endpoint := "") -> void:
	if _socket != null:
		return
	_ticket = ticket
	if endpoint != "":
		_endpoint = endpoint
	connect_to_server()


func connect_to_server() -> void:
	if _ticket == "":
		push_error("[KnockBox] missing ticket — cannot attach.")
		return
	if _endpoint == "":
		push_error("[KnockBox] missing endpoint — set one via set_launch_params().")
		return
	_stopped = false
	_attached = false
	_socket = WebSocketPeer.new()
	var err := _socket.connect_to_url(_endpoint)
	if err != OK:
		push_warning("[KnockBox] connect failed (%s); will retry." % err)
		_socket = null
		_schedule_reconnect()
		return
	set_process(true)


func send_to_host(payload) -> void:
	_send("host", payload)


func send_to_all(payload) -> void:
	_send("all", payload)


func send_to(player_id_: String, payload) -> void:
	_send(player_id_, payload)


func _send(to: String, payload) -> void:
	_send_frame({"type": "Game", "to": to, "payload": payload})


## Host-only control: set whether the lobby accepts new joins (open = listed + joinable). The game
## owns join policy; the server never changes it. Non-host senders are ignored by the server.
func set_lobby_open(open: bool) -> void:
	_send_frame({"type": "SetLobbyOpen", "open": open})


## Host-only control: remove a player from the lobby. The kick is permanent for this lobby (the
## target cannot rejoin) and their sockets are evicted. Non-host senders are ignored by the server.
func kick_player(player_id_: String) -> void:
	_send_frame({"type": "KickPlayer", "targetPlayerId": player_id_})


func _send_frame(msg: Dictionary) -> void:
	# sort_keys=false: the server deserializes polymorphically on a "type" discriminator, which
	# System.Text.Json requires to be the FIRST property. Godot's stringify sorts keys by default,
	# which would push "type" last and break the server's read.
	var frame := JSON.stringify(msg, "", false)
	if _socket != null and _attached and _socket.get_ready_state() == WebSocketPeer.STATE_OPEN:
		_socket.send_text(frame)
	else:
		# Not open/attached yet (initial connect or mid-reconnect) — queue and flush on open
		# so an eager send (e.g. a guest's first `sync`) isn't silently dropped.
		_pending.append(frame)


func _process(_delta: float) -> void:
	if _socket == null:
		return
	_socket.poll()
	match _socket.get_ready_state():
		WebSocketPeer.STATE_OPEN:
			if not _attached:
				# First frame on the data plane: authenticate with the lobby-scoped ticket.
				# sort_keys=false so the "type" discriminator stays first (see _send).
				_socket.send_text(JSON.stringify({"type": "Attach", "ticket": _ticket}, "", false))
				_attached = true
			_flush_pending()
			while _socket.get_available_packet_count() > 0:
				var text := _socket.get_packet().get_string_from_utf8()
				var msg = JSON.parse_string(text)
				if msg is Dictionary:
					_handle(msg)
				else:
					push_warning("[KnockBox] dropping malformed (non-JSON) frame.")
		WebSocketPeer.STATE_CLOSED:
			var code := _socket.get_close_code()
			var terminal := KBCore.is_terminal_close(code)
			_socket = null
			set_process(false)
			closed.emit(terminal)
			if terminal:
				# Invalid ticket or our lobby membership ended — retrying is pointless.
				_stopped = true
				_has_session = false
				reconnected = false
				push_warning("[KnockBox] data socket closed permanently (code %s)." % code)
			else:
				_schedule_reconnect()


func _flush_pending() -> void:
	if _pending.is_empty():
		return
	for frame in _pending:
		_socket.send_text(frame)
	_pending.clear()


func _schedule_reconnect() -> void:
	if _stopped:
		return
	var delay_ms := KBCore.reconnect_delay(_attempt)
	_attempt += 1
	# get_tree() is available because this node lives in the autoload tree.
	get_tree().create_timer(delay_ms / 1000.0).timeout.connect(connect_to_server)


func _handle(msg: Dictionary) -> void:
	match msg.get("type", ""):
		"Ready":
			var pid: String = msg.get("playerId", "")
			var roster: Array = msg.get("players", [])
			if pid == "" or roster.is_empty():
				push_error("[KnockBox] malformed Ready (missing playerId/players); ignoring.")
				return
			player_id = pid
			players = roster
			is_host = bool(msg.get("isHost", false))
			_attempt = 0  # healthy connection — reset backoff
			reconnected = _has_session  # a prior session means this Ready is a resume
			_has_session = true
			session_ready.emit(player_id, players, is_host)
			if reconnected:
				resumed.emit()
		"Game":
			message_received.emit(msg.get("from", ""), msg.get("payload"))
		"GamePlayerJoined":
			var player: Dictionary = msg.get("player", {})
			players = KBCore.roster_add(players, player)
			player_joined.emit(player)
		"GamePlayerLeft":
			var left_id: String = msg.get("playerId", "")
			players = KBCore.roster_remove(players, left_id)
			player_left.emit(left_id)
