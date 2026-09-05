using System;
using System.Collections.Generic;
using System.Linq;

namespace EndlessSky.Sim
{
    /// <summary>The cargo consequences that must be acknowledged before leaving a port.</summary>
    public sealed class CargoDeparture
    {
        public string? Refusal { get; internal set; }
        public bool CanDepart => Refusal == null;
        public IReadOnlyDictionary<string, long> CommoditiesToSell { get; internal set; } = new Dictionary<string, long>();
        public IReadOnlyList<Guid> MissionsToAbort { get; internal set; } = Array.Empty<Guid>();
        public long Income { get; internal set; }
        public long Profit { get; internal set; }
        public bool NeedsConfirmation => CommoditiesToSell.Count > 0 || MissionsToAbort.Count > 0;
    }

    public partial class PlayerState
    {
        /// <summary>Tries cargo distribution, then puts everything back ashore for review.</summary>
        public CargoDeparture PreviewTakeOff(MissionLog? missions = null)
        {
            var result = new CargoDeparture();
            if (CurrentSystem == null || CurrentPlanet == null || Flagship == null
                || Flagship.IsParked || Flagship.IsDisabled
                || !ReferenceEquals(Flagship.CurrentSystem, CurrentSystem))
            {
                result.Refusal = "select an active ship here before departing";
                return result;
            }
            Fleet.PoolCargo(CurrentSystem);
            CargoHold excess = Fleet.DistributeCargo()!;
            result.CommoditiesToSell = new Dictionary<string, long>(excess.Commodities, StringComparer.Ordinal);
            result.MissionsToAbort = excess.MissionCargo.Where(e => e.Value > 0).Select(e => e.Key).ToArray();
            Int128 income = 0, cost = 0;
            foreach (var entry in result.CommoditiesToSell)
            {
                income += (Int128)entry.Value * (_data?.Trade.Price(CurrentSystem.Name, entry.Key) ?? 0);
                cost += GetBasis(entry.Key, entry.Value);
            }
            Fleet.PoolCargo(CurrentSystem);
            if (income > long.MaxValue || income < long.MinValue
                || income + Credits > long.MaxValue || income + Credits < long.MinValue
                || income - cost > long.MaxValue || income - cost < long.MinValue)
                result.Refusal = "credit balance limit reached";
            else
            {
                result.Income = (long)income;
                result.Profit = (long)(income - cost);
            }
            if (result.MissionsToAbort.Count > 0 && (missions == null
                || result.MissionsToAbort.Any(id => !missions.Active.Any(m => m.Id == id))))
                result.Refusal = "mission freight must fit before departing";
            return result;
        }

        private void ResolveExcessCargo(CargoDeparture departure, MissionLog? missions)
        {
            // PlayerInfo::TakeOff aborts jobs with freight left ashore before selling
            // excess commodities. It does not refill the space those aborts release.
            foreach (Guid id in departure.MissionsToAbort)
                missions!.Abort(missions.Active.First(m => m.Id == id));
            foreach (var entry in departure.CommoditiesToSell)
            {
                long basis = GetBasis(entry.Key, entry.Value);
                Fleet.PortCargo!.Remove(entry.Key, entry.Value);
                RemoveBasis(entry.Key, basis);
            }
            AddCredits(departure.Income);
        }
    }
}
