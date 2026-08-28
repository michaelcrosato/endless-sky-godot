using System;

namespace EndlessSky.Sim
{
    /// <summary>
    /// Fixed-point clock angle, a port of upstream <c>Angle</c>.
    ///
    /// This is NOT a plain double. Upstream stores the angle as one of 65536 discrete
    /// steps and wraps by masking, so turning is quantized to ~0.0055 degrees. Ports
    /// that use a float angle drift measurably from upstream over a long turn, so the
    /// quantization is reproduced exactly.
    ///
    /// 0 degrees points "up" the screen, i.e. the direction (0, -1), and angles
    /// increase clockwise, like a clock face.
    /// </summary>
    public readonly struct Angle : IEquatable<Angle>
    {
        private const int Steps = 0x10000;
        private const int Mask = Steps - 1;
        private const double DegToStep = Steps / 360.0;
        private const double StepToRad = Math.PI / (Steps / 2);

        private static readonly Point[] UnitCache = BuildUnitCache();

        private readonly int _angle;

        private Angle(int step)
        {
            _angle = step & Mask;
        }

        public Angle(double degrees)
        {
            _angle = (int)(RoundHalfAwayFromZero(degrees * DegToStep) & Mask);
        }

        /// <summary>Angle pointing along the given vector.</summary>
        public static Angle FromPoint(Point point)
        {
            return new Angle(180.0 / Math.PI * Math.Atan2(point.X, -point.Y));
        }

        private static long RoundHalfAwayFromZero(double value)
        {
            // C's llround rounds halves away from zero; Math.Round defaults to banker's
            // rounding, which would place some angles one step off from upstream.
            return (long)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        private static Point[] BuildUnitCache()
        {
            var cache = new Point[Steps];
            for (int i = 0; i < Steps; i++)
            {
                double radians = i * StepToRad;
                cache[i] = new Point(Math.Sin(radians), -Math.Cos(radians));
            }

            return cache;
        }

        /// <summary>Unit vector in this direction.</summary>
        public Point Unit() => UnitCache[_angle];

        /// <summary>Degrees in the range [-180, 180), matching upstream.</summary>
        public double Degrees => _angle / DegToStep - 360.0 * (_angle >= Steps / 2 ? 1 : 0);

        /// <summary>Degrees in the range [0, 360).</summary>
        public double AbsDegrees => _angle / DegToStep;

        /// <summary>Internal step value; exposed for tests that assert on quantization.</summary>
        public int Step => _angle;

        /// <summary>Rotates a point by this angle, in the screen-style coordinate system.</summary>
        public Point Rotate(Point point)
        {
            Point unit = Unit();
            return new Point(
                -unit.Y * point.X - unit.X * point.Y,
                -unit.Y * point.Y + unit.X * point.X);
        }

        public static Angle operator +(Angle a, Angle b) => new Angle(a._angle + b._angle);

        public static Angle operator -(Angle a, Angle b) => new Angle(a._angle - b._angle);

        public static Angle operator -(Angle a) => new Angle(-a._angle);

        public bool Equals(Angle other) => _angle == other._angle;

        public override bool Equals(object obj) => obj is Angle other && Equals(other);

        public override int GetHashCode() => _angle;

        public static bool operator ==(Angle a, Angle b) => a._angle == b._angle;

        public static bool operator !=(Angle a, Angle b) => a._angle != b._angle;

        public override string ToString() => $"{Degrees:0.###}deg";
    }
}
