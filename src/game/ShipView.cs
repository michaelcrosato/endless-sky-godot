using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// Player-ship visual: a procedural low-poly hull with emissive engine
    /// nozzles and a thrust plume. Purely presentational — banking and pitch
    /// respond to sim state but never feed back into it (the directive's rule:
    /// visual motion must not change gameplay values).
    ///
    /// The Shuttle reads as a civilian workhorse, not a fighter: blunt nose,
    /// raised spine, under-wing cargo pods, one cool canopy accent. Dorsal and
    /// ventral surfaces carry a value break so the silhouette holds at
    /// gameplay distance. The mesh faces −Z, matching
    /// <see cref="WorldSpace.YawFromFacing"/>.
    /// </summary>
    public partial class ShipView : Node3D
    {
        private Node3D _hull = null!;             // built in _Ready
        private GpuParticles3D _plume = null!;    // built in _Ready
        private OmniLight3D _engineGlow = null!;  // built in _Ready
        private float _bank;
        private float _pitch;

        public override void _Ready()
        {
            _hull = new Node3D { Name = "Hull" };
            AddChild(_hull);

            var body = new MeshInstance3D { Name = "Body", Mesh = BuildHullMesh() };
            _hull.AddChild(body);

            var ventralMaterial = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.22f, 0.23f, 0.27f),
                Metallic = 0.55f,
                Roughness = 0.32f,
                RimEnabled = true,
                Rim = 0.55f,
                RimTint = 0.25f,
            };

            // Boxy under-wing cargo pods: the cheapest possible "this is a
            // hauler" silhouette cue.
            foreach (float side in new[] { -1.1f, 1.1f })
            {
                _hull.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.4f, 1.4f) },
                    MaterialOverride = ventralMaterial,
                    Position = new Vector3(side, -0.35f, 0.9f),
                });
            }

            // Canopy: one bright cool dot of identity.
            _hull.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.32f, 0.14f, 0.5f) },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.06f, 0.09f, 0.12f),
                    Metallic = 0.2f,
                    Roughness = 0.2f,
                    EmissionEnabled = true,
                    Emission = new Color(0.2f, 0.7f, 0.9f),
                    EmissionEnergyMultiplier = 0.8f,
                },
                Position = new Vector3(0f, 0.62f, -0.9f),
            });

            // Republic-blue spine stripe (a few percent of area, saturated).
            _hull.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.12f, 0.03f, 1.3f) },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.20f, 0.38f, 0.85f),
                    Roughness = 0.4f,
                },
                Position = new Vector3(0f, 0.70f, 0.35f),
            });

            // Twin engine nozzles + always-on emissive throats so the plume
            // never appears detached from the hull.
            var nozzleMesh = new CylinderMesh
            {
                TopRadius = 0.28f,
                BottomRadius = 0.36f,
                Height = 0.5f,
                RadialSegments = 10,
            };
            var nozzleMaterial = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.16f, 0.17f, 0.20f),
                Metallic = 0.6f,
                Roughness = 0.45f,
                EmissionEnabled = true,
                Emission = new Color(1.0f, 0.62f, 0.25f),
                EmissionEnergyMultiplier = 0.15f,
            };
            var throatMaterial = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                AlbedoColor = new Color(1.0f, 0.60f, 0.25f, 0.55f),
                DisableReceiveShadows = true,
            };
            foreach (float side in new[] { -0.62f, 0.62f })
            {
                _hull.AddChild(new MeshInstance3D
                {
                    Mesh = nozzleMesh,
                    MaterialOverride = nozzleMaterial,
                    Position = new Vector3(side, 0.0f, 1.95f),
                    RotationDegrees = new Vector3(90f, 0f, 0f),
                });
                _hull.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh
                    {
                        TopRadius = 0.02f,
                        BottomRadius = 0.20f,
                        Height = 0.4f,
                        RadialSegments = 8,
                    },
                    MaterialOverride = throatMaterial,
                    Position = new Vector3(side, 0.0f, 2.25f),
                    RotationDegrees = new Vector3(90f, 0f, 0f),
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
            }

            _engineGlow = new OmniLight3D
            {
                Name = "EngineGlow",
                Position = new Vector3(0f, 0.1f, 2.6f),
                LightColor = new Color(1.0f, 0.58f, 0.22f),
                LightEnergy = 0.0f,
                OmniRange = 7f,
            };
            _hull.AddChild(_engineGlow);

            _plume = BuildPlume();
            _hull.AddChild(_plume);
        }

        /// <summary>
        /// Hyperspace visual: stretch the hull along its heading (upstream
        /// stretches the sprite) and kill the plume. 0 restores normal flight.
        /// </summary>
        public void SetHyperspaceStretch(float fraction)
        {
            _hull.Scale = new Vector3(1f, 1f, 1f + fraction * 3f);
            if (fraction > 0f)
            {
                _plume.Emitting = false;
            }
        }

        /// <summary>Update transform + effects from the sim ship. Called once per sim step.</summary>
        public void SyncWith(Ship ship)
        {
            Position = WorldSpace.ToWorld(ship.Position);
            Rotation = new Vector3(0f, WorldSpace.YawFromFacing(ship.Facing), 0f);

            // Visual-only banking into the turn and a nose dip under thrust.
            float bankTarget = (float)(-ship.SteeringDirection * Mathf.DegToRad(24f));
            float pitchTarget = ship.IsThrusting ? Mathf.DegToRad(-3.5f)
                : ship.IsReversing ? Mathf.DegToRad(3f) : 0f;
            _bank = Mathf.Lerp(_bank, bankTarget, 0.12f);
            _pitch = Mathf.Lerp(_pitch, pitchTarget, 0.10f);
            _hull.Rotation = new Vector3(_pitch, 0f, _bank);

            bool burning = ship.IsThrusting;
            _plume.Emitting = burning;
            _engineGlow.LightEnergy = Mathf.Lerp(_engineGlow.LightEnergy, burning ? 1.4f : 0f, 0.25f);
            foreach (Node child in _hull.GetChildren())
            {
                if (child is MeshInstance3D { MaterialOverride: StandardMaterial3D m } &&
                    m.EmissionEnabled && m.Emission.R > 0.9f)
                {
                    m.EmissionEnergyMultiplier = Mathf.Lerp(m.EmissionEnergyMultiplier, burning ? 3.5f : 0.15f, 0.25f);
                }
            }
        }

        private static ArrayMesh BuildHullMesh()
        {
            // Blunt-nosed workhorse hull, two surfaces: bright dorsal, dark
            // ventral. Flat-shaded faces sell the low-poly style.
            Vector3 noseL = new(-0.28f, 0.10f, -1.9f);
            Vector3 noseR = new(0.28f, 0.10f, -1.9f);
            Vector3 spine = new(0f, 0.75f, 0.4f);
            Vector3 tailL = new(-0.75f, 0.22f, 2.1f);
            Vector3 tailR = new(0.75f, 0.22f, 2.1f);
            Vector3 wingL = new(-1.65f, -0.05f, 1.5f);
            Vector3 wingR = new(1.65f, -0.05f, 1.5f);
            Vector3 bellyF = new(0f, -0.30f, -1.5f);
            Vector3 bellyB = new(0f, -0.34f, 1.9f);

            static void Tri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c)
            {
                Vector3 n = (b - a).Cross(c - a).Normalized();
                st.SetNormal(n);
                st.AddVertex(a);
                st.SetNormal(n);
                st.AddVertex(b);
                st.SetNormal(n);
                st.AddVertex(c);
            }

            var dorsal = new SurfaceTool();
            dorsal.Begin(Mesh.PrimitiveType.Triangles);
            dorsal.SetSmoothGroup(uint.MaxValue);
            Tri(dorsal, noseL, spine, noseR);
            Tri(dorsal, noseR, spine, wingR);
            Tri(dorsal, noseL, wingL, spine);
            Tri(dorsal, spine, tailR, wingR);
            Tri(dorsal, spine, tailL, wingL);
            Tri(dorsal, spine, tailR, tailL);

            var ventral = new SurfaceTool();
            ventral.Begin(Mesh.PrimitiveType.Triangles);
            ventral.SetSmoothGroup(uint.MaxValue);
            Tri(ventral, noseR, noseL, bellyF);      // blunt front face
            Tri(ventral, noseR, bellyF, wingR);
            Tri(ventral, noseL, wingL, bellyF);
            Tri(ventral, bellyF, wingR, bellyB);
            Tri(ventral, bellyF, bellyB, wingL);
            Tri(ventral, bellyB, wingR, tailR);
            Tri(ventral, bellyB, tailR, tailL);
            Tri(ventral, bellyB, tailL, wingL);

            ArrayMesh mesh = dorsal.Commit();
            mesh = ventral.Commit(mesh);
            mesh.SurfaceSetMaterial(0, new StandardMaterial3D
            {
                AlbedoColor = new Color(0.78f, 0.79f, 0.82f),
                Metallic = 0.55f,
                Roughness = 0.32f,
                RimEnabled = true,
                Rim = 0.55f,
                RimTint = 0.25f,
            });
            mesh.SurfaceSetMaterial(1, new StandardMaterial3D
            {
                AlbedoColor = new Color(0.22f, 0.23f, 0.27f),
                Metallic = 0.55f,
                Roughness = 0.32f,
                RimEnabled = true,
                Rim = 0.55f,
                RimTint = 0.25f,
            });
            return mesh;
        }

        private static GpuParticles3D BuildPlume()
        {
            var scaleCurve = new Curve();
            scaleCurve.AddPoint(new Vector2(0f, 1f));
            scaleCurve.AddPoint(new Vector2(1f, 0f));

            var colorRamp = new Gradient
            {
                Offsets = new[] { 0.0f, 0.5f, 1.0f },
                Colors = new[]
                {
                    new Color(1.0f, 0.88f, 0.66f, 0.9f),
                    new Color(1.0f, 0.55f, 0.20f, 0.6f),
                    new Color(0.7f, 0.12f, 0.05f, 0.0f),
                },
            };

            var material = new ParticleProcessMaterial
            {
                Direction = new Vector3(0f, 0f, 1f),
                Spread = 5f,
                // Exhaust must leave the ship faster than the ship flies
                // (vmax ≈ 33 u/s) or the trail's shape is dictated by emitter
                // motion and beads into dots.
                InitialVelocityMin = 45f,
                InitialVelocityMax = 70f,
                Gravity = Vector3.Zero,
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
                EmissionSphereRadius = 0.15f,
                ScaleCurve = new CurveTexture { Curve = scaleCurve },
                ColorRamp = new GradientTexture1D { Gradient = colorRamp },
            };
            var quad = new QuadMesh { Size = new Vector2(0.45f, 0.45f) };
            quad.Material = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                VertexColorUseAsAlbedo = true,
                AlbedoColor = new Color(1f, 1f, 1f),
                DisableReceiveShadows = true,
            };
            return new GpuParticles3D
            {
                Name = "Plume",
                Position = new Vector3(0f, 0.1f, 2.15f),
                Amount = 140,
                Lifetime = 0.20,
                Emitting = false,
                Interpolate = true,
                ProcessMaterial = material,
                DrawPass1 = quad,
                LocalCoords = false,
                // World-space particles need an explicit AABB or the default
                // emitter-tracking bounds cull them into orphan clumps.
                VisibilityAabb = new Aabb(new Vector3(-25f, -25f, -25f), new Vector3(50f, 50f, 50f)),
            };
        }
    }
}
