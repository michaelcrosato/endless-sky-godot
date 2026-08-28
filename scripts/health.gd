class_name Health
extends RefCounted
## Bounded hit-point pool. Pure logic -- no nodes, no scene tree,
## so it can be unit-tested without booting the engine's renderer.

signal died

var maximum: int
var current: int


func _init(p_maximum: int = 100) -> void:
	maximum = maxi(1, p_maximum)
	current = maximum


func is_alive() -> bool:
	return current > 0


## Applies damage and returns the amount actually absorbed.
func damage(amount: int) -> int:
	if amount <= 0 or not is_alive():
		return 0
	var absorbed := mini(amount, current)
	current -= absorbed
	if current == 0:
		died.emit()
	return absorbed


## Heals up to `maximum`; the dead do not recover.
func heal(amount: int) -> int:
	if amount <= 0 or not is_alive():
		return 0
	var restored := mini(amount, maximum - current)
	current += restored
	return restored


func fraction() -> float:
	return float(current) / float(maximum)
