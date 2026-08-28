using System;
using System.Collections.Generic;

namespace EndlessSky.Sim
{
    /// <summary>Broad size bands, used to pick a silhouette family and a polygon budget.</summary>
    public enum HullClass
    {
        Drone,
        Fighter,
        Light,
        Medium,
        Heavy,
        Capital,
    }

    /// <summary>A mount the view layer needs to place geometry on.</summary>
    public readonly struct MountPlacement
    {
        public MountPlacement(Point offset, MountKind kind, string? outfitName)
        {
            Offset = offset;
            Kind = kind;
            OutfitName = outfitName;
        }

        /// <summary>Position in hull-local simulation units, already halved to true scale.</summary>
        public Point Offset { get; }

        public MountKind Kind { get; }

        /// <summary>Default outfit at this mount, when the data names one.</summary>
        public string? OutfitName { get; }

        public override string ToString() => $"{Kind} @ {Offset}";
    }

    public enum MountKind { Engine, Gun, Turret }

    /// <summary>
    /// The visual description of a ship, derived from its data rather than authored
    /// per-model. Milestone 8's "common rules" in executable form.
    /// </summary>
    /// <remarks>
    /// This is deliberately engine-free: it decides WHAT a hull should look like from
    /// numbers the simulation already has, and the presentation layer decides how to
    /// draw it. That split is what lets 902 upstream ships get a consistent look
    /// without 902 hand-built meshes.
    ///
    /// The governing constraint comes from the dataset itself. Hull mass runs from 10
    /// (a Drone) to 67400 (the largest warship) - a factor of nearly 7000. Scaling
    /// length linearly with mass would make a fighter invisible next to a capital
    /// ship, so length scales with the CUBE ROOT: mass tracks volume, and volume is
    /// the cube of a linear dimension. Across the real fleet that turns a 6700x mass
    /// range into a ~190x length range, matching the spread upstream's sprites
    /// actually have: a Korath World-Ship really is orders of magnitude longer
    /// than an interceptor.
    ///
    /// INCOMPLETE, tracked rather than dropped: faction design language cannot be
    /// derived from a ship definition (upstream associates ships with governments
    /// through fleets and shipyards, not on the hull), so <see cref="Faction"/> is
    /// settable and defaults to null. Windows and damage-state geometry are described
    /// here but generating them is the view layer's job.
    /// </remarks>
    public class ShipAppearance
    {
        /// <summary>
        /// Coefficient and exponent of the fallback length curve, fitted by least
        /// squares on log(mass) against log(hardpoint span) over the 318 ships that
        /// carry hardpoints and a mass (r = 0.84).
        /// </summary>
        /// <remarks>
        /// The exponent is 0.47, NOT the 1/3 a uniform solid would give. Ships are
        /// shells rather than solid bodies and larger hulls are proportionally
        /// hollower, so length outruns the cube root. Assuming the cube root put 287
        /// of the 339 armed ships' mounts outside their own hull, by up to 4.5x.
        /// </remarks>
        private const double MassLengthCoefficient = 4.66;
        private const double MassLengthExponent = 0.471;

        /// <summary>
        /// How far the hull extends past its outermost hardpoint. A mount sitting
        /// exactly on the extreme would hang half off the tip.
        /// </summary>
        private const double MountMargin = 1.15;

        /// <summary>
        /// Slenderness floor, from the fleet median of hardpoint span across to
        /// along (0.32). Applied only where the hardpoints do not already imply a
        /// wider hull, since mounts cluster near the centreline and so understate
        /// the true beam.
        /// </summary>
        private const double MinimumBeamRatio = 0.34;

        /// <summary>
        /// Beam ceiling. A handful of hulls (the Coalition Koryfi line) mount their
        /// hardpoints far wider than they are deep; without this the beam overtakes
        /// the length and the hull is drawn as a pancake flying sideways.
        /// </summary>
        private const double MaximumBeamRatio = 0.85;

        private readonly List<MountPlacement> _mounts = new List<MountPlacement>();

