using System;
using System.Collections.Generic;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// The asteroid belts a system declares, drawn as drifting low-poly rock.
    /// </summary>
    /// <remarks>
    /// The directive lists asteroid fields under Rendering, and systems have carried
    /// the data all along — a busy system declares thousands of rocks across several
    /// belts. Drawing them as separate nodes is not an option at that count, so each
    /// belt is one <see cref="MultiMeshInstance3D"/>: one mesh, one draw call,
    /// thousands of transforms.
    ///
    /// The rocks wrap rather than orbit. Upstream's asteroid field is a torus of
    /// fixed extent that scrolls past the camera, so rocks that leave one edge reappear
    /// at the other and the field is effectively endless without simulating one. Real
    /// orbits would be both more expensive and less useful, since the player never
    /// sees enough of a belt at once to tell.
    ///
    /// Counts are scaled down from what the data states: a system declaring 149 rocks
    /// of one type across half a dozen belts is describing density over a whole system,
    /// and rendering all of it around a camera that sees a few hundred units would be
    /// a wall of stone.
    ///
    /// INCOMPLETE, tracked rather than dropped: minable rocks are drawn like any other
    /// but cannot yet be shot or mined, and there is no collision with them.
    /// </remarks>
    public partial class AsteroidFieldView : Node3D
    {
        /// <summary>
        /// Half-extent of the wrapping field, in WORLD units.
        /// </summary>
        /// <remarks>
        /// Sized against what the camera can actually see, which is about 36 world
        /// units of height at the default framing (WorldSpace.Scale is 0.1, so a 36-unit
        /// hull is 3.6 world units and fills a tenth of the frame). Written first at
        /// 260 — a plausible-looking number in sim units — the field was 520 across
        /// against a 36-unit view, so the chance of any rock being in frame was
        /// essentially nil and the belts rendered as empty space.
        /// </remarks>
        private const float FieldRadius = 55f;

        /// <summary>Most rocks to draw for any one belt.</summary>
        private const int MaxPerBelt = 90;

        private readonly List<Belt> _belts = new List<Belt>();
        private readonly RandomNumberGenerator _random = new RandomNumberGenerator();

        private sealed class Belt
        {
            public MultiMesh Mesh = null!;
            public Vector3[] Positions = Array.Empty<Vector3>();
            public Vector3[] Velocities = Array.Empty<Vector3>();
            public Vector3[] Spins = Array.Empty<Vector3>();
            public Vector3[] Angles = Array.Empty<Vector3>();
            public float[] Scales = Array.Empty<float>();
        }

        /// <summary>Builds the views for one system's belts.</summary>
        public static AsteroidFieldView Create(StarSystem system, int seed = 12345)
        {
            var view = new AsteroidFieldView { Name = "Asteroids" };
            view._random.Seed = (ulong)seed;
            view._system = system;
            return view;
        }

        private StarSystem? _system;

        public override void _Ready()
        {
            if (_system == null)
                return;

            foreach (AsteroidBelt belt in _system.Asteroids)
            {
                int count = Math.Min(MaxPerBelt, Math.Max(1, belt.Count / 4));
                _belts.Add(BuildBelt(belt, count));
            }
        }

        private Belt BuildBelt(AsteroidBelt source, int count)
        {
            // Energy drives speed upstream; keep the relationship but at a rate that
            // reads as drift rather than a hail of gravel.
            float speed = (float)Math.Sqrt(Math.Max(0.0, source.Energy)) * 0.06f;

            var mesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = RockMesh(source.IsMinable),
                InstanceCount = count,
            };

            var belt = new Belt
            {
                Mesh = mesh,
                Positions = new Vector3[count],
                Velocities = new Vector3[count],
                Spins = new Vector3[count],
                Angles = new Vector3[count],
                Scales = new float[count],
            };

            for (int i = 0; i < count; i++)
            {
                belt.Positions[i] = new Vector3(
                    _random.RandfRange(-FieldRadius, FieldRadius),
                    _random.RandfRange(-5f, 5f),
                    _random.RandfRange(-FieldRadius, FieldRadius));

                float bearing = _random.RandfRange(0f, Mathf.Tau);
                belt.Velocities[i] = new Vector3(Mathf.Cos(bearing), 0f, Mathf.Sin(bearing)) * speed;

                belt.Spins[i] = new Vector3(
                    _random.RandfRange(-0.6f, 0.6f),
                    _random.RandfRange(-0.6f, 0.6f),
                    _random.RandfRange(-0.6f, 0.6f));

                belt.Angles[i] = new Vector3(
                    _random.RandfRange(0f, Mathf.Tau),
                    _random.RandfRange(0f, Mathf.Tau),
                    _random.RandfRange(0f, Mathf.Tau));

                // Minable rocks are the ones worth flying to, so they read larger.
                // In world units against a 3.6-unit ship: small rocks read as gravel,
                // minables as boulders worth flying to.
                float size = source.IsMinable
                    ? _random.RandfRange(0.9f, 1.8f)
                    : _random.RandfRange(0.25f, 0.9f);
                belt.Scales[i] = size;
            }

            AddChild(new MultiMeshInstance3D
            {
                Name = source.Name,
                Multimesh = mesh,
                MaterialOverride = RockMaterial(source.IsMinable),
                // The field wraps around the camera, so it is always in view.
                CustomAabb = new Aabb(new Vector3(-FieldRadius, -20f, -FieldRadius),
                                      new Vector3(FieldRadius * 2f, 40f, FieldRadius * 2f)),
            });

            Apply(belt);
            return belt;
        }

        /// <summary>
        /// Re-centres the field on the camera. Upstream's asteroid field is defined
        /// relative to the view rather than to the system, which is why rocks are
        /// always around the player instead of in one place a player could fly away
        /// from and never see again.
        /// </summary>
        public void Follow(Vector3 centre)
        {
            Position = centre;
        }

        public override void _Process(double delta)
        {
            float step = (float)delta;
            foreach (Belt belt in _belts)
            {
                for (int i = 0; i < belt.Positions.Length; i++)
                {
                    belt.Positions[i] += belt.Velocities[i] * step;
                    belt.Angles[i] += belt.Spins[i] * step;

                    // Wrap: a rock leaving one edge comes back at the other, which is
                    // what makes a bounded field feel endless.
                    Vector3 p = belt.Positions[i];
                    if (p.X > FieldRadius) p.X -= FieldRadius * 2f;
                    if (p.X < -FieldRadius) p.X += FieldRadius * 2f;
                    if (p.Z > FieldRadius) p.Z -= FieldRadius * 2f;
                    if (p.Z < -FieldRadius) p.Z += FieldRadius * 2f;
                    belt.Positions[i] = p;
                }

                Apply(belt);
            }
        }

        private static void Apply(Belt belt)
        {
            for (int i = 0; i < belt.Positions.Length; i++)
            {
                var basis = Basis.FromEuler(belt.Angles[i]).Scaled(Vector3.One * belt.Scales[i]);
                belt.Mesh.SetInstanceTransform(i, new Transform3D(basis, belt.Positions[i]));
            }
        }

        /// <summary>A lumpy low-poly rock: a subdivided box pushed out of true.</summary>
        private static Mesh RockMesh(bool minable)
        {
            var sphere = new SphereMesh
            {
                Radius = 0.5f,
                Height = 1.0f,
                // Deliberately coarse. These are set dressing at a distance, and the
                // directive's style is polished low-poly rather than detail.
                RadialSegments = minable ? 7 : 5,
                Rings = minable ? 4 : 3,
            };
            return sphere;
        }

        private static StandardMaterial3D RockMaterial(bool minable) => new StandardMaterial3D
        {
            // Minables carry ore and read warmer, so a player can pick them out.
            AlbedoColor = minable
                ? new Color(0.52f, 0.44f, 0.34f)
                : new Color(0.38f, 0.37f, 0.39f),
            Metallic = minable ? 0.35f : 0.1f,
            Roughness = 0.85f,
        };
    }
}
