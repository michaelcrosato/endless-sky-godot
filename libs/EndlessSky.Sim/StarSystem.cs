using System;
using System.Collections.Generic;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// A star, planet, moon or station in a system. Objects orbit their parent (or the
    /// system centre) on a circle of radius <see cref="Distance"/>.
    /// </summary>
    public class StellarObject
    {
        private readonly List<StellarObject> _children = new List<StellarObject>();

        public string? PlanetName { get; internal set; }

        /// <summary>
        /// The planet this object is, once <c>GameData.FinishLoading</c> has linked it.
        /// Upstream's StellarObject holds a Planet pointer for the same reason: a name
        /// alone cannot answer whether somewhere has a spaceport.
        /// </summary>
        public Planet? Planet { get; internal set; }

        public string Sprite { get; internal set; } = string.Empty;

        public double Distance { get; internal set; }

        /// <summary>Degrees per day. Derived from an explicit period, or from orbital mechanics.</summary>
        public double Speed { get; internal set; }

        /// <summary>Starting angle in degrees; used to place binary partners 180 apart.</summary>
        public double Offset { get; internal set; }

        public bool ExplicitPeriodSet { get; internal set; }

        public StellarObject? Parent { get; internal set; }

        public IReadOnlyList<StellarObject> Children => _children;

        public bool IsStar => Sprite.StartsWith("star/", StringComparison.Ordinal);

        public bool IsStation => Sprite.StartsWith("planet/station", StringComparison.Ordinal);

        public bool IsMoon => !IsStar && !IsStation && Parent != null && !Parent.IsStar;

        /// <summary>Position for the current date, relative to the system centre.</summary>
        public Point Position { get; private set; }

        internal void AddChild(StellarObject child)
        {
            child.Parent = this;
            _children.Add(child);
        }

        /// <summary>
        /// Places this object for a given day. Matches upstream <c>System::SetDate</c>:
        /// a circular orbit whose phase is <c>days * speed + offset</c>, plus the parent's
        /// position for moons.
        /// </summary>
        public void SetDate(double daysSinceEpoch)
        {
            var angle = new Angle(daysSinceEpoch * Speed + Offset);
            Position = angle.Unit() * Distance;
            if (Parent != null)
            {
                Position += Parent.Position;
            }

            foreach (StellarObject child in _children)
            {
                child.SetDate(daysSinceEpoch);
            }
        }
    }

    /// <summary>A star system: the unit of simulation. Upstream never simulates two at once.</summary>
    public class StarSystem
    {
        private readonly List<StellarObject> _objects = new List<StellarObject>();
        private readonly List<string> _links = new List<string>();

        public StarSystem(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public string Government { get; private set; } = string.Empty;

        /// <summary>Position on the galaxy map, not in-system coordinates.</summary>
        public Point MapPosition { get; private set; }

        /// <summary>Top-level objects; moons hang off their parents.</summary>
        public IReadOnlyList<StellarObject> Objects => _objects;

        /// <summary>Names of systems reachable by hyperspace from here.</summary>
        public IReadOnlyList<string> Links => _links;

        /// <summary>Opens a hyperspace link, as an event's "link" change does.</summary>
        public void AddLink(string other)
        {
            if (!string.IsNullOrEmpty(other) && !_links.Contains(other))
                _links.Add(other);
        }

        /// <summary>Closes a hyperspace link, as an event's "unlink" change does.</summary>
        public void RemoveLink(string other) => _links.Remove(other);

        public double Habitable { get; private set; }

        /// <summary>
        /// Extra distance from the arrival target for ships entering by hyperdrive,
        /// from the system's <c>arrival</c> node. Zero when unset.
        /// </summary>
        /// <remarks>
        /// This is not just a distance: upstream switches the arrival TARGET on it.
        /// A system with any extra arrival distance is entered aimed at the system
        /// centre, and only a system without one is entered aimed at a planet. Systems
        /// set it precisely so that arrivals cannot drop straight onto the inhabited
        /// worlds, so treating it as zero puts ships on top of what it exists to keep
        /// them away from.
        ///
        /// INCOMPLETE, tracked rather than dropped: upstream can also derive this from
        /// the habitable zone under the <c>habitable based arrival distance</c>
        /// gamerule, and clamp it to gamerule minima. We have no Gamerules type, so
        /// only the per-system value is honoured - which matches default gamerules.
        /// </remarks>
        public double ExtraHyperArrivalDistance { get; private set; }

        /// <summary>
        /// Extra arrival distance for jump drives. Always non-negative: upstream takes
        /// the absolute value, since a negative hyper arrival distance is meaningful
        /// (it arrives past the target) but a negative jump radius is not.
        /// </summary>
        public double ExtraJumpArrivalDistance { get; private set; }

        /// <summary>Every object in the system, parents before children.</summary>
        public IEnumerable<StellarObject> AllObjects()
        {
            foreach (StellarObject o in _objects)
            {
                foreach (StellarObject descendant in Walk(o))
                {
                    yield return descendant;
                }
            }
        }

        private static IEnumerable<StellarObject> Walk(StellarObject o)
        {
            yield return o;
            foreach (StellarObject child in o.Children)
            {
                foreach (StellarObject descendant in Walk(child))
                {
                    yield return descendant;
                }
            }
        }

        public void Load(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                string key = child.Token(0);
                switch (key)
                {
                    case "pos" when child.Size >= 3:
                        MapPosition = new Point(child.Value(1), child.Value(2));
                        break;

                    case "government" when child.Size >= 2:
                        Government = child.Token(1);
                        break;

                    case "habitable" when child.Size >= 2:
                        Habitable = child.Value(1);
                        break;

                    case "arrival":
                        if (child.Size >= 2)
                        {
                            ExtraHyperArrivalDistance = child.Value(1);
                            ExtraJumpArrivalDistance = Math.Abs(child.Value(1));
                        }

                        // The children override the bare value per drive type.
                        foreach (DataNode grand in child.Children)
                        {
                            if (grand.Size < 2)
                                continue;
                            if (grand.Token(0) == "link")
                                ExtraHyperArrivalDistance = grand.Value(1);
                            else if (grand.Token(0) == "jump")
                                ExtraJumpArrivalDistance = Math.Abs(grand.Value(1));
                        }
                        break;

                    case "link" when child.Size >= 2:
                        _links.Add(child.Token(1));
                        break;

                    case "object":
                        _objects.Add(LoadObject(child, null));
                        break;
                }
            }
        }

        private static StellarObject LoadObject(DataNode node, StellarObject? parent)
        {
            var obj = new StellarObject();
            if (node.Size >= 2)
            {
                obj.PlanetName = node.Token(1);
            }

            parent?.AddChild(obj);

            foreach (DataNode child in node.Children)
            {
                string key = child.Token(0);
                switch (key)
                {
                    case "sprite" when child.Size >= 2:
                        obj.Sprite = child.Token(1);
                        break;

                    case "distance" when child.Size >= 2:
                        obj.Distance = child.Value(1);
                        break;

                    case "period" when child.Size >= 2:
                    {
                        double period = child.Value(1);
                        if (period != 0.0)
                        {
                            obj.ExplicitPeriodSet = true;
                            obj.Speed = 360.0 / period;
                        }

                        break;
                    }

                    case "offset" when child.Size >= 2:
                        obj.Offset = child.Value(1);
                        break;

                    case "object":
                        LoadObject(child, obj);
                        break;
                }
            }

            return obj;
        }

        /// <summary>
        /// Fills in orbital speeds for objects with no explicit period, using upstream's
        /// rule: a lone star takes 10 days to rotate, and everything else follows
        /// <c>period = sqrt(distance^3 / mass)</c> against the mass it orbits.
        /// </summary>
        /// <summary>
        /// Derives the orbital period of every object that does not state one, port of
        /// the tail of upstream <c>System::UpdateSystem</c> (System.cpp:576).
        /// </summary>
        /// <remarks>
        /// The star case is easy to miss because a single star genuinely has a fixed
        /// period of 10: it sits at the centre and the number is arbitrary. In a
        /// BINARY the stars orbit their common centre of mass, and upstream derives
        /// that period from the summed separation and summed mass of every star in the
        /// system. Leaving them on the default spins both stars of every binary at the
        /// same rate no matter how far apart they are.
        /// </remarks>
        public void ResolveOrbits(Func<string, double> spriteMass)
        {
            double starMass = 0.0;
            double starDistance = 0.0;
            int starCount = 0;
            foreach (StellarObject o in AllObjects())
            {
                if (o.IsStar)
                {
                    starCount++;
                    starMass += spriteMass(o.Sprite);
                    starDistance += o.Distance;
                }
            }

            // If nothing is a star, upstream treats the first object as the centre of
            // mass AND as a star for period purposes.
            bool treatNextObjectAsStar = false;
            if (starCount == 0 && _objects.Count > 0)
            {
                treatNextObjectAsStar = true;
                starCount = 1;
                starMass = spriteMass(_objects[0].Sprite);
            }

            foreach (StellarObject o in AllObjects())
            {
                if (o.ExplicitPeriodSet || o.Distance == 0.0)
                {
                    continue;
                }

                double period = 10.0;
                if (o.Parent != null)
                {
                    // Moons are governed by the mass of the planet they orbit.
                    double mass = spriteMass(o.Parent.Sprite);
                    if (mass > 0.0)
                    {
                        period = Math.Sqrt(Math.Pow(o.Distance, 3) / mass);
                    }
                }
                else if (starMass <= 0.0)
                {
                    // No star, or stars with no defined mass: upstream warns and
                    // leaves the default rather than dividing by zero.
                }
                else if (o.IsStar || treatNextObjectAsStar)
                {
                    treatNextObjectAsStar = false;

                    // A lone star keeps the arbitrary default; it is at the centre and
                    // nothing orbits it but everything else. Two or more orbit each
                    // other, on their combined separation and mass.
                    if (starCount > 1)
                    {
                        period = Math.Sqrt(Math.Pow(starDistance, 3) / starMass);
                    }
                }
                else
                {
                    period = Math.Sqrt(Math.Pow(o.Distance, 3) / starMass);
                }

                o.Speed = 360.0 / period;
            }
        }

        public void SetDate(double daysSinceEpoch)
        {
            foreach (StellarObject o in _objects)
            {
                o.SetDate(daysSinceEpoch);
            }
        }

        public override string ToString() => Name;
    }
}
