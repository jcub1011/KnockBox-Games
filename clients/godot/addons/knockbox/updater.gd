@tool
extends RefCounted
## In-editor updater for the KnockBox addon — the Godot half of `knockbox addon`.
##
## Godot is the engine that CANNOT avoid vendoring: GDScript is compiled into the export, so the
## addon has to live in the project. It is also the engine whose developers are least likely to have
## Node installed, which makes "run the CLI" a poor answer here. So the addon updates itself, using
## only what Godot 4 ships in core:
##
##   HTTPRequest      fetch ADDONS.json and the release archive
##   HashingContext   verify the archive's sha256 before a byte is written
##   ZIPReader        extract it
##   ConfigFile       read the installed version out of plugin.cfg
##
## No dependencies, no build step, and nothing to install first.
##
## Two actions, mirroring the CLI's `update` / `add` split, because a Godot developer has no terminal
## fallback here:
##
##   Check for updates — move to a newer version. REFUSES on a locally modified file until confirmed,
##                       since a version change silently discarding an edit is a surprise.
##   Reinstall         — re-fetch the CURRENT version and restore it, reporting what it replaced.
##                       This is the "I broke the addon files" fix.
##
## Deliberately one file, and deliberately inert until clicked: a game project must not phone home
## while someone is typing, so there is no timer and no autoload here. Delete this file and the addon
## still works — plugin.gd guards for its absence.

const ADDON_DIR := "res://addons/knockbox"
const PLUGIN_CFG := ADDON_DIR + "/plugin.cfg"
const PROJECT_MANIFEST := "res://knockbox.json"
const ADDON_ID := "godot"

## Same default the CLI uses. The index is the trust root: it carries the sha256, and download URLs
## are DERIVED from repo/tag/asset rather than read from it, so a tampered entry has nothing to point
## elsewhere.
const INDEX_URL := "https://raw.githubusercontent.com/jcub1011/KnockBox-Games/main/.addons/ADDONS.json"
const DOWNLOAD_BASE := "https://github.com"

const MAX_INDEX_BYTES := 1048576
const MAX_ARCHIVE_BYTES := 33554432
const TIMEOUT_SECONDS := 30.0

## Where the archive is buffered before extraction. ZIPReader reads a FILE, not a byte buffer, so the
## download has to land somewhere first; user:// keeps it out of the project the operation is editing.
const SCRATCH_ZIP := "user://knockbox-addon-download.zip"

## The version every in-repo copy of plugin.cfg carries. The real number lives in exactly one place
## (clients/addons.manifest.json) and is stamped into the release archive at build time, so a checkout
## reads this instead. Seeing it means "this is the KnockBox source tree, not an installed addon" —
## and updating in place there would overwrite the repo's own files with a released copy, which is
## never what someone working on the addon wants.
const DEV_VERSION := "0.0.0-dev"

var _plugin: EditorPlugin
var _http: HTTPRequest


func setup(plugin: EditorPlugin) -> void:
	_plugin = plugin
	_http = HTTPRequest.new()
	_http.timeout = TIMEOUT_SECONDS
	_http.use_threads = true
	plugin.add_child(_http)


func teardown() -> void:
	if _http != null and _http.is_inside_tree():
		_http.get_parent().remove_child(_http)
	if _http != null:
		_http.queue_free()
	_http = null
	_plugin = null


# ── Public entry points (the two tool-menu actions) ───────────────────────────

## Fetch the index and offer the newer version, if there is one.
func check_for_updates() -> void:
	var installed := installed_version()
	if _refuse_in_source_tree(installed):
		return
	var index: Dictionary = await _fetch_index()
	if index.is_empty():
		return

	var entry: Dictionary = index.get("addons", {}).get(ADDON_ID, {})
	var offered := String(entry.get("version", ""))
	if offered.is_empty():
		_notify("KnockBox", "The addon index does not publish a '%s' addon." % ADDON_ID)
		return

	if offered == installed:
		_notify("KnockBox", "Up to date — the addon is %s." % installed)
		return

	# A modified file is named, not counted: the developer is about to lose that specific edit.
	var modified := modified_files()
	var message := "Update the KnockBox addon from %s to %s?" % [installed, offered]
	if not modified.is_empty():
		message += "\n\nThese files differ from the installed version and WILL be overwritten:\n  - %s" \
			% "\n  - ".join(modified)

	if not await _confirm("Update KnockBox addon", message):
		return
	await _install(entry, offered, false)


