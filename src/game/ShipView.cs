using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// A ship's visual: a generated low-poly hull with emissive engine nozzles and
    /// a thrust plume. Purely presentational — banking and pitch respond to sim
    /// state but never feed back into it (the directive's rule: visual motion must
    /// not change gameplay values).
    ///
    /// The hull is no longer one hand-built Shuttle. <see cref="ShipMeshBuilder"/>
    /// generates it from the ship's <see cref="ShipAppearance"/>, so any of the 902
    /// hulls in the dataset gets geometry sized from its own mass, fittings on its
    /// own hardpoints, and lit ports from its own berths — which is Milestone 8's
    /// "replace prototype assets with a consistent art set".
    ///
    /// Geometry is rebuilt when the ship's identity or damage state changes, so a
    /// hull visibly degrades as it is shot apart. The mesh faces −Z, matching
    /// <see cref="WorldSpace.YawFromFacing"/>, and is centred on its roll axis so the
    /// banking applied here swings it correctly.
    /// </summary>
    public partial class ShipView : Node3D
    {
        private Node3D _hull = null!;             // built in _Ready
        private Node3D _generated = null!;        // built in _Ready, filled on first SyncWith
        private GpuParticles3D _plume = null!;    // built in _Ready
        private OmniLight3D _engineGlow = null!;  // built in _Ready
        private ShipDefinition? _builtFor;
        private int _builtDamageState = -1;
        private float _bank;
        private float _pitch;

        public override void _Ready()
        {
            _hull = new Node3D { Name = "Hull" };
            AddChild(_hull);

            // Geometry is generated per ship on the first SyncWith, once the hull's
            // identity is known. It lives under its own node so it can be rebuilt
            // when the damage state changes without disturbing the plume or glow.
            _generated = new Node3D { Name = "Generated" };
            _hull.AddChild(_generated);

            _engineGlow = new OmniLight3D
            {
                Name = "EngineGlow",
                LightColor = new Color(1.0f, 0.58f, 0.22f),
                LightEnergy = 0.0f,
                OmniRange = 7f,
            };
            _hull.AddChild(_engineGlow);

            _plume = BuildPlume();
            _hull.AddChild(_plume);
        }

        /// <summary>
        /// (Re)builds the hull for this ship. Cheap enough to call on any change of
        /// identity or damage state, and skipped entirely when neither has moved.
        /// </summary>
        private void Rebuild(Ship ship)
        {
            // The government comes from the fleets that fly the hull and the shipyards
            // that stock it; a ship definition never names one.
            var appearance = new ShipAppearance(ship.Definition)
            {
                Faction = EsData.Universe?.GovernmentOf(ship.Definition.DisplayName),
            };
            int damageState = ShipAppearance.DamageState(ship.Hull, ship.MaxHull);

            if (ReferenceEquals(_builtFor, ship.Definition) && _builtDamageState == damageState)
                return;

            _builtFor = ship.Definition;
            _builtDamageState = damageState;

            foreach (Node child in _generated.GetChildren())
            {
                _generated.RemoveChild(child);
                child.QueueFree();
            }

            _generated.AddChild(ShipMeshBuilder.Build(appearance, damageState));

            // Park the plume and its light just aft of the tail, scaled to the hull
            // rather than to the one ship this view used to be hard-coded for.
            float length = WorldSpace.Length(appearance.Length);
            _plume.Position = new Vector3(0f, 0f, length * 0.55f);
            _engineGlow.Position = new Vector3(0f, 0f, length * 0.62f);
            _engineGlow.OmniRange = Mathf.Max(2f, length * 1.2f);
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
            Rebuild(ship);

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
            PulseNozzles(_generated, burning);
        }

        /// <summary>
        /// Drives the engine throats' emission with the throttle. Walks the generated
        /// tree because the geometry is now nested under its own node, and keys off
        /// the blue engine emission the mesh builder assigns to nozzles.
        /// </summary>
        private static void PulseNozzles(Node node, bool burning)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is MeshInstance3D { MaterialOverride: StandardMaterial3D m } &&
                    m.EmissionEnabled && m.Emission.B > 0.8f)
                {
                    m.EmissionEnergyMultiplier =
                        Mathf.Lerp(m.EmissionEnergyMultiplier, burning ? 3.5f : 0.15f, 0.25f);
                }

                PulseNozzles(child, burning);
            }
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
