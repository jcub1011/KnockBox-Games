## KBAuthority — optional host-authoritative glue on top of KBNet (or the raw KnockBox
## autoload). It implements the whole "guest sends intent → host validates, mutates, and
## broadcasts → everyone renders" pattern once, so a game only writes its rules.
##
## The new server is a blind relay with no server-side state, so exactly one client (the
## `is_host` player) owns the truth. This node wires that up:
##   • guests auto-send a sync request on connect AND on reconnect;
##   • the host answers sync/late-joins/reconnects with a full snapshot;
##   • the host applies each intent and broadcasts the resulting (small) delta;
##   • every client applies snapshots/deltas to its local model and gets `state_changed`.
##
## You supply a plain "model" object (RefCounted/Node/etc.) implementing:
##   apply_intent(from_id: String, action) -> Variant
##       Host only. Mutate authoritative state; RETURN a small "patch" value to broadcast
##       to everyone, or null to reject/no-op (nothing is sent). Authorize here using
##       `from_id` (e.g. host-only commands compare it to the host id).
##   apply_patch(patch) -> void        # every client applies a broadcast delta
##   snapshot() -> Dictionary          # full state for sync/join/reconnect
##   apply_snapshot(state) -> void     # every client adopts a full snapshot
##
## Usage:
##   var authority := KBAuthority.new()
##   add_child(authority)
##   authority.setup(Net, my_model)          # Net = a KBNet (or the KnockBox autoload)
##   authority.state_changed.connect(_render)
##   ...
##   authority.send_intent({ "kind": "roll", ... })   # from any client
##
## ── Per-recipient (hidden-information) mode ──
## Pass `per_recipient = true` to `setup()` for games where each player must see a DIFFERENT view
## (secret roles, hands, votes-before-reveal). The model then implements instead:
##   snapshot(for_player_id := "") -> Dictionary
##       Return the view projected for `for_player_id` (default-deny: include only what that player
##       may see). With "" it may return full state, but in this mode it's always called per player.
##   apply_intent(from_id, action) -> Variant   # return non-null to accept (the value is ignored —
##                                              # the host re-projects a fresh snapshot to everyone),
##                                              # or null to reject.
## In this mode there are no deltas and guests need no model: the host sends each player
## `snapshot(that_player)`, and `current_view` holds the local player's latest view — render from it
## (it is null until the first view arrives). `apply_patch`/`apply_snapshot` are unused here.
##
## `state_changed` fires whenever the local model may have changed (snapshot, delta, or a
## roster change), so just re-render from the model. Use the transport's own
## `session_ready` / `players` / `is_host` for lobby/roster UI.
class_name KBAuthority
extends Node

## The local model may have changed — re-render from it.
signal state_changed

const ENVELOPE := "_kb"  # discriminator marking messages this helper owns

var _net                  # KBNet or the KnockBox autoload (duck-typed)
var _model                # the game's authoritative/replicated model
var _per_recipient := false  # true => per-player projected snapshots (hidden-info games)

## In per-recipient mode, the local player's latest projected view (null until the first arrives).
## Render from this instead of the model. Unused (stays null) in the default broadcast mode.
var current_view = null


func setup(net, model, per_recipient := false) -> void:
	_net = net
	_model = model
	_per_recipient = per_recipient
	net.session_ready.connect(_on_session)
	net.message_received.connect(_on_message)
	net.player_joined.connect(_on_roster_changed)
	net.player_left.connect(_on_roster_changed)
	# `resumed` (reconnect) is handled via the `reconnected` flag inside _on_session, but
	# connect it too in case the transport emits it separately.
	if net.has_signal("resumed"):
		net.resumed.connect(_on_resumed)


## Send a game intent to the host (works the same on host and guests — the host's own
## intents loop back through the same path).
func send_intent(action) -> void:
	_net.send_to_host({ENVELOPE: "intent", "action": action})


## Convenience for the host's join policy: open/close the lobby to new players (see KBNet).
func set_open(open: bool) -> void:
	_net.set_lobby_open(open)


func _on_session(_pid: String, _players: Array, is_host: bool) -> void:
	if not is_host:
		# Ask the host for the current state (covers first join, late join and reconnect).
		_net.send_to_host({ENVELOPE: "sync"})
	elif _per_recipient:
		# Host renders its own projected view; guests' views arrive via their sync responses.
		current_view = _model.snapshot(_net.player_id)
	# Render whatever we have (host: its initial/own state; guest: until the snapshot lands).
	state_changed.emit()


func _on_resumed() -> void:
	# A reconnect re-fires session_ready with reconnected=true, which already triggers a
	# guest re-sync above; nothing extra needed here. Kept for clarity/extension.
	pass


func _on_roster_changed(_arg = null) -> void:
	# Host re-broadcasts the full state so newcomers (and everyone) converge.
	if _net.is_host:
		_broadcast_state()
	state_changed.emit()


## Host: push current state to everyone. Per-recipient mode sends each player their own projection
## and sets the host's own `current_view`; default mode sends one shared snapshot to all.
func _broadcast_state() -> void:
	if not _net.is_host:
		return
	if _per_recipient:
		for p in _net.players:
			var pid := str(p.get("id", ""))
			if pid == _net.player_id:
				current_view = _model.snapshot(pid)
			else:
				_net.send_to(pid, {ENVELOPE: "state", "state": _model.snapshot(pid)})
	else:
		_net.send_to_all({ENVELOPE: "state", "state": _model.snapshot()})


func _on_message(from_id: String, payload) -> void:
	if not (payload is Dictionary) or not payload.has(ENVELOPE):
		return  # not ours (a raw-KBNet game message) — ignore
	# The host is the single source of truth: it mutates only via apply_intent and never
	# consumes its own broadcast echoes. Guests never mutate locally; they only adopt the
	# host's deltas/snapshots. This split keeps every client converged on the host's state.
	match payload[ENVELOPE]:
		"intent":
			if not _net.is_host:
				return  # only the host acts on intents
			var patch = _model.apply_intent(from_id, payload.get("action"))
			if patch != null:
				if _per_recipient:
					# Re-project a fresh per-player snapshot to everyone (the patch value is only
					# an accept/reject signal here).
					_broadcast_state()
				else:
					_net.send_to_all({ENVELOPE: "delta", "patch": patch})
				state_changed.emit()  # host renders its own mutation now (ignores the echo below)
		"sync":
			if _net.is_host:
				if _per_recipient:
					_net.send_to(from_id, {ENVELOPE: "state", "state": _model.snapshot(from_id)})
				else:
					_net.send_to(from_id, {ENVELOPE: "state", "state": _model.snapshot()})
		"delta":
			if _net.is_host:
				return  # already applied via apply_intent; the echo is for guests
			_model.apply_patch(payload.get("patch"))
			state_changed.emit()
		"state":
			if _net.is_host:
				return  # host is authoritative; never adopts a snapshot
			if _per_recipient:
				current_view = payload.get("state")  # guests render this directly; no model needed
			else:
				_model.apply_snapshot(payload.get("state"))
			state_changed.emit()
