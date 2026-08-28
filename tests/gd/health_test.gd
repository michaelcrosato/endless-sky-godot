extends GdUnitTestSuite
## Unit tests for res://scripts/health.gd

func test_starts_full() -> void:
	var health := Health.new(80)
	assert_int(health.maximum).is_equal(80)
	assert_int(health.current).is_equal(80)
	assert_bool(health.is_alive()).is_true()


func test_maximum_is_clamped_to_at_least_one() -> void:
	assert_int(Health.new(0).maximum).is_equal(1)
	assert_int(Health.new(-50).maximum).is_equal(1)


func test_damage_returns_absorbed_amount() -> void:
	var health := Health.new(100)
	assert_int(health.damage(30)).is_equal(30)
	assert_int(health.current).is_equal(70)
	# Overkill absorbs only what remains.
	assert_int(health.damage(500)).is_equal(70)
	assert_int(health.current).is_equal(0)


func test_damage_ignores_non_positive_amounts() -> void:
	var health := Health.new(50)
	assert_int(health.damage(0)).is_equal(0)
	assert_int(health.damage(-10)).is_equal(0)
	assert_int(health.current).is_equal(50)


func test_died_is_emitted_once_on_reaching_zero() -> void:
	var health := Health.new(10)
	var monitor := monitor_signals(health)
	health.damage(10)
	await assert_signal(monitor).is_emitted("died")


func test_the_dead_do_not_heal() -> void:
	var health := Health.new(10)
	health.damage(10)
	assert_int(health.heal(5)).is_equal(0)
	assert_int(health.current).is_equal(0)


func test_heal_is_capped_at_maximum() -> void:
	var health := Health.new(100)
	health.damage(20)
	assert_int(health.heal(999)).is_equal(20)
	assert_int(health.current).is_equal(100)


func test_fraction_tracks_current_over_maximum() -> void:
	var health := Health.new(200)
	health.damage(50)
	assert_float(health.fraction()).is_equal_approx(0.75, 0.0001)
