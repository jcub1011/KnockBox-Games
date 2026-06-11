## Headless unit tests for KBCore — the GDScript analogue of `web/__tests__/kb-core.test.js`.
##
## Run from `clients/godot/`:
##   godot --headless --script res://test_kb_core.gd
## (or point --path at this folder). Exits with code 0 on success, 1 on any failure.
extends SceneTree

# Load by path rather than the `KBCore` global class name, so this runs via `--script` on a
# fresh checkout without first importing the project (which is what builds the class cache).
const KB := preload("res://addons/knockbox/kb_core.gd")

var _failures := 0


func _check(ok: bool, what: String) -> void:
	if ok:
		print("  ok   - %s" % what)
	else:
		_failures += 1
		printerr("  FAIL - %s" % what)


func _init() -> void:
	# reconnect_delay grows exponentially from the base and is capped at the max.
	_check(KB.reconnect_delay(0) == 1000, "reconnect_delay(0) == 1000")
	_check(KB.reconnect_delay(1) == 2000, "reconnect_delay(1) == 2000")
	_check(KB.reconnect_delay(2) == 4000, "reconnect_delay(2) == 4000")
	_check(KB.reconnect_delay(3) == 8000, "reconnect_delay(3) == 8000")
	_check(KB.reconnect_delay(100) == 30000, "reconnect_delay(100) capped at 30000")

	# is_terminal_close: only the policy-violation code is terminal.
	_check(KB.is_terminal_close(1008) == true, "is_terminal_close(1008) is terminal")
	_check(KB.is_terminal_close(1006) == false, "is_terminal_close(1006) is transient")
	_check(KB.is_terminal_close(1000) == false, "is_terminal_close(1000) is transient")

	# parse_launch_params reads credentials from the URL fragment and URL-decodes them.
	var lp := KB.parse_launch_params("#kbTicket=abc.def&kbEndpoint=ws%3A%2F%2Fh%2Fws")
	_check(lp["ticket"] == "abc.def", "parse_launch_params ticket")
	_check(lp["endpoint"] == "ws://h/ws", "parse_launch_params endpoint (decoded)")
	var empty := KB.parse_launch_params("")
	_check(empty["ticket"] == "" and empty["endpoint"] == "", "parse_launch_params empty")

	# default_endpoint: http→ws, https→wss.
	_check(KB.default_endpoint("https:", "h") == "wss://h/ws", "default_endpoint https→wss")
	_check(KB.default_endpoint("http:", "h") == "ws://h/ws", "default_endpoint http→ws")

	# roster_add is idempotent by id and immutable; roster_remove drops by id.
	var ann := {"id": "p1", "displayName": "Ann"}
	var bob := {"id": "p2", "displayName": "Bob"}
	var one := KB.roster_add([ann], bob)
	_check(one.size() == 2, "roster_add appends a new player")
	var again := KB.roster_add(one, {"id": "p2", "displayName": "Bob (dup)"})
	_check(again.size() == 2, "roster_add is idempotent by id")
	var src := [ann, bob]
	KB.roster_add(src, {"id": "p3", "displayName": "Cy"})
	_check(src.size() == 2, "roster_add does not mutate its input")
	var removed := KB.roster_remove([ann, bob], "p1")
	_check(removed.size() == 1 and removed[0]["id"] == "p2", "roster_remove drops by id")

	if _failures == 0:
		print("\nAll KBCore tests passed.")
		quit(0)
	else:
		printerr("\n%d KBCore test(s) failed." % _failures)
		quit(1)
