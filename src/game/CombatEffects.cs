using System.Collections.Generic;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// Owns every transient combat visual: projectile bolts, explosions and
    /// shield-impact flashes. The sim's firing loop drives it — presentation
    /// only ever mirrors sim state, never creates gameplay.
    ///
    /// Contract with the sim layer: call <see cref="SyncProjectiles"/> once
    /// per physics tick with the live projectile list; call
    /// <see cref="SpawnExplosion"/>/<see cref="FlashShields"/> from damage
    /// events. Views for dead projectiles are recycled automatically.
    /// </summary>
    public partial class CombatEffects : Node3D
    {
        private readonly Dictionary<Projectile, ProjectileView> _views = new();
        private readonly List<Projectile> _stale = new();

        /// <summary>Mirror the sim's live projectiles into view nodes.</summary>
        public void SyncProjectiles(IReadOnlyList<Projectile> projectiles)
        {
            foreach (Projectile projectile in projectiles)
            {
                if (!_views.TryGetValue(projectile, out ProjectileView? view))
                {
                    view = ProjectileView.Create(projectile);
                    _views[projectile] = view;
                    AddChild(view);
                }

                view.Sync();
            }

            _stale.Clear();
            foreach ((Projectile projectile, ProjectileView view) in _views)
            {
                if (projectile.IsDead)
                {
                    view.QueueFree();
                    _stale.Add(projectile);
                }
            }

            foreach (Projectile projectile in _stale)
            {
                _views.Remove(projectile);
            }
        }

        /// <summary>One-shot explosion burst. Scale ~1 for a bolt impact, larger for ship deaths.</summary>
        public void SpawnExplosion(Point simPosition, float scale = 1f)
        {
            var burst = ExplosionView.Create(scale);
            AddChild(burst);
            burst.Position = WorldSpace.ToWorld(simPosition);
            burst.Detonate();
        }

        /// <summary>Flash a shield shell around a ship view (call on shielded hits).</summary>
        public static void FlashShields(Node3D shipView, float hullRadius = 2.6f)
        {
            ShieldImpactView flash = shipView.GetNodeOrNull<ShieldImpactView>("ShieldFlash");
            if (flash == null)
            {
                flash = ShieldImpactView.Create(hullRadius);
                flash.Name = "ShieldFlash";
                shipView.AddChild(flash);
            }

            flash.Flash();
        }
    }
}
