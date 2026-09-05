using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Fleet ownership, salaries and cargo distribution, against upstream
    /// PlayerInfo::Salaries and the fleet cargo rules. Engine-free.
    /// </summary>
    [TestFixture]
    public class FleetTests
    {
        private static Ship MakeShip(string name, int requiredCrew = 1, int bunks = 3,
                                     int cargoSpace = 0, long cost = 100000)
        {
            var lines = new List<string>
            {
                "ship \"" + name + "\"",
                "\tattributes",
                "\t\t\"cost\" " + cost.ToString(CultureInfo.InvariantCulture),
                "\t\t\"mass\" 100",
                "\t\t\"thrust\" 20",
                "\t\t\"turn\" 400",
                "\t\t\"required crew\" " + requiredCrew.ToString(CultureInfo.InvariantCulture),
                "\t\t\"bunks\" " + bunks.ToString(CultureInfo.InvariantCulture),
                "\t\t\"cargo space\" " + cargoSpace.ToString(CultureInfo.InvariantCulture),
            };

            var definition = new ShipDefinition(name);
            definition.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);
            return new Ship(definition);
        }

        // --- Roster ---------------------------------------------------------------

        [Test]
        public void AcquiringAShipAssignsTheSamePlayerFactionAsTheRestOfTheFleet()
        {
            var fleet = new PlayerFleet();
            Ship first = MakeShip("Shuttle");
            Ship second = MakeShip("Star Barge");
            var seller = new Government("Merchant");
            second.Government = seller;
            fleet.Add(first);
            fleet.Add(second);
            Assert.IsTrue(first.Government?.IsPlayer);
            Assert.AreSame(first.Government, second.Government);
            Assert.IsFalse(seller.IsPlayer, "ownership does not change the seller's faction");
        }

        [Test]
        public void TheFirstShipAddedBecomesTheFlagship()
        {
            var fleet = new PlayerFleet();
            Ship first = MakeShip("Shuttle");
            Ship second = MakeShip("Star Barge");

            fleet.Add(first);
            fleet.Add(second);

            Assert.AreSame(first, fleet.Flagship);
            Assert.AreEqual(new[] { second }, fleet.Escorts.ToArray());
        }

        [Test]
        public void RemovingTheFlagshipPromotesAnotherFlyableShip()
        {
            var fleet = new PlayerFleet();
            Ship first = MakeShip("Shuttle");
            Ship second = MakeShip("Star Barge");
            fleet.Add(first);
            fleet.Add(second);

            fleet.Remove(first);

            Assert.AreSame(second, fleet.Flagship);
        }

        [Test]
        public void ParkedAndDestroyedShipsAreNotActive()
        {
            var fleet = new PlayerFleet();
            Ship flagship = MakeShip("Shuttle");
            Ship parked = MakeShip("Parked");
            Ship wreck = MakeShip("Wreck");
            parked.IsParked = true;
            wreck.SetLevels(hull: -1.0);

            fleet.Add(flagship);
            fleet.Add(parked);
            fleet.Add(wreck);

            Assert.AreEqual(new[] { flagship }, fleet.ActiveShips.ToArray());
        }

        // --- Salaries -------------------------------------------------------------

        [Test]
        public void ASoloCaptainPaysNothing()
        {
            // 100 * (crew - 1), and the player is that one crew member.
            var fleet = new PlayerFleet();
            fleet.Add(MakeShip("Shuttle", requiredCrew: 1));

            Assert.AreEqual(0L, fleet.DailySalaries());
        }

        [Test]
        public void SalariesAreOneHundredPerCrewExcludingThePlayer()
        {
            var fleet = new PlayerFleet();
            fleet.Add(MakeShip("Freighter", requiredCrew: 5, bunks: 10));

            // 5 required crew, minus the player = 4 paid.
            Assert.AreEqual(400L, fleet.DailySalaries());
        }

        [Test]
        public void ExtraCrewAreOnlyPaidOnTheFlagship()
        {
            // The trap: an escort with spare berths does NOT cost more when crewed up.
            // Only the flagship's crew above its requirement is counted.
            var fleet = new PlayerFleet();
            Ship flagship = MakeShip("Flagship", requiredCrew: 2, bunks: 10);
            Ship escort = MakeShip("Escort", requiredCrew: 2, bunks: 10);
            fleet.Add(flagship);
            fleet.Add(escort);

            long baseline = fleet.DailySalaries();      // 2 + 2 = 4 crew, minus player = 300

            escort.Crew = 10;
            Assert.AreEqual(baseline, fleet.DailySalaries(),
                "extra crew on an escort costs nothing");

            flagship.Crew = 6;                          // 4 extra on the flagship
            Assert.AreEqual(baseline + 400L, fleet.DailySalaries(),
                "extra crew on the flagship are paid");
        }

        [Test]
        public void ParkedShipsCostNoSalaries()
        {
            var fleet = new PlayerFleet();
            fleet.Add(MakeShip("Flagship", requiredCrew: 1));
            Ship escort = MakeShip("Escort", requiredCrew: 5, bunks: 10);
            fleet.Add(escort);

            Assert.AreEqual(500L, fleet.DailySalaries());

            escort.IsParked = true;
            Assert.AreEqual(0L, fleet.DailySalaries(), "a parked ship requires no crew");
        }

        [Test]
        public void DestroyedShipsCostNoSalaries()
        {
            var fleet = new PlayerFleet();
            fleet.Add(MakeShip("Flagship", requiredCrew: 1));
            Ship escort = MakeShip("Escort", requiredCrew: 5, bunks: 10);
            fleet.Add(escort);

            escort.SetLevels(hull: -1.0);
            Assert.IsTrue(escort.IsDestroyed);
            Assert.AreEqual(0L, fleet.DailySalaries());
        }

        [Test]
        public void FleetValueSumsEveryOwnedShipIncludingParkedOnes()
        {
            var fleet = new PlayerFleet();
            fleet.Add(MakeShip("A", cost: 190000));
            Ship parked = MakeShip("B", cost: 60000);
            parked.IsParked = true;
            fleet.Add(parked);

            Assert.AreEqual(250000L, fleet.FleetValue(),
                "a parked ship is still an asset even though it costs no salary");
        }

        // --- Cargo ----------------------------------------------------------------

        [Test]
        public void CargoCapacityIsTheWholeFlyingFleets()
        {
            var fleet = new PlayerFleet();
            fleet.Add(MakeShip("Flagship", cargoSpace: 20));
            fleet.Add(MakeShip("Hauler", cargoSpace: 80));

            Assert.AreEqual(100, fleet.CargoCapacity());
        }

        [Test]
        public void CargoSpillsFromTheFlagshipIntoTheEscorts()
        {
            // Upstream spreads a run across the fleet rather than making the player
            // place tonnage by hand, so capacity is the fleet's, not the flagship's.
            var fleet = new PlayerFleet();
            Ship flagship = MakeShip("Flagship", cargoSpace: 20);
            Ship hauler = MakeShip("Hauler", cargoSpace: 80);
            fleet.Add(flagship);
            fleet.Add(hauler);

            Assert.AreEqual(70, fleet.LoadCargo("Food", 70));

            Assert.AreEqual(20, flagship.Cargo.Used, "the flagship fills first");
            Assert.AreEqual(50, hauler.Cargo.Used);
            Assert.AreEqual(70, fleet.CargoCount("Food"));
        }

        [Test]
        public void LoadingStopsWhenTheFleetIsFull()
        {
            var fleet = new PlayerFleet();
            fleet.Add(MakeShip("Flagship", cargoSpace: 10));
            fleet.Add(MakeShip("Escort", cargoSpace: 10));

            Assert.AreEqual(20, fleet.LoadCargo("Metal", 500));
            Assert.AreEqual(0, fleet.CargoFree());
        }

        [Test]
        public void ParkedShipsDoNotContributeHoldSpace()
        {
            var fleet = new PlayerFleet();
            fleet.Add(MakeShip("Flagship", cargoSpace: 10));
            Ship parked = MakeShip("Parked", cargoSpace: 500);
            parked.IsParked = true;
            fleet.Add(parked);

            Assert.AreEqual(10, fleet.CargoCapacity(),
                "a ship left on the ground cannot carry the run");
            Assert.AreEqual(10, fleet.LoadCargo("Food", 100));
        }

        [Test]
        public void UnloadingDrawsFromAcrossTheFleet()
        {
            var fleet = new PlayerFleet();
            fleet.Add(MakeShip("Flagship", cargoSpace: 20));
            fleet.Add(MakeShip("Hauler", cargoSpace: 80));
            fleet.LoadCargo("Food", 70);

            Assert.AreEqual(70, fleet.UnloadCargo("Food", 999));
            Assert.AreEqual(0, fleet.CargoUsed());
        }

        [Test]
        public void FleetCargoIsValuedAtTheLocalMarket()
        {
            var trade = new TradeData();
            trade.SetPrice("Beta", "Food", 550);

            var fleet = new PlayerFleet();
            fleet.Add(MakeShip("Flagship", cargoSpace: 20));
            fleet.Add(MakeShip("Hauler", cargoSpace: 80));
            fleet.LoadCargo("Food", 70);

            Assert.AreEqual(70L * 550L, fleet.CargoValueAt(trade, "Beta"));
        }

        [Test]
        public void LoadedFleetCargoStillCountsTowardEachShipsMass()
        {
            var fleet = new PlayerFleet();
            Ship flagship = MakeShip("Flagship", cargoSpace: 50);
            fleet.Add(flagship);

            double empty = flagship.Mass;
            fleet.LoadCargo("Metal", 30);

            Assert.AreEqual(empty + 30.0, flagship.Mass, 1e-9);
        }
    }
}
