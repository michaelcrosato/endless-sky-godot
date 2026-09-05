using System;
using System.Collections.Generic;
using System.Linq;

namespace EndlessSky.Sim
{
    /// <summary>
    /// How much a used ship or outfit is worth. Port of upstream <c>Depreciation</c>.
    /// </summary>
    /// <remarks>
    /// New purchases keep their full value through a grace period. After that,
    /// resale value falls with age and bottoms out at a quarter. Items with no
    /// purchase history, including a starting ship's stock outfits, use that floor.
    /// </remarks>
    public static class Depreciation
    {
        /// <summary>Floor: a fully depreciated item keeps a quarter of its value.</summary>
        public const double Minimum = 0.25;

        /// <summary>Daily decay applied past the grace period.</summary>
        public const double Daily = 0.997;

        /// <summary>Days before depreciation starts at all.</summary>
        public const int GracePeriod = 7;

        /// <summary>Age at which an item is worth <see cref="Minimum"/> and no less.</summary>
        public const int MaxAge = 1000;

        /// <summary>The fraction of its cost an item of this age retains.</summary>
        public static double Fraction(int ageInDays)
        {
            if (ageInDays <= GracePeriod)
                return 1.0;

            if (ageInDays >= MaxAge)
                return Minimum;

            int effectiveAge = ageInDays - GracePeriod;
            double daily = Math.Pow(Daily, effectiveAge);
            double linear = (double)(MaxAge - effectiveAge) / MaxAge;
            return Minimum + (1.0 - Minimum) * daily * linear;
        }

        /// <summary>
        /// What the player is paid for something they own. Upstream defaults an item
        /// with no purchase record to FULLY depreciated, so a ship of unknown age sells
        /// for a quarter rather than for full price.
        /// </summary>
        public static long SaleValue(long cost, int ageInDays = MaxAge) =>
            ageInDays <= GracePeriod ? cost : (long)(cost * Fraction(ageInDays));
    }

    /// <summary>
    /// Why a purchase or sale was refused.
    /// </summary>
    public enum TradeResult
    {
        Ok,
        NotSold,
        CannotAfford,
        DoesNotFit,
        NotOwned,
        NoSuchThing,
        NotHere,
        InvalidAmount,
        CreditLimit,
    }

    /// <summary>
    /// Port transactions for commodities, ships and outfits. Ports the rules from
    /// upstream's <c>TradingPanel</c>, <c>ShipyardPanel</c> and <c>OutfitterPanel</c>.
    /// </summary>
    /// <remarks>
    /// The directive names "ship purchasing" and "outfit installation" under Milestone
    /// 4 and again under Gameplay Philosophy. Stock lists and installation rules both
    /// existed; nothing joined them to the player's money, so nothing could actually
    /// be bought.
    ///
    /// Every check here is a rule a player runs into: a shop only sells what it
    /// stocks, an outfit must physically fit before it can be paid for, and a ship
    /// must be available at the port to be sold. Selling the last ship is allowed;
    /// the pilot remains landed until a replacement can depart.
    ///
    /// INCOMPLETE, tracked rather than dropped: cost accounting for jettisoned cargo,
    /// individual port services and per-ship landing clearance,
    /// licences, outfits placed into cargo or
    /// planetary storage rather than installed, and trade-in when buying a
    /// replacement. Purchase ages ARE remembered now, per ship model and outfit rather
    /// than per individual hull, so two of the same model bought years apart are told
    /// apart by which is newer but not by which is which.
    /// </remarks>
    public static class Trading
    {
        /// <summary>Tradeable goods quoted at the player's current port.</summary>
        public static IEnumerable<TradeQuote> CommoditiesFor(GameData data, PlayerState player)
        {
            if (data is null || player?.CurrentPlanet is not { HasSpaceport: true }
                || player.CurrentSystem is null)
                return Enumerable.Empty<TradeQuote>();

            return data.Trade.Quotes(player.CurrentSystem.Name)
                .Where(q => q.Price > 0 && data.Trade.Commodities.TryGetValue(q.Commodity, out Commodity? c)
                    && c.IsTradeable);
        }

        /// <summary>
        /// Buys up to the requested tonnage, limited by credits and local cargo space.
        /// Upstream TradingPanel::Buy charges only for cargo actually added.
        /// </summary>
        public static TradeResult BuyCommodity(PlayerState player, GameData data, string commodity,
                                                int tons, out int bought)
        {
            bought = 0;
            TradeResult result = CommodityPrice(player, data, commodity, out int price);
            if (result != TradeResult.Ok)
                return result;
            if (tons <= 0)
                return TradeResult.InvalidAmount;

            // Clamp in 64 bits before narrowing. A negative balance divided by a
            // price can otherwise wrap into a positive 32-bit affordable quantity.
            int affordable = (int)Math.Min(tons, Math.Max(0L, player.Credits) / price);
            if (affordable == 0)
                return TradeResult.CannotAfford;

            affordable = (int)Int128.Min(affordable,
                ((Int128)long.MaxValue - player.TotalBasis(commodity)) / price);
            if (affordable == 0)
                return TradeResult.CreditLimit;

            bought = player.Fleet.LoadCargo(commodity, affordable, player.CurrentSystem);
            if (bought == 0)
                return TradeResult.DoesNotFit;

            long cost = (long)bought * price;
            player.AdjustBasis(commodity, cost);
            player.AddCredits(-cost);
            return TradeResult.Ok;
        }

