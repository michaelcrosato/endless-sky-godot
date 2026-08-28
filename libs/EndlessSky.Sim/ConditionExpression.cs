using System;
using System.Collections.Generic;
using System.Globalization;

namespace EndlessSky.Sim
{
    /// <summary>
    /// Evaluates an infix run of condition tokens to a number.
    /// Shared by <see cref="ConditionSet"/> (tests) and
    /// <see cref="ConditionAssignments"/> (right-hand sides).
    /// </summary>
    /// <remarks>
    /// Upstream gives arithmetic higher precedence than comparison, and comparison
    /// higher than <c>and</c>/<c>or</c>, so <c>"a" + 1 &gt; "b"</c> parses as
    /// <c>("a" + 1) &gt; "b"</c>. Comparisons yield 1 or 0, which is why a comparison
    /// can be fed straight back into arithmetic.
    /// </remarks>
    internal static class ConditionExpression
    {
        private static readonly Dictionary<string, int> Precedence =
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["or"] = 0,
                ["and"] = 1,
                ["=="] = 2, ["!="] = 2, ["<"] = 2, [">"] = 2, ["<="] = 2, [">="] = 2,
                ["+"] = 3, ["-"] = 3,
                ["*"] = 4, ["/"] = 4, ["%"] = 4,
            };

        internal static bool IsOperator(string token) =>
            token is not null && Precedence.ContainsKey(token);

        internal static long Evaluate(IReadOnlyList<string> tokens, Conditions conditions)
        {
            var values = new Stack<long>();
            var operators = new Stack<string>();

            foreach (string token in tokens)
            {
                if (Precedence.TryGetValue(token, out int precedence))
                {
                    // Left-associative: reduce anything of equal or higher precedence
                    // before pushing.
                    while (operators.Count > 0 && Precedence[operators.Peek()] >= precedence)
                        Reduce(values, operators);

                    operators.Push(token);
                }
                else
                {
                    values.Push(Operand(token, conditions));
                }
            }

            while (operators.Count > 0)
                Reduce(values, operators);

            return values.Count > 0 ? values.Peek() : 0L;
        }

        private static void Reduce(Stack<long> values, Stack<string> operators)
        {
            string op = operators.Pop();
            if (values.Count < 2)
                return;

            long right = values.Pop();
            long left = values.Pop();
            values.Push(Apply(op, left, right));
        }

        private static long Apply(string op, long left, long right) => op switch
        {
            "==" => left == right ? 1L : 0L,
            "!=" => left != right ? 1L : 0L,
            "<" => left < right ? 1L : 0L,
            ">" => left > right ? 1L : 0L,
            "<=" => left <= right ? 1L : 0L,
            ">=" => left >= right ? 1L : 0L,
            "+" => left + right,
            "-" => left - right,
            "*" => left * right,
            // Division and modulo by zero yield zero rather than faulting: one bad
            // condition in one content file must not take down the game.
            "/" => right == 0L ? 0L : left / right,
            "%" => right == 0L ? 0L : left % right,
            "and" => left != 0L && right != 0L ? 1L : 0L,
            "or" => left != 0L || right != 0L ? 1L : 0L,
            _ => 0L,
        };

        /// <summary>A token is a literal if it parses as a number, otherwise a condition name.</summary>
        private static long Operand(string token, Conditions conditions) =>
            long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long literal)
                ? literal
                : conditions.Get(token);
    }
}
