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
        // debt. Until conversations can grant ships, the flight scene picks one — but
        // from the DATA rather than by name, because a hardcoded hull is a hull that
        // does not exist in somebody else's universe.
        private string _startShip = "Shuttle";

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
        private Government? _playerGovernment;

        // How the galaxy reacts to what the player does. Reputation is one of the
        // things the directive names, and nothing moved it until this was wired in.
        private Politics? _politics;

        // One seeded stream for everything that spawns, so a run is reproducible.
        // Both spawners take injected randomness precisely so this is possible, and
        // nothing was supplying any: two headless runs of the same smoke test produced
        // different fights, which makes a capture impossible to compare against an
        // earlier one and a smoke run in CI a coin toss. Starfield already pins its own
        // seed (Starfield.cs:38) for the same reason.
        private const int SpawnSeed = 3013;
        private readonly Random _spawnRandom = new Random(SpawnSeed);
        private bool _missionSmoke;
        private bool _saveSmoke;
        private bool _smokeFire;
        private ActiveMission? _smokeJob;
        private Ship? _smokeTarget;
        private int _smokeFrames;
        private bool _fireKeyWasDown;

        // Mission NPCs are held apart from ambient traffic: they are not subject to
        // the traffic cap or the distance cull, because a bounty target that despawns
        // for drifting too far is a bounty that can never be collected.
        private readonly List<(Ship Ship, ShipView View)> _missionShips =
            new List<(Ship, ShipView)>();
        private readonly List<(Ship Ship, ShipView View)> _traffic =
            new List<(Ship, ShipView)>();

        /// <summary>Most ships to keep in flight at once, so a busy system stays playable.</summary>
        private const int TrafficLimit = 12;
        private AsteroidFieldView? _asteroidField;

        // The interface around the game: menu, pause, status, map, controls, options.
        private GameUi _ui = null!;   // built in _Ready
        private LandedOverlay? _landedOverlay;
        private StarSystem? _lastSystem;
        private DirectionalLight3D _keyLight = null!;  // set by BuildLighting
        private DirectionalLight3D _fillLight = null!; // set by BuildLighting

        public override void _Ready()
        {
            ParseUserArgs();

            // Saved graphics preferences, before anything is drawn.
            GameSettings.Apply();

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

            // The system's own asteroid belts, which the data has always carried.
            _asteroidField = AsteroidFieldView.Create(system);
            AddChild(_asteroidField);

            _startShip = ChooseStartingShip(universe, _startPlanet);
            _ship = universe.BuildShip(_startShip, out List<string> missingOutfits);
            if (missingOutfits.Count > 0)
            {
                GD.Print($"[flight] warning: missing outfits on {_startShip}: {string.Join(", ", missingOutfits)}");
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
            _politics = new Politics(universe);

            int Roll(int n) => n <= 0 ? 0 : _spawnRandom.Next(n);
            _spawner = new FleetSpawner(universe, Roll);
            _missions = new MissionLog(_player, new NpcSpawner(universe, _spawner, Roll));

            _ui = new GameUi { Name = "Ui" };
            _ui.Bind(_player, _missions, universe, () => _ship);
            _ui.DestinationChosen += OnDestinationChosen;
            _ui.QuitRequested += () => GetTree().Quit();
            _ui.SaveRequested += SaveNow;
            _ui.LoadRequested += LoadNow;
            AddChild(_ui);

            _shipView.SyncWith(_ship);

            _camera = new CameraRig { Name = "CameraRig" };
            AddChild(_camera);
            _camera.Snap(_ship);

            // Combat exists in every flight, not only in the demo. It used to be
            // built solely behind --combat-demo, which meant a normal game had no
            // projectile field at all: traffic fired into nothing, the player could
            // not shoot, and every bounty in the galaxy was unwinnable.
            BuildCombat(universe);

            if (_combatDemo)
            {
                BuildCombatDemo(universe);
            }

            BuildHud(null);
            GD.Print($"[flight] data={EsData.DataPath} system={system.Name} " +
                     $"objects={_stellarViews.Count} ship={_startShip} " +
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

            // Landed: the sim is frozen and the overlay owns input, including the keys
            // the shell would otherwise claim.
            if (_ui != null)
            {
                _ui.Suspended = _isLanded;
            }

            if (_isLanded)
            {
                return;
            }

            // Losing the flagship ends the run. Without this there was no death at
            // all: a destroyed hull had its mesh hidden and everything else carried on,
            // so the HUD, the landing key and the controls all kept answering for a
            // ship that no longer existed.
            if (_ship.IsDestroyed && _ui != null && !_ui.IsGameOver)
            {
                GD.Print($"[flight] destroyed after {_simFrames} frames in {_ship.CurrentSystem?.Name}");
                _effects?.SpawnExplosion(_ship.Position, 6f);
                _shipView.Visible = false;
                _ui.Show(UiScreen.Destroyed);
                return;
            }

            // A UI screen holds the simulation the same way landing does. Without this
            // the galaxy carries on while the player reads the map, and they come back
            // to a fight they never saw start.
            if (_ui != null && _ui.IsModal)
            {
                return;
            }

            if (_saveSmoke)
            {
                StepSaveSmoke();
                return;
            }

            // The game opens on its menu. Captures and the landed-at-start path skip
            // it: a screenshot of the menu is not a screenshot of the game.
            if (_simFrames == 1 && _capturePath == null && !_landAtStart && !_missionSmoke
                && !_saveSmoke)
            {
                _ui?.Show(UiScreen.MainMenu);
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
            else if (_missionSmoke && _smokeTarget != null && !_smokeTarget.IsDestroyed)
            {
                // The smoke run has no pilot, so it borrows the one the NPCs use.
                // Without this the player sits still, the target circles out of arc,
                // and a fight that works perfectly well never resolves.
                command = ShipAi.Attack(_ship, _smokeTarget);
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
            _asteroidField?.Follow(WorldSpace.ToWorld(_ship.Position));
            StepTraffic();
            StepMissionShips();
            if (_missionSmoke)
            {
                StepMissionSmoke();
            }
            StepPlayerFire();
            if (_jumpAutopilot && _ship.TryCommitJump())
            {
                _jumpAutopilot = false;
                GD.Print($"[flight] jump committed → {_ship.HyperspaceSystem!.Name}");
            }

            _shipView.SyncWith(_ship);
            StepCombatDemo();

            // One field step per frame, after everything that could have fired.
            StepCombat();
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

        /// <summary>
        /// The hull a new pilot starts in: the cheapest thing the starting world's
        /// shipyard will sell them.
        /// </summary>
        /// <remarks>
        /// Chosen from the data rather than named, so the scene works in any universe.
        /// "The cheapest hull on the board where you are standing" is also the right
        /// answer in fiction — it is what someone taking out their first loan could
        /// actually afford — and it degrades sensibly: if that world sells nothing, the
        /// cheapest hull anywhere will do.
        /// </remarks>
        private static string ChooseStartingShip(GameData universe, string startPlanet)
        {
            IEnumerable<string> candidates = Array.Empty<string>();

            if (universe.Planets.TryGetValue(startPlanet, out Planet? home))
            {
                candidates = Trading.ShipsFor(universe, home)
                    .Where(universe.Ships.ContainsKey);
            }

            var stocked = candidates.ToList();
            if (stocked.Count == 0)
            {
                stocked = universe.Ships.Values
                    .Where(d => d.Attributes.Get("cost") > 0)
                    .Select(d => d.DisplayName)
                    .ToList();
            }

            if (stocked.Count == 0)
            {
                return universe.Ships.Keys.FirstOrDefault() ?? "Shuttle";
            }

            return stocked
                .OrderBy(name => universe.Ships[name].Attributes.Get("cost"))
                .ThenBy(name => name, StringComparer.Ordinal)
                .First();
        }

        /// <summary>
        /// Course set from the galaxy map. The J key still performs the jump, so the
        /// map decides WHERE and the player decides WHEN.
        /// </summary>
        private void OnDestinationChosen(StarSystem system)
        {
            if (_ship == null)
            {
                return;
            }

            _ship.TargetSystem = system;
            _jumpAutopilot = true;
            GD.Print($"[flight] course set for {system.Name}");
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

            // Leaving the ground is TakeOff's job, below, because what the world
            // services depends on which world the player is still standing on. Clearing
            // the planet here first would make every departure look like one from a
            // world with no port.

            // The flagship may have changed at the shipyard.
            if (_player.Fleet.Flagship != null && !ReferenceEquals(_player.Fleet.Flagship, _ship))
            {
                Ship replacement = _player.Fleet.Flagship;
                replacement.Position = _ship.Position;
                replacement.Facing = _ship.Facing;
                replacement.CurrentSystem = _ship.CurrentSystem;
                replacement.Government = _playerGovernment;

                // A hull bought at a shipyard arrives with its hardpoints unbuilt and
                // whatever it came with uninstalled, so a newly bought warship would
                // fly out of the yard unable to fire.
                replacement.BuildMounts();
                foreach (Outfit outfit in replacement.Outfits.Where(o => o.IsWeapon).ToArray())
                {
                    replacement.InstallWeapon(outfit);
                }

                _field?.Remove(_ship);
                _ship = replacement;
                _field?.Add(_ship);
                GD.Print($"[flight] flagship is now a {_ship.Definition.DisplayName}");
            }
            _landedOverlay.QueueFree();
            _landedOverlay = null;
            _isLanded = false;
            _ship.Velocity = Point.Zero;

            // Servicing the fleet is the simulation's rule, not this screen's: it
            // restores shields, hull, energy and fuel on every ship that is neither
            // parked nor a wreck, or only what each ship makes for itself at a world
            // with no port. This used to top up the flagship's fuel and nothing else,
            // so every escort carried its battle damage for the rest of the game and no
            // hull was ever repaired anywhere.
            _player.TakeOff();

            GD.Print($"[flight] departed (credits={_credits:n0} fuel={_ship.Fuel:0} " +
                     $"hull={_ship.Hull:0}/{_ship.MaxHull:0})");
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

            // Escorts jump with the flagship. Without this an accompany objective
            // fails the moment the player leaves the system the job was taken in,
            // which made every escort job in the galaxy impossible to finish.
            if (_missions != null && !ReferenceEquals(_lastSystem, system))
            {
                IReadOnlyList<Ship> came = _missions.CarryAccompanying(_lastSystem, system);
                if (came.Count > 0)
                {
                    GD.Print($"[mission] {came.Count} escorted ship(s) followed to {system.Name}");
                }
            }

            _lastSystem = system;

            // A jump takes a day, and the day has to pass on the PLAYER'S calendar --
            // not just on the counter that positions the stellar objects. Advancing
            // only the render-side counter meant no day ever passed in game: no
            // deadline could expire, no salary was owed, no depreciation ticked, and
            // MissionLog.Step -- which fires the fail triggers -- was never called at
            // all. The counter is derived from the player's date afterwards so there is
            // one calendar rather than two that can drift apart.
            AdvanceDay();
            RebuildSystemViews(system);

            GD.Print($"[flight] entered {system.Name} on day {_currentDay:0} " +
                     $"(objects={_stellarViews.Count} fuel={_ship.Fuel:0})");
        }

        /// <summary>
        /// Replaces everything in the scene that belongs to one system: its worlds,
        /// its asteroid belts and the lighting keyed to where the action is.
        /// </summary>
        /// <remarks>
        /// The asteroid field used to be built once, for the starting system, and never
        /// touched again — so after one jump the player flew through the belts of the
        /// system they had left, in a system whose own rocks never appeared.
        /// </remarks>
        private void RebuildSystemViews(StarSystem system)
        {
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

            _asteroidField?.QueueFree();
            _asteroidField = AsteroidFieldView.Create(system);
            AddChild(_asteroidField);

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
                playArea = _ship!.Position;
            }

            _keyLight.QueueFree();
            _fillLight.QueueFree();
            BuildLighting(playArea);

            if (_titleLabel != null)
            {
                _titleLabel.Text = $"{system.Name.ToUpperInvariant()}  ·  {_startShip.ToUpperInvariant()}";
            }
        }

        /// <summary>
        /// --combat-demo: one hostile drone exercising the M2 pipeline so the
        /// visual gauntlet has combat evidence. The drone's steering here is
        /// throwaway placeholder logic; the real engagement behavior is the
        /// targeting AI milestone work in the sim layer.
        /// </summary>
        /// <summary>
        /// The projectile field every flight runs in, and the flag the player flies.
        /// </summary>
        /// <remarks>
        /// Hostility is government-driven, and for the player it is reputation-driven
        /// specifically: a ship with no government is nobody's enemy, so raiders
        /// ignore it and it can never be attacked. The player therefore needs a
        /// government of its own, marked as the player's, against which every other
        /// government's standing is read.
        /// </remarks>
        private void BuildCombat(GameData universe)
        {
            _field = new CombatField
            {
                // Null means "no such weapon"; the field skips those clusters.
                WeaponLookup = name =>
                    universe.Outfits.TryGetValue(name, out Outfit? outfit) ? outfit.Weapon : null!,
            };
            _field.Add(_ship!);

            _playerGovernment = new Government("Player") { IsPlayer = true };
            _ship!.Government = _playerGovernment;

            // The hardpoints the hull was designed with. Without this the player's
            // ship carries no mounts at all - not empty ones, none - so it can never
            // fire, never be armed at an outfitter, and never finish a combat job.
            // Every NPC in the game called this; the player alone never did.
            _ship.BuildMounts();

            // Weapons carried have to be installed in mounts before any of them can
            // fire. Snapshot first: InstallWeapon mutates the outfit collection.
            foreach (Outfit outfit in _ship.Outfits.Where(o => o.IsWeapon).ToArray())
            {
                _ship.InstallWeapon(outfit);
            }

            _ship.SetLevels(energy: _ship.MaxEnergy);

            _effects = new CombatEffects { Name = "CombatEffects" };
            AddChild(_effects);
        }

        private void BuildCombatDemo(GameData universe)
        {
            // Hostility is government-driven with no implicit aggression: both
            // sides need governments and an enmity edge or the AI stays calm.
            var raider = new Government("Pirate");
            raider.SetReputation(-1000.0);

            string droneType = universe.Ships.ContainsKey("Sparrow") ? "Sparrow" : _startShip;
            _drone = universe.BuildShip(droneType);
            _drone.Government = raider;
            _drone.BuildMounts();
            // Snapshot: InstallWeapon mutates the ship's outfit collection.
            foreach (Outfit outfit in _drone.Outfits.Where(o => o.IsWeapon).ToArray())
            {
                _drone.InstallWeapon(outfit);
            }

            _drone.Position = _ship!.Position + new Point(190.0, -150.0);
            _drone.Facing = new Angle(180.0);
            // Firing costs stored energy, so charge the battery.
            _drone.SetLevels(energy: _drone.MaxEnergy);
            _field!.Add(_drone);

            _droneView = new ShipView { Name = "Drone" };
            AddChild(_droneView);
            _droneView.SyncWith(_drone);

            GD.Print($"[flight] combat demo: hostile {droneType} with " +
                     $"{_drone.Mounts.Count} mounts engaged");
        }

        /// <summary>
        /// Puts the ships that accepted missions placed into the world, and takes them
        /// out again when they die or the player leaves.
        /// </summary>
        /// <remarks>
        /// Mission NPCs are built once, when the job is taken, and live in the mission
        /// log from then on. This only decides which of them currently deserve a mesh
        /// and a slot in the projectile field. Ambient traffic is culled for drifting
        /// too far; these deliberately are not, because a bounty target that vanishes
        /// is a job that cannot be finished.
        /// </remarks>
        private void StepMissionShips()
        {
            if (_ship?.CurrentSystem == null || _missions == null)
            {
                return;
            }

            for (int i = _missionShips.Count - 1; i >= 0; i--)
            {
                (Ship npc, ShipView view) = _missionShips[i];
                if (!npc.IsDestroyed && ReferenceEquals(npc.CurrentSystem, _ship.CurrentSystem))
                {
                    continue;
                }

                if (npc.IsDestroyed)
                {
                    _effects?.SpawnExplosion(npc.Position, 4.5f);
                }

                view.QueueFree();
                _field?.Remove(npc);
                _missionShips.RemoveAt(i);
            }

            foreach (Ship npc in _missions.NpcShipsIn(_ship.CurrentSystem))
            {
                if (_missionShips.Any(m => ReferenceEquals(m.Ship, npc)))
                {
                    continue;
                }

                var view = new ShipView { Name = $"MissionNpc{_missionShips.Count}" };
                AddChild(view);
                view.SyncWith(npc);
                _missionShips.Add((npc, view));
                _field?.Add(npc);

                ActiveMission? owner = _missions.MissionOwning(npc);
                GD.Print($"[mission] {npc.Definition.DisplayName} " +
                         $"({npc.Government?.Name ?? "unaligned"}) on station in " +
                         $"{_ship.CurrentSystem.Name} for \"{owner?.Mission.DisplayName}\"");
            }

            // Fly them. A disabled hull - a derelict waiting to be boarded - has no
            // say in where it goes, which is what makes it something to catch rather
            // than something to chase.
            foreach ((Ship npc, ShipView view) in _missionShips)
            {
                if (!npc.IsDisabled)
                {
                    IEnumerable<Ship> around = _traffic.Select(t => t.Ship)
                        .Concat(_missionShips.Select(m => m.Ship))
                        .Append(_ship);

                    Ship? target = ShipAi.FindTarget(npc, around);
                    npc.Step(target != null
                        ? ShipAi.Attack(npc, target)
                        : FleetOrders.MoveTo(npc, Point.Zero, Point.Zero, 400.0, 1.0));

                    if (target != null)
                    {
                        _field?.Add(ShipAi.AutoFire(npc, target));
                    }
                }

                view.SyncWith(npc);
            }
        }

        /// <summary>
        /// The player's guns. Space fires everything that will bear on the current
        /// target, or straight ahead when nothing is selected.
        /// </summary>
        /// <remarks>
        /// Held down rather than tapped: reload clocks already gate the rate inside
        /// <c>Ship.Step</c>, so a held key fires as fast as the hardware allows and no
        /// faster, which is how upstream reads its primary-fire command.
        /// </remarks>
        private void StepPlayerFire()
        {
            if (_field == null || _ship == null)
            {
                return;
            }

            bool firing = _smokeFire ||
                          Input.IsPhysicalKeyPressed(Key.Space) ||
                          Input.IsMouseButtonPressed(MouseButton.Left);

            _fireKeyWasDown = firing;
            if (!firing)
            {
                return;
            }

            // A person can always keep shooting a crippled hull - FireAll does not ask.
            // The smoke pilot fires through the AI, which by default leaves disabled
            // ships alone, so it is told not to: finishing the kill is the job.
            Ship? target = ShipAi.FindTarget(
                _ship,
                _traffic.Select(t => t.Ship).Concat(_missionShips.Select(m => m.Ship)),
                attackDisabled: _smokeFire);

            // A person holding the key fires everything, aimed or not - that is what
            // the key means, and aiming is their job. The smoke run has no person, so
            // it fires the way an NPC pilot does: only what will actually bear.
            _field.Add(_smokeFire && !Input.IsPhysicalKeyPressed(Key.Space)
                ? ShipAi.AutoFire(_ship, target)
                : _ship.FireAll(target, _ship.Government));
        }

        /// <summary>
        /// Advances every projectile in flight and turns what they hit into damage,
        /// explosions and mission progress.
        /// </summary>
        /// <remarks>
        /// The last of those is the wire that was missing: a bounty target could be
        /// destroyed and nothing told the mission log, so the job stayed open forever.
        /// </remarks>
        private void StepCombat()
        {
            if (_field == null || _ship == null)
            {
                return;
            }

            foreach (HitReport report in _field.Step())
            {
                Node3D? targetView = ViewOf(report.Target);

                // What the player did, and what every government thinks of it. Only
                // the player's own shots move reputation; NPCs fighting each other are
                // not the player's business.
                if (ReferenceEquals(report.Projectile.Government, _playerGovernment))
                {
                    ReportOffence(report);
                }

                if (report.Events.HasFlag(ShipEvent.Destroy))
                {
                    _effects?.SpawnExplosion(report.Target.Position, 3.5f);
                    if (targetView != null)
                    {
                        targetView.Visible = false;
                    }
                }
                else if (report.Target.Shields > 0.0 && targetView != null)
                {
                    CombatEffects.FlashShields(targetView);
                }
                else
                {
                    _effects?.SpawnExplosion(report.Projectile.Position, 1f);
                }

                // Anything the player did to a mission ship is mission progress.
                if (report.Events != ShipEvent.None)
                {
                    ReportMissionEvent(report.Target, report.Events);
                }
            }

            _effects?.SyncProjectiles(_field.Projectiles);
        }

        /// <summary>Tells the mission log what happened, and says so on screen.</summary>
        private void ReportMissionEvent(Ship target, ShipEvent happened)
        {
            if (_missions == null)
            {
                return;
            }

            foreach (ActiveMission touched in _missions.ReportShipEvent(target, happened))
            {
                GD.Print($"[mission] {happened} on {target.Definition.DisplayName} " +
                         $"advances \"{touched.Mission.DisplayName}\"");
            }
        }

        /// <summary>The mesh standing in for a ship, if it has one on screen.</summary>
        private Node3D? ViewOf(Ship ship)
        {
            if (ReferenceEquals(ship, _ship))
            {
                return _shipView;
            }

            if (_drone != null && ReferenceEquals(ship, _drone))
            {
                return _droneView;
            }

            foreach ((Ship other, ShipView view) in _traffic)
            {
                if (ReferenceEquals(other, ship))
                {
                    return view;
                }
            }

            foreach ((Ship other, ShipView view) in _missionShips)
            {
                if (ReferenceEquals(other, ship))
                {
                    return view;
                }
            }

            return null;
        }

        /// <summary>
        /// Flies the demo drone. Stepping the projectile field is no longer this
        /// method's job - <see cref="StepCombat"/> does it for every flight, and
        /// doing it here as well would advance every shot twice per frame.
        /// </summary>
        /// <summary>
        /// --mission-smoke: take a bounty, fly to it, and shoot it, with no keyboard.
        /// </summary>
        /// <remarks>
        /// Every part of this chain has unit coverage, and the chain still has to be
        /// watched end to end in a real engine process, because the parts that connect
        /// it are the ones tests do not see: whether the projectile field exists in a
        /// normal flight at all, whether placed hulls get a mesh, whether a kill
        /// reaches the mission log. Combat used to exist only behind --combat-demo, and
        /// nothing failed - the game simply had no weapons.
        ///
        /// The jump itself is skipped rather than flown. Hyperspace is covered
        /// elsewhere and takes hundreds of frames; what is under test here is what
        /// happens once the player arrives.
        /// </remarks>
        private void StepMissionSmoke()
        {
            if (_ship == null || _missions == null || _universe == null)
            {
                return;
            }

            _smokeFrames++;

            if (_smokeJob == null)
            {
                if (_smokeFrames < 3)
                {
                    return;
                }

                Mission? job = _universe.Missions.Values.FirstOrDefault(m =>
                    m.IsJob && m.Npcs.Any(n => (n.SucceedIf & ShipEvent.Destroy) != 0));

                if (job == null)
                {
                    GD.Print("[smoke] FAIL: no bounty job in the dataset");
                    GetTree().Quit(1);
                    return;
                }

                _smokeJob = _missions.Accept(job);
                if (_smokeJob == null || _smokeJob.Npcs.Count == 0)
                {
                    GD.Print($"[smoke] FAIL: accepting \"{job.DisplayName}\" placed nothing");
                    GetTree().Quit(1);
                    return;
                }

                NpcInstance npc = _smokeJob.Npcs[0];
                int armed = npc.Ships[0].Mounts.Count(m => !m.IsEmpty);
                GD.Print($"[smoke] took \"{job.DisplayName}\": {npc.Ships.Count} hull(s) " +
                         $"of {npc.Ships[0].Definition.DisplayName} " +
                         $"({npc.Ships[0].Government?.Name}) in {npc.System?.Name}; " +
                         $"target carries {armed}/{npc.Ships[0].Mounts.Count} armed mounts, " +
                         $"hostile to player = {npc.Ships[0].Government?.IsEnemy(_ship.Government)}");

                // Stand where the job sent us, close enough to shoot.
                if (npc.System != null)
                {
                    _ship.CurrentSystem = npc.System;
                    _player.EnterSystem(npc.System);
                    HandleArrival();
                }

                _ship.Position = npc.Ships[0].Position + new Point(320.0, 0.0);
                _ship.Velocity = Point.Zero;
                _ship.Facing = Angle.FromPoint(npc.Ships[0].Position - _ship.Position);
                _smokeFire = true;
                GD.Print($"[smoke] player {_ship.Definition.DisplayName}: " +
                         $"{_ship.Mounts.Count(m => !m.IsEmpty)}/{_ship.Mounts.Count} armed, " +
                         $"range {ShipAi.ShortestWeaponRange(_ship):0} vs target " +
                         $"{ShipAi.ShortestWeaponRange(npc.Ships[0]):0}");
                return;
            }

            NpcInstance target = _smokeJob.Npcs[0];
            int alive = target.Survivors.Count();
            _smokeTarget = target.Survivors.FirstOrDefault();

            if (_smokeFrames % 60 == 0)
            {
                Ship? enemy = _smokeTarget;
                double gap = enemy is null ? 0.0 : (enemy.Position - _ship.Position).Length;
                GD.Print($"[smoke] frame {_smokeFrames}: {alive} alive | " +
                         $"player {_ship.Shields:0}s/{_ship.Hull:0}h/{_ship.Energy:0}e | " +
                         $"target {enemy?.Shields ?? 0:0}s/{enemy?.Hull ?? 0:0}h/" +
                         $"{enemy?.Energy ?? 0:0}e | gap {gap:0} | " +
                         $"{_field?.Projectiles.Count ?? 0} in flight");
            }

            // What this asserts is that the MACHINERY works end to end: a job is
            // offered, accepted, its target placed where the job points, a fight joined
            // and resolved, and the mission log told about it. It deliberately does not
            // assert that the player WINS. Who wins is balance, and balance shifts
            // whenever a hull or a weapon is retuned; a test that fails when the
            // starting ship gets a little weaker reports a regression that is not one.
            if (target.HasSucceeded(_ship.CurrentSystem))
            {
                GD.Print($"[smoke] PASS: objective met after {_smokeFrames} frames; " +
                         $"player hull {_ship.Hull:0}/{_ship.MaxHull:0}; " +
                         $"mission still open pending hand-in at " +
                         $"{_smokeJob.Destination ?? "(nowhere)"}");
                GetTree().Quit(0);
                return;
            }

            if (_ship.IsDestroyed || _ship.IsDisabled)
            {
                // Losing is a resolution too, and the one that proves the target can
                // actually fight back. Disabled counts because upstream stops attacking
                // a crippled hull and boards it instead - to capture it, or to strip it
                // - and that endgame is not modelled yet, so the fight would otherwise
                // sit frozen forever with neither side able to end it.
                string how = _ship.IsDestroyed ? "destroyed" : "disabled";
                GD.Print($"[smoke] PASS: fight resolved after {_smokeFrames} frames — " +
                         $"the player was {how}, and the bounty went to " +
                         $"{_smokeJob.Destination ?? "(nowhere)"}. " +
                         $"INCOMPLETE: nothing boards a disabled hull, so a crippled " +
                         $"ship is neither captured nor finished.");
                GetTree().Quit(0);
                return;
            }

            if (_smokeFrames > 5400)
            {
                GD.Print($"[smoke] FAIL: {alive} target(s) still alive after {_smokeFrames} frames");
                GetTree().Quit(1);
            }
        }

        private void StepCombatDemo()
        {
            if (_field == null || _drone == null || _ship == null)
            {
                return;
            }

            // Sim-owned engagement, in the agreed frame order:
            // target -> steer -> fire -> field.Step. Reload clocks advance inside
            // Ship.Step, so stepping them here as well would reload at double rate.
            Ship? target = ShipAi.FindTarget(_drone, _field.Ships);
            _drone.Step(target is null ? Command.None : ShipAi.Attack(_drone, target));
            _droneView.SyncWith(_drone);
            if (target is not null)
            {
                _field.Add(ShipAi.AutoFire(_drone, target));
            }
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

            _titleLabel = new Label { Text = errorMessage ?? $"{_startSystem.ToUpperInvariant()}  ·  {_startShip.ToUpperInvariant()}" };
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
            // The discoverability line. A player who never opens a menu still has to
            // find out that a map and a status screen exist, so the keys that open them
            // are named here rather than only inside the screens they open.
            var keys = new Label
            {
                Text = "↑ thrust · ←/→ turn · ↓ brake · L land · J jump · " +
                       "M map · I status · F1 controls · ESC menu",
            };
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
                else if (arg == "--mission-smoke")
                {
                    _missionSmoke = true;
                }
                else if (arg == "--save-smoke")
                {
                    _saveSmoke = true;
                }
                else if (arg == "--land-at-start")
                {
                    _landAtStart = true;
                }
                else if (arg.StartsWith("--ui-screen=", StringComparison.Ordinal) &&
                         Enum.TryParse(arg["--ui-screen=".Length..], ignoreCase: true,
                                       out UiScreen screen))
                {
                    // Capture aid: every screen needs a keypress to reach, and a
                    // headless capture has no keyboard.
                    GameUi.OpenAtStart = screen;
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
        /// <summary>
        /// Tells the galaxy what the player just did to somebody's ship.
        /// </summary>
        /// <remarks>
        /// Only the transitions carry a reputation cost, not every hit: upstream prices
        /// disabling, boarding, capturing and destroying, and weights each by the crew
        /// aboard. Shooting an unmanned drone costs no standing, which is why a count
        /// of zero is a legitimate outcome rather than a bug.
        /// </remarks>
        private void ReportOffence(HitReport report)
        {
            if (_politics is null || report.Target.Government is null)
            {
                return;
            }

            int crew = Math.Max(1, (int)report.Target.Attributes.Get("required crew"));

            if (report.Events.HasFlag(ShipEvent.Destroy))
            {
                _politics.Offend(report.Target.Government, "destroy", crew);
            }
            else if (report.Events.HasFlag(ShipEvent.Disable))
            {
                _politics.Offend(report.Target.Government, "disable", crew);
            }
        }

        /// <summary>
        /// --save-smoke: writes a save, changes the player, loads it back, and reports
        /// whether the change was undone.
        /// </summary>
        /// <remarks>
        /// Persistence is exactly the kind of feature the sim suite cannot vouch for:
        /// the serialiser round-tripped correctly under test for as long as nothing in
        /// the game called it. This drives the real path in a real engine process.
        /// </remarks>
        private void StepSaveSmoke()
        {
            // A couple of frames in, so the world is fully built first.
            if (_simFrames != 3 || _player is null)
            {
                return;
            }

            long creditsBefore = _player.Credits;
            DateTime dateBefore = _player.Date;

            if (!SaveNow())
            {
                GD.Print("[smoke] FAIL: could not write a save");
                GetTree().Quit();
                return;
            }

            // Change the world, so a load that does nothing cannot look like success.
            _player.AddCredits(-123_456);
            _player.AdvanceDays(9);

            if (!LoadNow())
            {
                GD.Print("[smoke] FAIL: could not load the save back");
                GetTree().Quit();
                return;
            }

            bool creditsBack = _player.Credits == creditsBefore;
            bool dateBack = _player.Date == dateBefore;
            bool flagshipBack = _player.Fleet.Flagship != null;

            GD.Print(creditsBack && dateBack && flagshipBack
                ? $"[smoke] PASS: save round-tripped — {_player.Credits:n0} credits, " +
                  $"{_player.Date:d MMM yyyy}, flagship {_player.Fleet.Flagship!.Definition.DisplayName}"
                : $"[smoke] FAIL: credits {creditsBack}, date {dateBack}, flagship {flagshipBack}");

            GetTree().Quit();
        }

        /// <summary>
        /// Writes the whole player — money, calendar, fleet, cargo, conditions and the
        /// missions in progress — to the save slot.
        /// </summary>
        /// <remarks>
        /// The serialiser and its round-trip tests already existed; nothing ever called
        /// them, so the game had no Save and no Load in any menu and every session
        /// began again from the starting world. That made every other rule that
        /// expresses itself over days — deadlines, salaries, depreciation, reputation —
        /// unobservable, because the player's history was discarded at quit.
        /// </remarks>
        private bool SaveNow()
        {
            if (_player is null)
            {
                return false;
            }

            bool written = SaveSlot.Save(SaveGame.Write(_player, _missions));
            GD.Print(written
                ? $"[save] wrote {SaveSlot.Where}"
                : "[save] failed");

            return written;
        }

        /// <summary>
        /// Restores the saved game and rebuilds the world around it.
        /// </summary>
        private bool LoadNow()
        {
            string? text = SaveSlot.Load();
            if (text is null || _universe is null)
            {
                return false;
            }

            MissionLog? restoredLog = null;
            PlayerState restored = SaveGame.Read(text, _universe,
                player => restoredLog = new MissionLog(player));

            Ship? flagship = restored.Fleet.Flagship;
            if (flagship is null || restored.CurrentSystem is null)
            {
                GD.PrintErr("[save] the save names no flagship or no system; ignoring it");
                return false;
            }

            _player = restored;
            _missions = restoredLog ?? new MissionLog(restored);
            _credits = restored.Credits;

            // The restored hull is a different object, so everything holding the old
            // one has to be pointed at the new one: the view, the camera, the combat
            // field and the mission log's idea of who the player is.
            _ship = flagship;
            _ship.BuildMounts();
            _startShip = _ship.Definition.DisplayName;
            _shipView.SyncWith(_ship);
            _camera?.Snap(_ship);

            _ui?.Bind(_player, _missions, _universe, () => _ship);

            _lastSystem = restored.CurrentSystem;
            _currentDay = DaysSinceEpoch(_player.Date.Year, _player.Date.Month, _player.Date.Day);
            restored.CurrentSystem.SetDate(_currentDay);

            BuildCombat(_universe);
            RebuildSystemViews(restored.CurrentSystem);

            // A load can arrive while the player is standing in a spaceport; the
            // overlay belongs to the old player and has to go with it.
            _isLanded = false;
            _landedOverlay?.QueueFree();
            _landedOverlay = null;

            GD.Print($"[save] loaded {SaveSlot.Where}: {_ship.Definition.DisplayName} at " +
                     $"{restored.CurrentSystem.Name}, {_player.Date:d MMM yyyy}, " +
                     $"{_player.Credits:n0} credits, {_missions.Active.Count} mission(s) in progress");
            return true;
        }

        /// <summary>
        /// Moves the game on by one day: the player's calendar, the stellar-object
        /// clock derived from it, and everything that only happens between days.
        /// </summary>
        private void AdvanceDay()
        {
            _player.AdvanceDays(1);
            _currentDay = DaysSinceEpoch(_player.Date.Year, _player.Date.Month, _player.Date.Day);
            _ship?.CurrentSystem?.SetDate(_currentDay);

            IReadOnlyList<ActiveMission> ended = _missions?.Step() ?? System.Array.Empty<ActiveMission>();
            foreach (ActiveMission over in ended)
            {
                GD.Print($"[mission] \"{over.Mission.DisplayName}\" ended: {over.Outcome}");
            }
        }

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
