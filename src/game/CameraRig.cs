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
        [Export] public float PitchDegrees { get; set; } = 62f;
        [Export] public float MinDistance { get; set; } = 22f;
        [Export] public float MaxDistance { get; set; } = 170f;
        [Export] public float FollowSharpness { get; set; } = 4.5f;

        private Camera3D _camera = null!; // built in _Ready
        private float _targetDistance = 62f;
        private float _distance = 62f;
        private Vector3 _focus;

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
                    _targetDistance = Mathf.Clamp(_targetDistance * 0.88f, MinDistance, MaxDistance);
                }
                else if (wheel.ButtonIndex == MouseButton.WheelDown)
                {
                    _targetDistance = Mathf.Clamp(_targetDistance * 1.14f, MinDistance, MaxDistance);
                }
            }
        }

        /// <summary>Smoothly track the sim ship. Call every rendered frame.</summary>
        public void Follow(Ship ship, double delta)
        {
            Vector3 shipPos = WorldSpace.ToWorld(ship.Position);

            // Look ahead along the velocity so fast flight reads on screen.
            Vector3 velocity = WorldSpace.ToWorld(ship.Position + ship.Velocity * 24.0) - shipPos;
            Vector3 focusTarget = shipPos + velocity;

            float blend = 1f - Mathf.Exp(-FollowSharpness * (float)delta);
            _focus = _focus.Lerp(focusTarget, blend);

            // Faster ships get a wider frame.
            double vmax = ship.MaxVelocity;
            float speedFraction = vmax > 0.0 ? (float)Mathf.Clamp(ship.Velocity.Length / vmax, 0.0, 1.0) : 0f;
            float distance = Mathf.Lerp(_distance, _targetDistance * (1f + 0.4f * speedFraction), blend);
            _distance = distance;

            float pitch = Mathf.DegToRad(PitchDegrees);
            Vector3 offset = new(0f, Mathf.Sin(pitch) * distance, Mathf.Cos(pitch) * distance);
            Position = _focus;
            _camera.Position = offset;
            _camera.LookAt(_focus, Vector3.Up);
        }

        /// <summary>Jump straight to the target with no smoothing (scene start).</summary>
        public void Snap(Ship ship)
        {
            _focus = WorldSpace.ToWorld(ship.Position);
            Follow(ship, 10.0);
        }
    }
}
