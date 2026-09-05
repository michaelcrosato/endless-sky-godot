using System;
using System.Collections.Generic;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// The in-flight system dial: every body in this system drawn to scale, colour-coded
    /// by whether a ship can land on it, with the player's own position among them.
    /// </summary>
    /// <remarks>
    /// This is the part that makes the planet labels worth having. A system is tens of
    /// thousands of units across and the chase camera sits a few hundred units behind
    /// the ship, so at any given moment essentially NOTHING is on screen: labelling the
    /// worlds answers "which of these can I land on" only for a player who has already
    /// found one. The dial answers "where are they", which is the question that comes
    /// first and had no answer anywhere in the game.
    ///
    /// Two deliberate departures from upstream's <c>Radar</c>, both because our flight
    /// view is a close 3D chase camera where upstream's is a wide 2D one:
    ///
    /// - It is centred on the SYSTEM, not the ship. Ship-centred is the right choice
    ///   when the main view already shows the neighbourhood and the radar only has to
    ///   extend it; here nothing else shows the system at all, so the dial has to be
    ///   the map. The star sits in the middle and the player moves around it.
    /// - The scale FITS the system rather than being a constant. Systems in the
    ///   generated galaxy vary from a few thousand units across to tens of thousands,
    ///   and one fixed range either crops the outer worlds out of the tight ones or
    ///   collapses the wide ones into a single dot in the middle.
    ///
    /// Not a galaxy map — that is <see cref="MapScreen"/>, and it is modal. This is
    /// always visible and always about the system underfoot.
    ///
    /// INCOMPLETE, tracked rather than dropped: upstream also plots ships, projectiles
    /// and asteroids here, coloured by government and hostility. Only stellar objects
    /// are drawn, because only they answer the question this was added for.
    /// </remarks>
    public partial class SystemRadar : Control
    {
        /// <summary>Radius of the dial in screen pixels.</summary>
        private const float Dial = 88f;

        /// <summary>Headroom around the outermost body, so nothing sits on the rim.</summary>
        private const double FitMargin = 1.12;

        /// <summary>
        /// Smallest span the dial will scale to. Without a floor, a system whose worlds
        /// all sit close in magnifies until the ship's own drift swings wildly across
        /// the dial, which reads as a fault rather than as a scale.
        /// </summary>
        private const double MinimumSpan = 3000.0;

        private Ship? _ship;
        private IReadOnlyList<StellarObject> _objects = Array.Empty<StellarObject>();
        private double _span = MinimumSpan;

        /// <remarks>
        /// Sized in the constructor rather than in <c>_Ready</c>: the dial has a fixed
        /// size that owes nothing to the tree, and a test that has to fake the node
        /// lifecycle to check it is a test that can crash the engine on shutdown.
        /// </remarks>
        public SystemRadar()
        {
            CustomMinimumSize = new Vector2(Dial * 2f, Dial * 2f);
            Size = CustomMinimumSize;
        }

        /// <summary>Point the dial at a ship and the system it is flying in.</summary>
        public void Track(Ship? ship, IReadOnlyList<StellarObject> objects)
        {
            _ship = ship;
            _objects = objects;

            // Fit the SYSTEM, not the ship. Including the ship's own distance would
            // rescale the dial continuously as the player flies out, so the worlds
            // would drift about and no two glances would be comparable. Fitting the
            // bodies alone keeps the layout fixed and lets the ship run to the rim,
            // where it still shows a bearing; the HUD carries the distance in numbers.
            double furthest = MinimumSpan;
            foreach (StellarObject obj in objects)
            {
                furthest = Math.Max(furthest, obj.Position.Length);
            }

            _span = furthest * FitMargin;
            if (IsInsideTree())
            {
                QueueRedraw();
            }
        }

        public override void _Draw()
        {
            var centre = new Vector2(Dial, Dial);

            DrawCircle(centre, Dial, new Color(0.04f, 0.06f, 0.09f, 0.6f));
            DrawArc(centre, Dial, 0f, Mathf.Tau, 64, new Color(0.35f, 0.55f, 0.75f, 0.5f), 1.2f);
            DrawArc(centre, Dial * 0.5f, 0f, Mathf.Tau, 48, new Color(0.35f, 0.55f, 0.75f, 0.18f), 1f);

            foreach (StellarObject obj in _objects)
            {
                Vector2 blip = centre + Plot(obj.Position);
                bool selected = _ship != null && ReferenceEquals(obj, _ship.TargetStellar);

                if (obj.Planet == null)
                {
                    // Scenery: a star, or a body the dataset names no world for. Drawn,
                    // because a dial that omits the one thing you can fly into is worse
                    // than none, but never as somewhere to aim for.
                    DrawCircle(blip, 3.5f, Scenery);
                    continue;
                }

                Color colour = selected ? Selected : obj.Planet.HasServices ? Port : Rock;
                DrawCircle(blip, selected ? 5f : 3.8f, colour);
                if (selected)
                {
                    DrawArc(blip, 9f, 0f, Mathf.Tau, 24, colour, 1.6f);
                }
            }

            if (_ship == null)
            {
                return;
            }

            // The ship last, on top, pointing where it is pointing. Its heading comes
            // from the same Angle the simulation steers by and goes through the same
            // axis mapping as every blip, so "left on the dial" is left off the nose.
            Vector2 here = centre + Plot(_ship.Position);
            Point facing = _ship.Facing.Unit();
            var nose = new Vector2((float)facing.X, (float)facing.Y);
            if (nose.LengthSquared() > 0f)
            {
                nose = nose.Normalized();
            }

            Vector2 flank = new Vector2(-nose.Y, nose.X) * 3.4f;
            DrawColoredPolygon(
                new[] { here + nose * 6f, here - nose * 3f + flank, here - nose * 3f - flank },
                Bright);
        }

        /// <summary>
        /// System coordinates to dial coordinates. The simulation plane maps to the
        /// world's XZ and to the dial's XY, so sim +Y is DOWN on the dial — the same
        /// handedness <see cref="WorldSpace"/> uses, kept identical so a blip left of
        /// the nose is a world left of the nose.
        /// </summary>
        private Vector2 Plot(Point position)
        {
            float scale = (float)(Dial / _span);
            var plotted = new Vector2((float)position.X * scale, (float)position.Y * scale);

            // Anything beyond the rim is pinned to it rather than dropped: a world (or
            // a ship) further out than the fit still shows a bearing to steer by.
            float limit = Dial - 4f;
            return plotted.Length() > limit ? plotted.Normalized() * limit : plotted;
        }

        /// <summary>A world with a port: somewhere worth crossing a system for.</summary>
        private static readonly Color Port = new Color(0.62f, 0.88f, 0.70f, 0.95f);

        /// <summary>Landable, but nothing there.</summary>
        private static readonly Color Rock = new Color(0.58f, 0.64f, 0.72f, 0.78f);

        /// <summary>Stars and unnamed bodies: obstacles, not destinations.</summary>
        private static readonly Color Scenery = new Color(0.95f, 0.78f, 0.45f, 0.6f);

        /// <summary>The current landing target.</summary>
        private static readonly Color Selected = new Color(1.0f, 0.82f, 0.42f, 1.0f);

        private static readonly Color Bright = new Color(0.92f, 0.95f, 0.98f, 1.0f);
    }
}
