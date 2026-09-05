using System;
using System.Linq;

namespace EndlessSky.Sim
{
    public partial class PlayerFleet
    {
        public bool CanLoadMissionCargo(int tons, StarSystem? system) =>
            system != null && tons >= 0 && Ordered(system).Any()
            && CargoFree(system) >= tons;

        /// <summary>Distributes freight among local holds, retaining its mission identity.</summary>
        public int LoadMissionCargo(Guid mission, int tons, StarSystem? system)
        {
            if (mission == Guid.Empty || !CanLoadMissionCargo(tons, system))
                return 0;
            if (HasPortCargoHere(system))
            {
                RefreshPortCapacity();
                return (int)PortCargo!.AddMissionCargo(mission, tons);
            }
            int remaining = tons;
            foreach (Ship ship in Ordered(system))
            {
                remaining -= ship.LoadMissionCargo(mission, remaining);
                if (remaining == 0) break;
            }
            return tons - remaining;
        }

        /// <summary>All of this job's freight must survive, wherever its carriers are.</summary>
        public bool HasMissionCargo(Guid mission, int tons)
        {
            var carriers = _ships.Where(s => s.Cargo.MissionCargo.ContainsKey(mission)).ToArray();
            return tons >= 0 && (carriers.Length > 0 || PortCargo?.MissionCargo.ContainsKey(mission) == true)
                && carriers.All(s => !s.IsDestroyed)
                && AllCargo.Sum(h => h.MissionCargo.TryGetValue(mission, out long amount) ? amount : 0) >= tons;
        }

        /// <summary>All of this job's freight must still exist in the delivery system.</summary>
        public bool CanDeliverMissionCargo(Guid mission, int tons, StarSystem? system) =>
            system != null && HasMissionCargo(mission, tons)
            && (PortCargo?.MissionCargo.ContainsKey(mission) != true || ReferenceEquals(PortSystem, system))
            && _ships.All(s => !s.Cargo.MissionCargo.ContainsKey(mission)
                || ReferenceEquals(s.CurrentSystem, system));

        public void RemoveMissionCargo(Guid mission)
        {
            // Ended jobs must also release freight on parked or remote ships.
            foreach (Ship ship in _ships) ship.RemoveMissionCargo(mission);
            PortCargo?.RemoveMissionCargo(mission);
        }

        /// <summary>
        /// Old port saves mixed freight into commodities. Reserve only cargo that
        /// actually survived, on the same ships; never recreate missing goods.
        /// </summary>
        internal void ReserveLegacyMissionCargo(Guid mission, string commodity, int tons)
        {
            if (tons == 0)
            {
                if (PortCargo != null) PortCargo.AddMissionCargo(mission, 0);
                else Flagship?.LoadMissionCargo(mission, 0);
                return;
            }
            int remaining = tons;
            if (PortCargo != null) remaining -= (int)PortCargo.ReserveMissionCargo(mission, commodity, remaining);
            foreach (Ship ship in _ships)
            {
                int moved = (int)ship.Cargo.ReserveMissionCargo(mission, commodity, remaining);
                remaining -= moved;
                if (remaining == 0) break;
            }
        }
    }
}
