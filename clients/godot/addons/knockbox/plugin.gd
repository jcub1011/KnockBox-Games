@tool
extends EditorPlugin

## Registers the `KnockBox` autoload singleton when the plugin is enabled, so games can
## reference `KnockBox` from any script, and removes it when disabled.

const AUTOLOAD_NAME := "KnockBox"
const AUTOLOAD_PATH := "res://addons/knockbox/knockbox.gd"


func _enter_tree() -> void:
	add_autoload_singleton(AUTOLOAD_NAME, AUTOLOAD_PATH)


func _exit_tree() -> void:
	remove_autoload_singleton(AUTOLOAD_NAME)
