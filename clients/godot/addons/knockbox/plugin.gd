@tool
extends EditorPlugin

## Registers the `KnockBox` autoload singleton when the plugin is enabled, so games can
## reference `KnockBox` from any script, and removes it when disabled.
##
## Also adds the two updater actions under Project → Tools, when updater.gd is present. That file is
## optional on purpose: it is the only part of the addon that talks to the network, so a project that
## would rather not have it can delete it and everything here still works.

const AUTOLOAD_NAME := "KnockBox"
const AUTOLOAD_PATH := "res://addons/knockbox/knockbox.gd"
const UPDATER_PATH := "res://addons/knockbox/updater.gd"

const MENU_CHECK := "KnockBox: check for addon updates"
const MENU_REINSTALL := "KnockBox: reinstall addon (repair local edits)"

var _updater: RefCounted


func _enter_tree() -> void:
	add_autoload_singleton(AUTOLOAD_NAME, AUTOLOAD_PATH)

	# Nothing here runs on a timer or at startup — the menu items are the only trigger, so a game
	# project never reaches the network while someone is working in it.
	if ResourceLoader.exists(UPDATER_PATH):
		_updater = load(UPDATER_PATH).new()
		_updater.setup(self)
		add_tool_menu_item(MENU_CHECK, _on_check_for_updates)
		add_tool_menu_item(MENU_REINSTALL, _on_reinstall)


func _exit_tree() -> void:
	remove_autoload_singleton(AUTOLOAD_NAME)

	if _updater != null:
		remove_tool_menu_item(MENU_CHECK)
		remove_tool_menu_item(MENU_REINSTALL)
		_updater.teardown()
		_updater = null


func _on_check_for_updates() -> void:
	if _updater != null:
		_updater.check_for_updates()


func _on_reinstall() -> void:
	if _updater != null:
		_updater.reinstall()
