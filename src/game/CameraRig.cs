using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// Elevated chase camera per the directive: high enough to read nearby
    /// ships, planets and destinations; smooth follow with a velocity
    /// look-ahead; wheel zoom; configurable pitch; speed-sensitive distance.
    /// Cinematic motion, but readability first.
    /// </summary>
    public partial class CameraRig : Node3D
    {
        [Export] public float PitchDegrees { get; set; } = 52f;
        [Export] public float MinZoom { get; set; } = 0.6f;
        [Export] public float MaxZoom { get; set; } = 6f;
        [Export] public float FollowSharpness { get; set; } = 4.5f;

        private Camera3D _camera = null!; // built in _Ready
        private float _targetZoom = 1f;
        private float _distance = 30f;
        private Vector3 _focus;
        private ShipDefinition? _framedHull;
        private float _hullRadius;

        public override void _Ready()
        {
            _camera = new Camera3D { Far = 8000f, Fov = 55f };
            AddChild(_camera);
            _camera.MakeCurrent();
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            if (@event is InputEventMouseButton { Pressed: true } wheel)
            {
                if (wheel.ButtonIndex == MouseButton.WheelUp)
                {
                    _targetZoom = Mathf.Clamp(_targetZoom * 0.88f, MinZoom, MaxZoom);
                }
                else if (wheel.ButtonIndex == MouseButton.WheelDown)
                {
                    _targetZoom = Mathf.Clamp(_targetZoom * 1.14f, MinZoom, MaxZoom);
                }
            }
        }

        /// <summary>Smoothly track the sim ship. Call every rendered frame.</summary>
        public void Follow(Ship ship, double delta)
        {
            Vector3 shipPos = WorldSpace.ToWorld(ship.Position);
            if (!ReferenceEquals(_framedHull, ship.Definition))
            {
                _framedHull = ship.Definition;
                _hullRadius = WorldSpace.Length(new ShipAppearance(ship.Definition).Radius);
            }

            // At normal zoom, leave most of the shorter viewport dimension for the
            // surrounding fight. Cache hull geometry, but adapt to window resizing.
            Vector2 viewport = GetViewport().GetVisibleRect().Size;
            float aspect = viewport.Y > 0 ? Mathf.Min(1f, viewport.X / viewport.Y) : 1f;
            float halfField = Mathf.Tan(Mathf.DegToRad(_camera.Fov * 0.5f)) * Mathf.Max(aspect, 0.1f);
            float baseDistance = Mathf.Max(30f, _hullRadius / (halfField * 0.30f));
            float blend = 1f - Mathf.Exp(-FollowSharpness * (float)delta);

            // Faster ships get a modestly wider frame. Zoom stays relative to the
            // hull, so changing flagships does not reuse a fighter's camera distance.
            double vmax = ship.MaxVelocity;
            float speedFraction = vmax > 0.0 ? (float)Mathf.Clamp(ship.Velocity.Length / vmax, 0.0, 1.0) : 0f;
            _distance = Mathf.Lerp(_distance, baseDistance * _targetZoom * (1f + 0.15f * speedFraction), blend);

            // Look ahead along the velocity so fast flight reads on screen —
            // bounded by the current frame, or a fast ship leaves its own view.
            Vector3 velocity = WorldSpace.ToWorld(ship.Position + ship.Velocity * 24.0) - shipPos;
            velocity = velocity.LimitLength(Mathf.Min(22f, _distance * halfField * 0.30f));
            Vector3 focusTarget = shipPos + velocity;

            _focus = _focus.Lerp(focusTarget, blend);

            float pitch = Mathf.DegToRad(PitchDegrees);
            Vector3 offset = new(0f, Mathf.Sin(pitch) * _distance, Mathf.Cos(pitch) * _distance);
            Position = _focus;
            _camera.Position = offset;
            // Bias the look target up in screen space so the ship rides the
            // lower third instead of dead center.
            _camera.LookAt(_focus + Vector3.Up * (_distance * 0.10f), Vector3.Up);
        }

        /// <summary>Jump straight to the target with no smoothing (scene start).</summary>
        public void Snap(Ship ship)
        {
            _focus = WorldSpace.ToWorld(ship.Position);
            Follow(ship, 10.0);
        }

        /// <summary>Frame a port while the pilot has no flagship.</summary>
        public void Snap(Point point)
        {
            _focus = WorldSpace.ToWorld(point);
            Position = _focus;
            _camera.LookAt(_focus + Vector3.Up * (_distance * 0.10f), Vector3.Up);
        }
    }
}
