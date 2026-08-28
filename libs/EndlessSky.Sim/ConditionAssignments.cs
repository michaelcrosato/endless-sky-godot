using System;
using System.Collections.Generic;
using System.Globalization;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// Changes applied to the player's conditions when something happens.
    /// Port of upstream <c>ConditionAssignments</c>.
    /// </summary>
    /// <remarks>
    /// The write half of the condition system, found in <c>on offer</c>,
    /// <c>on complete</c>, event blocks and conversation actions:
    /// <code>
    /// on complete
    ///     set "delivered the package"
    ///     clear "package offered"
    ///     "reputation: Republic" += 10
    ///     payment 50000
    /// </code>
    /// Right-hand sides are themselves expressions over conditions, so content can
    /// write <c>"total" += "this run"</c>.
    /// </remarks>
    public class ConditionAssignments
    {
        private readonly List<(string Name, string Op, List<string> Expression)> _assignments =
            new List<(string, string, List<string>)>();

        public bool IsEmpty => _assignments.Count == 0;

        public List<string> Diagnostics { get; } = new List<string>();

        public static ConditionAssignments Load(DataNode node)
        {
            var assignments = new ConditionAssignments();
            assignments.LoadInto(node);
            return assignments;
        }

        private void LoadInto(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                string key = child.Token(0);

                // "set X" is shorthand for X = 1; "clear X" for X = 0.
                if (key == "set" && child.Size >= 2)
                {
                    _assignments.Add((child.Token(1), "=", new List<string> { "1" }));
                    continue;
                }

                if (key == "clear" && child.Size >= 2)
                {
                    _assignments.Add((child.Token(1), "=", new List<string> { "0" }));
                    continue;
                }

                // "<name> ++" / "<name> --": increment or decrement by one.
                if (child.Size == 2 && (child.Token(1) == "++" || child.Token(1) == "--"))
                {
                    _assignments.Add((child.Token(0),
                        child.Token(1) == "++" ? "+=" : "-=",
                        new List<string> { "1" }));
                    continue;
                }

                // "<name> <op> <expression...>"
                if (child.Size >= 3 && IsAssignmentOperator(child.Token(1)))
                {
                    var expression = new List<string>();
                    for (int i = 2; i < child.Size; i++)
                        expression.Add(child.Token(i));

                    _assignments.Add((child.Token(0), child.Token(1), expression));
                    continue;
                }

                // A bare name with no operator increments it, which upstream uses for
                // counters such as "visited <planet>".
                if (child.Size == 1 && !string.IsNullOrEmpty(key))
                {
                    _assignments.Add((key, "++", new List<string> { "1" }));
                    continue;
                }

                Diagnostics.Add(Where(child) + ": unrecognised assignment");
            }
        }

        private static bool IsAssignmentOperator(string token) =>
            token is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "<?=" or ">?=";

        /// <summary>Applies every assignment, in declaration order.</summary>
        public void Apply(Conditions conditions)
        {
            if (conditions is null) throw new ArgumentNullException(nameof(conditions));

            foreach ((string name, string op, List<string> expression) in _assignments)
            {
                long value = EvaluateExpression(expression, conditions);
                long current = conditions.Get(name);

                long result = op switch
                {
                    "=" => value,
                    "+=" => current + value,
                    "-=" => current - value,
                    "*=" => current * value,
                    // Upstream saturates rather than skipping, matching the expression
                    // evaluator's division rule.
                    "/=" => value == 0L ? long.MaxValue : current / value,
                    "%=" => value == 0L ? current : current % value,
                    // "<?=" keeps the smaller value, ">?=" the larger: min/max assign.
                    "<?=" => Math.Min(current, value),
                    ">?=" => Math.Max(current, value),
                    _ => current,
                };

                conditions.Set(name, result);
            }
        }

        /// <summary>
        /// Right-hand sides are expressions over conditions, so a value can be built
        /// from other conditions rather than only from literals.
        /// </summary>
        private static long EvaluateExpression(List<string> tokens, Conditions conditions)
        {
            if (tokens.Count == 1)
            {
                // Content writes non-integer literals (".5", "1.0"); conditions are an
                // integer keyspace, so upstream truncates rather than treating the
                // token as an unknown condition name worth zero.
                if (long.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                                  out long literal))
                    return literal;

                if (double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture,
                                    out double real))
                    return (long)real;

                return conditions.Get(tokens[0]);
            }

            return ConditionExpression.Evaluate(tokens, conditions);
        }

        /// <summary>
        /// Source location for a diagnostic. DataNode.Trace is internal to the data
        /// layer, so this rebuilds it from the public file and line.
        /// </summary>
        private static string Where(DataNode node) =>
            (node.SourceFile ?? "<data>") + ":" +
            node.LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
