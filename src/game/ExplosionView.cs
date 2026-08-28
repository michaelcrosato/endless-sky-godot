using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// A one-shot explosion: a spherical particle burst plus a light pop,
    /// self-freeing when spent. Scale 1 reads as a bolt impact; ship deaths
    /// pass larger scales for a slower, wider burst.
    /// </summary>
    public partial class ExplosionView : Node3D
    {
        private float _scale = 1f;
        private GpuParticles3D _burst = null!; // built in _Ready
        private OmniLight3D _flash = null!;    // built in _Ready
        private double _age;

        public static ExplosionView Create(float scale)
        {
            return new ExplosionView { _scale = Mathf.Clamp(scale, 0.5f, 6f), Name = "Explosion" };
        }

        public override void _Ready()
        {
            var scaleCurve = new Curve();
            scaleCurve.AddPoint(new Vector2(0f, 1f));
            scaleCurve.AddPoint(new Vector2(1f, 0.1f));

            var colorRamp = new Gradient
            {
                Offsets = new[] { 0.0f, 0.35f, 1.0f },
                Colors = new[]
                {
                    new Color(1.0f, 0.95f, 0.80f, 1.0f),
                    new Color(1.0f, 0.55f, 0.18f, 0.8f),
                    new Color(0.25f, 0.10f, 0.08f, 0.0f),
                },
            };

            var quad = new QuadMesh { Size = new Vector2(0.5f * _scale, 0.5f * _scale) };
            quad.Material = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                VertexColorUseAsAlbedo = true,
                DisableReceiveShadows = true,
            };

            _burst = new GpuParticles3D
            {
                OneShot = true,
                Explosiveness = 1.0f,
                Amount = (int)(60 * _scale),
                Lifetime = 0.45 * _scale,
                Emitting = false,
                ProcessMaterial = new ParticleProcessMaterial
                {
                    EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
                    EmissionSphereRadius = 0.2f * _scale,
                    Direction = Vector3.Zero,
                    Spread = 180f,
                    InitialVelocityMin = 6f * _scale,
                    InitialVelocityMax = 14f * _scale,
                    Gravity = Vector3.Zero,
                    DampingMin = 3f,
                    DampingMax = 5f,
                    ScaleCurve = new CurveTexture { Curve = scaleCurve },
                    ColorRamp = new GradientTexture1D { Gradient = colorRamp },
                },
                DrawPass1 = quad,
                LocalCoords = false,
                VisibilityAabb = new Aabb(Vector3.One * (-12f * _scale), Vector3.One * (24f * _scale)),
            };
            AddChild(_burst);

            _flash = new OmniLight3D
            {
                LightColor = new Color(1.0f, 0.72f, 0.40f),
                LightEnergy = 0f,
                OmniRange = 14f * _scale,
            };
            AddChild(_flash);
        }

        public void Detonate()
        {
            _burst.Emitting = true;
            _flash.LightEnergy = 3.0f * _scale;
            _age = 0.0;
        }

        public override void _Process(double delta)
        {
            _age += delta;
            _flash.LightEnergy = Mathf.Max(0f, _flash.LightEnergy - (float)(delta * 10.0 * _scale));
            if (_age > _burst.Lifetime + 0.3)
            {
                QueueFree();
            }
        }
    }
}
