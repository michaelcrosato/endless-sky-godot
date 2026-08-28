using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// A distant shell of stars: one MultiMesh of billboarded quads on a large
    /// sphere. Deterministic given the seed, so screenshots are comparable
    /// across runs. Purely decorative background — it never moves.
    /// </summary>
    public partial class Starfield : MultiMeshInstance3D
    {
        private const int StarCount = 1700;
        private const float ShellRadius = 2600f;

        public override void _Ready()
        {
            var quad = new QuadMesh { Size = new Vector2(1.6f, 1.6f) };
            quad.Material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                VertexColorUseAsAlbedo = true,
                AlbedoColor = new Color(1f, 1f, 1f),
                DisableReceiveShadows = true,
            };

            var multi = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = quad,
                InstanceCount = StarCount,
            };

            var rng = new RandomNumberGenerator();
            rng.Seed = 3013;
            for (int i = 0; i < StarCount; i++)
            {
                // Uniform direction on the sphere.
                float z = rng.RandfRange(-1f, 1f);
                float t = rng.RandfRange(0f, Mathf.Tau);
                float r = Mathf.Sqrt(1f - z * z);
                var dir = new Vector3(r * Mathf.Cos(t), z, r * Mathf.Sin(t));

                float scale = rng.RandfRange(0.4f, 1.6f);
                var transform = new Transform3D(Basis.Identity.Scaled(Vector3.One * scale), dir * ShellRadius);
                multi.SetInstanceTransform(i, transform);

                // Mostly white, a scatter of warm and cool stars, dimmer = smaller.
                float warm = rng.Randf();
                Color color = warm < 0.12f ? new Color(1.0f, 0.78f, 0.62f)
                    : warm > 0.88f ? new Color(0.72f, 0.82f, 1.0f)
                    : new Color(0.92f, 0.94f, 1.0f);
                multi.SetInstanceColor(i, color * rng.RandfRange(0.35f, 1.0f));
            }

            Multimesh = multi;
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }
    }
}
