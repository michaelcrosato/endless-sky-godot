using System;
using System.Collections.Generic;
using System.Linq;

namespace EndlessSky.Sim
{
    /// <summary>
    /// How much a used ship or outfit is worth. Port of upstream <c>Depreciation</c>.
    /// </summary>
    /// <remarks>
    /// Endless Sky does not let the player buy a ship and sell it back for the same
    /// money, which is what stops the shipyard from being an infinite-credit machine
    /// and what makes an early ship purchase a real commitment. New stock sells at
    /// full price; anything out of the player's own fleet is worth a fraction that
    /// falls with age and bottoms out at a quarter.
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
            (long)(cost * Fraction(ageInDays));
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
        LastShip,
    }

    /// <summary>
    /// The shipyard and outfitter counters: buying and selling ships and outfits.
    /// Ports the transaction rules from upstream's <c>ShipyardPanel</c> and
    /// <c>OutfitterPanel</c>.
    /// </summary>
    /// <remarks>
    /// The directive names "ship purchasing" and "outfit installation" under Milestone
    /// 4 and again under Gameplay Philosophy. Stock lists and installation rules both
    /// existed; nothing joined them to the player's money, so nothing could actually
    /// be bought.
    ///
    /// Every check here is a rule a player runs into: a shop only sells what it
    /// stocks, an outfit must physically fit before it can be paid for, and the last
    /// flyable ship cannot be sold out from under its pilot.
    ///
    /// INCOMPLETE, tracked rather than dropped: licences, outfits placed into cargo or
    /// planetary storage rather than installed, and trade-in when buying a
    /// replacement. Purchase ages ARE remembered now, per ship model and outfit rather
    /// than per individual hull, so two of the same model bought years apart are told
    /// apart by which is newer but not by which is which.
    /// </remarks>
    public static class Trading
    {
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
            ship.SetLevels(shields: ship.MaxShields, hull: ship.MaxHull,
                           energy: ship.MaxEnergy, fuel: ship.MaxFuel);
            player.Fleet.Add(ship);
            player.Purchases.Record(PurchaseLog.ShipKey(model), player.Date);
            bought = ship;
            return TradeResult.Ok;
        }

        /// <summary>
        /// Sells a ship out of the fleet at its depreciated value.
        /// </summary>
        public static TradeResult SellShip(PlayerState player, Ship ship,
                                           int ageInDays = Depreciation.MaxAge)
        {
            if (player is null || ship is null)
                return TradeResult.NoSuchThing;

            if (!player.Fleet.Ships.Contains(ship))
                return TradeResult.NotOwned;

            // Selling the only flyable ship would strand the player with no way to
            // leave, which upstream does not allow either.
            if (player.Fleet.ActiveShips.Count() <= 1 && !ship.IsParked)
                return TradeResult.LastShip;

            player.Fleet.Remove(ship);

            int age = player.Purchases.TakeAge(
                PurchaseLog.ShipKey(ship.Definition.DisplayName), player.Date) ?? ageInDays;

            player.AddCredits(Depreciation.SaleValue(ship.Cost, age));
            return TradeResult.Ok;
        }

        /// <summary>
        /// Buys an outfit and installs it on a ship. The fit is checked BEFORE the
        /// money changes hands, so a refusal never costs the player anything.
        /// </summary>
        public static TradeResult BuyOutfit(PlayerState player, GameData data, Ship ship,
                                            string outfitName)
        {
            if (player is null || data is null || ship is null || string.IsNullOrEmpty(outfitName))
                return TradeResult.NoSuchThing;

            Planet? where = player.CurrentPlanet;
            if (where is null)
                return TradeResult.NotSold;

            if (!data.Outfits.TryGetValue(outfitName, out Outfit? outfit))
                return TradeResult.NoSuchThing;

            if (!OutfitsFor(data, where).Contains(outfitName, StringComparer.Ordinal))
                return TradeResult.NotSold;

            if (!Outfitting.Fits(ship, outfit))
                return TradeResult.DoesNotFit;

            if (player.Credits < outfit.Cost)
                return TradeResult.CannotAfford;

            player.AddCredits(-outfit.Cost);
            ship.AddOutfit(outfit);

            // Note when it was bought, so selling it back is priced on how long the
            // player kept it. Without this every sale falls through to the no-record
            // default -- the 0.25 floor -- and buying anything by mistake costs three
            // quarters of its price.
            player.Purchases.Record(PurchaseLog.OutfitKey(outfit.Name), player.Date);
            return TradeResult.Ok;
        }

        /// <summary>Removes an outfit from a ship and pays its depreciated value.</summary>
        public static TradeResult SellOutfit(PlayerState player, Ship ship, Outfit outfit,
                                             int ageInDays = Depreciation.MaxAge)
        {
            if (player is null || ship is null || outfit is null)
                return TradeResult.NoSuchThing;

            if (!ship.Outfits.Any(o => ReferenceEquals(o, outfit) ||
                                       string.Equals(o.Name, outfit.Name, StringComparison.Ordinal)))
            {
                return TradeResult.NotOwned;
            }

            // Taking an outfit off can break the ship as surely as putting one on:
            // upstream gates every uninstall on CanAdd(outfit, -1)
            // (OutfitterPanel.cpp:1102), which is what stops you selling the expansion
            // that is holding the rest of your loadout. Selling went straight to
            // RemoveOutfit, which checks nothing, so any outfit could always come off
            // and leave the ship with negative capacity.
            if (Outfitting.CanInstall(ship, outfit, -1) != -1)
                return TradeResult.DoesNotFit;

            if (ship.RemoveOutfit(outfit) == 0)
                return TradeResult.NotOwned;

            // The player's own record when there is one; the caller's age otherwise,
            // which defaults to fully depreciated exactly as upstream treats an item it
            // has no record of -- a hull's stock loadout, say, which was never bought.
            int age = player.Purchases.TakeAge(PurchaseLog.OutfitKey(outfit.Name), player.Date)
                      ?? ageInDays;

            player.AddCredits(Depreciation.SaleValue(outfit.Cost, age));
            return TradeResult.Ok;
        }
    }
}
