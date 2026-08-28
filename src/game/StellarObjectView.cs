using System;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// One star, planet or moon. Geometry is a sphere sized by object class;
    /// the palette keys off the upstream sprite path so the same data that
    /// drives the real game picks the look here. Stars carry the system's key
    /// light — the directive asks for lighting quality over polygon count.
    /// </summary>
    public partial class StellarObjectView : Node3D
    {
        public StellarObject Object { get; private set; } = null!; // set by Create

        public float VisualRadius { get; private set; }

        public static StellarObjectView Create(StellarObject obj)
        {
            var view = new StellarObjectView
            {
                Name = string.IsNullOrEmpty(obj.PlanetName) ? obj.Sprite.Replace('/', '_') : obj.PlanetName,
                Object = obj,
            };
            view.VisualRadius = RadiusFor(obj);
            return view;
        }

        public override void _Ready()
        {
            var mesh = new SphereMesh
            {
                Radius = VisualRadius,
                Height = VisualRadius * 2f,
                RadialSegments = 24,
                Rings = 14,
            };
            var instance = new MeshInstance3D { Mesh = mesh };

            if (Object.IsStar)
            {
                // The star body must NOT cast shadows: a closed mesh wrapped
                // around its own light writes depth in every direction and
                // blacks out the entire system (the original M1 lighting bug).
                instance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                instance.MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = StarColor(Object.Sprite),
                    EmissionEnabled = true,
                    Emission = StarColor(Object.Sprite),
                    // Headroom above the tonemap white point so the core blooms
                    // as a gradient instead of clipping to a flat disc.
                    EmissionEnergyMultiplier = 9.0f,
                };
                // Local warm accent only; the real key light is a directional
                // in FlightWorld so shadowing is consistent at system scale.
                AddChild(new OmniLight3D
                {
                    Name = "StarLight",
                    LightColor = new Color(1.0f, 0.96f, 0.88f),
                    LightEnergy = 1.2f,
                    OmniRange = 120f,
                    ShadowEnabled = false,
                });

                // Corona: one billboarded quad with a radial gradient — real
                // falloff, two triangles (the old constant-alpha sphere shell
                // rendered as a hard-edged flat plate).
                var gradient = new Gradient
                {
                    Offsets = new[] { 0.0f, 0.45f, 1.0f },
                    Colors = new[]
                    {
                        new Color(1.0f, 0.88f, 0.62f, 0.55f),
                        new Color(1.0f, 0.55f, 0.25f, 0.12f),
                        new Color(1.0f, 0.45f, 0.20f, 0.0f),
                    },
                };
                var coronaTexture = new GradientTexture2D
                {
                    Gradient = gradient,
                    Fill = GradientTexture2D.FillEnum.Radial,
                    FillFrom = new Vector2(0.5f, 0.5f),
                    FillTo = new Vector2(0.5f, 0.0f),
                    Width = 256,
                    Height = 256,
                };
                var coronaMaterial = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
                    AlbedoTexture = coronaTexture,
                    DisableReceiveShadows = true,
                    RenderPriority = 1,
                };
                var corona = new MeshInstance3D
                {
                    Mesh = new QuadMesh { Size = new Vector2(VisualRadius * 6f, VisualRadius * 6f) },
                    MaterialOverride = coronaMaterial,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                AddChild(corona);
            }
            else
            {
                instance.MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = PlanetColor(Object.Sprite),
                    Roughness = 0.9f,
                    Metallic = 0.0f,
                };
            }

            AddChild(instance);
            SyncPosition();
        }

        /// <summary>Reposition from the sim (stellar positions change only with the date).</summary>
        public void SyncPosition()
        {
            Position = WorldSpace.ToWorld(Object.Position);
        }

        private static float RadiusFor(StellarObject obj)
        {
            // Upstream derives collision/landing radii from sprite dimensions,
            // which we do not load; these class-based sizes keep the same feel
            // (stars dominant, gas giants large, moons small) at our scale.
            string sprite = obj.Sprite ?? string.Empty;
            if (obj.IsStar)
            {
                return 8.0f;
            }

            if (sprite.Contains("gas", StringComparison.Ordinal))
            {
                return obj.IsMoon ? 3.0f : 10.5f;
            }

            if (obj.IsMoon)
            {
                return 2.4f;
            }

            if (sprite.Contains("cloud", StringComparison.Ordinal))
            {
                return 6.5f;
            }

            return 4.2f;
        }

        private static Color StarColor(string sprite)
        {
            // Rough spectral tinting by sprite name (star/g5, star/k0, star/b…).
            if (sprite.Contains("/b", StringComparison.Ordinal)) return new Color(0.72f, 0.82f, 1.0f);
            if (sprite.Contains("/a", StringComparison.Ordinal)) return new Color(0.88f, 0.92f, 1.0f);
            if (sprite.Contains("/f", StringComparison.Ordinal)) return new Color(1.0f, 0.98f, 0.92f);
            if (sprite.Contains("/g", StringComparison.Ordinal)) return new Color(1.0f, 0.93f, 0.78f);
            if (sprite.Contains("/k", StringComparison.Ordinal)) return new Color(1.0f, 0.82f, 0.60f);
            if (sprite.Contains("/m", StringComparison.Ordinal)) return new Color(1.0f, 0.65f, 0.45f);
            return new Color(1.0f, 0.95f, 0.85f);
        }

        private static Color PlanetColor(string sprite)
        {
            if (sprite.Contains("cloud", StringComparison.Ordinal)) return new Color(0.55f, 0.66f, 0.78f);
            if (sprite.Contains("ice", StringComparison.Ordinal)) return new Color(0.72f, 0.82f, 0.88f);
            if (sprite.Contains("gas", StringComparison.Ordinal)) return new Color(0.82f, 0.62f, 0.42f);
            if (sprite.Contains("ganymede", StringComparison.Ordinal)) return new Color(0.62f, 0.55f, 0.46f);
            if (sprite.Contains("rock", StringComparison.Ordinal)) return new Color(0.52f, 0.46f, 0.42f);
            if (sprite.Contains("ocean", StringComparison.Ordinal)) return new Color(0.30f, 0.48f, 0.68f);
            if (sprite.Contains("desert", StringComparison.Ordinal)) return new Color(0.76f, 0.62f, 0.40f);
            if (sprite.Contains("forest", StringComparison.Ordinal) ||
                sprite.Contains("earth", StringComparison.Ordinal)) return new Color(0.36f, 0.55f, 0.40f);
            if (sprite.Contains("lava", StringComparison.Ordinal)) return new Color(0.58f, 0.30f, 0.22f);
            return new Color(0.55f, 0.52f, 0.50f);
        }
    }
}