        /// <summary>Sells cargo from ships in this system, paying only for tons removed.</summary>
        public static TradeResult SellCommodity(PlayerState player, GameData data, string commodity,
                                                 int tons, out int sold) =>
            SellCommodity(player, data, commodity, tons, out sold, out _);

        /// <summary>Reports sale proceeds minus the proportional purchase cost of those tons.</summary>
        public static TradeResult SellCommodity(PlayerState player, GameData data, string commodity,
                                                 int tons, out int sold, out long profit)
        {
            sold = 0;
            profit = 0;
            TradeResult result = CommodityPrice(player, data, commodity, out int price);
            if (result != TradeResult.Ok)
                return result;
            if (tons <= 0)
                return TradeResult.InvalidAmount;

            // int tonnage times int price fits in a long. A positive existing balance
            // can still overflow on receipt; leave the excess goods aboard instead.
            if (player.Credits > 0)
                tons = (int)Math.Min(tons, (long.MaxValue - player.Credits) / price);
            if (tons == 0)
                return TradeResult.CreditLimit;

            tons = (int)Math.Min(tons, player.Fleet.CargoCount(commodity, player.CurrentSystem));
            if (tons == 0)
                return TradeResult.NotOwned;
            // Compute the share before unloading, once for the entire transaction.
            // Applying rounding separately to each carrier changes the sale's cost.
            long basis = player.GetBasis(commodity, tons);
            Int128 margin = (Int128)tons * price - basis;
            if (margin > long.MaxValue || margin < long.MinValue)
                return TradeResult.CreditLimit;

            sold = player.Fleet.UnloadCargo(commodity, tons, player.CurrentSystem);
            if (sold == 0)
                return TradeResult.NotOwned;

            player.AddCredits((long)sold * price);
            player.RemoveBasis(commodity, basis);
            profit = (long)margin;
            data.Trade.AddPurchase(player.CurrentSystem!.Name, commodity, -sold);
            return TradeResult.Ok;
        }

        private static TradeResult CommodityPrice(PlayerState player, GameData data, string commodity,
                                                  out int price)
        {
            price = 0;
            if (player is null || data is null || string.IsNullOrWhiteSpace(commodity)
                || !data.Trade.Commodities.TryGetValue(commodity, out Commodity? good))
                return TradeResult.NoSuchThing;
            if (!good.IsTradeable || player.CurrentPlanet is not { HasSpaceport: true }
                || player.CurrentSystem is null)
                return TradeResult.NotSold;

            // A zero quote means unavailable in TradingPanel::Buy, not free cargo.
            price = data.Trade.Price(player.CurrentSystem.Name, commodity) ?? 0;
            return price > 0 ? TradeResult.Ok : TradeResult.NotSold;
        }

        /// <summary>Every ship model this planet's shipyards stock.</summary>
        public static IEnumerable<string> ShipsFor(GameData data, Planet planet)
        {
            if (data is null || planet is null)
                return Enumerable.Empty<string>();

            return planet.Shipyards
                .Where(data.Shipyards.ContainsKey)
                .SelectMany(name => data.Shipyards[name].Items)
                .Distinct(StringComparer.Ordinal);
        }

        /// <summary>Every outfit this planet's outfitters stock.</summary>
        public static IEnumerable<string> OutfitsFor(GameData data, Planet planet)
        {
            if (data is null || planet is null)
                return Enumerable.Empty<string>();

            return planet.Outfitters
                .Where(data.Outfitters.ContainsKey)
                .SelectMany(name => data.Outfitters[name].Items)
                .Distinct(StringComparer.Ordinal);
        }

