using System;
using System.Collections.Generic;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// A bag of named numeric attributes, the currency of Endless Sky's ship model.
    ///
    /// Upstream treats ship hulls and outfits uniformly: a ship's effective attributes
    /// are its hull attributes plus the sum of every installed outfit's attributes.
    /// Keeping that structure (rather than hard-coding fields like "thrust") is what
    /// lets unmodified upstream content drive the simulation.
    /// </summary>
    public class Attributes
    {
        private readonly Dictionary<string, double> _values =
            new Dictionary<string, double>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, double> Values => _values;

        public double Get(string key)
        {
            return _values.TryGetValue(key, out double value) ? value : 0.0;
        }

        public bool Has(string key) => _values.ContainsKey(key);

        public void Set(string key, double value) => _values[key] = value;

        public void Add(string key, double value)
        {
            _values.TryGetValue(key, out double existing);
            double sum = existing + value;
            if (sum == 0.0)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = sum;
            }
        }

        /// <summary>Adds every attribute of <paramref name="other"/>, scaled by <paramref name="count"/>.</summary>
        public void Add(Attributes other, int count = 1)
        {
            foreach (KeyValuePair<string, double> pair in other._values)
            {
                Add(pair.Key, pair.Value * count);
            }
        }

        /// <summary>
        /// Reads attribute lines. A node like <c>"thrust" 13.545</c> becomes thrust=13.545.
        /// Non-numeric child nodes (category, weapon blocks, flare sprites) are not
        /// attributes and are left for the caller to interpret.
        /// </summary>
        public void Load(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                if (child.Size >= 2 && child.IsNumber(1))
                {
                    Add(child.Token(0), child.Value(1));
                }
            }
        }
    }

    /// <summary>An outfit definition: a named set of attributes that can be installed on a ship.</summary>
    public class Outfit
    {
        public Outfit(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public string Category { get; private set; } = string.Empty;

        public Attributes Attributes { get; } = new Attributes();

        /// <summary>
        /// The outfit's <c>weapon</c> block, if it has one. Upstream models weapons as
        /// ordinary outfits carrying an extra block, so guns, turrets, missiles and
        /// even a ship hull's death explosion all share this one shape.
        /// </summary>
        public Weapon Weapon { get; } = new Weapon();

        /// <summary>True when this outfit can actually be fired.</summary>
        public bool IsWeapon => Weapon.IsWeapon;

        public double Mass => Attributes.Get("mass");

        public void Load(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                string key = child.Token(0);
                if (key == "category" && child.Size >= 2)
                {
                    Category = child.Token(1);
                }
                else if (key == "weapon")
                {
                    // Nested block, not a scalar attribute: it carries its own
                    // damage, reload and projectile values.
                    Weapon.Load(child);
                }
                else if (child.Size >= 2 && child.IsNumber(1))
                {
                    Attributes.Add(key, child.Value(1));
                }
            }
        }

        public override string ToString() => Name;
    }
}
