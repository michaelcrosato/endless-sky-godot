using System;

namespace EndlessSky.Sim
{
    /// <summary>
    /// What a ship is worth and what it can carry. Port of upstream
    /// <c>Ship::Cost</c> / <c>Ship::ChassisCost</c> and the cargo capacity rules.
    /// </summary>
    /// <remarks>
    /// Cost follows the same data-driven pattern as capacity: an outfit's price is
    /// just its <c>cost</c> attribute, and installing it adds that to the ship's
    /// attribute total. So a fitted ship's value needs no bookkeeping - it is the
    /// summed attribute, and it stays correct automatically as outfits come and go.
    ///
    /// INCOMPLETE, tracked rather than dropped: depreciation (upstream tracks it per
    /// ship and per outfit, and it is what makes a used hull cheaper than a new one),
    /// licence costs, and the crew-salary side of ownership.
    /// </remarks>
    public partial class Ship
    {
        private CargoHold? _cargo;

        /// <summary>
        /// The ship's hold. Capacity tracks the <c>cargo space</c> attribute, so
        /// installing an expander or a cargo-eating outfit resizes it.
        /// </summary>
        public CargoHold Cargo
        {
            get
            {
                _cargo ??= new CargoHold((int)Attributes.Get("cargo space"));
                SyncCargoCapacity();
                return _cargo;
            }
        }

        /// <summary>
        /// Re-reads capacity from attributes and republishes the load as mass.
        /// Cargo counts toward mass, so a loaded freighter genuinely handles worse.
        /// </summary>
        private void SyncCargoCapacity()
        {
            if (_cargo is null)
                return;

            _cargo.SetCapacity((int)Attributes.Get("cargo space"));
            CargoMass = _cargo.Used;
        }

        /// <summary>Total value: hull plus everything installed.</summary>
        public long Cost => (long)Attributes.Get("cost");

        /// <summary>Value of the bare hull, with no outfits.</summary>
        public long ChassisCost => (long)Definition.Attributes.Get("cost");

        /// <summary>Value of the installed outfits alone.</summary>
        public long OutfitCost => Cost - ChassisCost;

        /// <summary>Crew required to fly the ship at all.</summary>
        public int RequiredCrew => (int)Attributes.Get("required crew");

        private int? _crew;

        /// <summary>
        /// Crew actually aboard. Defaults to the required minimum; extra crew matter
        /// for boarding actions and, on the flagship only, for salaries.
        /// </summary>
        public int Crew
        {
            get => _crew ??= RequiredCrew;
            set => _crew = Math.Max(0, Math.Min(value, Math.Max(Bunks, RequiredCrew)));
        }

        /// <summary>
        /// A parked ship stays on the ground: it flies nowhere and costs no salaries.
        /// </summary>
        public bool IsParked { get; set; }

        /// <summary>Total berths, which bounds crew plus passengers.</summary>
        public int Bunks => (int)Attributes.Get("bunks");

        /// <summary>
        /// Whether the ship can be flown as configured. Upstream refuses to launch a
        /// ship missing thrust, steering or the crew to operate it.
        /// </summary>
        public bool IsFlyable =>
            Thrust > 0.0 && TurnTorque > 0.0 && Bunks >= RequiredCrew;

        /// <summary>
        /// Loads cargo, limited by free space, and updates mass. Returns tons loaded.
        /// </summary>
        public int LoadCargo(string commodity, int tons)
        {
            int loaded = Cargo.Add(commodity, tons);
            SyncCargoCapacity();
            return loaded;
        }

        /// <summary>Unloads cargo and updates mass. Returns tons removed.</summary>
        public int UnloadCargo(string commodity, int tons)
        {
            int removed = Cargo.Remove(commodity, tons);
            SyncCargoCapacity();
            return removed;
        }
    }
}
