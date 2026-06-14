## KnockBox client core — pure, WebSocket/DOM-free helpers shared by the addon.
##
## A direct GDScript port of `web/kb-core.js`. Kept side-effect-free (only static
## functions, no engine state) so it can be exercised headlessly without a socket or a
## browser — see `clients/godot/test_kb_core.gd`.
##
## Loaded by path (`preload`) rather than via a global `class_name`, so it resolves in any
## run context (autoload at startup, `--script`, export) without depending on the editor
## having built the global-class cache first.
extends RefCounted

## Wire-protocol version this addon speaks, declared in the first frame (Attach). The server
## accepts anything up to its own version and terminally rejects anything newer, so an addon
## that outpaces an old server fails loudly instead of being silently misrouted. Mirrors
## KnockBoxProtocol.Version in KnockBox.Contracts (and PROTOCOL_VERSION in web/kb-core.js).
const PROTOCOL_VERSION := 1

## Server close code used for terminal rejections (WebSocketCloseStatus.PolicyViolation):
## an invalid ticket or an expired lobby membership. There is no point reconnecting — the
## credential won't work.
const TERMINAL_CLOSE_CODE := 1008


static func is_terminal_close(code: int) -> bool:
	return code == TERMINAL_CLOSE_CODE


## Capped exponential backoff for transient drops. `attempt` is 0-based: 1s, 2s, 4s, …
## up to `max`. Returns MILLISECONDS (the JS version is also in ms).
static func reconnect_delay(attempt: int, base := 1000, max := 30000) -> int:
	# Clamp the exponent before pow() — the delay is capped at `max` anyway, and an
	# unbounded attempt count would otherwise overflow when shifted.
	var n := clampi(attempt, 0, 30)
	return mini(max, base * int(pow(2, n)))


## The shell hands the game its credentials in the URL FRAGMENT (not the query string) so
## they are never sent in a Referer header or written to server/proxy logs. Parses
## "#kbTicket=…&kbEndpoint=…" and returns { "ticket": String, "endpoint": String } with
## "" for any field that was absent.
static func parse_launch_params(hash: String) -> Dictionary:
	var raw := hash
	if raw.begins_with("#"):
		raw = raw.substr(1)
	var out := {"ticket": "", "endpoint": ""}
	for pair in raw.split("&", false):
		var eq := pair.find("=")
		if eq < 0:
			continue
		var key := pair.substr(0, eq).uri_decode()
		var value := pair.substr(eq + 1).uri_decode()
		if key == "kbTicket":
			out["ticket"] = value
		elif key == "kbEndpoint":
			out["endpoint"] = value
	return out


## Default data-socket endpoint when the shell didn't supply one: this origin's /ws
## (http→ws, https→wss). `protocol` is the browser `location.protocol` (e.g. "https:").
static func default_endpoint(protocol: String, host: String) -> String:
	var scheme := "wss" if protocol == "https:" else "ws"
	return "%s://%s/ws" % [scheme, host]


## Roster reducers (immutable): add is idempotent by `id`; remove drops by `id`. Returns a
## new Array, leaving the input untouched. Player dictionaries keep the wire keys
## (`id`, `displayName`).
static func roster_add(players: Array, player: Dictionary) -> Array:
	for p in players:
		if p.get("id") == player.get("id"):
			return players.duplicate()
	var out := players.duplicate()
	out.append(player)
	return out


static func roster_remove(players: Array, player_id: String) -> Array:
	var out: Array = []
	for p in players:
		if p.get("id") != player_id:
			out.append(p)
	return out
