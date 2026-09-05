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
        private Label? _conditionLabel;
        private Label? _conditionWarning;
        private Control? _flightKeys;

        /// <summary>The system dial: where everything in this system is, and what is landable.</summary>
        private SystemRadar? _radar;

        /// <summary>
        /// The current system's objects, cached for the radar so the HUD does not
        /// rebuild the list on every physics frame.
        /// </summary>
        private readonly List<StellarObject> _radarObjects = new();
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

        /// <summary>Flying to <c>_ship.TargetStellar</c> to land, upstream's autoPilot LAND.</summary>
        private bool _landAutopilot;

        /// <summary>What the last landing key press had to say, shown in the HUD.</summary>
        private string _landMessage = string.Empty;
        private bool _jumpKeyWasDown;
        private bool _landKeyWasDown;
        private bool _landAtStart;
        private UiScreen _landedStartScreen;
        private bool _isLanded;
        // The player's fleet, money, date, and travel history live in the simulation.
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

        /// <summary>Headless "press land and get to the ground" run; see StepLandSmoke.</summary>
        private bool _landSmoke;

        /// <summary>Headless run through the whole opening tutorial; see StepTutorialSmoke.</summary>
        private bool _tutorialSmoke;

        /// <summary>The opening tutorial, and the one panel that shows it.</summary>
        private readonly Tutorial _tutorial = new();

        private TutorialPanel? _tutorialPanel;

        private bool _dismissKeyWasDown;

        /// <summary>Job-board count, taken once per landing rather than once per frame.</summary>
        private bool _jobBoardCounted;

        private int _jobsOnOffer;

        /// <summary>Destination world → its system, remembered across frames.</summary>
        private string? _cachedDestinationPlanet;

        private string? _cachedDestinationSystem;
        private bool _smokeFire;
        private ActiveMission? _smokeJob;
        private int _smokeReloads;
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

        /// <summary>
        /// Whether this process has no window. A headless run has no one to read a
        /// menu, so it goes straight to flying.
        /// </summary>
        private static bool IsHeadless =>
            DisplayServer.GetName() == "headless";

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
            if (_landAtStart)
            {
                _landedStartScreen = GameUi.OpenAtStart;
                GameUi.OpenAtStart = UiScreen.None;
            }

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
                if (DisplayServer.GetName() == "headless") GetTree().Quit(1);
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

            RefreshRadarObjects();

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
                GD.Print($"[start] {_start.DisplayName}: {_startPlanet}, {_startSystem}, " +
                         $"{startDate:d MMM yyyy}, {_player.Credits:n0} credits, " +
                         $"{_player.Conditions.Values.Count} conditions set");
            }
            else
            {
                _player.SetCredits(480_000);
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

            // Before the landed early-out, because half the tutorial happens at a port:
            // "take a job" is a step the player can only complete while the simulation
            // is stopped, so a tutorial that only ticked in flight would sit on step two
            // watching them do it and never notice.
            StepTutorial();

            // Before the landed early-out for the same reason StepTutorial is: taking a
            // job and leaving the ground both happen while the simulation is stopped,
            // and a driver that only ran in flight would land on step one and never act
            // again.
            if (_tutorialSmoke && StepTutorialSmoke())
            {
                return;
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

            if (_landSmoke && StepLandSmoke())
            {
                return;
            }

            // The game opens on its menu. Captures, the landed-at-start path and the
            // smoke runs skip it: a screenshot of the menu is not a screenshot of the
            // game, and a headless run that sits on the menu steps the simulation
            // exactly zero times — so CI's smoke run proved the scene could be built
            // and nothing whatever about whether it runs.
            if (_simFrames == 1 && _capturePath == null && !_landAtStart && !_missionSmoke
                && !_saveSmoke && !IsHeadless)
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
                    if (_landedStartScreen != UiScreen.None)
                        _ui?.Show(_landedStartScreen);
                    return;
                }
            }

            // L picks somewhere to land and flies there. Pressing it again with a
            // target already chosen steps to the next world (upstream cycles on a rapid
            // re-press; the key-repeat cooldown that distinguishes the two is an input
            // concern, so the rule takes it as a parameter).
            bool landKeyDown = Input.IsPhysicalKeyPressed(Key.L);
            if (landKeyDown && !_landKeyWasDown)
            {
                TryLand();
                if (_isLanded)
                {
                    _landKeyWasDown = landKeyDown;
                    return;
                }

                LandingChoice choice = ShipAi.SelectLandingTarget(_ship, cycle: _landAutopilot);
                _landMessage = choice.Message;
                _landAutopilot = choice.Succeeded;
                if (_landAutopilot)
                {
                    // One autopilot at a time: a ship cannot both line up for a jump
                    // and fly an approach.
                    _jumpAutopilot = false;
                }

                SyncLandingTargetRings();
                GD.Print($"[flight] {choice.Message}");
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
                else
                {
                    // Braking and lining up are one rule, and it is upstream's
                    // (AI::PrepareForHyperspace), so it lives in the sim where the
                    // suite can see it. What stood here was an invention: a brake with
                    // no terminal condition that spun the ship in circles forever
                    // rather than ever committing the jump.
                    command = ShipAi.PrepareForHyperspace(_ship);
                }
            }
            else if (_landAutopilot)
            {
                bool manual = !_autopilot &&
                              (Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up) ||
                               Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down) ||
                               Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left) ||
                               Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right));
                if (manual || _ship.TargetStellar == null)
                {
                    // Any manual input cancels the autopilot, as upstream. The TARGET
                    // survives it: the pilot who nudges the stick has not changed their
                    // mind about where they are going, and clearing it would make the
                    // ring vanish and the next press start over.
                    _landAutopilot = false;
                    command = Command.None;
                }
                else
                {
                    command = ShipAi.MoveToPlanet(_ship, out bool arrived);
                    if (arrived)
                    {
                        TryLand();
                        if (_isLanded)
                        {
                            return;
                        }
                    }
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
                // The player's saved preference, not a constant. GameSettings wrote
                // this on every options change and nothing ever read it back, so
                // turning glow off survived exactly until the next launch.
                GlowEnabled = GameSettings.GlowPreference(true),
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
                Ship? target = ShipAi.FindTarget(npc, _field?.Ships);
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

        /// <summary>
        /// Headless proof that a player can press land and get to the ground: select a
        /// world, let the autopilot fly the approach, and report what happened.
        /// </summary>
        /// <remarks>
        /// The sim suite already pins the selection rule and the approach, but neither
        /// can see the wiring — which key runs them, whether the ring and the HUD track
        /// the target, whether arrival actually hands off to <see cref="TryLand"/>.
        /// That wiring is exactly where the equivalent jump defect lived, so it gets a
        /// smoke run of its own.
        /// </remarks>
        private bool StepLandSmoke()
        {
            if (_ship == null || _isLanded)
            {
                return false;
            }

            if (!_landAutopilot && _ship.TargetStellar == null)
            {
                // The player spawns sitting on their starting world, where "fly me
                // there" is answered before it is asked. Push the ship out past the
                // outermost body first, so the run exercises an actual approach.
                double outermost = _ship.CurrentSystem?.AllObjects()
                    .Select(o => o.Position.Length)
                    .DefaultIfEmpty(0.0)
                    .Max() ?? 0.0;
                _ship.Position = new Point(0.0, -(outermost + 4000.0));
                _ship.Velocity = Point.Zero;

                LandingChoice choice = ShipAi.SelectLandingTarget(_ship);
                _landMessage = choice.Message;
                _landAutopilot = choice.Succeeded;
                SyncLandingTargetRings();

                int labelled = _stellarViews.Count(v => v.Object.Planet != null);
                GD.Print($"[smoke] {_ship.CurrentSystem?.Name}: {_stellarViews.Count} objects, " +
                         $"{labelled} labelled as landable; {choice.Message}");

                if (!choice.Succeeded)
                {
                    GD.Print("[smoke] FAIL: nothing to land on in the starting system");
                    GetTree().Quit(1);
                    return true;
                }
            }

            if (_landAutopilot && _ship.TargetStellar is { } target)
            {
                if (_simFrames % 120 == 0)
                {
                    GD.Print($"[smoke] frame {_simFrames}: {(target.Position - _ship.Position).Length:n0} " +
                             $"from {target.PlanetName}, |v| {_ship.Velocity.Length:0.##}");
                }

                Command command = ShipAi.MoveToPlanet(_ship, out bool arrived);
                _ship.Step(command);
                _shipView?.SyncWith(_ship);
                UpdateHud();

                if (arrived)
                {
                    TryLand();
                    GD.Print(_isLanded
                        ? $"[smoke] PASS: flew to {target.PlanetName} and landed after {_simFrames} frames " +
                          $"({_simFrames / Ship.FramesPerSecond:0.#}s)"
                        : "[smoke] FAIL: the approach arrived but the ship could not put down");
                    GetTree().Quit(_isLanded ? 0 : 1);
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// The landing half of the status line: which world is selected and how far off
        /// it is, so "where can I land" has an answer without opening a map.
        /// </summary>
        private string LandingStatus()
        {
            if (_ship?.TargetStellar is not { } target)
            {
                // No target: whatever the last press had to say (usually why there was
                // nothing to pick) is more useful than a blank.
                return string.IsNullOrEmpty(_landMessage) ? string.Empty : "   " + _landMessage;
            }

            double distance = (target.Position - _ship.Position).Length;
            string verb = _landAutopilot ? "LAND →" : "TARGET";
            return $"   {verb} {target.PlanetName} · {distance:n0}";
        }

        /// <summary>
        /// Headless proof that the tutorial's four steps can all actually be performed,
        /// in order, in the shipped galaxy: land, take a job, jump to where it is going,
        /// land again and hand it in.
        /// </summary>
        /// <remarks>
        /// The simulation suite proves the state machine advances correctly and that
        /// the starting world can supply every step. What it cannot see is whether the
        /// GAME can: whether landing reaches the job board, whether the board's
        /// destination is somewhere the jump autopilot will actually go, whether
        /// arriving there finds the world the job named. A tutorial is a promise about
        /// the real thing, so it gets checked against the real thing.
        ///
        /// Returns true while it owns the frame.
        /// </remarks>
        private bool StepTutorialSmoke()
        {
            if (_ship == null || _missions == null || _universe == null)
            {
                return false;
            }

            if (_tutorial.IsComplete)
            {
                bool delivered = _missions.Finished.Any(m => m.Outcome == MissionOutcome.Completed);
                GD.Print($"[smoke] {(delivered ? "PASS" : "FAIL")}: tutorial finished at step {_tutorial.Step} " +
                         $"after {_simFrames} frames ({_simFrames / Ship.FramesPerSecond:0.#}s)");
                GetTree().Quit(delivered ? 0 : 1);
                return true;
            }

            if (_simFrames > 60_000)
            {
                GD.Print($"[smoke] FAIL: stuck on {_tutorial.Step} — {_tutorial.Prompt}");
                GetTree().Quit(1);
                return true;
            }

            // Hyperspace owns the frame wherever the sequence has got to — including
            // the INBOUND deceleration, which runs on after CurrentSystem has already
            // changed. The tutorial advances to "deliver" the instant the system
            // changes, which is the START of that run, so a driver that switched away
            // from the jump there left HyperspaceCount frozen at a non-zero value and
            // the ship permanently unable to land: CanLandOn refuses while
            // IsHyperspacing, and nothing was left to finish the count.
            if (_ship.StepHyperspace())
            {
                if (!ReferenceEquals(_ship.CurrentSystem, _lastSystem))
                {
                    HandleArrival();
                    _ship.TargetSystem = null;
                    if (!ReferenceEquals(_player.CurrentSystem, _ship.CurrentSystem) ||
                        !_player.VisitedSystems.Contains(_ship.CurrentSystem!.Name))
                    {
                        GD.Print("[smoke] FAIL: arrival did not update the pilot's system and exploration history");
                        GetTree().Quit(1);
                    }
                }

                _shipView?.SyncWith(_ship);
                return true;
            }

            switch (_tutorial.Step)
            {
                case TutorialStep.Land:
                    return FlyToAWorldAndLand();

                case TutorialStep.Deliver:
                    return _isLanded ? HandTheJobIn() : FlyToAWorldAndLand();

                case TutorialStep.TakeJob:
                    return TakeAJobAndLeave();

                case TutorialStep.Jump:
                    return JumpTowardTheJob();
            }

            return false;
        }

        /// <summary>Pick somewhere (or the job's destination) and put the ship on it.</summary>
        private bool FlyToAWorldAndLand()
        {
            if (_isLanded)
            {
                return false;
            }

            if (_ship!.TargetStellar == null)
            {
                // On the delivery leg, aim at the world the job actually named rather
                // than at whatever is nearest — landing on the wrong world in the right
                // system is the classic way to "arrive" and still not be able to hand in.
                string? wanted = _missions!.Active.FirstOrDefault()?.Destination;
                StellarObject? named = wanted == null
                    ? null
                    : _ship.CurrentSystem?.AllObjects().FirstOrDefault(o => o.PlanetName == wanted);

                if (named != null)
                {
                    _ship.TargetStellar = named;
                    GD.Print($"[smoke] delivering to {wanted}");
                }
                else if (!ShipAi.SelectLandingTarget(_ship).Succeeded)
                {
                    GD.Print($"[smoke] FAIL: nowhere to land in {_ship.CurrentSystem?.Name}");
                    GetTree().Quit(1);
                    return true;
                }

                SyncLandingTargetRings();
            }

            _ship.Step(ShipAi.MoveToPlanet(_ship, out bool arrived));
            _shipView?.SyncWith(_ship);

            if (_simFrames % 600 == 0 && _ship.TargetStellar != null)
            {
                GD.Print($"[smoke] frame {_simFrames}: " +
                         $"{(_ship.TargetStellar.Position - _ship.Position).Length:n0} from " +
                         $"{_ship.TargetStellar.PlanetName}, |v| {_ship.Velocity.Length:0.##}, " +
                         $"energy {_ship.Energy:0}");
            }

            if (arrived)
            {
                TryLand();
                if (!_isLanded)
                {
                    StellarObject? where = _ship.TargetStellar;
                    bool known = where?.PlanetName != null &&
                                 _universe!.Planets.ContainsKey(where.PlanetName);
                    GD.Print($"[smoke] FAIL: arrived at {where?.PlanetName} but TryLand refused — " +
                             $"|v| {_ship.Velocity.Length:0.###} (limit {Ship.LandingSpeed}), " +
                             $"range {(where!.Position - _ship.Position).Length:0.#} " +
                             $"(radius {where.LandingRadius}), " +
                             $"planet known: {known}, hyperspacing: {_ship.IsHyperspacing}, " +
                             $"entering: {_ship.IsEnteringHyperspace}, disabled: {_ship.IsDisabled}");
                    GetTree().Quit(1);
                }
            }

            return true;
        }

        /// <summary>Hand the carried job in, which is what the JOBS counter's B key does.</summary>
        private bool HandTheJobIn()
        {
            ActiveMission? carrying = _missions!.Active.FirstOrDefault();
            if (carrying == null)
            {
                return false;
            }

            long before = _player.Credits;
            if (_missions.Complete(carrying))
            {
                GD.Print($"[smoke] handed in \"{carrying.Mission.DisplayName}\" " +
                         $"at {_player.CurrentPlanet?.Name} for {_player.Credits - before:n0} credits");
                return false;
            }

            GD.Print($"[smoke] FAIL: standing on {_player.CurrentPlanet?.Name} but cannot hand in " +
                     $"\"{carrying.Mission.DisplayName}\" (wants {carrying.Destination ?? "nowhere"})");
            GetTree().Quit(1);
            return true;
        }

        /// <summary>Take the first offered job that fits, then leave the ground.</summary>
        private bool TakeAJobAndLeave()
        {
            if (!_isLanded)
            {
                // Landed, took the job, and already back in space: nothing to do here.
                return false;
            }

            if (_missions!.Active.Count == 0)
            {
                Mission? job = _missions.Available(_universe!, MissionLocation.Job).FirstOrDefault(_missions.CanAccept);
                if (job == null)
                {
                    GD.Print("[smoke] no work on this board; the tutorial should stand down");
                    return false;
                }

                ActiveMission? taken = _missions.Accept(job);
                GD.Print($"[smoke] accepted \"{taken?.Mission.DisplayName}\" → " +
                         $"{taken?.Destination ?? "(nowhere)"} in " +
                         $"{_universe!.SystemOf(taken?.Destination)?.Name ?? "(no system)"}");
            }

            return false;
        }

        /// <summary>Set course for the job's system and fly the jump.</summary>
        private bool JumpTowardTheJob()
        {
            if (_isLanded)
            {
                // Leave the ground first. The tutorial advances to "jump" the moment
                // the job is accepted, which is one frame before the driver would have
                // got round to taking off — and a ship that jumps while still landed
                // arrives in the next system with the player standing on a world in the
                // previous one, holding a job it can never hand in.
                OnDepart();
                return true;
            }

            StarSystem? going = _universe!.SystemOf(_missions!.Active.FirstOrDefault()?.Destination);
            if (going == null)
            {
                return false;
            }

            if (_ship!.CurrentSystem != null && !_ship.CanReach(going))
            {
                // The destination is more than one jump out, which is ordinary: step
                // toward it through whichever neighbour is closest to it on the map.
                StarSystem? hop = _ship.CurrentSystem.Links
                    .Select(name => _universe.Systems.TryGetValue(name, out StarSystem? s) ? s : null)
                    .Where(s => s != null)
                    .OrderBy(s => (s!.MapPosition - going.MapPosition).Length)
                    .FirstOrDefault();

                if (hop == null)
                {
                    GD.Print($"[smoke] FAIL: {_ship.CurrentSystem.Name} leads nowhere useful");
                    GetTree().Quit(1);
                    return true;
                }

                going = hop;
            }

            if (!ReferenceEquals(_ship.TargetSystem, going))
            {
                _ship.TargetSystem = going;
                GD.Print($"[smoke] course set for {going.Name}");
            }

            _ship.Step(ShipAi.PrepareForHyperspace(_ship));
            _shipView?.SyncWith(_ship);
            _ship.TryCommitJump();
            return true;
        }

        /// <summary>
        /// Show the tutorial where the player has got to, and let them wave it away.
        /// </summary>
        /// <remarks>
        /// Everything the tutorial needs is READ from state here rather than pushed to
        /// it from the places where each thing happens. That is the whole point of the
        /// design: there is no "tell the tutorial the player landed" call to forget at
        /// one of the several sites that can land a ship, and no ordering to get wrong.
        /// </remarks>
        private void StepTutorial()
        {
            if (_tutorialPanel == null || _ship == null)
            {
                return;
            }

            bool dismissDown = Input.IsPhysicalKeyPressed(Key.F3);
            if (dismissDown && !_dismissKeyWasDown)
            {
                _tutorial.Dismiss();
            }

            _dismissKeyWasDown = dismissDown;
            if (_flightKeys != null)
                _flightKeys.Visible = !_isLanded && !_ui.IsModal;

            if (_tutorial.IsComplete)
            {
                // Nothing left to say, and nothing left to compute. Both lookups below
                // are galaxy-wide scans; leaving them running for the rest of the
                // session to produce a prompt nobody will see is pure waste.
                _tutorialPanel.Show(_tutorial, null, _isLanded, _ui.IsModal);
                return;
            }

            ActiveMission? job = _missions?.Active.FirstOrDefault();
            string? destinationPlanet = job?.Destination;

            var state = new TutorialState
            {
                IsLanded = _isLanded,
                HasJob = job != null,
                DeliveredAJob = _missions?.Finished
                    .Any(f => f.Outcome == MissionOutcome.Completed) ?? false,
                JobsOnOffer = JobsOnOfferHere(),
                CurrentSystem = _ship.CurrentSystem?.Name,
                DestinationPlanet = destinationPlanet,
                DestinationSystem = SystemHolding(destinationPlanet),
            };

            string? finished = _tutorial.Observe(state);
            if (finished != null)
            {
                GD.Print($"[tutorial] {_tutorial.Step}: {finished}");
            }

            _tutorialPanel.Show(_tutorial, finished, _isLanded, _ui.IsModal);
        }

        /// <summary>
        /// How much work the counter the player is standing at is offering. Zero when
        /// they are not standing at one, which is not the same as "this world has no
        /// work" — the tutorial only reads it while landed.
        /// </summary>
        /// <remarks>
        /// Counted ONCE per landing. `Available` evaluates every mission in the dataset
        /// against its offer conditions — a thousand of them in the generated galaxy —
        /// and the first version of this called it from the per-frame tutorial tick.
        /// The board cannot change while the player stands at it, so sixty scans a
        /// second bought exactly nothing and made a headless run take minutes.
        /// </remarks>
        private int JobsOnOfferHere()
        {
            if (!_isLanded || _missions == null || _universe == null)
            {
                _jobBoardCounted = false;
                return 0;
            }

            if (!_jobBoardCounted)
            {
                _jobsOnOffer = _missions.Available(_universe, MissionLocation.Job).Count();
                _jobBoardCounted = true;
            }

            return _jobsOnOffer;
        }

        /// <summary>
        /// The system a destination world sits in, remembered so the galaxy is walked
        /// once per destination rather than once per frame.
        /// </summary>
        /// <remarks>
        /// Worlds are named globally and live inside a system's object tree, so this is
        /// a search over every system in the galaxy. The destination changes when the
        /// player accepts a different job — a few times an hour — and caching on the
        /// name is enough to make the difference between a lookup and a scan.
        /// </remarks>
        private string? SystemHolding(string? planetName)
        {
            if (planetName == null)
            {
                return null;
            }

            if (!string.Equals(planetName, _cachedDestinationPlanet, StringComparison.Ordinal))
            {
                _cachedDestinationPlanet = planetName;
                _cachedDestinationSystem = _universe?.SystemOf(planetName)?.Name;
            }

            return _cachedDestinationSystem;
        }

        /// <summary>Re-cache the radar's object list after the system's views change.</summary>
        private void RefreshRadarObjects()
        {
            _radarObjects.Clear();
            foreach (StellarObjectView view in _stellarViews)
            {
                _radarObjects.Add(view.Object);
            }
        }

        /// <summary>Point the selection ring at whatever the ship is currently targeting.</summary>
        private void SyncLandingTargetRings()
        {
            foreach (StellarObjectView view in _stellarViews)
            {
                view.SetSelected(_ship != null && ReferenceEquals(view.Object, _ship.TargetStellar));
            }
        }

        private void TryLand()
        {
            if (_ship == null || _ship.CurrentSystem == null)
            {
                return;
            }

            // The selected world gets first refusal. Landing on whichever body happens
            // to pass the test is how a player who asked for the port ends up on the
            // moon beside it, because both were in reach when the key came up.
            IEnumerable<StellarObject> candidates = _ship.TargetStellar != null
                ? new[] { _ship.TargetStellar }.Concat(_ship.CurrentSystem.AllObjects())
                : _ship.CurrentSystem.AllObjects();

            foreach (StellarObject obj in candidates)
            {
                if (obj.PlanetName == null ||
                    !_universe.Planets.TryGetValue(obj.PlanetName, out Planet? planet))
                {
                    continue;
                }

                // Whether a ship may put down is the simulation's rule, not this
                // screen's. It used to live here with constants of its own — a speed
                // limit three times upstream's and a flat reach that ignored how big the
                // world was — where neither the sim suite nor the architecture test
                // could see it.
                if (!_ship.CanLandOn(obj, planet))
                {
                    continue;
                }

                _isLanded = true;
                _jumpAutopilot = false;
                _landAutopilot = false;
                _landMessage = string.Empty;
                _player.Land(planet);
                _landedOverlay = LandedOverlay.Open(this, _player, _missions, planet,
                    _ship.CurrentSystem.Name, _universe);
                _landedOverlay.Departed += OnDepart;
                _ui.Port = _landedOverlay;
                GD.Print($"[flight] landed on {planet.Name} (credits={_player.Credits:n0})");
                return;
            }
        }

        private void OnDepart(bool acceptCargoLoss = false)
        {
            if (_landedOverlay == null || _ship == null)
            {
                return;
            }

            if (!_player.TakeOff(_missions, acceptCargoLoss)) return;

            // The flagship may have changed at the shipyard.
            if (_player.Fleet.Flagship != null && !ReferenceEquals(_player.Fleet.Flagship, _ship))
            {
                Ship replacement = _player.Fleet.Flagship;
                replacement.Position = _ship.Position;
                replacement.Facing = _ship.Facing;
                replacement.CurrentSystem = _ship.CurrentSystem;

                // Rebuild mounts from the existing inventory. Installing the weapons
                // again would add free copies whenever the hull has spare hardpoints.
                replacement.BuildMounts();

                _field?.Remove(_ship);
                _ship = replacement;
                _field?.Add(_ship);
                GD.Print($"[flight] flagship is now a {_ship.Definition.DisplayName}");
            }
            _landedOverlay.QueueFree();
            _landedOverlay = null;
            _ui.Port = null;
            _isLanded = false;
            _ship.Velocity = Point.Zero;

            GD.Print($"[flight] departed (credits={_player.Credits:n0} fuel={_ship.Fuel:0} " +
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
            _player.EnterSystem(system);

            // A landing target belongs to the system it was chosen in. Carried across a
            // jump it names a body that is not here, at coordinates that mean something
            // else, and the HUD would go on reporting a distance to it.
            _ship.TargetStellar = null;
            _landAutopilot = false;
            _landMessage = string.Empty;

            // A jump takes a day, and the day has to pass on the PLAYER'S calendar --
            // not just on the counter that positions the stellar objects. Advancing
            // only the render-side counter meant no day ever passed in game: no
            // deadline could expire, no salary was owed, no depreciation ticked, and
            // MissionLog.Step -- which fires the fail triggers -- was never called at
            // all. The counter is derived from the player's date afterwards so there is
            // one calendar rather than two that can drift apart.
            AdvanceDay();
            ResetLocalCombat(_universe);
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

            RefreshRadarObjects();

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
            _playerGovernment = _player.Fleet.Government;

            // The hardpoints the hull was designed with. Without this the player's
            // ship carries no mounts at all - not empty ones, none - so it can never
            // fire, never be armed at an outfitter, and never finish a combat job.
            // Every NPC in the game called this; the player alone never did.
            _ship!.BuildMounts();

            ResetLocalCombat(universe);
        }

        /// <summary>Replace the current system's transient combat state and visuals.</summary>
        private void ResetLocalCombat(GameData universe)
        {
            foreach ((_, ShipView view) in _traffic)
                view.QueueFree();
            _traffic.Clear();
            foreach ((_, ShipView view) in _missionShips)
                view.QueueFree();
            _missionShips.Clear();
            if (_drone != null)
            {
                _droneView.QueueFree();
                _drone = null;
            }

            _field = new CombatField
            {
                WeaponLookup = name =>
                    universe.Outfits.TryGetValue(name, out Outfit? outfit) ? outfit.Weapon : null!,
            };
            _field.Add(_ship);

            _effects?.QueueFree();
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
            _drone.CurrentSystem = _ship!.CurrentSystem;
            _drone.BuildMounts();
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

            // A disabled hull still drifts and updates heat through Ship.Step.
            // Only its pilot's commands stop until it can act again.
            foreach ((Ship npc, ShipView view) in _missionShips)
            {
                if (!npc.IsDisabled)
                {
                    Ship? target = ShipAi.FindTarget(npc, _field?.Ships);
                    npc.Step(target != null
                        ? ShipAi.Attack(npc, target)
                        : FleetOrders.MoveTo(npc, Point.Zero, Point.Zero, 400.0, 1.0));

                    if (target != null)
                    {
                        _field?.Add(ShipAi.AutoFire(npc, target));
                    }
                }
                else
                {
                    npc.Step(Command.None);
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
                _field.Ships,
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
        /// --mission-smoke: take an offered bounty, reload mid-fight and after victory,
        /// land and collect payment.
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

                // This is a combat-capable test pilot, using an unchanged stock hull
                // and loadout. Winning a bounty must not depend on the starter trader
                // outgunning a warship. The fight uses normal projectiles and damage.
                Ship equipped = _universe.Ships.Keys.Select(_universe.BuildShip)
                    .Where(s => s.Outfits.Any(o => o.IsWeapon) && s.Thrust > 0)
                    .OrderByDescending(s => s.MaxHull + s.MaxShields)
                    .ThenBy(s => s.Definition.DisplayName, StringComparer.Ordinal).First();
                equipped.CurrentSystem = _ship.CurrentSystem;
                _player.Fleet.Remove(_ship);
                _player.Fleet.Add(equipped);
                _player.Fleet.SetFlagship(equipped);
                _ship = equipped;
                _startShip = equipped.Definition.DisplayName;
                BuildCombat(_universe);
                equipped.Recharge(RechargeType.All);

                StellarObject? home = equipped.CurrentSystem?.AllObjects()
                    .FirstOrDefault(o => o.PlanetName == _startPlanet);
                if (home == null)
                {
                    GD.Print("[smoke] FAIL: no starting port for the bounty pilot");
                    GetTree().Quit(1);
                    return;
                }
                equipped.Position = home.Position;
                equipped.TargetStellar = home;
                TryLand();
                Mission? job = _missions.Available(_universe, MissionLocation.Job).FirstOrDefault(m =>
                    m.Npcs.Any(n => (n.SucceedIf & ShipEvent.Destroy) != 0));

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
                OnDepart();
                int armed = npc.Ships[0].Mounts.Count(m => !m.IsEmpty);
                GD.Print($"[smoke] took \"{job.DisplayName}\": {npc.Ships.Count} hull(s) " +
                         $"of {npc.Ships[0].Definition.DisplayName} " +
                         $"({npc.Ships[0].Government?.Name}) in {npc.System?.Name}; " +
                         $"target carries {armed}/{npc.Ships[0].Mounts.Count} armed mounts, " +
                         $"hostile to player = {npc.Ships[0].Government?.IsEnemy(_ship.Government)}");

                // Stand where the job sent us, close enough to shoot.
                if (npc.System != null && !ReferenceEquals(_ship.CurrentSystem, npc.System))
                {
                    _ship.CurrentSystem = npc.System;
                    HandleArrival();
                }

                // A disabled mission ship must still drift and update heat. This probes
                // the real controller, then restores the target for the live fight.
                Ship victim = npc.Ships[0];
                Point placed = victim.Position;
                victim.Velocity = new Point(2, 0);
                victim.SetLevels(hull: victim.MinimumHull * 0.5, heat: 100);
                StepMissionShips();
                if (victim.Position == placed || victim.Heat == 100)
                {
                    GD.Print("[smoke] FAIL: a disabled mission ship stopped drifting or updating heat");
                    GetTree().Quit(1);
                    return;
                }
                victim.Position = placed;
                victim.Velocity = Point.Zero;
                victim.Recharge(RechargeType.All);
                GD.Print("[smoke] disabled mission ship continued drifting and updating heat");

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

            if (_smokeReloads == 0 && _smokeFrames >= 60 && !ReloadMissionSmoke("during combat"))
            {
                GetTree().Quit(1);
                return;
            }

            Ship[] survivors = _smokeJob.Npcs.SelectMany(n => n.Survivors).ToArray();
            int alive = survivors.Length;
            _smokeTarget = survivors.FirstOrDefault();

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

            if (_smokeJob.Npcs.All(n => n.HasSucceeded(_ship.CurrentSystem)))
            {
                if (_smokeReloads < 2 && !ReloadMissionSmoke("after victory"))
                {
                    GetTree().Quit(1);
                    return;
                }
                _smokeFire = false;
                StarSystem? destination = _universe.SystemOf(_smokeJob.Destination);
                StellarObject? port = destination?.AllObjects()
                    .FirstOrDefault(o => o.PlanetName == _smokeJob.Destination);
                if (destination == null || port == null)
                {
                    GD.Print("[smoke] FAIL: the won bounty has no hand-in port");
                    GetTree().Quit(1);
                    return;
                }
                // Travel has its own smoke. Exercise the actual landing and mission
                // completion paths here, after real projectiles won the fight.
                if (!ReferenceEquals(_ship.CurrentSystem, destination))
                {
                    _ship.CurrentSystem = destination;
                    HandleArrival();
                    if (!HasOnlyPlayerCombat())
                    {
                        GD.Print("[smoke] FAIL: ships or shots from the fight survived entering another system");
                        GetTree().Quit(1);
                        return;
                    }
                }
                _ship.Position = port.Position;
                _ship.Velocity = Point.Zero;
                _ship.TargetStellar = port;
                TryLand();
                long before = _player.Credits;
                bool paid = _isLanded && _missions.Complete(_smokeJob)
                    && _smokeJob.Outcome == MissionOutcome.Completed && _player.Credits > before;
                GD.Print(paid
                    ? $"[smoke] PASS: bounty won and handed in at {port.PlanetName}; " +
                      $"paid {_player.Credits - before} credits after {_smokeFrames} combat frames"
                    : "[smoke] FAIL: the won bounty could not be handed in for payment");
                GetTree().Quit(paid ? 0 : 1);
                return;
            }

            if (_ship.IsDestroyed || _ship.IsDisabled)
            {
                string how = _ship.IsDestroyed ? "destroyed" : "disabled";
                GD.Print($"[smoke] FAIL: the bounty pilot was {how} after {_smokeFrames} frames");
                GetTree().Quit(1);
                return;
            }

            if (_smokeFrames > 5400)
            {
                GD.Print($"[smoke] FAIL: {alive} target(s) still alive after {_smokeFrames} frames");
                GetTree().Quit(1);
            }
        }

        /// <summary>Reload the live bounty through the same file and session path as the menu.</summary>
        private bool ReloadMissionSmoke(string stage)
        {
            string path = $"user://smoke-bounty-{Guid.NewGuid():N}.txt";
            try
            {
                ActiveMission previous = _smokeJob!;
                ShipView[] oldViews = _missionShips.Select(m => m.View).ToArray();
                string before = SaveGame.Write(_player, _missions);
                if (!SaveTo(path) || !LoadFrom(path))
                {
                    GD.Print($"[smoke] FAIL: could not reload the bounty {stage}");
                    return false;
                }

                _smokeJob = _missions!.Active.FirstOrDefault(m => m.Mission.Name == previous.Mission.Name);
                if (_smokeJob == null || ReferenceEquals(previous, _smokeJob)
                    || _smokeJob.Npcs.Count != previous.Npcs.Count
                    || !SavedStateMatches(before, SaveGame.Write(_player, _missions))
                    || !HasOnlyPlayerCombat() || oldViews.Any(v => !v.IsQueuedForDeletion()))
                {
                    GD.Print($"[smoke] FAIL: bounty state or old combat views changed incorrectly on reload {stage}");
                    return false;
                }

                _smokeTarget = _smokeJob.Npcs.SelectMany(n => n.Survivors).FirstOrDefault();
                _smokeReloads++;
                GD.Print($"[smoke] bounty reloaded {stage}: ships, condition and objective history retained");
                return true;
            }
            finally { DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path)); }
        }

        /// <summary>Fly and fire the demo drone; StepCombat advances its projectiles.</summary>
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
            if (errorMessage == null)
            {
                _conditionLabel = new Label();
                _conditionLabel.AddThemeFontSizeOverride("font_size", 15);
                _conditionLabel.AddThemeColorOverride("font_color", new Color(0.72f, 0.85f, 0.94f));
                column.AddChild(_conditionLabel);
                _conditionWarning = new Label { Visible = false };
                _conditionWarning.AddThemeFontSizeOverride("font_size", 15);
                _conditionWarning.AddThemeColorOverride("font_color", new Color(1f, 0.56f, 0.36f));
                column.AddChild(_conditionWarning);
            }
            if (errorMessage != null)
            {
                _statusLabel.Text = " ";
            }

            // Top-right: the system dial. It goes opposite the telemetry because it is
            // the other thing a pilot looks at constantly, and stacking the two would
            // put one of them under the other.
            var radarPanel = new PanelContainer();
            radarPanel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
            radarPanel.GrowHorizontal = Control.GrowDirection.Begin;
            radarPanel.Position = new Vector2(-14, 14);
            radarPanel.AddThemeStyleboxOverride("panel", HudPanelStyle());
            hud.AddChild(radarPanel);

            _radar = new SystemRadar { Name = "Radar" };
            radarPanel.AddChild(_radar);

            // Its own layer, above the landed overlay: two of the tutorial's four steps
            // happen at a port and two happen in flight, and one panel spanning both is
            // the only version that cannot contradict itself across a takeoff.
            _tutorialPanel = new TutorialPanel { Name = "Tutorial" };
            AddChild(_tutorialPanel);

            // Bottom-left, out of the hero corner: the keybinds.
            var keysPanel = new PanelContainer();
            _flightKeys = keysPanel;
            keysPanel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
            keysPanel.Position = new Vector2(14, -14 - 34);
            keysPanel.AddThemeStyleboxOverride("panel", HudPanelStyle());
            hud.AddChild(keysPanel);
            // The discoverability line. A player who never opens a menu still has to
            // find out that a map and a status screen exist, so the keys that open them
            // are named here rather than only inside the screens they open.
            var keys = new Label
            {
                // "L land" undersold the key badly: it read as "land where you are",
                // which was all it used to do, so a player with no world under them
                // pressed it, saw nothing happen, and concluded landing was broken.
                Text = "↑ thrust · ←/→ turn · ↓ brake · L land (again: next world) · J jump · " +
                       "Space fire · Wheel zoom · M map · I status · F1 controls · ESC menu",
            };
            keys.AddThemeFontSizeOverride("font_size", 12);
            keys.AddThemeColorOverride("font_color", new Color(0.40f, 0.48f, 0.56f));
            keysPanel.AddChild(keys);
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
                    : LandingStatus();
            _statusLabel.Text =
                $"{speed:0} KM/S · {pct:0}%   HDG {_ship.Facing.AbsDegrees:000}°   FUEL {_ship.Fuel:0}{status}";

            if (_conditionLabel != null)
                _conditionLabel.Text =
                    $"SHIELDS {ResourcePercent(_ship.Shields, _ship.MaxShields)}   " +
                    $"HULL {ResourcePercent(_ship.Hull, _ship.MaxHull)}\n" +
                    $"ENERGY {ResourcePercent(_ship.Energy, _ship.MaxEnergy)}   " +
                    $"HEAT {ResourcePercent(_ship.Heat, _ship.MaxHeat)}";
            if (_conditionWarning != null)
            {
                _conditionWarning.Text = _ship.IsOverheated ? "OVERHEATED"
                    : _ship.IsDisabled ? "DISABLED" : string.Empty;
                _conditionWarning.Visible = _conditionWarning.Text.Length > 0;
            }

            _radar?.Track(_ship, _radarObjects);
        }

        private static string ResourcePercent(double amount, double capacity) =>
            capacity > 0 ? $"{Math.Max(0, amount) / capacity * 100:0}%" : "—";

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
                else if (arg == "--land-smoke")
                {
                    _landSmoke = true;
                }
                else if (arg == "--tutorial-smoke")
                {
                    _tutorialSmoke = true;
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
            GD.Print($"[flight] capture {(err == Error.Ok ? "saved" : $"FAILED ({err})")}: " +
                     $"{_capturePath} ({image.GetWidth()}x{image.GetHeight()})");
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

            // Exercise the real file path without replacing the pilot's only save.
            string path = $"user://smoke-save-{Guid.NewGuid():N}.txt";
            try
            {
                _universe.StepEconomy(() => 1.0);
                TradeQuote market = _universe.Trade.Quotes(_ship!.CurrentSystem!.Name).First(q => q.Price > 0);
                _universe.Trade.AddPurchase(market.SystemName, market.Commodity, -7);
                int cargo = _ship.LoadCargo(market.Commodity, 3);
                _player.AdjustBasis(market.Commodity, (long)cargo * market.Price);
                if (cargo != 3)
                {
                    GD.Print("[smoke] FAIL: no room for the commodity cost save probe");
                    GetTree().Quit(1);
                    return;
                }
                _ship!.SetLevels(shields: Math.Min(10, _ship.MaxShields),
                    hull: Math.Max(_ship.MinimumHull + 1, _ship.MaxHull * 0.75),
                    energy: Math.Min(5, _ship.MaxEnergy), fuel: Math.Min(17, _ship.MaxFuel), heat: 100);

                // Loading in the same system must retire the old traffic and shots,
                // as well as replacing the player. Otherwise old ships become ghosts
                // outside the new combat field, or stale shots hit the restored ship.
                Ship oldTraffic = _universe.Ships.Keys.Select(_universe.BuildShip)
                    .First(s => s.Mounts.Any(m => !m.IsEmpty));
                oldTraffic.CurrentSystem = _ship.CurrentSystem;
                oldTraffic.Recharge(RechargeType.All);
                var oldView = new ShipView { Name = "SaveSmokeTraffic" };
                AddChild(oldView);
                oldView.SyncWith(oldTraffic);
                _traffic.Add((oldTraffic, oldView));
                _field!.Add(oldTraffic);
                _field.Add(oldTraffic.FireAll(_ship, oldTraffic.Government));
                if (_field.Projectiles.Count == 0)
                {
                    GD.Print("[smoke] FAIL: the old-traffic probe did not fire any shots");
                    GetTree().Quit(1);
                    return;
                }
                string before = SaveGame.Write(_player, _missions);
                if (!SmokeSaveMenu(path, load: false))
                {
                    GD.Print("[smoke] FAIL: could not write a save");
                    GetTree().Quit(1);
                    return;
                }

                // Change the world so a load that does nothing cannot pass.
                _player.AddCredits(-123_456);
                _player.AdjustBasis(market.Commodity, 12345);
                _player.AdvanceDays(9);
                _ship.Recharge(RechargeType.All);
                _universe.StepEconomy(() => -3.0);

                if (!SmokeSaveMenu(path, load: true))
                {
                    GD.Print("[smoke] FAIL: could not load the save back");
                    GetTree().Quit(1);
                    return;
                }
                string after = SaveGame.Write(_player, _missions);
                bool restored = SavedStateMatches(before, after)
                    && HasOnlyPlayerCombat() && oldView.IsQueuedForDeletion();

                // A bad save must not reset the live market while its pilot is being
                // validated. This slot belongs only to the smoke and is replaced below.
                if (restored)
                {
                    restored = SaveSlot.Save("economy\n", path) && !LoadFrom(path)
                        && SavedStateMatches(after, SaveGame.Write(_player, _missions));
                    GD.Print(restored ? "[smoke] invalid save left the pilot and markets intact"
                        : "[smoke] FAIL: rejecting an invalid save changed the active game");
                }

                // Loading a landed save must rebuild its port screen, and departure
                // must clear the saved planet as well as the presentation state.
                if (restored)
                {
                    StellarObject? port = _ship!.CurrentSystem?.AllObjects()
                        .FirstOrDefault(o => _ship.CanEverLandOn(o));
                    if (port == null)
                    {
                        GD.Print("[smoke] FAIL: no port for the landed save check");
                        GetTree().Quit(1);
                        return;
                    }
                    _ship.Position = port.Position;
                    _ship.Velocity = Point.Zero;
                    _ship.TargetStellar = port;
                    TryLand();
                    if (!_isLanded || !SmokeSaveMenu(path, load: false))
                    {
                        GD.Print("[smoke] FAIL: could not save at a port");
                        GetTree().Quit(1);
                        return;
                    }

                    before = SaveGame.Write(_player, _missions);
                    _player.AddCredits(-13);
                    restored = SmokeSaveMenu(path, load: true)
                        && SavedStateMatches(before, SaveGame.Write(_player, _missions))
                        && ReferenceEquals(_ui.Port, _landedOverlay);
                    OnDepart();
                    _jumpAutopilot = true;
                    _landAutopilot = true;
                    restored &= SmokeSaveMenu(path, load: true)
                        && SavedStateMatches(before, SaveGame.Write(_player, _missions))
                        && _isLanded && _landedOverlay != null && !_jumpAutopilot && !_landAutopilot;

                    // Changing the flagship at a port must not duplicate its weapons
                    // on departure. Use an actual stock hull with spare gun mounts.
                    Ship replacement = _universe.Ships.Keys.Select(_universe.BuildShip).First(s =>
                        s.Outfits.Any(o => o.IsWeapon && o.Attributes.Get("gun ports") < 0)
                        && s.Mounts.Any(m => m.IsEmpty && !m.IsTurret));
                    Outfit[] stock = replacement.Outfits.ToArray();
                    replacement.CurrentSystem = _player.CurrentSystem;
                    _player.Fleet.Add(replacement);
                    _player.Fleet.SetFlagship(replacement);
                    OnDepart();
                    restored &= !_isLanded && _landedOverlay == null && _ui.Port == null
                        && _player.CurrentPlanet == null && ReferenceEquals(_ship, replacement)
                        && stock.SequenceEqual(replacement.Outfits);
                }
                if (restored) restored = SmokePortCargoDeparture(path, market.Commodity);
                GD.Print(restored
                    ? "[smoke] PASS: flight and port menus restored pilot, cargo costs and markets; invalid save rejected; old combat cleared; flagship retained stock outfits; excess cargo reloaded, cancelled and sold on confirmed departure"
                    : "[smoke] FAIL: restored state differs from the saved game");
                GetTree().Quit(restored ? 0 : 1);
            }
            finally { DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path)); }
        }

        private bool SmokePortCargoDeparture(string path, string commodity)
        {
            StellarObject? port = _ship!.CurrentSystem?.AllObjects().FirstOrDefault(o => _ship.CanEverLandOn(o));
            if (port == null) return false;
            _ship.Position = port.Position;
            _ship.Velocity = Point.Zero;
            _ship.TargetStellar = port;
            TryLand();
            if (!_isLanded) return false;

            long capacity = _player.Fleet.CargoCapacity(_player.CurrentSystem);
            Ship extra = _universe.Ships.Keys.Select(_universe.BuildShip).First(s => s.Cargo.Capacity >= 5);
            extra.CurrentSystem = _player.CurrentSystem;
            _player.Fleet.Add(extra);
            int amount = checked((int)(capacity + 5 - _player.Fleet.CargoUsed(_player.CurrentSystem)));
            int price = _universe.Trade.Price(_player.CurrentSystem!.Name, commodity) ?? 0;
            if (price <= 0 || _player.Fleet.LoadCargo(commodity, amount, _player.CurrentSystem) != amount)
                return false;
            _player.AdjustBasis(commodity, (long)amount * price);
            extra.IsParked = true;

            // The parked ship is already empty; all goods remain ashore, five tons
            // beyond what the ships still departing can carry. Exercise the real slot.
            string before = SaveGame.Write(_player, _missions);
            if (!SmokeSaveMenu(path, load: false)) return false;
            _player.Fleet.UnloadCargo(commodity, 5, _player.CurrentSystem);
            if (!SmokeSaveMenu(path, load: true)
                || !SavedStateMatches(before, SaveGame.Write(_player, _missions))) return false;

            long credits = _player.Credits;
            long basis = _player.CostBasis[commodity] - _player.GetBasis(commodity, 5);
            Func<Key, bool> readKeys = _ui.KeyDown;
            void Frame(params Key[] down)
            {
                _ui.KeyDown = key => down.Contains(key);
                _ui._Process(1.0 / 60.0);
            }
            try
            {
                Frame();
                Frame(Key.D);
                Frame();
                if (_landedOverlay?.IsConfirmingDeparture != true) return false;
                Frame(Key.Escape);
                Frame();
                if (!_isLanded || _landedOverlay.IsConfirmingDeparture || _ui.Screen != UiScreen.None
                    || !SavedStateMatches(before, SaveGame.Write(_player, _missions))) return false;
                Frame(Key.D);
                Frame();
                Frame(Key.Enter);
                Frame();
                bool departed = !_isLanded && _landedOverlay == null && _ui.Port == null
                    && _player.CurrentPlanet == null && _player.Fleet.PortCargo == null
                    && _player.Fleet.CargoUsed(_player.CurrentSystem) == capacity
                    && _player.Credits == credits + 5L * price && _player.CostBasis[commodity] == basis;
                GD.Print(departed ? "[smoke] excess cargo survived reload and cancellation; confirmation sold five tons and launched"
                    : "[smoke] FAIL: excess cargo departure changed the wrong state");
                return departed;
            }
            finally { _ui.KeyDown = readKeys; }
        }

        private bool SmokeSaveMenu(string path, bool load)
        {
            Func<Key, bool> readKeys = _ui.KeyDown;
            int saved = 0, loaded = 0;
            bool success = false;
            Func<bool> saveAction = () => { saved++; return success = SaveTo(path); };
            Func<bool> loadAction = () => { loaded++; return success = LoadFrom(path); };
            // Route the normal menu requests to this smoke's temporary slot.
            _ui.SaveRequested -= SaveNow;
            _ui.LoadRequested -= LoadNow;
            _ui.SaveRequested += saveAction;
            _ui.LoadRequested += loadAction;
            void Frame(params Key[] down)
            {
                _ui.KeyDown = key => down.Contains(key);
                _ui._Process(1.0 / 60.0);
            }
            try
            {
                Frame();
                Frame(Key.Escape);
                Frame();
                Frame(Key.Down);
                Frame();
                if (load)
                {
                    Frame(Key.Down);
                    Frame();
                }
                Frame(Key.Enter);
                Frame();
                bool completed = success && (load ? loaded == 1 && saved == 0 : saved == 1 && loaded == 0)
                    && _ui.Screen == (load ? UiScreen.None : UiScreen.Pause);
                GD.Print(completed ? $"[smoke] {(load ? "Load" : "Save")} game menu action completed"
                    : "[smoke] FAIL: save/load menu action did not complete");
                return completed;
            }
            finally
            {
                _ui.Show(UiScreen.None);
                _ui.KeyDown = readKeys;
                _ui.SaveRequested -= saveAction;
                _ui.LoadRequested -= loadAction;
                _ui.SaveRequested += SaveNow;
                _ui.LoadRequested += LoadNow;
            }
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
        private bool SaveNow() => SaveTo(SaveSlot.DefaultPath);

        private static bool SavedStateMatches(string before, string after)
        {
            if (before == after)
                return true;

            string[] savedLines = before.Split('\n'), loadedLines = after.Split('\n');
            for (int i = 0; i < Math.Max(savedLines.Length, loadedLines.Length); i++)
            {
                string savedLine = i < savedLines.Length ? savedLines[i] : "(missing)";
                string loadedLine = i < loadedLines.Length ? loadedLines[i] : "(missing)";
                if (savedLine != loadedLine)
                {
                    GD.Print($"[smoke] save difference at line {i + 1}: {savedLine} -> {loadedLine}");
                    break;
                }
            }
            return false;
        }

        private bool HasOnlyPlayerCombat() => _traffic.Count == 0 && _missionShips.Count == 0
            && _field != null && _field.Projectiles.Count == 0
            && _field.Ships.Count == 1 && ReferenceEquals(_field.Ships[0], _ship);

        private bool SaveTo(string path)
        {
            if (_player is null)
            {
                return false;
            }

            bool written = SaveSlot.Save(SaveGame.Write(_player, _missions), path);
            GD.Print(written
                ? $"[save] wrote {ProjectSettings.GlobalizePath(path)}"
                : "[save] failed");

            return written;
        }

        /// <summary>
        /// Restores the saved game and rebuilds the world around it.
        /// </summary>
        private bool LoadNow() => LoadFrom(SaveSlot.DefaultPath);

        private bool LoadFrom(string path)
        {
            string? text = SaveSlot.Load(path);
            if (text is null || _universe is null)
            {
                return false;
            }

            MissionLog? restoredLog = null;
            PlayerState restored = SaveGame.Read(text, _universe,
                player => restoredLog = new MissionLog(player), out Action restoreEconomy);

            Ship? flagship = restored.Fleet.Flagship;
            if (flagship is null || restored.CurrentSystem is null)
            {
                GD.PrintErr("[save] the save names no flagship or no system; ignoring it");
                return false;
            }

            restoreEconomy();
            _player = restored;
            _missions = restoredLog ?? new MissionLog(restored);

            // The restored hull is a different object, so everything holding the old
            // one has to be pointed at the new one: the view, the camera, the combat
            // field and the mission log's idea of who the player is.
            _ship = flagship;
            _startShip = _ship.Definition.DisplayName;
            _shipView.SyncWith(_ship);
            _camera?.Snap(_ship);

            _ui?.Bind(_player, _missions, _universe, () => _ship);

            _lastSystem = restored.CurrentSystem;
            _currentDay = DaysSinceEpoch(_player.Date.Year, _player.Date.Month, _player.Date.Day);
            restored.CurrentSystem.SetDate(_currentDay);

            BuildCombat(_universe);
            RebuildSystemViews(restored.CurrentSystem);

            // Navigation commands and the old port screen belong to the previous
            // session. Rebuild the screen from the saved landing state.
            _jumpAutopilot = false;
            _landAutopilot = false;
            _landMessage = string.Empty;
            _landedOverlay?.QueueFree();
            _landedOverlay = null;
            _isLanded = restored.CurrentPlanet != null;
            if (restored.CurrentPlanet is { } planet)
            {
                _landedOverlay = LandedOverlay.Open(this, _player, _missions, planet,
                    restored.CurrentSystem.Name, _universe);
                _landedOverlay.Departed += OnDepart;
            }
            if (_ui != null)
                _ui.Port = _landedOverlay;

            GD.Print($"[save] loaded {ProjectSettings.GlobalizePath(path)}: {_ship.Definition.DisplayName} at " +
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

            // Apply cargo sales, normal production shocks and exchanges along links.
            // The session's random stream keeps a run reproducible.
            _universe?.StepEconomy(_spawnRandom);

            // Events fire on their day. 416 of them were parsed and nothing ever fired
            // one, which is most of how the galaxy is supposed to change underneath the
            // player over a long game.
            foreach (string fired in _player.FireDueEvents(_universe))
            {
                GD.Print($"[event] {fired}");
            }

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