## Re-fetch the CURRENTLY installed version and restore every file. The repair path.
func reinstall() -> void:
	var installed := installed_version()
	if _refuse_in_source_tree(installed):
		return
	var index: Dictionary = await _fetch_index()
	if index.is_empty():
		return

	var entry: Dictionary = index.get("addons", {}).get(ADDON_ID, {})
	# Pinned reinstalls come out of the index's `versions` history rather than from a guessed URL: a
	# version the index does not publish is a version there is no verified hash for.
	var target := installed
	if String(entry.get("version", "")) != installed:
		var history: Dictionary = entry.get("versions", {})
		if not history.has(installed):
			_notify("KnockBox", ("The index no longer publishes %s, so it cannot be restored.\n\n"
				+ "Use \"Check for updates\" to move to %s instead.")
				% [installed, entry.get("version", "the current release")])
			return
		entry = history[installed]
		entry["version"] = installed
		target = installed

	var modified := modified_files()
	var summary := "Reinstall the KnockBox addon (%s)?" % target
	if modified.is_empty():
		summary += "\n\nNo local changes detected — this will simply re-verify every file."
	else:
		summary += "\n\nThese files will be restored, discarding local changes:\n  - %s" \
			% "\n  - ".join(modified)

	if not await _confirm("Reinstall KnockBox addon", summary):
		return
	await _install(entry, target, true)


## True (and explains itself) when this is the KnockBox source tree rather than an installed addon.
func _refuse_in_source_tree(installed: String) -> bool:
	if installed != DEV_VERSION:
		return false
	_notify("KnockBox", ("This is the KnockBox source tree (plugin.cfg reads %s), not an installed addon.

"
		+ "Updating would overwrite these files with a released copy. Edit the addon here and release it "
		+ "instead; to test the updater, install the addon into a separate project.") % DEV_VERSION)
	return true


# ── State on disk ─────────────────────────────────────────────────────────────

## The installed version, read from plugin.cfg — the file that ships inside the addon.
func installed_version() -> String:
	var cfg := ConfigFile.new()
	if cfg.load(PLUGIN_CFG) != OK:
		return ""
	return String(cfg.get_value("plugin", "version", ""))


## Recorded per-file hashes from the project's knockbox.json, or an empty dictionary.
func recorded_files() -> Dictionary:
	if not FileAccess.file_exists(PROJECT_MANIFEST):
		return {}
	var text := FileAccess.get_file_as_string(PROJECT_MANIFEST)
	var parsed: Variant = JSON.parse_string(text)
	if typeof(parsed) != TYPE_DICTIONARY:
		return {}
	var record: Variant = parsed.get("addons", {}).get(ADDON_ID, {})
	if typeof(record) != TYPE_DICTIONARY:
		return {}
	var files: Variant = record.get("files", {})
	return files if typeof(files) == TYPE_DICTIONARY else {}


## Project-relative paths whose contents no longer match what was installed. This is what makes the
## guide's "don't fork it" checkable rather than merely requested.
func modified_files() -> Array[String]:
	var out: Array[String] = []
	var recorded := recorded_files()
	for path: String in recorded:
		var expected := String(recorded[path])
		var absolute: String = "res://" + path
		if not FileAccess.file_exists(absolute):
			out.append(path + " (missing)")
			continue
		if _sha256_hex(FileAccess.get_file_as_bytes(absolute)) != expected:
			out.append(path)
	out.sort()
	return out


# ── Install ───────────────────────────────────────────────────────────────────

func _install(entry: Dictionary, version: String, is_reinstall: bool) -> void:
	var source: Dictionary = entry.get("source", {})
	var problem := _validate_source(source)
	if not problem.is_empty():
		_notify("KnockBox", "Refusing to install: %s" % problem)
		return

	var url := "%s/%s/releases/download/%s/%s" % [
		DOWNLOAD_BASE, source["repo"], source["tag"], source["asset"]]
	var body := await _fetch_bytes(url, MAX_ARCHIVE_BYTES)
	if body.is_empty():
		return

	# The whole point of the index carrying a hash: a release asset can be re-uploaded in place, so
	# this is the check that would catch it. Before anything is written.
	var actual := _sha256_hex(body)
	if actual != String(source["sha256"]).to_lower():
		_notify("KnockBox", ("Archive hash mismatch — refusing to install.\n\nexpected %s\nactual   %s\n\n"
			+ "The download does not match what the index published.") % [source["sha256"], actual])
		return

	var extracted := _extract(body)
	if extracted.is_empty():
		return

	var written := 0
	var restored: Array[String] = []
	var recorded := recorded_files()
	for path: String in extracted:
		var absolute: String = "res://" + path
		var payload: PackedByteArray = extracted[path]
		var existed := FileAccess.file_exists(absolute)
		var differs := existed and _sha256_hex(FileAccess.get_file_as_bytes(absolute)) != _sha256_hex(payload)
		if not _write_file(absolute, payload):
			_notify("KnockBox", "Could not write %s — the install is incomplete." % path)
			return
		if differs:
			restored.append(path)
		elif not existed:
			written += 1

	# Prune only what a PREVIOUS install recorded and this version no longer ships. Scoped to the
	# recorded list deliberately: a script the developer put in addons/knockbox/ was never ours.
	var pruned: Array[String] = []
	for path: String in recorded:
		if extracted.has(path):
			continue
		var absolute: String = "res://" + path
		if FileAccess.file_exists(absolute) and DirAccess.remove_absolute(ProjectSettings.globalize_path(absolute)) == OK:
			pruned.append(String(path))

	_write_project_manifest(extracted, version)
	EditorInterface.get_resource_filesystem().scan()

	var report := "%s the KnockBox addon (%s)." % ["Reinstalled" if is_reinstall else "Updated to", version]
	if written > 0:
		report += "\n\nInstalled %d new file(s)." % written
	if not restored.is_empty():
		report += "\n\nRestored (local changes discarded):\n  - %s" % "\n  - ".join(restored)
	if not pruned.is_empty():
		report += "\n\nRemoved (not in this version):\n  - %s" % "\n  - ".join(pruned)
	if written == 0 and restored.is_empty() and pruned.is_empty():
		report += "\n\nEvery file already matched the published version — nothing to repair."
	report += "\n\nRestart the editor if the addon's scripts do not reload cleanly."
	_notify("KnockBox", report)


