using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// The single sim ↔ render coordinate bridge. Nothing outside this class
    /// converts between the two spaces.
    ///
    /// Simulation space is upstream Endless Sky screen convention: units are
    /// pixels, +X right, +Y DOWN, angle 0° points "up" (0,-1) and increases
    /// clockwise. Rendering space is Godot 3D: the sim plane maps onto the XZ
    /// plane (sim X → world X, sim Y → world Z, so sim "up" is world −Z),
    /// world Y is altitude and is always 0 for gameplay entities. A node whose
    /// forward is −Z therefore faces sim angle θ when its yaw is −θ.
    /// </summary>
    public static class WorldSpace
    {
        /// <summary>World units per simulation pixel.</summary>
        public const float Scale = 0.1f;

        public static Vector3 ToWorld(Point simPosition)
        {
            return new Vector3((float)(simPosition.X * Scale), 0f, (float)(simPosition.Y * Scale));
        }

        /// <summary>Yaw (radians, for Node3D.Rotation.Y) for a sim facing.</summary>
        public static float YawFromFacing(Angle facing)
        {
            return (float)Mathf.DegToRad(-facing.AbsDegrees);
        }

        /// <summary>A sim length (px) as a world length.</summary>
        public static float Length(double simLength) => (float)(simLength * Scale);

        /// <summary>Sim px/frame → world units per second (sim runs 60 frames/s).</summary>
        public static float SpeedToWorldPerSecond(double pxPerFrame)
        {
            return (float)(pxPerFrame * Ship.FramesPerSecond * Scale);
        }
    }
}
