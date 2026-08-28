using System;
using System.Collections.Generic;
using System.Linq;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// Milestone 1 flight slice: loads the real Endless Sky dataset, builds the
    /// vanilla starting system (Rutilicus, 16 Nov 3013) in 3D, and flies the
    /// stock Shuttle with the ported upstream physics at a fixed 60 Hz.
    ///
    /// Everything visible is constructed from data at runtime — no hand-placed
    /// content. When no dataset is available (bare CI checkout) the scene boots,
    /// says so on screen and in the log, and idles instead of crashing.
    /// </summary>
    public partial class FlightWorld : Node3D
    {
        // Fallbacks only. The date, system, planet, opening balance and starting
        // conditions all come from the dataset's "start" definition — Milestone 7's
        // rule is not to hard-code content that can be loaded from the source data,
        // and all of it is in starts.txt. These are used only if that is missing.
        private const string FallbackSystem = "Rutilicus";
        private const string FallbackPlanet = "New Boston";

        // A start does not name a ship: upstream's opening conversation sells the
        // player their first hull on credit, which is why the classic start begins in
        // debt. Until conversations can grant ships, this is still chosen here.
        private const string StartShip = "Shuttle";

        private string _startSystem = FallbackSystem;
        private string _startPlanet = FallbackPlanet;
        private StartScenario? _start;

        private Ship? _ship;
        private ShipView _shipView = null!;   // set with _ship; guarded by _ship != null
        private CameraRig _camera = null!;    // set with _ship; guarded by _ship != null
        private readonly List<StellarObjectView> _stellarViews = new();
        private Label? _statusLabel;
        private string? _capturePath;
        private int _captureFrames = 90;
        private int _renderedFrames;
        private bool _autopilot;
        private int _simFrames;
        private bool _combatDemo;
        private CombatField? _field;
        private CombatEffects _effects = null!; // set with _field; guarded by _field != null
        private Ship? _drone;
        private ShipView _droneView = null!;    // set with _drone; guarded by _drone != null
        private GameData _universe = null!;     // set with _ship; guarded by _ship != null
        private Label? _titleLabel;
        private double _currentDay;
        private bool _jumpAutopilot;
        private bool _jumpKeyWasDown;
        private bool _landKeyWasDown;
        private bool _landAtStart;
        private bool _isLanded;
        private long _credits = 480_000; // vanilla start: mortgaged to the hilt

        // The player as the simulation models one: fleet, money, date, where they have
        // been. The loose _credits field above is kept in step with it so the existing
        // HUD and log lines carry on working.
        private PlayerState _player = null!;      // set with _ship
        private MissionLog _missions = null!;     // set with _ship

        // Live NPC traffic. Systems declare the fleets that frequent them; without a
        // spawner every system in the galaxy is empty no matter what the data says.
        private FleetSpawner _spawner = null!;    // set with _ship
        private readonly List<(Ship Ship, ShipView View)> _traffic =
            new List<(Ship, ShipView)>();

        /// <summary>Most ships to keep in flight at once, so a busy system stays playable.</summary>
        private const int TrafficLimit = 12;
        private LandedOverlay? _landedOverlay;
        private StarSystem? _lastSystem;
        private DirectionalLight3D _keyLight = null!;  // set by BuildLighting
        private DirectionalLight3D _fillLight = null!; // set by BuildLighting

        public override void _Ready()
        {
            ParseUserArgs();
            BuildEnvironment();
            AddChild(new Starfield { Name = "Starfield" });

            GameData? universe = EsData.Universe;

            // Where a new pilot begins, as the dataset states it.
            _start = universe?.DefaultStart;
            if (_start?.SystemName != null) _startSystem = _start.SystemName;
            if (_start?.PlanetName != null) _startPlanet = _start.PlanetName;

            if (universe == null || !universe.Systems.TryGetValue(_startSystem, out StarSystem? system))
            {
                string message = EsData.DataPath == null
                    ? "Endless Sky data not found.\nSet ENDLESS_SKY_DATA or clone endless-sky as ../es-upstream."
                    : $"System \"{_startSystem}\" missing from dataset at {EsData.DataPath}.";
                GD.Print($"[flight] data=missing — {message.Replace('\n', ' ')}");
                BuildHud(message);
                return;
            }

            _universe = universe;

            // The start's own date, not a constant.
            DateTime startDate = _start?.Date ?? new DateTime(3013, 11, 16);
            _currentDay = DaysSinceEpoch(startDate.Year, startDate.Month, startDate.Day);
            system.SetDate(_currentDay);

            foreach (StellarObject obj in system.AllObjects())
            {
                var view = StellarObjectView.Create(obj);
                AddChild(view);
                _stellarViews.Add(view);
            }

            _ship = universe.BuildShip(StartShip, out List<string> missingOutfits);
            if (missingOutfits.Count > 0)
            {
                GD.Print($"[flight] warning: missing outfits on {StartShip}: {string.Join(", ", missingOutfits)}");
            }

            // Depart from just off New Boston, facing system north.
            Point planetPos = Point.Zero;
            foreach (StellarObject obj in system.AllObjects())
            {
                if (obj.PlanetName == _startPlanet)
                {
                    planetPos = obj.Position;
                    break;
                }
            }

            // Off the planet's shoulder, not on its horizontal — keeps the
            // planet out of the ship's line and off the HUD corner.
            _ship.Position = planetPos + new Point(-120.0, 210.0);
            _ship.Facing = new Angle(0.0);
            _ship.CurrentSystem = system;
            _lastSystem = system;
            BuildLighting(planetPos);

            _shipView = new ShipView { Name = "PlayerShip" };
            AddChild(_shipView);
            _player = new PlayerState(universe);
            _player.Fleet.Add(_ship);
            _player.Fleet.SetFlagship(_ship);

            // Money and opening conditions from the start definition. The conditions
            // matter as much as the credits: the default start sets a pilot's licence
            // and a species, and content gates on both, so a player placed in the world
            // by hand never sees the campaign that checks for them.
            if (_start != null)
            {
                _start.ApplyTo(_player, universe);
                _credits = _player.Credits;
                GD.Print($"[start] {_start.DisplayName}: {_startPlanet}, {_startSystem}, " +
                         $"{startDate:d MMM yyyy}, {_credits:n0} credits, " +
                         $"{_player.Conditions.Values.Count} conditions set");
            }
            else
            {
                _player.SetCredits(_credits);
            }

            _player.EnterSystem(system);
            _missions = new MissionLog(_player);
            _spawner = new FleetSpawner(universe);

            _shipView.SyncWith(_ship);

            _camera = new CameraRig { Name = "CameraRig" };
            AddChild(_camera);
            _camera.Snap(_ship);

            if (_combatDemo)
            {
                BuildCombatDemo(universe);
            }

            BuildHud(null);
            GD.Print($"[flight] data={EsData.DataPath} system={system.Name} " +
                     $"objects={_stellarViews.Count} ship={StartShip} " +
                     $"mass={_ship.Mass:0.#} vmax={_ship.MaxVelocity:0.###}px/f " +
                     $"turn={_ship.TurnRate:0.###}deg/f accel={_ship.Acceleration:0.####}px/f2");
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_ship == null)
            {
                return;
            }

            // One sim step per physics tick; the project pins physics to 60 Hz,
            // the rate every upstream per-frame quantity assumes.
            _simFrames++;

            // Landed: the sim is frozen and the overlay owns input.
            if (_isLanded)
            {
                return;
            }

            // L lands on a nearby planet when slow enough (menu-driven per the
            // directive; upstream's landing zoom animation is later polish).
            if (_landAtStart && _simFrames == 5)
            {
                TryLand();
                if (_isLanded)
                {
                    return;
                }
            }

            bool landKeyDown = Input.IsPhysicalKeyPressed(Key.L);
            if (landKeyDown && !_landKeyWasDown)
            {
                TryLand();
                if (_isLanded)
                {
                    _landKeyWasDown = landKeyDown;
                    return;
                }
            }

            _landKeyWasDown = landKeyDown;

            // Hyperspace owns the whole frame: no turning, thrust, or combat.
            if (_ship.StepHyperspace())
            {
                if (!ReferenceEquals(_ship.CurrentSystem, _lastSystem))
                {
                    HandleArrival();
                }

                _shipView.SyncWith(_ship);
                _shipView.SetHyperspaceStretch(_ship.HyperspaceCount / (float)Ship.HyperspaceFrames);
                UpdateHud();
                return;
            }

            _shipView.SetHyperspaceStretch(0f);

            // J engages the jump autopilot: pick the linked system best
            // aligned with the current facing (upstream AI::MovePlayer).
            bool jumpKeyDown = Input.IsPhysicalKeyPressed(Key.J);
            if (jumpKeyDown && !_jumpKeyWasDown)
            {
                SelectJumpTarget();
            }

            _jumpKeyWasDown = jumpKeyDown;

            // Demo flights hand over to the jump autopilot after the opening
            // turn, so captures exercise the full M3 sequence.
            if (_autopilot && _simFrames == 170 && !_jumpAutopilot)
            {
                SelectJumpTarget();
            }

            Command command;
            if (_jumpAutopilot)
            {
                bool manual = !_autopilot &&
                              (Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up) ||
                               Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down) ||
                               Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left) ||
                               Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right));
                if (manual || _ship.TargetSystem == null)
                {
                    // Any manual input cancels the autopilot, as upstream.
                    _jumpAutopilot = false;
                    command = Command.None;
                }
                else if (_ship.Velocity.Length > _ship.JumpSpeedLimit)
                {
                    // Brake first: the reverse-key translation flips the ship
                    // retrograde; thrust once roughly aligned.
                    bool retroAligned =
                        _ship.Facing.Unit().Dot((-_ship.Velocity).Unit()) > 0.96;
                    command = FlightControls.BuildPlayerCommand(_ship, retroAligned, back: true, turnInput: 0.0);
                }
                else
                {
                    command = new Command { Turn = FlightControls.TurnToward(_ship, _ship.JumpDirection) };
                }
            }
            else if (_autopilot)
            {
                // Deterministic demo flight for captures: bank through a turn,
                // then fly straight — exercises thrust, steering and the plume.
                command = new Command { Forward = true, Turn = _simFrames < 100 ? 0.55 : 0.0 };
            }
            else
            {
                // The BACK key goes through the upstream AI::MovePlayer
                // translation (FlightControls): on a ship with no reverse
                // thruster — the Shuttle — it turns retrograde. Space is
                // deliberately unbound: upstream ships Stop unbound, and the
                // raw Stop flag without its autopilot is worse than nothing.
                bool forward = Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up);
                bool back = Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down);
                double turn = 0.0;
                if (Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left)) turn -= 1.0;
                if (Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right)) turn += 1.0;
                command = FlightControls.BuildPlayerCommand(_ship, forward, back, turn);
            }

            _ship.Step(command);
            StepTraffic();
            if (_jumpAutopilot && _ship.TryCommitJump())
            {
                _jumpAutopilot = false;
                GD.Print($"[flight] jump committed → {_ship.HyperspaceSystem!.Name}");
            }

            _shipView.SyncWith(_ship);
            StepCombatDemo();
            UpdateHud();
        }

        public override void _Process(double delta)
        {
            if (_ship != null)
            {
                _camera.Follow(_ship, delta);
            }

            if (_capturePath != null && ++_renderedFrames >= _captureFrames)
            {
                SaveCapture();
                _capturePath = null;
                GetTree().Quit();
            }
        }

        private void BuildEnvironment()
        {
            var environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.010f, 0.012f, 0.020f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.5f, 0.55f, 0.65f),
                // Low: ambient is fill-of-last-resort, not the key. At 0.22 it
                // flattened every terminator.
                AmbientLightEnergy = 0.08f,
                GlowEnabled = true,
                GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive,
                GlowIntensity = 1.0f,
                GlowStrength = 1.2f,
                GlowHdrThreshold = 0.85f,
                GlowBloom = 0.15f,
                TonemapMode = Godot.Environment.ToneMapper.Aces,
                // Emission above 1.0 renders as a gradient instead of clipping
                // to a white hole.
                TonemapWhite = 6.0f,
            };
            for (int level = 1; level <= 5; level++)
            {
                environment.SetGlowLevel(level, new[] { 1.0f, 0.8f, 0.6f, 0.4f, 0.3f }[level - 1]);
            }

            AddChild(new WorldEnvironment { Environment = environment });
        }

        /// <summary>
        /// Key + fill lighting. The key is a shadowed directional aimed from
        /// the star toward the play area (an omni at the star cannot shadow
        /// consistently at system scale); the cool fill from the opposite
        /// quadrant rims the dark limbs so bodies never dissolve into space.
        /// </summary>
        private void BuildLighting(Point playAreaSim)
        {
            Vector3 toPlayArea = WorldSpace.ToWorld(playAreaSim);
            if (toPlayArea.LengthSquared() < 1e-4f)
            {
                toPlayArea = new Vector3(0f, 0f, 1f);
            }

            _keyLight = new DirectionalLight3D
            {
                Name = "StarKeyLight",
                LightColor = new Color(1.0f, 0.94f, 0.85f),
                LightEnergy = 2.2f,
                ShadowEnabled = true,
                DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal,
                DirectionalShadowMaxDistance = 400f,
                ShadowBias = 0.03f,
            };
            AddChild(_keyLight);
            _keyLight.LookAtFromPosition(Vector3.Zero, toPlayArea, Vector3.Up);

            _fillLight = new DirectionalLight3D
            {
                Name = "CoolFill",
                LightColor = new Color(0.42f, 0.58f, 1.0f),
                LightEnergy = 0.30f,
                ShadowEnabled = false,
            };
            AddChild(_fillLight);
            _fillLight.LookAtFromPosition(Vector3.Zero, -toPlayArea + new Vector3(0f, -0.35f, 0f) * toPlayArea.Length(), Vector3.Up);
        }

        /// <summary>
        /// Land on the nearest named planet within range at low speed. Opens
        /// the landed overlay; departing refuels at spaceport worlds (an
        /// approximation of upstream's landing refuel/recharge) and hands
        /// flight control back.
        /// </summary>
        /// <summary>
        /// Runs the system's own traffic: spawns what the system declares, flies it,
        /// and retires anything that has left or been destroyed.
        /// </summary>
        /// <remarks>
        /// Capped, and the cap is the point. Systems declare fleets on a per-frame
        /// probability with no notion of how many are already present, so an
        /// uncapped spawner fills a busy system without limit and the frame rate goes
        /// with it. Upstream bounds this through its own strength checks and the
        /// player's limited view; a hard ceiling is the honest version here.
        /// </remarks>
        private void StepTraffic()
        {
            if (_ship?.CurrentSystem == null || _spawner == null)
            {
                return;
            }

            // Retire the dead and the departed.
            for (int i = _traffic.Count - 1; i >= 0; i--)
            {
                (Ship npc, ShipView view) = _traffic[i];
                bool gone = npc.IsDestroyed ||
                            !ReferenceEquals(npc.CurrentSystem, _ship.CurrentSystem) ||
                            (npc.Position - _ship.Position).Length > 60_000.0;

                if (!gone)
                {
                    continue;
                }

                view.QueueFree();
                _field?.Remove(npc);
                _traffic.RemoveAt(i);
            }

            if (_traffic.Count < TrafficLimit)
            {
                var present = _traffic.Select(t => t.Ship).ToList();
                present.Add(_ship);

                foreach (Ship arrival in _spawner.Step(_ship.CurrentSystem, present))
                {
                    if (_traffic.Count >= TrafficLimit)
                    {
                        break;
                    }

                    var view = new ShipView { Name = $"Traffic{_traffic.Count}" };
                    AddChild(view);
                    view.SyncWith(arrival);
                    _traffic.Add((arrival, view));
                    _field?.Add(arrival);

                    GD.Print($"[traffic] {arrival.Definition.DisplayName} " +
                             $"({arrival.Government?.Name ?? "unaligned"}) entered " +
                             $"{_ship.CurrentSystem.Name}; {_traffic.Count} in system");
                }
            }

            // Fly it. Traffic heads for the middle of the system unless something
            // hostile is in reach, which is enough to make a system look alive
            // without a full port of upstream's personality-driven AI.
            foreach ((Ship npc, ShipView view) in _traffic)
            {
                Ship? target = ShipAi.FindTarget(npc, _traffic.Select(t => t.Ship).Append(_ship));
                Command command = target != null
                    ? ShipAi.Attack(npc, target)
                    : FleetOrders.MoveTo(npc, Point.Zero, Point.Zero, 400.0, 1.0);

                npc.Step(command);
                if (target != null)
                {
                    _field?.Add(ShipAi.AutoFire(npc, target));
                }

                view.SyncWith(npc);
            }
        }

        private void TryLand()
        {
            if (_ship == null || _ship.CurrentSystem == null || _ship.IsHyperspacing)
            {
                return;
            }

            if (_ship.Velocity.Length > 3.0)
            {
                return;
            }

            foreach (StellarObject obj in _ship.CurrentSystem.AllObjects())
            {
                if (obj.PlanetName == null || (obj.Position - _ship.Position).Length > 260.0)
                {
                    continue;
                }

                if (!_universe.Planets.TryGetValue(obj.PlanetName, out Planet? planet))
                {
                    continue;
                }

                _isLanded = true;
                _jumpAutopilot = false;
                _player.Land(planet);
                _landedOverlay = LandedOverlay.Open(this, _player, _missions, planet,
                    _ship.CurrentSystem.Name, _universe);
                _landedOverlay.Departed += OnDepart;
                GD.Print($"[flight] landed on {planet.Name} (credits={_credits:n0})");
                return;
            }
        }

        private void OnDepart()
        {
            if (_landedOverlay == null || _ship == null)
            {
                return;
            }

            _credits = _landedOverlay.Credits;
            _player.Depart();

            // The flagship may have changed at the shipyard.
            if (_player.Fleet.Flagship != null && !ReferenceEquals(_player.Fleet.Flagship, _ship))
            {
                Ship replacement = _player.Fleet.Flagship;
                replacement.Position = _ship.Position;
                replacement.Facing = _ship.Facing;
                replacement.CurrentSystem = _ship.CurrentSystem;
                _field?.Remove(_ship);
                _ship = replacement;
                _field?.Add(_ship);
                GD.Print($"[flight] flagship is now a {_ship.Definition.DisplayName}");
            }
            bool refuel = _landedOverlay.PlanetHasSpaceport;
            _landedOverlay.QueueFree();
            _landedOverlay = null;
            _isLanded = false;
            _ship.Velocity = Point.Zero;
            if (refuel)
            {
                _ship.SetLevels(fuel: _ship.MaxFuel);
            }

            GD.Print($"[flight] departed (credits={_credits:n0} fuel={_ship.Fuel:0})");
        }

        /// <summary>Upstream J behavior: target the linked system best aligned with facing.</summary>
        private void SelectJumpTarget()
        {
            StarSystem? current = _ship?.CurrentSystem;
            if (_ship == null || current == null)
            {
                return;
            }

            double bestMatch = -2.0;
            StarSystem? best = null;
            foreach (string linkName in current.Links)
            {
                if (!_universe.Systems.TryGetValue(linkName, out StarSystem? link))
                {
                    continue;
                }

                Point direction = link.MapPosition - current.MapPosition;
                double match = _ship.Facing.Unit().Dot(direction.Unit());
                if (match > bestMatch)
                {
                    bestMatch = match;
                    best = link;
                }
            }

            if (best != null)
            {
                _ship.TargetSystem = best;
                _jumpAutopilot = true;
                GD.Print($"[flight] jump autopilot → {best.Name}");
            }
        }

        /// <summary>
        /// The engine side of upstream Engine::EnterSystem: one day passes per
        /// jump, stellar objects re-place for the new date, and the scene
        /// rebuilds around the new system.
        /// </summary>
        private void HandleArrival()
        {
            StarSystem system = _ship!.CurrentSystem!;
            _lastSystem = system;
            _currentDay += 1.0;
            system.SetDate(_currentDay);

            foreach (StellarObjectView view in _stellarViews)
            {
                view.QueueFree();
            }

            _stellarViews.Clear();
            foreach (StellarObject obj in system.AllObjects())
            {
                var view = StellarObjectView.Create(obj);
                AddChild(view);
                _stellarViews.Add(view);
            }

            Point playArea = Point.Zero;
            foreach (StellarObject obj in system.AllObjects())
            {
                if (obj.PlanetName != null)
                {
                    playArea = obj.Position;
                    break;
                }
            }

            if (playArea == Point.Zero)
            {
                playArea = _ship.Position;
            }

            _keyLight.QueueFree();
            _fillLight.QueueFree();
            BuildLighting(playArea);

            if (_titleLabel != null)
            {
                _titleLabel.Text = $"{system.Name.ToUpperInvariant()}  ·  {StartShip.ToUpperInvariant()}";
            }

            GD.Print($"[flight] entered {system.Name} on day {_currentDay:0} " +
                     $"(objects={_stellarViews.Count} fuel={_ship.Fuel:0})");
        }

        /// <summary>
        /// --combat-demo: one hostile drone exercising the M2 pipeline so the
        /// visual gauntlet has combat evidence. The drone's steering here is
        /// throwaway placeholder logic; the real engagement behavior is the
        /// targeting AI milestone work in the sim layer.
        /// </summary>
        private void BuildCombatDemo(GameData universe)
        {
            _field = new CombatField
            {
                // Null means "no such weapon"; the field skips those clusters.
                WeaponLookup = name =>
                    universe.Outfits.TryGetValue(name, out Outfit? outfit) ? outfit.Weapon : null!,
            };
            _field.Add(_ship!);

            // Hostility is government-driven with no implicit aggression: both
            // sides need governments and an enmity edge or the AI stays calm.
            var merchant = new Government("Merchant");
            var raider = new Government("Pirate");
            raider.Enemies.Add("Merchant");
            _ship!.Government = merchant;

            string droneType = universe.Ships.ContainsKey("Sparrow") ? "Sparrow" : StartShip;
            _drone = universe.BuildShip(droneType);
            _drone.Government = raider;
            _drone.BuildMounts();
            // Snapshot: InstallWeapon mutates the ship's outfit collection.
            foreach (Outfit outfit in _drone.Outfits.Where(o => o.IsWeapon).ToArray())
            {
                _drone.InstallWeapon(outfit);
            }

            _drone.Position = _ship.Position + new Point(190.0, -150.0);
            _drone.Facing = new Angle(180.0);
            // Charge both sides' batteries: firing costs stored energy.
            _drone.SetLevels(energy: _drone.MaxEnergy);
            _ship.SetLevels(energy: _ship.MaxEnergy);
            _field.Add(_drone);

            _droneView = new ShipView { Name = "Drone" };
            AddChild(_droneView);
            _droneView.SyncWith(_drone);

            _effects = new CombatEffects { Name = "CombatEffects" };
            AddChild(_effects);
            GD.Print($"[flight] combat demo: hostile {droneType} with " +
                     $"{_drone.Mounts.Count} mounts engaged");
        }

        private void StepCombatDemo()
        {
            if (_field == null || _drone == null || _ship == null)
            {
                return;
            }

            // Sim-owned engagement, in the agreed frame order:
            // target → steer → fire → field.Step. Reload clocks now advance inside
            // Ship.Step, so stepping them here as well would reload at double rate.
            Ship? target = ShipAi.FindTarget(_drone, _field.Ships);
            _drone.Step(target is null ? Command.None : ShipAi.Attack(_drone, target));
            _droneView.SyncWith(_drone);
            if (target is not null)
            {
                _field.Add(ShipAi.AutoFire(_drone, target));
            }

            foreach (HitReport report in _field.Step())
            {
                Node3D targetView = report.Target == _ship ? _shipView : _droneView;
                if (report.Events.HasFlag(ShipEvent.Destroy))
                {
                    _effects.SpawnExplosion(report.Target.Position, 3.5f);
                    targetView.Visible = false;
                }
                else if (report.Target.Shields > 0.0)
                {
                    CombatEffects.FlashShields(targetView);
                }
                else
                {
                    _effects.SpawnExplosion(report.Projectile.Position, 1f);
                }
            }

            _effects.SyncProjectiles(_field.Projectiles);
        }

        private static StyleBoxFlat HudPanelStyle()
        {
            var style = new StyleBoxFlat
            {
                BgColor = new Color(0.05f, 0.07f, 0.11f, 0.55f),
                BorderColor = new Color(0.35f, 0.55f, 0.75f, 0.5f),
                CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
                ContentMarginLeft = 12, ContentMarginRight = 12,
                ContentMarginTop = 8, ContentMarginBottom = 8,
            };
            style.SetBorderWidthAll(1);
            return style;
        }

        private void BuildHud(string? errorMessage)
        {
            var hud = new CanvasLayer { Name = "Hud" };
            AddChild(hud);

            // Top-left: identity + telemetry, three type tiers.
            var panel = new PanelContainer();
            panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            panel.Position = new Vector2(14, 14);
            panel.AddThemeStyleboxOverride("panel", HudPanelStyle());
            hud.AddChild(panel);

            var column = new VBoxContainer();
            column.AddThemeConstantOverride("separation", 2);
            panel.AddChild(column);

            _titleLabel = new Label { Text = errorMessage ?? $"{_startSystem.ToUpperInvariant()}  ·  {StartShip.ToUpperInvariant()}" };
            _titleLabel.AddThemeFontSizeOverride("font_size", 20);
            _titleLabel.AddThemeColorOverride("font_color", new Color(0.88f, 0.93f, 1.0f));
            column.AddChild(_titleLabel);

            _statusLabel = new Label();
            _statusLabel.AddThemeFontSizeOverride("font_size", 15);
            _statusLabel.AddThemeColorOverride("font_color", new Color(0.65f, 0.75f, 0.85f));
            // Outline keeps the digits readable over the star and masks
            // proportional-font reflow.
            _statusLabel.AddThemeConstantOverride("outline_size", 3);
            _statusLabel.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
            column.AddChild(_statusLabel);
            if (errorMessage != null)
            {
                _statusLabel.Text = " ";
            }

            // Bottom-left, out of the hero corner: the keybinds.
            var keysPanel = new PanelContainer();
            keysPanel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
            keysPanel.AddThemeStyleboxOverride("panel", HudPanelStyle());
            hud.AddChild(keysPanel);
            var keys = new Label { Text = "↑ thrust · ←/→ turn · ↓ retrograde brake · J jump · L land · wheel zoom · WASD too" };
            keys.AddThemeFontSizeOverride("font_size", 12);
            keys.AddThemeColorOverride("font_color", new Color(0.40f, 0.48f, 0.56f));
            keysPanel.AddChild(keys);
            keysPanel.Position = new Vector2(14, -14 - 34);
        }

        private void UpdateHud()
        {
            if (_statusLabel == null || _ship == null)
            {
                return;
            }

            double speed = _ship.Velocity.Length * Ship.FramesPerSecond;
            double pct = _ship.Velocity.Length / Math.Max(_ship.MaxVelocity, 1e-9) * 100.0;
            string status = _ship.IsHyperspacing
                ? $"   HYPERSPACE {(_ship.IsEnteringHyperspace ? "→ " + _ship.HyperspaceSystem!.Name : "· arriving")}"
                : _jumpAutopilot && _ship.TargetSystem != null
                    ? $"   JUMP → {_ship.TargetSystem.Name}"
                    : string.Empty;
            _statusLabel.Text =
                $"{speed:0} KM/S · {pct:0}%   HDG {_ship.Facing.AbsDegrees:000}°   FUEL {_ship.Fuel:0}{status}";
        }

        public override void _ExitTree()
        {
            if (_ship != null)
            {
                GD.Print($"[flight] exit: simFrames={_simFrames} speed={_ship.Velocity.Length:0.###}px/f " +
                         $"heading={_ship.Facing.AbsDegrees:0.#}deg pos={_ship.Position}");
            }
        }

        private void ParseUserArgs()
        {
            string[] userArgs = OS.GetCmdlineUserArgs();
            if (userArgs.Length > 0)
            {
                GD.Print($"[flight] user args: {string.Join(" ", userArgs)}");
            }

            foreach (string arg in userArgs)
            {
                if (arg.StartsWith("--capture=", StringComparison.Ordinal))
                {
                    _capturePath = arg["--capture=".Length..];
                }
                else if (arg.StartsWith("--capture-frames=", StringComparison.Ordinal) &&
                         int.TryParse(arg["--capture-frames=".Length..], out int frames))
                {
                    _captureFrames = frames;
                }
                else if (arg == "--autopilot")
                {
                    _autopilot = true;
                }
                else if (arg == "--combat-demo")
                {
                    _combatDemo = true;
                }
                else if (arg == "--land-at-start")
                {
                    _landAtStart = true;
                }
                else if (arg.StartsWith("--landed-tab=", StringComparison.Ordinal) &&
                         int.TryParse(arg["--landed-tab=".Length..], out int tab))
                {
                    // Capture aid: the landed screen's other counters need a keypress
                    // to reach, and a headless capture has no keyboard.
                    LandedOverlay.OpenOnCounter = tab;
                }
            }

            if (_capturePath != null && DisplayServer.GetName() == "headless")
            {
                GD.Print("[flight] capture requested but the display server is headless; skipping.");
                _capturePath = null;
            }
        }

        private void SaveCapture()
        {
            Image image = GetViewport().GetTexture().GetImage();
            Error err = image.SavePng(_capturePath);
            GD.Print($"[flight] capture {(err == Error.Ok ? "saved" : $"FAILED ({err})")}: {_capturePath}");
        }

        /// <summary>
        /// Proleptic-Gregorian day count from year 1, matching upstream
        /// Date::DaysSinceEpoch (365.2425-day calendar; stellar angles depend
        /// on absolute alignment, so the epoch must match).
        /// </summary>
        internal static double DaysSinceEpoch(int year, int month, int day)
        {
            int[] mdays = { 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334 };
            long y = year - 1;
            long days = 365L * y + y / 4 - y / 100 + y / 400;
            days += mdays[month - 1];
            bool isLeap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
            if (isLeap && month > 2)
            {
                days += 1;
            }

            return days + day;
        }
    }
}
