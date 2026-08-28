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

            _ship.Position = planetPos + new Point(0.0, 170.0);
            _ship.Facing = new Angle(0.0);

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
                command = new Command
                {
                    Forward = Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up),
                    Back = Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down),
                    Stop = Input.IsPhysicalKeyPressed(Key.Space),
                };
                double turn = 0.0;
                if (Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left)) turn -= 1.0;
                if (Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right)) turn += 1.0;
                command.Turn = turn;
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
                AmbientLightEnergy = 0.22f,
                GlowEnabled = true,
                GlowIntensity = 0.6f,
                GlowBloom = 0.05f,
                TonemapMode = Godot.Environment.ToneMapper.Aces,
            };
            AddChild(new WorldEnvironment { Environment = environment });
        }

        private void BuildHud(string? errorMessage)
        {
            var hud = new CanvasLayer { Name = "Hud" };
            AddChild(hud);

            var panel = new PanelContainer();
            panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            panel.Position = new Vector2(14, 14);
            var style = new StyleBoxFlat
            {
                BgColor = new Color(0.05f, 0.07f, 0.11f, 0.72f),
                CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
                ContentMarginLeft = 12, ContentMarginRight = 12,
                ContentMarginTop = 8, ContentMarginBottom = 8,
            };
            panel.AddThemeStyleboxOverride("panel", style);
            hud.AddChild(panel);

            _statusLabel = new Label();
            _statusLabel.AddThemeFontSizeOverride("font_size", 15);
            panel.AddChild(_statusLabel);

            _statusLabel.Text = errorMessage ??
                $"{StartSystem} — {StartShip}\nW/↑ thrust · A/D or ←/→ turn · S/↓ reverse · wheel zoom";
        }

        private void UpdateHud()
        {
            if (_statusLabel == null || _ship == null)
            {
                return;
            }

            double speed = _ship.Velocity.Length;
            double heading = _ship.Facing.AbsDegrees;
            _statusLabel.Text =
                $"{StartSystem} — {StartShip}\n" +
                $"speed {speed * Ship.FramesPerSecond:0} px/s ({speed / Math.Max(_ship.MaxVelocity, 1e-9) * 100:0}% of max) · heading {heading:000}°\n" +
                "W/↑ thrust · A/D or ←/→ turn · S/↓ reverse · wheel zoom";
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
