## KBLocalRelay — an in-process stand-in for the server relay, for solo multiplayer testing.
##
## The editor loopback in KBNet simulates a single solo host. A KBLocalRelay instead lets MANY
## KBNet peers in one process form one lobby and message each other — so host + guest logic (and
## hidden-information per-recipient flows) can be exercised in the editor or a headless test with no
## server, ticket, or export.
##
## Usage (e.g. in a test or a dev scene):
##   var relay := KBLocalRelay.new()
##   for i in 4:
##       var net := KBNet.new()
##       add_child(net)                       # _ready() runs; its solo loopback is suppressed
##       # connect this peer's signals / attach a game instance here, BEFORE joining:
##       net.connect_local(relay, "p%d" % i, "Player %d" % i, i == 0)   # peer 0 is the host
##
## The first peer to join is the host. Joins/leaves fan out as player_joined/player_left; the
## joining peer gets session_ready with the full roster. Routing mirrors the server: "host" → the
## host peer, "all" → everyone (incl. sender), "<id>" → that one peer.
class_name KBLocalRelay
extends RefCounted

var _peers: Array = []  # [{ "id": String, "displayName": String, "net": KBNet }], index 0 = host


func register(net, id: String, display_name: String, _is_host: bool) -> void:
	# Host is whoever joined first; the explicit is_host arg is advisory and recomputed here.
	var is_host := _peers.is_empty()
	_peers.append({"id": id, "displayName": display_name, "net": net})
	# Tell already-present peers about the newcomer.
	var player := {"id": id, "displayName": display_name}
	for e in _peers:
		if e["id"] != id:
			e["net"]._lr_joined(roster(), player)
	# Hand the newcomer its session (full roster + host flag).
	net._lr_ready(roster(), is_host)


func roster() -> Array:
	var out: Array = []
	for e in _peers:
		out.append({"id": e["id"], "displayName": e["displayName"]})
	return out


func host_id() -> String:
	return str(_peers[0]["id"]) if not _peers.is_empty() else ""


func deliver(to: String, from_id: String, payload) -> void:
	match to:
		"all":
			for e in _peers:
				e["net"]._lr_deliver(from_id, payload)
		"host":
			if not _peers.is_empty():
				_peers[0]["net"]._lr_deliver(from_id, payload)
		_:
			for e in _peers:
				if e["id"] == to:
					e["net"]._lr_deliver(from_id, payload)
					return


## Host-only: remove a peer and notify everyone (mirrors the server's KickPlayer).
func kick(by_id: String, target_id: String) -> void:
	if _peers.is_empty() or _peers[0]["id"] != by_id:
		return  # only the host may kick
	if target_id == _peers[0]["id"]:
		return  # can't kick the host
	var idx := -1
	for i in _peers.size():
		if _peers[i]["id"] == target_id:
			idx = i
			break
	if idx < 0:
		return
	var removed = _peers[idx]
	_peers.remove_at(idx)
	for e in _peers:
		e["net"]._lr_left(roster(), target_id)
	removed["net"]._lr_closed()  # the kicked peer's session ends
