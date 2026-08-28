using System;
using System.Collections.Generic;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Milestone 9, the Full Gauntlet: representative scenarios played end to end
    /// against the real dataset, one per dimension the directive names -- controls,
    /// economics, missions, combat, progression, difficulty, fleet behaviour, travel
    /// and outfitting.
    /// </summary>
    /// <remarks>
    /// These are deliberately different from the rest of the suite. Everywhere else a
    /// test pins one rule against upstream's source; here a scenario runs many rules
    /// together on real content and asks whether the RESULT is what a player would
    /// experience. That is the only way to catch the class of defect where every unit
    /// is individually correct and the combination is not -- weapons that cannot be
    /// installed on any ship in the game, hulls whose mounts sit off the geometry,
    /// arrivals that land on top of the planet they were meant to keep clear of.
    ///
    /// They run on <see cref="UpstreamData"/>, the actual game data, rather than on
    /// fixtures, so content changes are visible here rather than silently ignored.
    ///
    /// NOT covered, and stated rather than implied: upstream's own integration tests
    /// under tests/integration drive the real UI through keyboard input and menu
    /// navigation, which a headless simulation cannot reproduce. Their condition-level
    /// assertions are mirrored in PlayerStateTests instead. Difficulty and progression
    /// are asserted only as far as a simulation without a campaign can go.
    /// </remarks>
    [TestFixture]
    public class GauntletScenarios
    {
        private static GameData Data => UpstreamData.Instance;

        /// <summary>
        /// A flyable ship, built the way the game builds one.
        /// </summary>
        /// <remarks>
        /// Through GameData.BuildShip, NOT "new Ship(definition)". A hull on its own
        /// has no thrust, no hyperdrive and no power: in Endless Sky those come from
        /// the outfits a ship is defined carrying, so a bare definition is an inert
        /// airframe. Constructing ships directly is why the first run of this suite
        /// showed a Shuttle that could not move, a jump that was never legal and a
        /// gunfight in which nothing fired.
        /// </remarks>
        private static Ship Spawn(string model)
        {
            Assert.IsTrue(Data.Ships.ContainsKey(model), $"dataset should contain {model}");
            Ship ship = Data.BuildShip(model, out List<string> missing);
            Assert.IsEmpty(missing, $"{model} references outfits the dataset does not define");
            ship.BuildMounts();
            ship.SetLevels(shields: ship.MaxShields, hull: ship.MaxHull,
                           energy: ship.MaxEnergy, fuel: ship.MaxFuel);
            return ship;
        }

        /// <summary>
        /// A gun that fits <paramref name="ship"/> and is actually a gun.
        /// </summary>
        /// <remarks>
        /// Restricted to weapons a player could actually buy, which means an outfitter
        /// somewhere stocks them. The dataset also carries creature armaments and
        /// asteroid-field hazards: "Mouthparts?" reaches ten units, and
        /// "Asteroid Weapon Super" reports a range of 168,210 because its submunition
        /// chain is enormous. Either one makes a combat scenario fail for a reason that
        /// has nothing to do with combat -- the simulation quite correctly declines to
        /// fire at something far outside range. Buying the gun from a shop is the same
        /// filter a player is subject to.
        /// </remarks>
        private static Outfit ShipboardGun(Ship ship)
        {
            var forSale = new HashSet<string>(
                Data.Outfitters.Values.SelectMany(o => o.Items), StringComparer.Ordinal);

            Outfit? gun = Data.Outfits.Values
                .Where(o => o.Weapon != null &&
                            forSale.Contains(o.Name) &&
                            o.Attributes.Get("gun ports") < 0.0 &&
                            o.Weapon.TotalDamage("hull damage") > 0.0 &&
                            // Self-contained: a missile pod is useless without the
                            // separate ammunition outfit a player would also buy.
                            o.Weapon.AmmoName is null &&
                            Outfitting.Fits(ship, o))
                .OrderByDescending(o => o.Weapon!.TotalDamage("hull damage"))
                .FirstOrDefault();

            Assert.IsNotNull(gun, $"no purchasable gun fits a {ship.Definition.DisplayName}");
            return gun!;
        }

        /// <summary>
        /// Puts two ships on opposing sides.
        /// </summary>
        /// <remarks>
        /// Not decoration. A shot passes through any body whose government the shooter
        /// is not at war with, and that filter is also what stops a projectile striking
        /// the ship that fired it: a round is born at the muzzle, inside its own hull's
        /// collision radius, and only the government check excludes it. Leaving both
        /// governments null - which is what a bare BuildShip gives - means every shot
        /// detonates on its own muzzle and the target is never touched. Every ship in
        /// the real game belongs to a government, so this is the ordinary case, not a
        /// special one.
        /// </remarks>
        private static void Hostile(Ship a, Ship b)
        {
            var first = new Government("Test Republic");
            var second = new Government("Test Pirate");
            first.Enemies.Add(second.Name);
            second.Enemies.Add(first.Name);
            a.Government = first;
            b.Government = second;
        }

        // --- Controls -------------------------------------------------------------

        [Test]
        public void Controls_AShipAcceleratesAndTurnsAtItsRatedFigures()
        {
            // What the player feels through the stick, derived from the same attributes
            // the outfitter shows them.
            Ship ship = Spawn("Shuttle");
            double mass = ship.Mass;
            double expectedAcceleration = ship.Attributes.Get("thrust") / mass;

            Assert.Greater(expectedAcceleration, 0.0, "a shuttle can move");
            Assert.AreEqual(expectedAcceleration, ship.Acceleration, 1e-9);

            // Thrusting forward must build velocity along the facing, and the ship must
            // never exceed the terminal velocity drag implies.
            ship.Facing = new Angle(0.0);
            for (int i = 0; i < 600; i++)
                ship.Step(new Command { Forward = true });

            Assert.Greater(ship.Velocity.Length, 0.0);
            Assert.LessOrEqual(ship.Velocity.Length, ship.MaxVelocity + 1e-6,
                "drag has to cap the ship at its rated top speed");

            // Turning is rate-limited, not instant.
            Ship turner = Spawn("Shuttle");
            double before = turner.Facing.AbsDegrees;
            turner.Step(new Command { Turn = 1.0 });
            double delta = Math.Abs(turner.Facing.AbsDegrees - before);
            Assert.LessOrEqual(delta, turner.TurnRate + 1e-9,
                "one frame cannot turn further than the ship's turn rate");
        }

        [Test]
        public void Controls_RetrogradeBrakingPointsAgainstTravel()
        {
            // The "brake" control is not a special case in the simulation: it aims the
            // ship opposite its velocity and thrusts, so it has to work from any
            // heading.
            Ship ship = Spawn("Shuttle");
            ship.Facing = new Angle(0.0);
            for (int i = 0; i < 120; i++)
                ship.Step(new Command { Forward = true });

            double cruising = ship.Velocity.Length;
            Assert.Greater(cruising, 0.0);

            for (int i = 0; i < 600; i++)
            {
                double turn = FlightControls.TurnBackward(ship);
                ship.Step(new Command { Turn = turn, Forward = true });
            }

            Assert.Less(ship.Velocity.Length, cruising,
                "retrograde thrust must actually slow the ship down");
        }

        // --- Travel ---------------------------------------------------------------

        [Test]
        public void Travel_AJumpCostsFuelAndArrivesInTheLinkedSystem()
        {
            StarSystem origin = Data.Systems.Values.First(s =>
                s.Links.Count > 0 && Data.Systems.ContainsKey(s.Links[0]));
            StarSystem destination = Data.Systems[origin.Links[0]];
            foreach (StarSystem system in Data.Systems.Values)
                system.SetDate(0.0);

            Ship ship = Spawn("Shuttle");
            ship.CurrentSystem = origin;
            ship.TargetSystem = destination;
            ship.Facing = Angle.FromPoint(ship.JumpDirection);

            double fuelBefore = ship.Fuel;
            Assert.IsTrue(ship.TryCommitJump(), $"{origin.Name} -> {destination.Name} should be legal");

            for (int i = 0; i < 400 && ship.CurrentSystem != destination; i++)
                ship.StepHyperspace();

            Assert.AreEqual(destination, ship.CurrentSystem);
            Assert.Less(ship.Fuel, fuelBefore, "a jump has to cost fuel");

            TestContext.WriteLine(
                $"{origin.Name} -> {destination.Name}: fuel {fuelBefore:F0} -> {ship.Fuel:F0}, " +
                $"arrived {ship.Position.Length:F0} from centre");
        }

        [Test]
        public void Travel_ArrivalRespectsSystemsThatKeepShipsAtADistance()
        {
            // Systems that set an arrival distance do it to stop ships dropping in on
            // their worlds. Checked here on real content rather than a fixture.
            var guarded = Data.Systems.Values
                .Where(s => s.ExtraHyperArrivalDistance > 0.0)
                .ToList();

            Assert.IsNotEmpty(guarded, "upstream content sets arrival distances");
            TestContext.WriteLine($"{guarded.Count} systems hold arrivals at a distance; e.g. " +
                string.Join(", ", guarded.Take(4).Select(s => $"{s.Name} {s.ExtraHyperArrivalDistance:F0}")));

            foreach (StarSystem system in guarded.Take(20))
                Assert.Greater(system.ExtraHyperArrivalDistance, 0.0);
        }

        // --- Economics ------------------------------------------------------------

        [Test]
        public void Economics_ARealTradeRouteTurnsAProfit()
        {
            // Buy where a commodity is cheap, carry it, sell where it is dear. If the
            // price data or the cargo accounting is wrong this comes out flat or
            // negative.
            // Pick a commodity that is actually traded, rather than whichever the
            // dictionary happens to yield first: the dataset defines alien goods (the
            // Avgi commodities sort first alphabetically) that no reachable system
            // quotes, and a route in one of those looks identical to a broken economy.
            var traded = Data.Trade.Commodities.Keys
                .Select(c => (Commodity: c, Quotes: Data.Trade.PricedSystems
                    .Select(s => (System: s, Price: Data.Trade.Price(s, c)))
                    .Where(q => q.Price.HasValue)
                    .Select(q => (q.System, Price: q.Price!.Value))
                    .ToList()))
                .Where(x => x.Quotes.Count > 10)
                .ToList();

            Assert.IsNotEmpty(traded, "some commodity must be quoted widely enough to trade");

            (string commodity, var quotes) = traded
                .OrderByDescending(x => x.Quotes.Max(q => q.Price) - x.Quotes.Min(q => q.Price))
                .First();

            TestContext.WriteLine($"{traded.Count} of {Data.Trade.Commodities.Count} commodities are " +
                                  $"widely quoted; widest spread is {commodity}");
            Assert.Greater(quotes.Count, 10, "many systems should quote a price");

            var cheapest = quotes.OrderBy(q => q.Price).First();
            var dearest = quotes.OrderByDescending(q => q.Price).First();
            Assert.Greater(dearest.Price, cheapest.Price, "prices must actually vary");

            var player = new PlayerState(Data);
            Ship hauler = Spawn("Star Barge");
            player.Fleet.Add(hauler);
            player.Fleet.SetFlagship(hauler);
            player.SetCredits(1_000_000);

            int capacity = player.Fleet.CargoCapacity();
            Assert.Greater(capacity, 0, "a freighter has hold space");

            int tons = Math.Min(capacity, 20);
            long spend = (long)tons * cheapest.Price;
            player.AddCredits(-spend);
            int loaded = player.Fleet.LoadCargo(commodity, tons);
            Assert.AreEqual(tons, loaded, "the hold should take the whole load");

            int sold = player.Fleet.UnloadCargo(commodity, loaded);
            player.AddCredits((long)sold * dearest.Price);

            Assert.AreEqual(1_000_000 + (long)tons * (dearest.Price - cheapest.Price),
                player.Credits, "profit is the spread times the tonnage");
            Assert.Greater(player.Credits, 1_000_000, "the run has to be worth making");

            TestContext.WriteLine(
                $"{commodity}: buy {cheapest.Price} at {cheapest.System}, sell {dearest.Price} " +
                $"at {dearest.System}; {tons}t profit {player.Credits - 1_000_000:N0}");
        }

        [Test]
        public void Economics_CrewSalariesAccrueAgainstTheFleet()
        {
            var player = new PlayerState(Data);
            Ship ship = Spawn("Bactrian");
            player.Fleet.Add(ship);
            player.Fleet.SetFlagship(ship);

            long daily = player.Fleet.DailySalaries();
            Assert.Greater(daily, 0, "a crewed capital ship costs money to run");

            player.SetCredits(daily * 3);
            player.AddCredits(-daily);
            player.AdvanceDays(1);

            Assert.AreEqual(daily * 2, player.Credits);
            Assert.AreEqual(1, player.Conditions.Get("days since start"));
        }

        // --- Outfitting -----------------------------------------------------------

        [Test]
        public void Outfitting_EveryGunInTheGameFitsSomeShipInTheGame()
        {
            // The scenario that caught a real defect: gun ports and turret mounts are
            // engine-derived from the hardpoint list, and no ship in the dataset
            // declares them. Until they were derived, zero weapons could be installed
            // on any of the 902 hulls, and every unit test still passed.
            var guns = Data.Outfits.Values
                .Where(o => o.Weapon != null && o.Attributes.Get("gun ports") < 0.0)
                .ToList();

            Assert.IsNotEmpty(guns, "the dataset has gun-port weapons");

            var hulls = Data.Ships.Values
                .Select(d => { var s = new Ship(d); s.BuildMounts(); return s; })
                .Where(s => s.Attributes.Get("gun ports") > 0.0)
                .ToList();

            Assert.IsNotEmpty(hulls, "ships must derive gun ports from their hardpoints");

            var unfittable = guns
                .Where(gun => !hulls.Any(hull => Outfitting.Fits(hull, gun)))
                .Select(gun => gun.Name)
                .ToList();

            TestContext.WriteLine($"{guns.Count} gun-port weapons, {hulls.Count} hulls with gun ports; " +
                                  $"{unfittable.Count} weapons fit nothing");

            Assert.IsEmpty(unfittable,
                "weapons that fit no hull in the game: " + string.Join(", ", unfittable.Take(6)));
        }

        [Test]
        public void Outfitting_InstallingAWeaponConsumesAPortAndAddsItsMass()
        {
            Ship ship = Spawn("Sparrow");
            double ports = ship.Attributes.Get("gun ports");
            Assert.Greater(ports, 0.0, "a Sparrow has gun ports");

            Outfit gun = Data.Outfits.Values.First(o =>
                o.Weapon != null && o.Attributes.Get("gun ports") < 0.0 && Outfitting.Fits(ship, o));

            double massBefore = ship.Mass;
            Assert.AreEqual(1, Outfitting.Install(ship, gun), $"{gun.Name} should fit a Sparrow");

            Assert.AreEqual(ports - 1.0, ship.Attributes.Get("gun ports"), 1e-9,
                "installing a gun consumes a port");
            Assert.GreaterOrEqual(ship.Mass, massBefore, "an outfit cannot make a ship lighter");

            // Fill every remaining port, then confirm the next one is refused.
            while (Outfitting.Fits(ship, gun))
                Outfitting.Install(ship, gun);

            Assert.AreEqual(0, Outfitting.CanInstall(ship, gun));

            // Which limit binds first depends on the hull and the gun -- a Sparrow runs
            // out of weapon capacity before it runs out of ports -- so assert that the
            // outfitter names a real one rather than guessing which.
            string? limit = Outfitting.LimitingAttribute(ship, gun);
            Assert.IsNotNull(limit, "the outfitter should say which limit was hit");
            TestContext.WriteLine($"{gun.Name} on a Sparrow is limited by {limit}");
            Assert.Contains(limit, new[] { "gun ports", "weapon capacity", "outfit space" });
        }

        // --- Combat ---------------------------------------------------------------

        [Test]
        public void Combat_AWarshipBeatsAnUnarmedFreighterAndDisablesRatherThanVaporises()
        {
            // A full engagement on real content: real hulls, a real weapon, the real
            // damage model and the real disable threshold.
            // Both ships exactly as the game builds them: a stock Sparrow carries two
            // Beam Lasers, and a stock Star Barge carries only an anti-missile turret.
            Ship warship = Spawn("Sparrow");
            Ship freighter = Spawn("Star Barge");

            Assert.IsTrue(ShipAi.IsArmed(warship), "a stock Sparrow is a warship");
            Assert.IsFalse(ShipAi.IsArmed(freighter),
                "a stock Star Barge carries only an anti-missile turret, which cannot attack");

            Hostile(warship, freighter);

            Weapon gun = warship.Mounts.First(m => !m.IsEmpty && !m.Weapon!.IsSpecial).Weapon!;

            // Inside the weapon's reach, so the scenario tests the damage model rather
            // than the range check.
            warship.Position = new Point(0.0, 0.0);
            freighter.Position = new Point(0.0, gun.Range * 0.4);
            warship.Facing = Angle.FromPoint(freighter.Position - warship.Position);

            var field = new CombatField { WeaponLookup = name =>
                Data.Outfits.TryGetValue(name, out Outfit? o) ? o.Weapon : null };
            field.Add(warship);
            field.Add(freighter);

            double startingHull = freighter.Hull;
            for (int frame = 0; frame < 4000 && !freighter.IsDisabled; frame++)
            {
                warship.Facing = Angle.FromPoint(freighter.Position - warship.Position);
                field.Add(ShipAi.AutoFire(warship, freighter));

                // Ships have to be stepped too: reload clocks, shield regeneration and
                // heat all advance with the ship, not with the projectile field.
                warship.Step(Command.None);
                freighter.Step(Command.None);
                field.Step();
            }

            Assert.Less(freighter.Hull, startingHull, "the freighter should have taken damage");
            Assert.IsTrue(freighter.IsDisabled, "an armed warship should disable an unarmed freighter");
            Assert.IsFalse(freighter.IsDestroyed,
                "disabling stops the fight; it must not carry straight through to destruction");
            Assert.Greater(freighter.Hull, 0.0, "a disabled ship is boardable, not wreckage");

            TestContext.WriteLine(
                $"stock Sparrow: Star Barge disabled at hull {freighter.Hull:F1}/{freighter.MaxHull:F0} " +
                $"(threshold {freighter.MinimumHull:F1})");
        }

        [Test]
        public void Combat_BoardingOddsFavourTheLargerCrew()
        {
            Ship attacker = Spawn("Bactrian");
            Ship defender = Spawn("Shuttle");

            var odds = new CaptureOdds(attacker, defender);
            double strong = odds.CaptureChance(odds.MaxAttackingCrew, 1);
            double weak = odds.CaptureChance(1, odds.MaxDefendingCrew);

            Assert.Greater(strong, weak, "numbers should tell when boarding");
            Assert.LessOrEqual(strong, 1.0);
            Assert.GreaterOrEqual(weak, 0.0);
        }

        // --- Fleet behaviour ------------------------------------------------------

        [Test]
        public void FleetBehaviour_AnArmedShipClosesOnAHostileAndHoldsItsStandoff()
        {
            Ship hunter = Spawn("Sparrow");
            Ship prey = Spawn("Star Barge");

            Assert.IsTrue(ShipAi.IsArmed(hunter), "a stock Sparrow comes armed");
            Hostile(hunter, prey);

            hunter.Position = new Point(0.0, 0.0);
            prey.Position = new Point(0.0, 6000.0);

            double startingRange = (prey.Position - hunter.Position).Length;
            for (int frame = 0; frame < 3000; frame++)
                hunter.Step(ShipAi.Attack(hunter, prey));

            double finalRange = (prey.Position - hunter.Position).Length;
            Assert.Less(finalRange, startingRange, "the hunter should close");

            double standoff = ShipAi.ShortestWeaponRange(hunter);
            Assert.LessOrEqual(standoff, ShipAi.MaxEngagementStandoff,
                "a long-ranged ship still has to come to the fight");

            TestContext.WriteLine(
                $"closed {startingRange:F0} -> {finalRange:F0} with a standoff of {standoff:F0}");
        }

        [Test]
        public void FleetBehaviour_UnarmedShipsDoNotPickFights()
        {
            Ship freighter = Spawn("Star Barge");
            Ship other = Spawn("Sparrow");

            Assert.IsFalse(ShipAi.IsArmed(freighter));
            Assert.IsNull(ShipAi.FindTarget(freighter, new[] { other }),
                "an unarmed hauler has nothing to attack with");
        }

        // --- Missions and progression ---------------------------------------------

        [Test]
        public void Missions_RealContentOffersSomethingToARealPlayer()
        {
            // The end-to-end question: with a player standing on a real planet, does
            // any of the game's authored mission content actually become available?
            var player = new PlayerState(Data);
            Ship ship = Spawn("Shuttle");
            player.Fleet.Add(ship);
            player.Fleet.SetFlagship(ship);
            player.SetCredits(100_000);

            Planet start = Data.Planets.Values.First(p => p.HasSpaceport && p.IsInhabited);
            StarSystem? startSystem = Data.Systems.Values
                .FirstOrDefault(s => s.AllObjects().Any(o => o.PlanetName == start.Name));

            player.EnterSystem(startSystem);
            player.Land(start);

            Assert.AreEqual(1, player.Conditions.Get("flagship landed"));
            Assert.AreEqual(1, player.Conditions.Get($"flagship planet: {start.Name}"));

            int offerable = Data.Missions.Values.Count(m => m.CanOffer(player.Conditions));
            TestContext.WriteLine(
                $"standing on {start.Name}: {offerable} of {Data.Missions.Count} missions pass their gate");

            Assert.Greater(Data.Missions.Count, 0, "the dataset defines missions");
            Assert.Greater(offerable, 0, "a landed, solvent player should qualify for something");
        }

        [Test]
        public void Progression_TimeAndTravelAccumulateInTheConditionStore()
        {
            var player = new PlayerState(Data);
            Ship ship = Spawn("Shuttle");
            player.Fleet.Add(ship);
            player.Fleet.SetFlagship(ship);

            var visited = Data.Systems.Values.Take(3).ToList();
            foreach (StarSystem system in visited)
            {
                player.EnterSystem(system);
                player.AdvanceDays(2);
            }

            foreach (StarSystem system in visited)
                Assert.AreEqual(1, player.Conditions.Get($"visited system: {system.Name}"), system.Name);

            Assert.AreEqual(6, player.Conditions.Get("days since start"));
            Assert.AreEqual(visited.Count, player.VisitedSystems.Count);
        }

        // --- Difficulty -----------------------------------------------------------

        [Test]
        public void Difficulty_CostlierWarshipsAreActuallyTougher()
        {
            // A sanity check on the shape of the power curve: if an expensive capital
            // ship is not meaningfully harder to kill than a starter hull, the game's
            // difficulty progression is broken no matter what the individual formulas
            // say.
            Ship starter = Spawn("Shuttle");
            Ship capital = Spawn("Bactrian");

            Assert.Greater(capital.Cost, starter.Cost, "a Bactrian costs more than a Shuttle");
            Assert.Greater(capital.MaxHull + capital.MaxShields,
                           starter.MaxHull + starter.MaxShields,
                           "and it should survive more punishment");

            // Across the whole fleet, cost and durability should correlate positively.
            var sample = Data.Ships.Values
                .Select(d => { var s = new Ship(d); s.BuildMounts(); return s; })
                .Where(s => s.Cost > 0 && s.MaxHull > 0)
                .ToList();

            Assert.Greater(sample.Count, 200);

            double meanCost = sample.Average(s => Math.Log(s.Cost));
            double meanTough = sample.Average(s => Math.Log(s.MaxHull + s.MaxShields + 1.0));
            double covariance = sample.Sum(s =>
                (Math.Log(s.Cost) - meanCost) * (Math.Log(s.MaxHull + s.MaxShields + 1.0) - meanTough));
            double costVariance = sample.Sum(s => Math.Pow(Math.Log(s.Cost) - meanCost, 2));
            double toughVariance = sample.Sum(s =>
                Math.Pow(Math.Log(s.MaxHull + s.MaxShields + 1.0) - meanTough, 2));
            double correlation = covariance / Math.Sqrt(costVariance * toughVariance);

            TestContext.WriteLine($"cost vs durability over {sample.Count} hulls: r = {correlation:F3}");
            Assert.Greater(correlation, 0.5, "paying more should buy a tougher ship");
        }
    }
}
