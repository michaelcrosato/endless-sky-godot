using System;
using System.Collections.Generic;
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
        private const string StartSystem = "Rutilicus";
        private const string StartPlanet = "New Boston";
        private const string StartShip = "Shuttle";

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

        public override void _Ready()
        {
            ParseUserArgs();
            BuildEnvironment();
            AddChild(new Starfield { Name = "Starfield" });

            GameData? universe = EsData.Universe;
            if (universe == null || !universe.Systems.TryGetValue(StartSystem, out StarSystem? system))
            {
                string message = EsData.DataPath == null
                    ? "Endless Sky data not found.\nSet ENDLESS_SKY_DATA or clone endless-sky as ../es-upstream."
                    : $"System \"{StartSystem}\" missing from dataset at {EsData.DataPath}.";
                GD.Print($"[flight] data=missing — {message.Replace('\n', ' ')}");
                BuildHud(message);
                return;
            }

            // Vanilla start date: 16 November 3013.
            system.SetDate(DaysSinceEpoch(3013, 11, 16));

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
                if (obj.PlanetName == StartPlanet)
                {
                    planetPos = obj.Position;
                    break;
                }
            }

            // Off the planet's shoulder, not on its horizontal — keeps the
            // planet out of the ship's line and off the HUD corner.
            _ship.Position = planetPos + new Point(-120.0, 210.0);
            _ship.Facing = new Angle(0.0);
            BuildLighting(planetPos);

            _shipView = new ShipView { Name = "PlayerShip" };
            AddChild(_shipView);
            _shipView.SyncWith(_ship);

            _camera = new CameraRig { Name = "CameraRig" };
            AddChild(_camera);
            _camera.Snap(_ship);

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
            Command command;
            if (_autopilot)
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
            _shipView.SyncWith(_ship);
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

            var key = new DirectionalLight3D
            {
                Name = "StarKeyLight",
                LightColor = new Color(1.0f, 0.94f, 0.85f),
                LightEnergy = 2.2f,
                ShadowEnabled = true,
                DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal,
                DirectionalShadowMaxDistance = 400f,
                ShadowBias = 0.03f,
            };
            AddChild(key);
            key.LookAtFromPosition(Vector3.Zero, toPlayArea, Vector3.Up);

            var fill = new DirectionalLight3D
            {
                Name = "CoolFill",
                LightColor = new Color(0.42f, 0.58f, 1.0f),
                LightEnergy = 0.30f,
                ShadowEnabled = false,
            };
            AddChild(fill);
            fill.LookAtFromPosition(Vector3.Zero, -toPlayArea + new Vector3(0f, -0.35f, 0f) * toPlayArea.Length(), Vector3.Up);
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

            var title = new Label { Text = errorMessage ?? $"{StartSystem.ToUpperInvariant()}  ·  {StartShip.ToUpperInvariant()}" };
            title.AddThemeFontSizeOverride("font_size", 20);
            title.AddThemeColorOverride("font_color", new Color(0.88f, 0.93f, 1.0f));
            column.AddChild(title);

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
            var keys = new Label { Text = "↑ thrust · ←/→ turn · ↓ turn retrograde (brake) · wheel zoom · WASD also works" };
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
            _statusLabel.Text = $"{speed:0} KM/S · {pct:0}%   HDG {_ship.Facing.AbsDegrees:000}°";
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