        /// <summary>
        /// Buys a ship from the planet the player is standing on, adding it to the
        /// fleet. New stock is never depreciated, so the player pays list price.
        /// </summary>
        public static TradeResult BuyShip(PlayerState player, GameData data, string model,
                                          out Ship? bought)
        {
            bought = null;
            if (player is null || data is null || string.IsNullOrEmpty(model))
                return TradeResult.NoSuchThing;

            Planet? where = player.CurrentPlanet;
            if (where is null)
                return TradeResult.NotSold;

            if (!data.Ships.ContainsKey(model))
                return TradeResult.NoSuchThing;

            if (!ShipsFor(data, where).Contains(model, StringComparer.Ordinal))
                return TradeResult.NotSold;

            Ship ship = data.BuildShip(model);
            ship.BuildMounts();

            if (player.Credits < ship.Cost)
                return TradeResult.CannotAfford;

            player.AddCredits(-ship.Cost);
            ship.CurrentSystem = player.CurrentSystem;
            ship.SetLevels(shields: ship.MaxShields, hull: ship.MaxHull,
                           energy: ship.MaxEnergy, fuel: ship.MaxFuel);
            player.Fleet.Add(ship);
            player.Purchases.Record(PurchaseLog.ShipKey(model), player.Date);
            bought = ship;
            return TradeResult.Ok;
        }

        /// <summary>Owned hulls available to this shipyard, including models it does not stock.</summary>
        public static IEnumerable<Ship> ShipsToSell(PlayerState player) =>
            player?.CurrentPlanet is { HasShipyard: true } ? ShipsAtPort(player) : Enumerable.Empty<Ship>();

        /// <summary>Owned local hulls the outfitter can equip, including parked ships.</summary>
        public static IEnumerable<Ship> ShipsToOutfit(PlayerState player) =>
            player?.CurrentPlanet is { HasOutfitter: true } ? ShipsAtPort(player) : Enumerable.Empty<Ship>();

        private static IEnumerable<Ship> ShipsAtPort(PlayerState player) =>
            player.CurrentSystem != null
                ? player.Fleet.Ships.Where(s => ReferenceEquals(s.CurrentSystem, player.CurrentSystem)
                    && !s.IsDestroyed && !s.IsEnteringHyperspace && !s.IsHyperspacing)
                : Enumerable.Empty<Ship>();

        /// <summary>The amount a sale will pay, without consuming any purchase records.</summary>
        public static long ShipSaleValue(PlayerState player, Ship ship, int ageInDays = Depreciation.MaxAge) =>
            Depreciation.SaleValue(ship.Cost,
                player.Purchases.PeekAge(PurchaseLog.ShipKey(ship.Definition.DisplayName), player.Date) ?? ageInDays);

        /// <summary>Sells an available ship at its depreciated value, even if it is the last hull.</summary>
        public static TradeResult SellShip(PlayerState player, Ship ship,
                                           int ageInDays = Depreciation.MaxAge)
        {
            if (player is null || ship is null)
                return TradeResult.NoSuchThing;

            if (!player.Fleet.Ships.Contains(ship))
                return TradeResult.NotOwned;

            if (player.CurrentPlanet is not { HasShipyard: true } || player.CurrentSystem == null)
                return TradeResult.NotSold;
            if (!ShipsToSell(player).Contains(ship)) return TradeResult.NotHere;

            string key = PurchaseLog.ShipKey(ship.Definition.DisplayName);
            long value = ShipSaleValue(player, ship, ageInDays);
            Int128 balance = (Int128)player.Credits + value;
            if (balance > long.MaxValue || balance < long.MinValue) return TradeResult.CreditLimit;

            player.Fleet.Remove(ship);
            player.Purchases.TakeAge(key, player.Date);
            player.AddCredits(value);
            return TradeResult.Ok;
        }

        /// <summary>Stock and installed equipment visible at the current outfitter.</summary>
        public static IEnumerable<string> OutfitsToShow(PlayerState player, GameData data, Ship? ship)
        {
            if (player.CurrentPlanet is not { HasOutfitter: true } where)
                return Enumerable.Empty<string>();
            var names = OutfitsFor(data, where).ToHashSet(StringComparer.Ordinal);
            if (ship != null && ShipsToOutfit(player).Contains(ship))
                names.UnionWith(ship.Outfits.Select(o => o.Name));
            return data.Outfits.Keys.Where(name => names.Contains(name)
                || player.OutfitStock.Count(PurchaseLog.OutfitKey(name)) > 0)
                .OrderBy(name => name, StringComparer.Ordinal);
        }

        /// <summary>Next purchase price, taking the oldest used stock before new items.</summary>
        public static long? OutfitPurchaseValue(PlayerState player, GameData data, Outfit outfit)
        {
            if (player.CurrentPlanet is not { HasOutfitter: true } where) return null;
            int? age = player.OutfitStock.PeekAge(PurchaseLog.OutfitKey(outfit.Name), player.Date);
            if (!age.HasValue && !OutfitsFor(data, where).Contains(outfit.Name, StringComparer.Ordinal))
                return null;
            return OutfitValue(outfit, age ?? 0);
        }

        /// <summary>Sale price of the newest owned copy; unknown ages are fully depreciated.</summary>
        public static long OutfitSaleValue(PlayerState player, Outfit outfit, int ageInDays = Depreciation.MaxAge) =>
            OutfitValue(outfit, player.Purchases.PeekAge(PurchaseLog.OutfitKey(outfit.Name), player.Date) ?? ageInDays);

