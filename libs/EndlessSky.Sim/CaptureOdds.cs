using System;
using System.Collections.Generic;
using System.Linq;

namespace EndlessSky.Sim
{
    /// <summary>
    /// Odds of capturing a boarded ship. Port of upstream <c>CaptureOdds</c>.
    /// </summary>
    /// <remarks>
    /// Boarding is resolved as a sequence of one-on-one rounds. Each round the
    /// attacker wins with probability <c>ap / (ap + dp)</c>, where the two powers
    /// come from crew counts and hand-to-hand equipment; the loser's side loses a
    /// crew member. The capture probability is the 2-D dynamic program over those
    /// rounds, which is why a small edge in equipment compounds into a large edge in
    /// outcome.
    ///
    /// Two rules shape the whole system:
    /// crew defend better than they attack (1.0 attack against 2.0 defense by
    /// default), so boarding an evenly-crewed ship is a losing proposition; and each
    /// crew member wields exactly one weapon, best first, so the tenth boarding
    /// cutlass does nothing for a crew of nine.
    ///
    /// INCOMPLETE, tracked rather than dropped: per-government crew attack and
    /// defense overrides are read from the government when present but default to
    /// upstream's 1.0/2.0; expected-casualty tables are not exposed yet.
    /// </remarks>
    public class CaptureOdds
    {
        /// <summary>Upstream's default power of one crew member on the attack.</summary>
        public const double DefaultCrewAttack = 1.0;

        /// <summary>Upstream's default power of one crew member defending.</summary>
        public const double DefaultCrewDefense = 2.0;

        private readonly double[] _attackerPower;
        private readonly double[] _defenderPower;

        // capture[a, d] = probability an attacker with a crew takes a defender with d.
        private readonly double[,] _capture;

        public CaptureOdds(Ship attacker, Ship defender)
        {
            if (attacker is null) throw new ArgumentNullException(nameof(attacker));
            if (defender is null) throw new ArgumentNullException(nameof(defender));

            _attackerPower = Power(attacker, isDefender: false);
            _defenderPower = Power(defender, isDefender: true);
            _capture = Calculate(_attackerPower, _defenderPower);
        }

        public int MaxAttackingCrew => _attackerPower.Length;
        public int MaxDefendingCrew => _defenderPower.Length;

        /// <summary>Total attacking power with this many crew, 0 when out of range.</summary>
        public double AttackerPower(int crew) =>
            crew >= 1 && crew <= _attackerPower.Length ? _attackerPower[crew - 1] : 0.0;

        /// <summary>Total defending power with this many crew, 0 when out of range.</summary>
        public double DefenderPower(int crew) =>
            crew >= 1 && crew <= _defenderPower.Length ? _defenderPower[crew - 1] : 0.0;

        /// <summary>
        /// Probability the attacker captures the ship. An attacker down to one crew
        /// can never capture: somebody has to be left to fly the prize.
        /// </summary>
        public double CaptureChance(int attackingCrew, int defendingCrew)
        {
            if (defendingCrew <= 0)
                return 1.0;

            if (attackingCrew <= 1 || attackingCrew > MaxAttackingCrew)
                return 0.0;

            if (defendingCrew > MaxDefendingCrew)
                defendingCrew = MaxDefendingCrew;

            return _capture[attackingCrew, defendingCrew];
        }

        /// <summary>
        /// Cumulative power for each crew count. Each crew member wields one weapon,
        /// strongest first, on top of their innate power.
        /// </summary>
        private static double[] Power(Ship ship, bool isDefender)
        {
            int crew = ship.Crew;
            if (crew <= 0)
                return Array.Empty<double>();

            string attribute = isDefender ? "capture defense" : "capture attack";

            // A government may override its crew's innate fighting power; upstream
            // reads it from the ship's government rather than assuming 1.0/2.0.
            double crewPower = ship.Government is not null
                ? (isDefender ? ship.Government.CrewDefense : ship.Government.CrewAttack)
                : (isDefender ? DefaultCrewDefense : DefaultCrewAttack);

            // One entry per installed copy, so two cutlasses arm two crew.
            var weapons = new List<double>();
            foreach (Outfit outfit in ship.Outfits)
            {
                double bonus = outfit.Attributes.Get(attribute);
                if (bonus > 0.0)
                    weapons.Add(bonus);
            }

            weapons.Sort();
            weapons.Reverse();

            var power = new double[crew];
            double running = 0.0;
            for (int i = 0; i < crew; i++)
            {
                running += crewPower + (i < weapons.Count ? weapons[i] : 0.0);
                power[i] = running;
            }

            return power;
        }

        /// <summary>
        /// The dynamic program. capture[a, d] is built from the outcomes of one round:
        /// win and face one fewer defender, or lose a crew member and face the same.
        /// </summary>
        private static double[,] Calculate(double[] attackerPower, double[] defenderPower)
        {
            int maxA = attackerPower.Length;
            int maxD = defenderPower.Length;
            var capture = new double[maxA + 1, maxD + 1];

            // With no defenders left the ship is taken; with one attacker it never is.
            for (int a = 0; a <= maxA; a++)
                capture[a, 0] = 1.0;

            for (int a = 2; a <= maxA; a++)
            {
                double ap = attackerPower[a - 1];
                for (int d = 1; d <= maxD; d++)
                {
                    double dp = defenderPower[d - 1];
                    double odds = ap / (ap + dp);

                    capture[a, d] = odds * capture[a, d - 1] + (1.0 - odds) * capture[a - 1, d];
                }
            }

            return capture;
        }
    }
}
