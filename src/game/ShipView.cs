using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// Player-ship visual: a procedural low-poly dart with emissive engine
    /// nozzles and a thrust plume. Purely presentational — banking and pitch
    /// respond to sim state but never feed back into it (the directive's rule:
    /// visual motion must not change gameplay values).
    ///
    /// The mesh faces −Z, matching <see cref="WorldSpace.YawFromFacing"/>.
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

            // Twin engine nozzles: small emissive discs either side of the tail.
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
                EmissionEnergyMultiplier = 0.0f,
            };
            foreach (float side in new[] { -0.62f, 0.62f })
            {
                var nozzle = new MeshInstance3D
                {
                    Mesh = nozzleMesh,
                    MaterialOverride = nozzleMaterial,
                    Position = new Vector3(side, 0.0f, 1.95f),
                    RotationDegrees = new Vector3(90f, 0f, 0f),
                };
                _hull.AddChild(nozzle);
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
            _engineGlow.LightEnergy = Mathf.Lerp(_engineGlow.LightEnergy, burning ? 2.4f : 0f, 0.25f);
            foreach (Node child in _hull.GetChildren())
            {
                if (child is MeshInstance3D { MaterialOverride: StandardMaterial3D m } && m.EmissionEnabled)
                {
                    m.EmissionEnergyMultiplier = Mathf.Lerp(m.EmissionEnergyMultiplier, burning ? 3.5f : 0.15f, 0.25f);
                }
            }
        }

        private static ArrayMesh BuildHullMesh()
        {
            // A recognizable dart silhouette: long nose, swept wings, raised spine.
            // Flat-shaded faces sell the low-poly style.
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            st.SetSmoothGroup(uint.MaxValue); // flat shading via per-face normals

            Vector3 nose = new(0f, 0.08f, -2.6f);
            Vector3 spine = new(0f, 0.55f, 0.4f);
            Vector3 tailL = new(-0.75f, 0.18f, 2.1f);
            Vector3 tailR = new(0.75f, 0.18f, 2.1f);
            Vector3 wingL = new(-1.65f, -0.05f, 1.5f);
            Vector3 wingR = new(1.65f, -0.05f, 1.5f);
            Vector3 bellyF = new(0f, -0.28f, -1.2f);
            Vector3 bellyB = new(0f, -0.32f, 1.9f);

            void Tri(Vector3 a, Vector3 b, Vector3 c)
            {
                Vector3 n = (b - a).Cross(c - a).Normalized();
                st.SetNormal(n);
                st.AddVertex(a);
                st.SetNormal(n);
                st.AddVertex(b);
                st.SetNormal(n);
                st.AddVertex(c);
            }

            // Top deck
            Tri(nose, spine, wingR);
            Tri(nose, wingL, spine);
            Tri(spine, tailR, wingR);
            Tri(spine, tailL, wingL);
            Tri(spine, tailR, tailL);
            // Belly
            Tri(nose, wingR, bellyF);
            Tri(nose, bellyF, wingL);
            Tri(bellyF, wingR, bellyB);
            Tri(bellyF, bellyB, wingL);
            Tri(bellyB, wingR, tailR);
            Tri(bellyB, tailR, tailL);
            Tri(bellyB, tailL, wingL);

            var mesh = st.Commit();
            var material = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.72f, 0.74f, 0.78f),
                Metallic = 0.35f,
                Roughness = 0.55f,
            };
            mesh.SurfaceSetMaterial(0, material);
            return mesh;
        }

        private static GpuParticles3D BuildPlume()
        {
            var material = new ParticleProcessMaterial
            {
                Direction = new Vector3(0f, 0f, 1f),
                Spread = 7f,
                InitialVelocityMin = 9f,
                InitialVelocityMax = 13f,
                Gravity = Vector3.Zero,
                ScaleMin = 0.5f,
                ScaleMax = 1.0f,
                Color = new Color(1.0f, 0.62f, 0.25f, 0.85f),
            };
            var quad = new QuadMesh { Size = new Vector2(0.35f, 0.35f) };
            quad.Material = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                VertexColorUseAsAlbedo = true,
                AlbedoColor = new Color(1f, 0.75f, 0.4f),
            };
            return new GpuParticles3D
            {
                Name = "Plume",
                Position = new Vector3(0f, 0.1f, 2.4f),
                Amount = 90,
                Lifetime = 0.35,
                Emitting = false,
                ProcessMaterial = material,
                DrawPass1 = quad,
                LocalCoords = false,
            };
        }
    }
}