        private static long OutfitValue(Outfit outfit, int age) =>
            outfit.Attributes.Get("installable") < 0 ? outfit.Cost : Depreciation.SaleValue(outfit.Cost, age);

        private static TradeResult CanOutfit(PlayerState player, Ship ship)
        {
            if (!player.Fleet.Ships.Contains(ship)) return TradeResult.NotOwned;
            if (player.CurrentPlanet is not { HasOutfitter: true } || player.CurrentSystem == null)
                return TradeResult.NotSold;
            return ShipsToOutfit(player).Contains(ship) ? TradeResult.Ok : TradeResult.NotHere;
        }

        /// <summary>Buys and installs one outfit; refusals do not change money, stock or equipment.</summary>
        public static TradeResult BuyOutfit(PlayerState player, GameData data, Ship ship,
                                            string outfitName)
        {
            if (player is null || data is null || ship is null || string.IsNullOrEmpty(outfitName))
                return TradeResult.NoSuchThing;

            if (!data.Outfits.TryGetValue(outfitName, out Outfit? outfit))
                return TradeResult.NoSuchThing;

            TradeResult access = CanOutfit(player, ship);
            if (access != TradeResult.Ok) return access;
            long? value = OutfitPurchaseValue(player, data, outfit);
            if (!value.HasValue) return TradeResult.NotSold;
            long cost = value.Value;

            if (!Outfitting.Fits(ship, outfit))
                return TradeResult.DoesNotFit;

            if (player.Credits < cost)
                return TradeResult.CannotAfford;
            Int128 balance = (Int128)player.Credits - cost;
            if (balance > long.MaxValue || balance < long.MinValue) return TradeResult.CreditLimit;

            int crew = ship.Crew;
            player.SetCredits((long)balance);
            ship.AddOutfit(outfit);
            ServiceOutfittedShip(ship, outfit, crew, 1);
            player.Fleet.RefreshFlagship();
            string key = PurchaseLog.OutfitKey(outfit.Name);
            int age = player.OutfitStock.TakeAge(key, player.Date) ?? 0;
            player.Purchases.RecordAge(key, player.Date, age);
            return TradeResult.Ok;
        }

        /// <summary>Removes an outfit from a ship and pays its depreciated value.</summary>
        public static TradeResult SellOutfit(PlayerState player, Ship ship, Outfit outfit,
                                             int ageInDays = Depreciation.MaxAge)
        {
            if (player is null || ship is null || outfit is null)
                return TradeResult.NoSuchThing;

            TradeResult access = CanOutfit(player, ship);
            if (access != TradeResult.Ok) return access;
            // Use the installed definition, never a caller-supplied same-name price or capacity.
            Outfit? installed = ship.Outfits.FirstOrDefault(o => o.Name == outfit.Name);
            if (installed is null) return TradeResult.NotOwned;
            outfit = installed;

            // Taking an outfit off can break the ship as surely as putting one on:
            // upstream gates every uninstall on CanAdd(outfit, -1)
            // (OutfitterPanel.cpp:1102), which is what stops you selling the expansion
            // that is holding the rest of your loadout. Selling went straight to
            // RemoveOutfit, which checks nothing, so any outfit could always come off
            // and leave the ship with negative capacity.
            if (Outfitting.CanInstall(ship, outfit, -1) != -1)
                return TradeResult.DoesNotFit;

            long value = OutfitSaleValue(player, outfit, ageInDays);
            Int128 balance = (Int128)player.Credits + value;
            if (balance > long.MaxValue || balance < long.MinValue) return TradeResult.CreditLimit;

            int crew = ship.Crew;
            if (ship.RemoveOutfit(outfit) == 0) return TradeResult.NotOwned;
            ServiceOutfittedShip(ship, outfit, crew, -1);
            player.Fleet.RefreshFlagship();
            string key = PurchaseLog.OutfitKey(outfit.Name);
            int age = player.Purchases.TakeAge(key, player.Date) ?? ageInDays;
            player.OutfitStock.RecordAge(key, player.Date, age);
            player.SetCredits((long)balance);
            return TradeResult.Ok;
        }

        private static void ServiceOutfittedShip(Ship ship, Outfit outfit, int crew, int direction)
        {
            long adjustment = (long)(outfit.Attributes.Get("required crew") + outfit.Attributes.Get("mandatory crew")) * direction;
            if (direction < 0 || crew + adjustment <= ship.Bunks)
                crew = (int)Math.Clamp(crew + adjustment, 0, int.MaxValue);
            ship.Crew = Math.Min(ship.Bunks, Math.Max(crew, ship.RequiredCrew));
            ship.Recharge(RechargeType.All);
        }
    }
}