        public ShipAppearance(ShipDefinition definition)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));

            double mass = Math.Max(1.0, definition.Attributes.Get("mass"));
            Class = Classify(definition.Category, mass);

            // Hardpoint coordinates in ship data are stored at twice scale, exactly as
            // the armament layer halves them.
            foreach (Hardpoint engine in definition.Engines)
                _mounts.Add(new MountPlacement(engine.Offset * 0.5, MountKind.Engine, engine.OutfitName));

            foreach (Hardpoint gun in definition.Guns)
                _mounts.Add(new MountPlacement(gun.Offset * 0.5, MountKind.Gun, gun.OutfitName));

            foreach (Hardpoint turret in definition.Turrets)
                _mounts.Add(new MountPlacement(turret.Offset * 0.5, MountKind.Turret, turret.OutfitName));

            // Size the hull from its own hardpoints where it has any. Upstream has no
            // "length" field at all - a ship's size is the size of its sprite - and
            // hardpoint offsets are sprite pixel coordinates, so they are the only
            // real measurement of a hull in the data files. Deriving size from mass
            // instead is an invention, and it disagreed with the mounts badly enough
            // that most of the fleet wore its guns off the hull.
            double alongExtent = 0.0, acrossExtent = 0.0;
            foreach (MountPlacement mount in _mounts)
            {
                alongExtent = Math.Max(alongExtent, Math.Abs(mount.Offset.Y));
                acrossExtent = Math.Max(acrossExtent, Math.Abs(mount.Offset.X));
            }

            double alongSpan = 2.0 * alongExtent * MountMargin;
            double acrossSpan = 2.0 * acrossExtent * MountMargin;

            // Three lower bounds, all of which must hold: long enough for the fore-aft
            // mounts, long enough that the widest mounts still fit inside the beam
            // ceiling, and never below what the ship's mass implies. The mass term is
            // what keeps a hull whose mounts all sit near its centre from collapsing
            // to a speck.
            Length = Math.Max(
                Math.Max(alongSpan, acrossSpan / MaximumBeamRatio),
                MassLengthCoefficient * Math.Pow(mass, MassLengthExponent));

            Beam = Math.Clamp(acrossSpan, Length * MinimumBeamRatio, Length * MaximumBeamRatio);

            // Bounding radius has to cover the wider of the two axes, not just length.
            Radius = Math.Max(Length, Beam) * 0.5;
        }

        public ShipDefinition Definition { get; }

        public HullClass Class { get; }

        /// <summary>Nose-to-tail length in simulation units.</summary>
        public double Length { get; }

        /// <summary>Widest beam in simulation units.</summary>
        public double Beam { get; }

        /// <summary>Half-length, a reasonable default collision and framing radius.</summary>
        public double Radius { get; }

        /// <summary>
        /// Faction whose design language this hull follows. Not derivable from a ship
        /// definition; the loader or the view layer sets it.
        /// </summary>
        public string? Faction { get; set; }

        public IReadOnlyList<MountPlacement> Mounts => _mounts;

        /// <summary>
        /// Triangle budget for this hull. Ties polygon density to on-screen size so a
        /// swarm of drones cannot cost more than the capital ship they are attacking.
        /// </summary>
        public int TriangleBudget => Class switch
        {
            HullClass.Drone => 150,
            HullClass.Fighter => 300,
            HullClass.Light => 700,
            HullClass.Medium => 1500,
            HullClass.Heavy => 3000,
            HullClass.Capital => 6000,
            _ => 700,
        };

        /// <summary>
        /// Lit ports along the hull. Scaled to length so a freighter reads as crewed
        /// and a drone reads as unmanned: upstream drones carry no crew at all.
        /// </summary>
        public int WindowCount
        {
            get
            {
                if (Definition.Attributes.Get("automaton") != 0.0)
                    return 0;

                int bunks = (int)Definition.Attributes.Get("bunks");
                if (bunks <= 0)
                    return 0;

                // One port per few berths, capped so a liner does not become a grid.
                return Math.Clamp(bunks / 4, 1, 40);
            }
        }

        /// <summary>
        /// Engine glow strength, from thrust per unit mass. A tug with heavy engines
        /// and a light hull should visibly burn harder than a laden freighter.
        /// </summary>
        public double EngineGlow
        {
            get
            {
                double mass = Math.Max(1.0, Definition.Attributes.Get("mass"));
                double thrust = Definition.Attributes.Get("thrust");
                if (thrust <= 0.0)
                    return 0.0;

                // Normalised against a typical thrust-to-mass ratio so most ships sit
                // near 1 and outliers stand out rather than saturating everything.
                return Math.Clamp(thrust / mass / 0.02, 0.2, 3.0);
            }
        }

        /// <summary>
        /// Hull integrity band, for swapping damage-state geometry and decals.
        /// 0 is pristine, 3 is a wreck.
        /// </summary>
        public static int DamageState(double hull, double maxHull)
        {
            if (maxHull <= 0.0)
                return 0;

            double fraction = hull / maxHull;
            if (fraction > 0.75) return 0;
            if (fraction > 0.45) return 1;
            if (fraction > 0.15) return 2;
            return 3;
        }

        /// <summary>
        /// Size band. Category is authoritative when the data gives one, because it
        /// carries intent that mass alone does not: a Utility hull can outweigh a
        /// warship without being one.
        /// </summary>
        public static HullClass Classify(string? category, double mass) => category switch
        {
            "Drone" => HullClass.Drone,
            "Fighter" => HullClass.Fighter,
            "Interceptor" or "Light Warship" or "Light Freighter" => HullClass.Light,
            "Medium Warship" or "Transport" or "Space Liner" => HullClass.Medium,
            "Heavy Warship" or "Heavy Freighter" => HullClass.Heavy,
            "Superheavy" => HullClass.Capital,
            _ => FromMass(mass),
        };

        /// <summary>Fallback for the ~30 hulls that carry no usable category.</summary>
        private static HullClass FromMass(double mass) =>
            mass < 60.0 ? HullClass.Drone
            : mass < 170.0 ? HullClass.Fighter
            : mass < 500.0 ? HullClass.Light
            : mass < 1500.0 ? HullClass.Medium
            : mass < 10000.0 ? HullClass.Heavy
            : HullClass.Capital;

        public override string ToString() =>
            $"{Definition.DisplayName}: {Class}, {Length:F0}u, {_mounts.Count} mounts";
    }
}
