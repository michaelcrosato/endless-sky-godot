using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// The shield shell: an additive sphere around the hull that pops visible
    /// on a shielded hit and decays over ~a quarter second. Upstream rule
    /// (docs/upstream-reference.md, combat findings): shields block hull
    /// damage entirely, so this flash IS the feedback that a hit did no hull
    /// harm — it must read distinctly from an explosion.
    /// </summary>
    public partial class ShieldImpactView : Node3D
    {
        private float _radius = 2.6f;
        private StandardMaterial3D _material = null!; // built in _Ready
        private float _intensity;

        public static ShieldImpactView Create(float hullRadius)
        {
            return new ShieldImpactView { _radius = hullRadius };
        }

        public override void _Ready()
        {
            _material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                AlbedoColor = new Color(0.35f, 0.65f, 1.0f, 0.0f),
                DisableReceiveShadows = true,
                // Fresnel-ish: strongest at the silhouette edge.
                RimEnabled = true,
                Rim = 1.0f,
                RimTint = 0.0f,
            };
            AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh
                {
                    Radius = _radius,
                    Height = _radius * 2f,
                    RadialSegments = 20,
                    Rings = 12,
                },
                MaterialOverride = _material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }

        public void Flash() => _intensity = 1f;

        public override void _Process(double delta)
        {
            if (_intensity <= 0f)
            {
                return;
            }

            _intensity = Mathf.Max(0f, _intensity - (float)(delta * 4.0));
            Color color = _material.AlbedoColor;
            color.A = 0.55f * _intensity;
            _material.AlbedoColor = color;
        }
    }
}
