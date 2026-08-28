using System;

namespace EndlessSky.Sim
{
    /// <summary>
    /// Double-precision 2D vector, the simulation's position/velocity type.
    ///
    /// Deliberately not <c>UnityEngine.Vector2</c>: the simulation assembly has no
    /// engine reference, and upstream runs its physics in doubles. Using floats here
    /// would introduce drift that shows up as position divergence over a long flight.
    ///
    /// Coordinate convention matches upstream: screen-style, with +Y pointing DOWN.
    /// The Unity presentation layer maps this plane to world XZ.
    /// </summary>
    public readonly struct Point : IEquatable<Point>
    {
        public readonly double X;
        public readonly double Y;

        public Point(double x, double y)
        {
            X = x;
            Y = y;
        }

        public static readonly Point Zero = new Point(0.0, 0.0);

        public double LengthSquared => X * X + Y * Y;

        public double Length => Math.Sqrt(X * X + Y * Y);

        /// <summary>Unit vector, or (0,0) for a zero-length vector (upstream returns (1,0); see note).</summary>
        public Point Unit()
        {
            double lengthSquared = LengthSquared;
            if (lengthSquared == 0.0)
            {
                // Upstream returns (1, 0) here. Callers in DoMovement guard against the
                // zero case before calling, so the choice is not observable there, but
                // matching upstream keeps any future port of AI code honest.
                return new Point(1.0, 0.0);
            }

            double inverse = 1.0 / Math.Sqrt(lengthSquared);
            return new Point(X * inverse, Y * inverse);
        }

        public double Dot(Point other) => X * other.X + Y * other.Y;

        public double Cross(Point other) => X * other.Y - Y * other.X;

        /// <summary>True when the vector is non-zero, mirroring upstream's <c>operator bool</c>.</summary>
        public bool IsNonZero => X != 0.0 || Y != 0.0;

        public static Point operator +(Point a, Point b) => new Point(a.X + b.X, a.Y + b.Y);

        public static Point operator -(Point a, Point b) => new Point(a.X - b.X, a.Y - b.Y);

        public static Point operator -(Point a) => new Point(-a.X, -a.Y);

        public static Point operator *(Point a, double s) => new Point(a.X * s, a.Y * s);

        public static Point operator *(double s, Point a) => new Point(a.X * s, a.Y * s);

        public static Point operator /(Point a, double s) => new Point(a.X / s, a.Y / s);

        public bool Equals(Point other) => X.Equals(other.X) && Y.Equals(other.Y);

        public override bool Equals(object obj) => obj is Point other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (X.GetHashCode() * 397) ^ Y.GetHashCode();
            }
        }

        public static bool operator ==(Point a, Point b) => a.Equals(b);

        public static bool operator !=(Point a, Point b) => !a.Equals(b);

        public override string ToString() => $"({X:R}, {Y:R})";
    }
}
