using System;
using System.Collections.Generic;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>A hardpoint or engine mount, in the sprite-space offsets upstream uses.</summary>
    public readonly struct Hardpoint
    {
        public readonly Point Offset;
        // Null for a mount with no pre-assigned outfit (bare engine points).
        public readonly string? OutfitName;

        public Hardpoint(Point offset, string? outfitName)
        {
            Offset = offset;
            OutfitName = outfitName;
        }
    }

    /// <summary>
    /// A ship model as defined in upstream data: hull attributes, default outfits, and
    /// the mount points used for engine flares and weapon hardpoints.
    ///
    /// Upstream ship coordinates are in sprite pixels with +Y down; the presentation
    /// layer converts. Values are kept exactly as authored so nothing is lost.
    /// </summary>
    public class ShipDefinition
    {
        private readonly List<string> _outfitNames = new List<string>();
        private readonly List<Hardpoint> _engines = new List<Hardpoint>();
        private readonly List<Hardpoint> _guns = new List<Hardpoint>();
        private readonly List<Hardpoint> _turrets = new List<Hardpoint>();
        private readonly List<string> _descriptionLines = new List<string>();

        public ShipDefinition(string name, string? variantName = null)
        {
            Name = name;
            VariantName = variantName;
        }

        public string Name { get; }

        /// <summary>Set when this is a named variant, e.g. <c>ship "Shuttle" "Shuttle (Armed)"</c>.</summary>
        public string? VariantName { get; }

        public string DisplayName => VariantName ?? Name;

        public string Sprite { get; private set; } = string.Empty;

        public string Category { get; private set; } = string.Empty;

        public Attributes Attributes { get; } = new Attributes();

        /// <summary>
        /// Attributes from an <c>add attributes</c> block. Upstream keeps these separate
        /// because they layer on top of whatever the base model provides, rather than
        /// replacing it.
        /// </summary>
        public Attributes AddedAttributes { get; } = new Attributes();

        public IReadOnlyList<string> OutfitNames => _outfitNames;

        public IReadOnlyList<Hardpoint> Engines => _engines;

        public IReadOnlyList<Hardpoint> Guns => _guns;

        public IReadOnlyList<Hardpoint> Turrets => _turrets;

        public string Description => string.Join("\n", _descriptionLines);

        internal bool Resolved { get; set; }

        public void Load(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                string key = child.Token(0);

                // "add attributes" layers extra attributes onto the inherited ones.
                if (key == "add" && child.Token(1) == "attributes")
                {
                    AddedAttributes.Load(child);
                    continue;
                }

                switch (key)
                {
                    case "sprite":
                        Sprite = child.Token(1);
                        break;

                    case "attributes":
                        LoadAttributes(child);
                        break;

                    case "outfits":
                        foreach (DataNode outfit in child.Children)
                        {
                            // "Outfit Name" [count]
                            int count = outfit.Size >= 2 && outfit.IsNumber(1) ? (int)outfit.Value(1) : 1;
                            for (int i = 0; i < count; i++)
                            {
                                _outfitNames.Add(outfit.Token(0));
                            }
                        }

                        break;

                    case "engine":
                    case "reverse engine":
                    case "steering engine":
                        _engines.Add(ReadHardpoint(child));
                        break;

                    case "gun":
                        _guns.Add(ReadHardpoint(child));
                        break;

                    case "turret":
                        _turrets.Add(ReadHardpoint(child));
                        break;

                    case "description":
                        if (child.Size >= 2)
                        {
                            _descriptionLines.Add(child.Token(1));
                        }

                        break;
                }
            }
        }

        private void LoadAttributes(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                string key = child.Token(0);
                if (key == "category" && child.Size >= 2)
                {
                    Category = child.Token(1);
                }
                else if (child.Size >= 2 && child.IsNumber(1))
                {
                    Attributes.Add(key, child.Value(1));
                }
            }
        }

        private static Hardpoint ReadHardpoint(DataNode node)
        {
            // "engine -9.5 38" or "turret 0 -18 \"Anti-Missile Turret\""
            double x = node.Size >= 2 ? node.Value(1) : 0.0;
            double y = node.Size >= 3 ? node.Value(2) : 0.0;
            string? outfit = null;
            for (int i = 3; i < node.Size; i++)
            {
                if (!node.IsNumber(i))
                {
                    outfit = node.Token(i);
                    break;
                }
            }

            return new Hardpoint(new Point(x, y), outfit);
        }

        /// <summary>
        /// Copies from the base model every field this variant left empty, then folds in
        /// any <c>add attributes</c>. Port of the base-model half of upstream's
        /// <c>Ship::FinishLoading</c>.
        ///
        /// The test is emptiness of the loaded data, not presence of the node: real
        /// content contains variants with a bare <c>attributes</c> header and nothing
        /// under it (see "Modified Dromedary Wreck"), which upstream still treats as
        /// "inherit the base attributes".
        /// </summary>
        internal void InheritFrom(ShipDefinition baseShip)
        {
            if (baseShip == null || ReferenceEquals(baseShip, this))
            {
                return;
            }

            if (Attributes.Values.Count == 0)
            {
                Attributes.Add(baseShip.Attributes);
                Category = baseShip.Category;
            }

            if (string.IsNullOrEmpty(Sprite))
            {
                Sprite = baseShip.Sprite;
            }

            if (_outfitNames.Count == 0)
            {
                _outfitNames.AddRange(baseShip._outfitNames);
            }

            if (_engines.Count == 0)
            {
                _engines.AddRange(baseShip._engines);
            }

            if (_guns.Count == 0 && _turrets.Count == 0)
            {
                _guns.AddRange(baseShip._guns);
                _turrets.AddRange(baseShip._turrets);
            }

            if (_descriptionLines.Count == 0)
            {
                _descriptionLines.AddRange(baseShip._descriptionLines);
            }
        }

        /// <summary>Folds <c>add attributes</c> into the effective attribute set.</summary>
        internal void ApplyAddedAttributes()
        {
            Attributes.Add(AddedAttributes);
        }

        public override string ToString() => DisplayName;
    }
}
