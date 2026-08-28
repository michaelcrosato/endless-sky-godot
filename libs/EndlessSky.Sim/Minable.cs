using System;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// One belt of asteroids in a system: what they are, how many, and how fast they
    /// move. Port of upstream's <c>System::Asteroid</c>.
    /// </summary>
    /// <remarks>
    /// The third number is energy, not speed as such: upstream derives each rock's
    /// velocity from it, so a high-energy belt is a fast, dangerous one. Two entries
    /// with the same name and different energies are two distinct belts, which is how
    /// content layers slow debris and fast fragments in the same system.
    /// </remarks>
    public readonly struct AsteroidBelt
    {
        public AsteroidBelt(string name, int count, double energy, bool isMinable)
        {
            Name = name;
            Count = Math.Max(0, count);
            Energy = energy;
            IsMinable = isMinable;
        }

        /// <summary>Sprite name for plain rock, or the minable type's name.</summary>
        public string Name { get; }

        public int Count { get; }

        /// <summary>Drives how fast the rocks drift; higher is faster.</summary>
        public double Energy { get; }

        /// <summary>Whether these can be mined for cargo rather than just collided with.</summary>
        public bool IsMinable { get; }

        public override string ToString() =>
            $"{Count} x {Name}{(IsMinable ? " (minable)" : "")} at {Energy:0.##}";
    }

    /// <summary>
    /// A mineable asteroid type: what it is made of and how hard it is to crack.
    /// Port of upstream <c>Minable</c>.
    /// </summary>
    /// <remarks>
    /// Mining is the reason a belt is more than scenery. A minable rock has real hull,
    /// takes real damage, and drops a named commodity when it breaks — which is why
    /// the payload is an outfit name and a count rather than an abstract reward.
    ///
    /// INCOMPLETE, tracked rather than dropped: the "toughness" that decides how much
    /// of the payload survives, explosion effects, and spawning rocks into a system
    /// so they can actually be shot.
    /// </remarks>
    public class Minable
    {
        public Minable(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }

        public string Name { get; }

        public double Hull { get; private set; }

        /// <summary>Extra hull varying per rock, so a belt is not uniform.</summary>
        public double RandomHull { get; private set; }

        /// <summary>The commodity or outfit this drops when broken.</summary>
        public string? PayloadName { get; private set; }

        public int PayloadCount { get; private set; }

        public void Load(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                switch (child.Token(0))
                {
                    case "hull" when child.Size >= 2:
                        Hull = child.Value(1);
                        break;

                    case "random hull" when child.Size >= 2:
                        RandomHull = child.Value(1);
                        break;

                    case "payload" when child.Size >= 2:
                        PayloadName = child.Token(1);
                        PayloadCount = child.Size >= 3 && child.IsNumber(2)
                            ? (int)child.Value(2)
                            : 1;
                        break;
                }
            }
        }

        /// <summary>Total hull of one rock, given a roll in [0, 1).</summary>
        public double HullFor(double roll) => Hull + RandomHull * Math.Clamp(roll, 0.0, 1.0);

        public override string ToString() =>
            $"{Name} ({Hull:0} hull, drops {PayloadCount} {PayloadName ?? "nothing"})";
    }
}
