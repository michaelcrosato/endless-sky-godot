using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// One projectile in flight: an emissive bolt stretched along its heading.
    /// Homing weapons read as missiles (longer, warmer, with a spark trail
    /// light); everything else is a short hot bolt. Purely presentational.
    /// </summary>
    public partial class ProjectileView : Node3D
    {
        private Projectile _projectile = null!; // set by Create

        public static ProjectileView Create(Projectile projectile)
        {
            return new ProjectileView { _projectile = projectile, Name = "Projectile" };
        }

        public override void _Ready()
        {
            bool homing = _projectile.Weapon.IsHoming;
            Color core = homing ? new Color(1.0f, 0.72f, 0.35f) : new Color(0.55f, 0.85f, 1.0f);

            AddChild(new MeshInstance3D
            {
                Mesh = new CapsuleMesh
                {
                    Radius = homing ? 0.16f : 0.10f,
                    Height = homing ? 1.5f : 0.9f,
                    RadialSegments = 8,
                },
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = core,
                    EmissionEnabled = true,
                    Emission = core,
                    EmissionEnergyMultiplier = 4.5f,
                },
                // Capsule's long axis is Y; lay it along -Z (the node's forward).
                RotationDegrees = new Vector3(90f, 0f, 0f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });

            if (homing)
            {
                AddChild(new OmniLight3D
                {
                    LightColor = core,
                    LightEnergy = 0.8f,
                    OmniRange = 4f,
                });
            }

            Sync();
        }

        /// <summary>Mirror sim position/heading. Called once per physics tick.</summary>
        public void Sync()
        {
            Position = WorldSpace.ToWorld(_projectile.Position);
            Rotation = new Vector3(0f, WorldSpace.YawFromFacing(_projectile.Angle), 0f);
        }
    }
}
