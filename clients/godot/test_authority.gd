## Headless test for the per-recipient KBAuthority + KBLocalRelay. Run:
##   <godot> --headless --path clients/godot --script res://test_authority.gd
## Builds a local 3-peer lobby with a hidden-information model and asserts each peer receives ONLY
## its own secret, plus that a host kick ejects a peer.
extends SceneTree

var _fail := 0
var _views := {}     # player id -> latest current_view
var _closed := {}    # player id -> terminal flag
var _left := {}      # observer id -> last player id it saw leave


## A tiny hidden-information model: each player has a secret number; snapshot(for_id) reveals only
## that player's own secret (plus the public roster of known ids).
class SecretModel:
	extends RefCounted
	var secrets := {}  # id -> int

	func apply_intent(from_id: String, action):
		if typeof(action) == TYPE_DICTIONARY and action.get("kind") == "set":
			secrets[from_id] = int(action.get("value", 0))
			return true   # accept (value ignored in per-recipient mode)
		return null

	func snapshot(for_id := "") -> Dictionary:
		return {"my_secret": int(secrets.get(for_id, -1)), "known": secrets.keys()}


func _initialize() -> void:
	test_per_recipient_and_kick()
	if _fail == 0:
		print("\n[TEST] ALL PASSED")
	else:
		printerr("\n[TEST] %d FAILURE(S)" % _fail)
	quit(0 if _fail == 0 else 1)


func ok(cond: bool, msg: String) -> void:
	if cond:
		print("  ok: ", msg)
	else:
		_fail += 1
		printerr("  FAIL: ", msg)


func test_per_recipient_and_kick() -> void:
	print("\n[TEST] per-recipient projection + local relay + kick")
	var relay := KBLocalRelay.new()
	var nets: Array = []
	var auths: Array = []

	for i in 3:
		var id := "p%d" % i
		var net := KBNet.new()
		get_root().add_child(net)
		var auth := KBAuthority.new()
		get_root().add_child(auth)
		var model := SecretModel.new()
		auth.setup(net, model, true)  # per_recipient = true
		# Capture this peer's rendered view whenever it changes.
		auth.state_changed.connect(func(): _views[id] = auth.current_view)
		net.closed.connect(func(t): _closed[id] = t)
		net.player_left.connect(func(left_id): _left[id] = left_id)
		nets.append(net)
		auths.append(auth)
		net.connect_local(relay, id, "P%d" % i, i == 0)

	ok(nets[0].is_host and not nets[1].is_host, "first peer is host, others are guests")
	ok(nets[1].players.size() == 3, "guest sees the full 3-player roster")

	# Each peer sets its own secret (guest intents route to the host, who re-projects to everyone).
	for i in 3:
		auths[i].send_intent({"kind": "set", "value": (i + 1) * 10})

	ok(_views["p0"]["my_secret"] == 10, "p0 sees its own secret (10)")
	ok(_views["p1"]["my_secret"] == 20, "p1 sees its own secret (20)")
	ok(_views["p2"]["my_secret"] == 30, "p2 sees its own secret (30)")
	# Hidden info: a peer's view must NOT carry another player's secret.
	ok(_views["p1"]["my_secret"] != 10 and _views["p1"]["my_secret"] != 30,
		"p1's view hides other players' secrets")

	# Host kicks p2.
	nets[0].kick_player("p2")
	ok(_closed.get("p2", false) == true, "kicked peer's session closed (terminal)")
	ok(_left.get("p0", "") == "p2" or _left.get("p1", "") == "p2", "remaining peers saw p2 leave")
	ok(relay.roster().size() == 2, "relay roster down to 2 after kick")