## Read the archive into { project-relative path: bytes }, validating every entry BEFORE writing any.
func _extract(archive: PackedByteArray) -> Dictionary:
	var file := FileAccess.open(SCRATCH_ZIP, FileAccess.WRITE)
	if file == null:
		_notify("KnockBox", "Could not buffer the download to %s." % SCRATCH_ZIP)
		return {}
	file.store_buffer(archive)
	file.close()

	var reader := ZIPReader.new()
	if reader.open(SCRATCH_ZIP) != OK:
		_notify("KnockBox", "The download is not a readable ZIP archive.")
		return {}

	var out := {}
	for name: String in reader.get_files():
		if name.ends_with("/"):
			continue
		if not _is_safe_path(name):
			# An archive that tries to escape the project is not a partially-usable archive.
			reader.close()
			_notify("KnockBox", "Refusing to install: the archive contains an unsafe path '%s'." % name)
			return {}
		out[name] = reader.read_file(name)
	reader.close()
	DirAccess.remove_absolute(ProjectSettings.globalize_path(SCRATCH_ZIP))

	# The archive's own knockbox.json is its record of itself; the project's is the merge of every
	# installed addon, so it is rebuilt rather than copied over.
	out.erase("knockbox.json")
	if out.is_empty():
		_notify("KnockBox", "The archive contains no files.")
	return out


## Rebuild res://knockbox.json, preserving records for any other addon installed in this project.
func _write_project_manifest(files: Dictionary, version: String) -> void:
	var manifest := {}
	if FileAccess.file_exists(PROJECT_MANIFEST):
		var parsed: Variant = JSON.parse_string(FileAccess.get_file_as_string(PROJECT_MANIFEST))
		if typeof(parsed) == TYPE_DICTIONARY:
			manifest = parsed
	if typeof(manifest.get("addons")) != TYPE_DICTIONARY:
		manifest["addons"] = {}

	var hashes := {}
	var names := files.keys()
	names.sort()
	for name: String in names:
		var payload: PackedByteArray = files[name]
		hashes[name] = _sha256_hex(payload)

	manifest["$comment"] = ("Written by `knockbox addon`. Commit this: it records which addon versions "
		+ "this game was built against, `knockbox addon check` verifies the files against it, and "
		+ "`knockbox pack` stamps it into the shipped .kbg.")
	manifest["addons"][ADDON_ID] = { "version": version, "files": hashes }

	var file := FileAccess.open(PROJECT_MANIFEST, FileAccess.WRITE)
	if file == null:
		return
	file.store_string(JSON.stringify(manifest, "  ") + "\n")
	file.close()


# ── Networking ────────────────────────────────────────────────────────────────

func _fetch_index() -> Dictionary:
	var body := await _fetch_bytes(INDEX_URL, MAX_INDEX_BYTES)
	if body.is_empty():
		return {}
	var parsed: Variant = JSON.parse_string(body.get_string_from_utf8())
	if typeof(parsed) != TYPE_DICTIONARY:
		_notify("KnockBox", "The addon index is not valid JSON.")
		return {}

	# A newer schema major is refused rather than half-read — the same rule the CLI and the server's
	# marketplace client apply.
	var schema := String(parsed.get("schemaVersion", ""))
	if schema.split(".")[0].to_int() > 1:
		_notify("KnockBox", ("The addon index uses schema %s, which this addon does not understand.\n\n"
			+ "Update the addon by hand from the GitHub release, or use the knockbox CLI.") % schema)
		return {}
	return parsed


