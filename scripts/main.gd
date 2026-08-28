extends Node2D
## Entry scene. Doubles as a smoke test: `tools/run.ps1 -Headless`
## boots this, prints the wiring it can see, and exits cleanly.

@onready var _label: Label = $UI/Status


func _ready() -> void:
	var health := Health.new(100)
	health.damage(35)

	var lines := [
		"Godot %s" % Engine.get_version_info().string,
		"GDScript: Health %d/%d (%.0f%%)" % [health.current, health.maximum, health.fraction() * 100.0],
		"C#: %s" % _probe_csharp(),
	]
	var report := "\n".join(lines)

	print(report)
	if is_instance_valid(_label):
		_label.text = report

	# Headless smoke runs pass --quit-after, so this only matters interactively.
	if DisplayServer.get_name() == "headless":
		print("[ok] headless boot succeeded")


func _probe_csharp() -> String:
	# Loaded dynamically: referencing the C# class by name would be a parse
	# error whenever the assembly has not been built yet.
	if not ResourceLoader.exists("res://src/InventoryNode.cs"):
		return "source missing"
	var script: Script = load("res://src/InventoryNode.cs")
	# can_instantiate() is false when the C# assembly was never built or, in an
	# exported build, never bundled. Calling new() in that state hard-crashes.
	if script == null or not script.can_instantiate():
		return "assembly unavailable (run tools/build.ps1)"
	var inventory: Object = script.new()
	inventory.Add("potion", 3)
	return "Inventory holds %d potion(s)" % inventory.CountOf("potion")
