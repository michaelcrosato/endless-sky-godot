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

        public string PlanetName { get; internal set; }

        public string Sprite { get; internal set; } = string.Empty;

        public double Distance { get; internal set; }

        /// <summary>Degrees per day. Derived from an explicit period, or from orbital mechanics.</summary>
        public double Speed { get; internal set; }

        /// <summary>Starting angle in degrees; used to place binary partners 180 apart.</summary>
        public double Offset { get; internal set; }

        public bool ExplicitPeriodSet { get; internal set; }

        public StellarObject Parent { get; internal set; }

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

        public double Habitable { get; private set; }

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

                    case "link" when child.Size >= 2:
                        _links.Add(child.Token(1));
                        break;

                    case "object":
                        _objects.Add(LoadObject(child, null));
                        break;
                }
            }
        }

        private static StellarObject LoadObject(DataNode node, StellarObject parent)
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
        public void ResolveOrbits(Func<string, double> spriteMass)
        {
            double starMass = 0.0;
            int starCount = 0;
            foreach (StellarObject o in AllObjects())
            {
                if (o.IsStar)
                {
                    starCount++;
                    starMass += spriteMass(o.Sprite);
                }
            }

            // If nothing is a star, upstream treats the first object as the centre of mass.
            if (starCount == 0 && _objects.Count > 0)
            {
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
                    double mass = spriteMass(o.Parent.Sprite);
                    if (mass > 0.0)
                    {
                        period = Math.Sqrt(Math.Pow(o.Distance, 3) / mass);
                    }
                }
                else if (!o.IsStar && starMass > 0.0)
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
