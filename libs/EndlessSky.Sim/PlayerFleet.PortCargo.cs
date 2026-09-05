using System;
using System.Collections.Generic;
using System.Linq;

namespace EndlessSky.Sim
{
    public partial class PlayerFleet
    {
        /// <summary>Cargo ashore at the current port, possibly exceeding the remaining fleet's capacity.</summary>
        public CargoHold? PortCargo { get; private set; }
        public StarSystem? PortSystem { get; private set; }

        public IEnumerable<CargoHold> AllCargo => _ships.Select(s => s.Cargo)
            .Concat(PortCargo == null ? Array.Empty<CargoHold>() : new[] { PortCargo });

        private bool HasPortCargoHere(StarSystem? system) =>
            PortCargo != null && (system == null || ReferenceEquals(system, PortSystem));

        private IEnumerable<CargoHold> CargoHolds(StarSystem? system = null)
        {
            if (HasPortCargoHere(system)) yield return PortCargo!;
            foreach (Ship ship in Ordered(system)) yield return ship.Cargo;
        }

        private void RefreshPortCapacity() => PortCargo?.SetCapacity(Math.Max(0,
            Ordered(PortSystem).Sum(s => s.Cargo.Capacity - s.Cargo.Used)));

        internal void PoolCargo(StarSystem? system)
        {
            if (system == null) return;
            PortSystem = system;
            PortCargo ??= new CargoHold();
            // Per-ship planets and landing clearance are not yet modeled: local
            // active ships stand in for ships landed alongside the flagship.
            // Unload everything before restoring capacity. A ship sale or outfit
            // change may have left more ashore than the fleet can currently carry.
            PortCargo.SetCapacity(long.MaxValue);
            foreach (Ship ship in Ordered(system)) ship.UnloadInto(PortCargo);
            RefreshPortCapacity();
        }

        internal CargoHold? DistributeCargo()
        {
            if (PortCargo == null) return null;
            foreach (Ship ship in Ordered(PortSystem).Where(s => !s.IsDisabled))
                ship.LoadFrom(PortCargo);
            return PortCargo;
        }

        internal void RestoreCargo(CargoHold source)
        {
            if (PortCargo != null)
            {
                PortCargo.SetCapacity(long.MaxValue);
                source.TransferAll(PortCargo);
                RefreshPortCapacity();
            }
            else foreach (Ship ship in Ordered()) ship.LoadFrom(source);
        }

        internal bool LeavePort()
        {
            if (PortCargo?.IsEmpty == false) return false;
            PortCargo = null;
            PortSystem = null;
            return true;
        }
    }
}