func _fetch_bytes(url: String, max_bytes: int) -> PackedByteArray:
	if _http == null:
		return PackedByteArray()
	if not url.begins_with("https://"):
		_notify("KnockBox", "Refusing a non-HTTPS URL: %s" % url)
		return PackedByteArray()

	var err := _http.request(url)
	if err != OK:
		_notify("KnockBox", "Could not start the request to %s (error %d)." % [url, err])
		return PackedByteArray()

	var response: Array = await _http.request_completed
	var result: int = response[0]
	var code: int = response[1]
	var body: PackedByteArray = response[3]

	if result != HTTPRequest.RESULT_SUCCESS:
		_notify("KnockBox", "Network error fetching %s (result %d)." % [url, result])
		return PackedByteArray()
	if code != 200:
		_notify("KnockBox", "Fetching %s returned HTTP %d." % [url, code])
		return PackedByteArray()
	if body.size() > max_bytes:
		_notify("KnockBox", "%s is %d bytes, over the %d-byte cap." % [url, body.size(), max_bytes])
		return PackedByteArray()
	return body


# ── Validation helpers ────────────────────────────────────────────────────────

## Mirrors the CLI's entry validation: everything that goes into a URL is pattern-checked, and the
## sha256 is REQUIRED, before any request is made.
func _validate_source(source: Dictionary) -> String:
	if source.is_empty():
		return "the index entry has no 'source'."
	if String(source.get("type", "")) != "github-release":
		return "unsupported source type '%s'." % source.get("type", "")

	var repo := String(source.get("repo", ""))
	var tag := String(source.get("tag", ""))
	var asset := String(source.get("asset", ""))
	var digest := String(source.get("sha256", ""))

	if not RegEx.create_from_string("^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$").search(repo):
		return "invalid source.repo '%s'." % repo
	if not RegEx.create_from_string("^[A-Za-z0-9][A-Za-z0-9._-]*$").search(tag):
		return "invalid source.tag '%s'." % tag
	if not RegEx.create_from_string("^[A-Za-z0-9][A-Za-z0-9._-]*\\.zip$").search(asset):
		return "invalid source.asset '%s' — must be a .zip filename." % asset
	if not RegEx.create_from_string("^[a-fA-F0-9]{64}$").search(digest):
		return "source.sha256 is required and must be 64 hex characters."
	return ""


## No absolute paths, no drive letters, no "." or ".." segments — the archive must not reach outside
## the project. Mirrors normalizePath in the CLI's kbg.mjs.
func _is_safe_path(path: String) -> bool:
	if path.is_empty() or path.begins_with("/") or path.begins_with("\\"):
		return false
	if path.contains(":") or path.contains("\\"):
		return false
	for segment in path.split("/"):
		if segment.is_empty() or segment == "." or segment == "..":
			return false
	return true


func _sha256_hex(bytes: PackedByteArray) -> String:
	var ctx := HashingContext.new()
	ctx.start(HashingContext.HASH_SHA256)
	ctx.update(bytes)
	return ctx.finish().hex_encode()


func _write_file(absolute: String, bytes: PackedByteArray) -> bool:
	var dir := absolute.get_base_dir()
	if not DirAccess.dir_exists_absolute(dir):
		if DirAccess.make_dir_recursive_absolute(dir) != OK:
			return false
	var file := FileAccess.open(absolute, FileAccess.WRITE)
	if file == null:
		return false
	file.store_buffer(bytes)
	file.close()
	return true


# ── Dialogs ───────────────────────────────────────────────────────────────────

func _confirm(title: String, message: String) -> bool:
	var dialog := ConfirmationDialog.new()
	dialog.title = title
	dialog.dialog_text = message
	dialog.dialog_autowrap = true
	dialog.min_size = Vector2i(520, 200)
	EditorInterface.get_base_control().add_child(dialog)
	dialog.popup_centered()

	var accepted := false
	# await on either signal: confirmed fires only on OK, canceled/close on the other two exits.
	dialog.confirmed.connect(func() -> void: accepted = true)
	await dialog.visibility_changed
	dialog.queue_free()
	return accepted


func _notify(title: String, message: String) -> void:
	var dialog := AcceptDialog.new()
	dialog.title = title
	dialog.dialog_text = message
	dialog.dialog_autowrap = true
	dialog.min_size = Vector2i(520, 160)
	EditorInterface.get_base_control().add_child(dialog)
	dialog.popup_centered()
	dialog.confirmed.connect(dialog.queue_free)
	dialog.canceled.connect(dialog.queue_free)
