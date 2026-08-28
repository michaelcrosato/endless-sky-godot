using System;
using System.Collections.Generic;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// Builds a low-poly hull for any ship in the dataset from its
    /// <see cref="ShipAppearance"/>. Milestone 8's art set, generated rather than
    /// authored.
    /// </summary>
    /// <remarks>
    /// The dataset has 902 ships. Hand-modelling them is not on the table, and a
    /// single prototype hull reused for all of them is what M8 exists to replace.
    /// So the geometry is lofted: a series of cross-sections along the hull, each
    /// scaled by a profile curve chosen per hull class, joined into a shell.
    ///
    /// Everything that varies between ships comes from data the simulation already
    /// has — length and beam from mass, station and segment counts from the triangle
    /// budget, nozzles and barrels from real hardpoint positions, lit ports from
    /// bunks. That is what makes 902 hulls read as one art set instead of 902
    /// unrelated shapes.
    ///
    /// Flat shading is deliberate: each triangle gets its own face normal, which is
    /// what produces the faceted low-poly read rather than a smooth blob.
    ///
    /// The mesh faces −Z, matching <see cref="WorldSpace.YawFromFacing"/>.
    /// </remarks>
    public static class ShipMeshBuilder
    {
        /// <summary>A cross-section: how wide and how tall the hull is at this point.</summary>
        private readonly record struct Station(float Along, float HalfWidth, float HalfHeight, float Rise);

        /// <summary>
        /// Builds the hull shell plus its fittings under a single parent node.
        /// </summary>
        /// <param name="appearance">Visual description derived from ship data.</param>
        /// <param name="damageState">0 pristine … 3 wreck; drives material wear.</param>
        public static Node3D Build(ShipAppearance appearance, int damageState = 0)
        {
            ArgumentNullException.ThrowIfNull(appearance);

            var root = new Node3D { Name = "Hull" };

            float length = WorldSpace.Length(appearance.Length);
            float beam = WorldSpace.Length(appearance.Beam);

            root.AddChild(new MeshInstance3D
            {
                Name = "Body",
                Mesh = BuildShell(appearance, length, beam, damageState),
            });

            AddFittings(root, appearance, length, beam, damageState);
            AddWindows(root, appearance, length, beam, damageState);

            return root;
        }

        // --- Shell ----------------------------------------------------------------

        private static ArrayMesh BuildShell(ShipAppearance appearance, float length, float beam,
                                            int damageState)
        {
            // Spend the triangle budget on stations and radial segments. Two triangles
            // per quad, so budget ≈ 2 × (stations−1) × segments.
            int budget = appearance.TriangleBudget;
            int segments = Math.Clamp(4 + budget / 260, 5, 12);
            int stations = Math.Clamp(3 + budget / 500, 4, 10);

            IReadOnlyList<Station> profile = Centre(Profile(appearance.Class, stations));

            // Two surfaces, split by whether a face points up or down. A single
            // uniform material reads as a featureless white blob at gameplay
            // distance; the dorsal/ventral value break is what holds the silhouette.
            var dorsal = new SurfaceTool();
            dorsal.Begin(Mesh.PrimitiveType.Triangles);
            var ventral = new SurfaceTool();
            ventral.Begin(Mesh.PrimitiveType.Triangles);

            var rings = new List<Vector3[]>(profile.Count);
            foreach (Station station in profile)
                rings.Add(Ring(station, segments, length, beam));

            for (int i = 0; i < rings.Count - 1; i++)
            {
                Vector3[] a = rings[i];
                Vector3[] b = rings[i + 1];

                for (int s = 0; s < segments; s++)
                {
                    int n = (s + 1) % segments;
                    Facet(dorsal, ventral, a[s], b[s], b[n]);
                    Facet(dorsal, ventral, a[s], b[n], a[n]);
                }
            }

            // Cap the nose and tail so the shell is closed.
            Cap(dorsal, ventral, rings[0], profile[0].Along * length, segments, facingForward: true);
            Cap(dorsal, ventral, rings[^1], profile[^1].Along * length, segments, facingForward: false);

            ArrayMesh mesh = dorsal.Commit();
            mesh = ventral.Commit(mesh);
            mesh.SurfaceSetMaterial(0, PlateMaterial(appearance, damageState, dark: false));
            mesh.SurfaceSetMaterial(1, PlateMaterial(appearance, damageState, dark: true));
            return mesh;
        }

        /// <summary>
        /// Shifts a profile so its mean rise is zero, putting the roll axis on the
        /// hull centreline. The view layer layers banking and nose-dip on top of yaw,
        /// and an off-centre hull would swing around a point outside itself.
        /// </summary>
        private static IReadOnlyList<Station> Centre(IReadOnlyList<Station> profile)
        {
            float mean = 0f;
            foreach (Station station in profile)
                mean += station.Rise;

            mean /= profile.Count;

            var centred = new List<Station>(profile.Count);
            foreach (Station station in profile)
                centred.Add(station with { Rise = station.Rise - mean });

            return centred;
        }

        /// <summary>
        /// Hull profiles per class. These are the silhouette rules: a fighter tapers
        /// to a point, a freighter is a slab, a capital ship is long and stepped.
        /// </summary>
        private static IReadOnlyList<Station> Profile(HullClass hull, int stations)
        {
            // Control points as (position along hull 0..1, width, height, vertical rise),
            // interpolated to the requested station count.
            (float t, float w, float h, float rise)[] control = hull switch
            {
                HullClass.Drone => new[]
                {
                    (0f, 0.15f, 0.15f, 0f), (0.4f, 0.9f, 0.7f, 0.02f),
                    (0.75f, 0.8f, 0.6f, 0f), (1f, 0.4f, 0.35f, -0.02f),
                },
                HullClass.Fighter => new[]
                {
                    (0f, 0.10f, 0.12f, 0f), (0.30f, 0.55f, 0.45f, 0.04f),
                    (0.62f, 1.0f, 0.55f, 0.02f), (0.85f, 0.7f, 0.45f, 0f),
                    (1f, 0.35f, 0.3f, -0.02f),
                },
                HullClass.Light => new[]
                {
                    (0f, 0.16f, 0.18f, 0f), (0.28f, 0.62f, 0.5f, 0.05f),
                    (0.58f, 1.0f, 0.62f, 0.04f), (0.84f, 0.78f, 0.5f, 0f),
                    (1f, 0.42f, 0.34f, -0.02f),
                },
                HullClass.Medium => new[]
                {
                    (0f, 0.24f, 0.22f, 0f), (0.24f, 0.66f, 0.52f, 0.05f),
                    (0.55f, 1.0f, 0.72f, 0.06f), (0.82f, 0.88f, 0.62f, 0.02f),
                    (1f, 0.55f, 0.42f, -0.02f),
                },
                HullClass.Heavy => new[]
                {
                    (0f, 0.30f, 0.26f, 0f), (0.20f, 0.72f, 0.56f, 0.04f),
                    (0.48f, 1.0f, 0.82f, 0.06f), (0.78f, 0.95f, 0.74f, 0.03f),
                    (1f, 0.66f, 0.5f, -0.01f),
                },
                _ => new[]                                    // Capital: long and stepped
                {
                    (0f, 0.34f, 0.28f, 0f), (0.16f, 0.7f, 0.5f, 0.03f),
                    (0.38f, 0.92f, 0.78f, 0.05f), (0.62f, 1.0f, 0.88f, 0.05f),
                    (0.85f, 0.94f, 0.8f, 0.02f), (1f, 0.72f, 0.56f, 0f),
                },
            };

            var result = new List<Station>(stations);
            for (int i = 0; i < stations; i++)
            {
                float t = stations == 1 ? 0f : (float)i / (stations - 1);
                (float w, float h, float rise) = Sample(control, t);

                // Along runs nose (−Z, negative) to tail (+Z, positive).
                result.Add(new Station(t - 0.5f, w, h, rise));
            }

            return result;
        }

        /// <summary>Linear interpolation through the control points.</summary>
        private static (float w, float h, float rise) Sample(
            (float t, float w, float h, float rise)[] control, float t)
        {
            for (int i = 0; i < control.Length - 1; i++)
            {
                if (t > control[i + 1].t)
                    continue;

                float span = control[i + 1].t - control[i].t;
                float k = span <= 0f ? 0f : (t - control[i].t) / span;

                return (Mathf.Lerp(control[i].w, control[i + 1].w, k),
                        Mathf.Lerp(control[i].h, control[i + 1].h, k),
                        Mathf.Lerp(control[i].rise, control[i + 1].rise, k));
            }

            var last = control[^1];
            return (last.w, last.h, last.rise);
        }

        private static Vector3[] Ring(Station station, int segments, float length, float beam)
        {
            var ring = new Vector3[segments];
            float z = station.Along * length;
            float halfWidth = station.HalfWidth * beam * 0.5f;
            float halfHeight = station.HalfHeight * beam * 0.35f;
            float rise = station.Rise * length;

            for (int s = 0; s < segments; s++)
            {
                float angle = Mathf.Tau * s / segments;
                ring[s] = new Vector3(
                    Mathf.Cos(angle) * halfWidth,
                    Mathf.Sin(angle) * halfHeight + rise,
                    z);
            }

            return ring;
        }

        private static void Cap(SurfaceTool dorsal, SurfaceTool ventral, Vector3[] ring,
                                float z, int segments, bool facingForward)
        {
            var centre = new Vector3(0f, 0f, z);
            for (int s = 0; s < segments; s++)
            {
                int n = (s + 1) % segments;
                if (facingForward)
                    Facet(dorsal, ventral, centre, ring[n], ring[s]);
                else
                    Facet(dorsal, ventral, centre, ring[s], ring[n]);
            }
        }

        /// <summary>
        /// The outward normal of a front-facing triangle.
        /// </summary>
        /// <remarks>
        /// Godot treats CLOCKWISE winding as front-facing, so the outward normal of a
        /// front face is <c>(c-a) x (b-a)</c> — the negation of the counter-clockwise
        /// convention. Getting this backwards does not make the hull vanish, because
        /// culling keys off winding rather than off this attribute: the geometry still
        /// draws, but every outward face carries an inward-pointing normal, N.L goes
        /// negative across the whole lit side, and the hull renders black with only
        /// the view-based rim term surviving. Flat black and blown white are the same
        /// bug seen with rim off and rim on.
        /// </remarks>
        private static Vector3 FaceNormal(Vector3 a, Vector3 b, Vector3 c) =>
            (c - a).Cross(b - a);

        /// <summary>Routes a face to the lit or dark surface by which way it points.</summary>
        private static void Facet(SurfaceTool dorsal, SurfaceTool ventral,
                                  Vector3 a, Vector3 b, Vector3 c)
        {
            Triangle(FaceNormal(a, b, c).Y >= 0f ? dorsal : ventral, a, b, c);
        }

        /// <summary>One flat-shaded triangle: its own face normal, no smoothing.</summary>
        private static void Triangle(SurfaceTool surface, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 normal = FaceNormal(a, b, c);
            if (normal.LengthSquared() > 0f)
                normal = normal.Normalized();

            foreach (Vector3 v in stackalloc[] { a, b, c })
            {
                surface.SetNormal(normal);
                surface.AddVertex(v);
            }
        }

        // --- Fittings -------------------------------------------------------------

        private static void AddFittings(Node3D root, ShipAppearance appearance,
                                        float length, float beam, int damageState)
        {
            float unit = Math.Max(length, beam);

            foreach (MountPlacement mount in appearance.Mounts)
            {
                // Mount offsets are in hull-local sim units and already at true scale.
                // Sim +Y is toward the stern, which is Godot +Z.
                var position = new Vector3(
                    WorldSpace.Length(mount.Offset.X),
                    0f,
                    WorldSpace.Length(mount.Offset.Y));

                switch (mount.Kind)
                {
                    case MountKind.Engine:
                        root.AddChild(new MeshInstance3D
                        {
                            Name = "Nozzle",
                            Position = position,
                            Mesh = new CylinderMesh
                            {
                                TopRadius = unit * 0.05f,
                                BottomRadius = unit * 0.07f,
                                Height = unit * 0.12f,
                                RadialSegments = 6,
                                Rings = 0,
                            },
                            RotationDegrees = new Vector3(90f, 0f, 0f),
                            MaterialOverride = EmissiveMaterial(
                                new Color(0.35f, 0.62f, 1f), appearance.EngineGlow, damageState),
                        });
                        break;

                    case MountKind.Gun:
                        root.AddChild(new MeshInstance3D
                        {
                            Name = "Barrel",
                            Position = position,
                            Mesh = new BoxMesh
                            {
                                Size = new Vector3(unit * 0.035f, unit * 0.035f, unit * 0.16f),
                            },
                            MaterialOverride = PlateMaterial(appearance, damageState, dark: true),
                        });
                        break;

                    case MountKind.Turret:
                        root.AddChild(new MeshInstance3D
                        {
                            Name = "Turret",
                            Position = position + new Vector3(0f, beam * 0.12f, 0f),
                            Mesh = new SphereMesh
                            {
                                Radius = unit * 0.055f,
                                Height = unit * 0.08f,
                                RadialSegments = 6,
                                Rings = 3,
                            },
                            MaterialOverride = PlateMaterial(appearance, damageState, dark: true),
                        });
                        break;
                }
            }
        }

        private static void AddWindows(Node3D root, ShipAppearance appearance,
                                       float length, float beam, int damageState)
        {
            int count = appearance.WindowCount;
            if (count <= 0)
                return;

            // A wreck's lights are out. This is the visual tell that a hull is a
            // derelict rather than a ship still fighting.
            float brightness = damageState >= 3 ? 0f : 1f - damageState * 0.3f;
            if (brightness <= 0f)
                return;

            var material = EmissiveMaterial(new Color(1f, 0.86f, 0.6f), brightness, damageState);
            float size = beam * 0.05f;

            // Spaced along the flank, both sides, avoiding the extreme nose and tail.
            int perSide = Math.Max(1, count / 2);
            for (int i = 0; i < perSide; i++)
            {
                float t = perSide == 1 ? 0.5f : 0.25f + 0.5f * i / (perSide - 1);
                float z = (t - 0.5f) * length;

                foreach (float side in stackalloc[] { -1f, 1f })
                {
                    root.AddChild(new MeshInstance3D
                    {
                        Name = "Port",
                        Position = new Vector3(side * beam * 0.24f, beam * 0.04f, z),
                        Mesh = new BoxMesh { Size = new Vector3(size * 0.4f, size, size) },
                        MaterialOverride = material,
                    });
                }
            }
        }

        // --- Materials ------------------------------------------------------------

        private static StandardMaterial3D HullMaterial(ShipAppearance appearance, int damageState) =>
            PlateMaterial(appearance, damageState, dark: false);

        /// <summary>
        /// Hull plate. Desaturated by design so emissives, weapon fire and shield
        /// impacts are the only saturated colour on screen; damage darkens and
        /// roughens it.
        /// </summary>
        private static StandardMaterial3D PlateMaterial(ShipAppearance appearance, int damageState,
                                                        bool dark)
        {
            Color baseColour = FactionPlate(appearance.Faction);

            // The lit dorsal keeps the plate's full value; the ventral drops well
            // below it. The contrast BETWEEN them is the silhouette. Darkening both
            // (as a first attempt did) just makes the ship invisible against space,
            // and brightening both makes it a featureless white blob.
            baseColour = dark ? baseColour.Darkened(0.62f) : baseColour;

            float wear = Math.Clamp(damageState, 0, 3) / 3f;

            return new StandardMaterial3D
            {
                AlbedoColor = baseColour.Darkened(wear * 0.45f),
                // Low metallic and a fairly rough finish: at 0.5 metallic the specular
                // lobe blew every facet facing the key to flat white and took the
                // faceting with it.
                Metallic = 0.22f - wear * 0.12f,
                Roughness = 0.58f + wear * 0.3f,
                // Rim light on the lit half only: it is what separates a hull from
                // black space at gameplay distance. On the ventral it would erase the
                // very contrast the two surfaces exist to create.
                RimEnabled = !dark,
                Rim = 0.5f,
                RimTint = 0.3f,
            };
        }

        /// <summary>
        /// One plate hue per faction. Null reads as unaffiliated neutral, which is
        /// what the whole fleet uses until ship-to-government association lands.
        /// </summary>
        /// <summary>
        /// Plate colour for a government.
        /// </summary>
        /// <remarks>
        /// Matching is by SUBSTRING, because governments in the data are families
        /// rather than flat names: the index resolves hulls to "Hai Merchant (Human)",
        /// "Avgi (Twilight Guard)" and "Coalition" as readily as to "Hai". Exact-match
        /// switching dropped 58 real governments down to the handful spelled exactly
        /// right and painted everything else neutral grey.
        ///
        /// Anything still unmatched takes a stable colour derived from the name, so
        /// unfamiliar factions stay visually distinct from each other and from the
        /// human powers without anyone having to enumerate them. The hash is over the
        /// name, so a faction keeps its colour between runs.
        /// </remarks>
        private static Color FactionPlate(string? faction)
        {
            if (string.IsNullOrEmpty(faction))
                return new Color(0.47f, 0.48f, 0.51f);

            foreach ((string family, Color plate) in FactionPlates)
            {
                if (faction!.Contains(family, StringComparison.OrdinalIgnoreCase))
                    return plate;
            }

            // Deterministic fallback: hue from the name, held to the same restrained
            // value and saturation as the curated plates so nothing screams.
            int hash = 0;
            foreach (char c in faction!)
                hash = (hash * 31 + c) & 0x7fffffff;

            return Color.FromHsv((hash % 360) / 360f, 0.18f, 0.48f);
        }

        /// <summary>
        /// Curated plates, most specific family first: "Hai Merchant (Human)" has to
        /// meet "Hai Merchant" before it meets "Merchant", or human-built hulls in Hai
        /// service would be painted as human merchants.
        /// </summary>
        private static readonly (string Family, Color Plate)[] FactionPlates =
        {
            ("Hai Merchant", new Color(0.44f, 0.52f, 0.52f)),
            ("Hai", new Color(0.40f, 0.50f, 0.47f)),
            ("Republic", new Color(0.50f, 0.53f, 0.58f)),
            ("Free Worlds", new Color(0.46f, 0.44f, 0.40f)),
            ("Syndicate", new Color(0.52f, 0.50f, 0.46f)),
            ("Merchant", new Color(0.50f, 0.52f, 0.56f)),
            ("Pirate", new Color(0.30f, 0.26f, 0.28f)),
            ("Korath", new Color(0.44f, 0.39f, 0.28f)),
            ("Heliarch", new Color(0.52f, 0.47f, 0.32f)),
            ("Coalition", new Color(0.48f, 0.44f, 0.34f)),
            ("Quarg", new Color(0.33f, 0.40f, 0.49f)),
            ("Remnant", new Color(0.34f, 0.44f, 0.42f)),
            ("Wanderer", new Color(0.38f, 0.46f, 0.34f)),
            ("Pug", new Color(0.36f, 0.30f, 0.46f)),
            ("Avgi", new Color(0.34f, 0.36f, 0.48f)),
            ("Drak", new Color(0.26f, 0.26f, 0.30f)),
        };

        private static StandardMaterial3D EmissiveMaterial(Color colour, double strength,
                                                           int damageState)
        {
            // Emissives fail as a hull is destroyed, so a wreck goes dark.
            float wear = Math.Clamp(damageState, 0, 3) / 3f;
            float energy = (float)Math.Clamp(strength, 0.0, 3.0) * (1f - wear);

            return new StandardMaterial3D
            {
                AlbedoColor = colour,
                EmissionEnabled = true,
                Emission = colour,
                EmissionEnergyMultiplier = Math.Max(0.05f, energy),
                Metallic = 0f,
                Roughness = 0.6f,
            };
        }
    }
}
