using System;

namespace EndlessSky.Sim
{
    /// <summary>
    /// Geometry for projectile impacts.
    /// </summary>
    /// <remarks>
    /// Upstream tests projectiles against per-sprite collision masks
    /// (<c>Mask::Collide</c>) along the segment the projectile travelled this frame.
    /// The segment matters more than the mask: a blaster bolt covers 10+ units per
    /// frame and a fighter is only a few units across, so a point-in-circle test at
    /// the frame boundary would let shots pass straight through their targets.
    ///
    /// INCOMPLETE, tracked rather than dropped: sprite-accurate masks. Ships are
    /// approximated as circles here, so glancing hits near a hull's silhouette differ
    /// from upstream. Swapping in a mask only changes <see cref="SweepCircle"/>.
    /// </remarks>
    public static class Collision
    {
        /// <summary>
        /// Finds where a point moving from <paramref name="from"/> to
        /// <paramref name="to"/> first enters a circle, as a fraction of the step.
        /// Returns null when it never does.
        /// </summary>
        /// <returns>
        /// The fraction along the segment in [0, 1] of the first intersection, or 0
        /// when the segment already starts inside the circle.
        /// </returns>
        public static double? SweepCircle(Point from, Point to, Point center, double radius)
        {
            if (radius <= 0.0)
                return null;

            Point offset = from - center;

            // Already touching at the start of the step: an immediate hit.
            if (offset.LengthSquared <= radius * radius)
                return 0.0;

            Point step = to - from;
            double a = step.LengthSquared;

            // A stationary projectile that was not already inside can never enter.
            if (a <= 0.0)
                return null;

            // Solve |offset + t*step|^2 = radius^2 for the smaller root.
            double b = 2.0 * offset.Dot(step);
            double c = offset.LengthSquared - radius * radius;

            double discriminant = b * b - 4.0 * a * c;
            if (discriminant < 0.0)
                return null;

            double root = Math.Sqrt(discriminant);
            double t = (-b - root) / (2.0 * a);

            // The near root is behind the start; the far root would mean the segment
            // begins inside, which the early check above already handled.
            if (t < 0.0 || t > 1.0)
                return null;

            return t;
        }

        /// <summary>Whether a segment passes within <paramref name="radius"/> of a point.</summary>
        public static bool SegmentHitsCircle(Point from, Point to, Point center, double radius) =>
            SweepCircle(from, to, center, radius).HasValue;
    }
}
