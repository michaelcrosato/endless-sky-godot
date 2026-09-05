using System;
using System.Collections.Generic;
using System.Linq;

namespace EndlessSky.Sim
{
    public partial class PlayerFleet
    {
        private readonly Dictionary<Ship, (StarSystem From, StarSystem Goal, StarSystem Next)> _escortRoutes = new();
        public FleetOrder Order { get; private set; } = FleetOrder.Escort;
        public Ship? OrderTarget { get; private set; }

        /// <summary>Commands all escorts. Holding position overrides following a jump.</summary>
        public void IssueOrder(FleetOrder order, Ship? target = null)
        {
            Order = order;
            OrderTarget = order == FleetOrder.AttackTarget ? target : null;
        }

        /// <summary>
        /// Steps each active owned escort once, including ships catching up from another
        /// system. The flagship is stepped by its pilot. Port of AI::MoveEscort and the
        /// owned-escort branch of AI::Step; ships pay and fly their own jumps.
        /// </summary>
        /// <remarks>
        /// Independent refuelling, per-ship landing state, fighter recovery, named
        /// formations and orders for selected groups remain incomplete.
        /// </remarks>
        public IReadOnlyList<Projectile> StepEscorts(GameData data, bool flagshipJumping = false,
            IEnumerable<Ship>? candidates = null)
        {
            var shots = new List<Projectile>();
            Ship? flagship = Flagship;
            if (flagship?.CurrentSystem == null || flagship.IsDestroyed || PortCargo != null)
                return shots;

            foreach (Ship escort in Escorts)
            {
                if (escort.CurrentSystem == null || escort.StepHyperspace()) continue;
                if (escort.IsDisabled)
                {
                    escort.Step(Command.None);
                    continue;
                }

                bool here = ReferenceEquals(escort.CurrentSystem, flagship.CurrentSystem);
                StarSystem? goal = Order == FleetOrder.Hold ? null
                    : !here ? flagship.CurrentSystem
                    : flagshipJumping ? flagship.HyperspaceSystem ?? flagship.TargetSystem : null;
                if (goal != null)
                {
                    escort.TargetSystem = EscortRoute(escort, goal, data);
                    if (escort.TargetSystem != null && escort.Fuel >= escort.JumpFuelCost)
                    {
                        escort.Step(ShipAi.PrepareForHyperspace(escort));
                        Ship? threat = here ? ShipAi.FindTarget(escort, candidates) : null;
                        if (threat != null) shots.AddRange(ShipAi.AutoFire(escort, threat));
                        // A ready escort waits for its parent to be ready or entering.
                        // Once separated, it can catch up without waiting for the parent.
                        if (!here || flagship.IsEnteringHyperspace || flagship.IsReadyToJump())
                            escort.TryCommitJump();
                        continue;
                    }
                }
                else escort.TargetSystem = null;

                Ship? target = Order == FleetOrder.AttackTarget && OrderTarget is { IsTargetable: true }
                    && ReferenceEquals(OrderTarget.CurrentSystem, escort.CurrentSystem)
                    ? OrderTarget : ShipAi.FindTarget(escort, candidates);
                // AI::Step prioritizes following a jump, and normally only pursues
                // nearby threats. An explicit attack order may pursue farther.
                bool pursue = goal == null && target != null && (Order == FleetOrder.AttackTarget
                    || (Order == FleetOrder.Escort && (target.Position - escort.Position).Length < 2000
                        && !(escort.HealthFraction < 0.25 && (escort.Position - flagship.Position).Length > 500)));
                Command command = Order == FleetOrder.Hold
                    ? FleetOrders.For(FleetOrder.Hold, escort, flagship)
                    : pursue ? ShipAi.Attack(escort, target)
                    : here ? FleetOrders.For(Order, escort, flagship)
                    : FleetOrders.MoveTo(escort, Point.Zero, Point.Zero, 40, 0.1);
                escort.Step(command);
                if (here && target != null) shots.AddRange(ShipAi.AutoFire(escort, target));
            }
            return shots;
        }

        private StarSystem? EscortRoute(Ship escort, StarSystem goal, GameData data)
        {
            StarSystem from = escort.CurrentSystem!;
            if (_escortRoutes.TryGetValue(escort, out var route) && ReferenceEquals(route.From, from)
                && ReferenceEquals(route.Goal, goal) && escort.CanReach(route.Next)) return route.Next;
            StarSystem? next = NextJump(escort, from, goal, data);
            if (next != null) _escortRoutes[escort] = (from, goal, next);
            else _escortRoutes.Remove(escort);
            return next;
        }

        private static StarSystem? NextJump(Ship ship, StarSystem from, StarSystem goal, GameData data)
        {
            if (ship.CanReach(goal)) return goal;
            if (!ship.HasHyperdrive && !ship.HasJumpDrive) return null;
            var seen = new HashSet<StarSystem> { from };
            var queue = new Queue<(StarSystem System, StarSystem? First)>();
            queue.Enqueue((from, null));
            while (queue.TryDequeue(out var current))
            {
                IEnumerable<StarSystem> neighbors = ship.HasJumpDrive ? data.Systems.Values
                    : current.System.Links.Where(data.Systems.ContainsKey).Select(name => data.Systems[name]);
                foreach (StarSystem next in neighbors)
                {
                    if (seen.Contains(next) || !ship.CanReach(current.System, next)) continue;
                    seen.Add(next);
                    StarSystem first = current.First ?? next;
                    if (ReferenceEquals(next, goal)) return first;
                    queue.Enqueue((next, first));
                }
            }
            return null;
        }

        internal void LaunchEscorts(StarSystem? system, Planet? planet)
        {
            IssueOrder(FleetOrder.Escort); // Engine::Place clears flight orders at takeoff.
            _escortRoutes.Clear();
            StellarObject? port = system?.AllObjects().FirstOrDefault(o => ReferenceEquals(o.Planet, planet));
            if (port == null) return;
            foreach (Ship escort in Escorts.Where(s => !s.IsDisabled && ReferenceEquals(s.CurrentSystem, system)))
            {
                // Engine::Place launches each local hull from a random point on the
                // port's disk, facing outward at one simulation unit per frame.
                var angle = new Angle(Random.Shared.NextDouble() * 360);
                escort.Position = port.Position + angle.Unit() * Random.Shared.NextDouble() * port.LandingRadius;
                escort.Facing = angle;
                escort.Velocity = angle.Unit();
            }
        }
    }
}
