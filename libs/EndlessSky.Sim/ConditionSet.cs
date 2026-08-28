using System;
using System.Collections.Generic;
using System.Globalization;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// A boolean test over the player's <see cref="Conditions"/>.
    /// Port of upstream <c>ConditionSet</c>.
    /// </summary>
    /// <remarks>
    /// This is the gate on nearly everything in Endless Sky: which missions offer,
    /// when events fire, which description a planet shows, which conversation branch
    /// runs. The syntax is a small expression language:
    /// <code>
    /// to offer
    ///     has "main plot: started"
    ///     not "main plot: failed"
    ///     "reputation: Republic" &gt; 100
    ///     or
    ///         has "shortcut"
    ///         "credits" &gt;= 500000
    /// </code>
    /// A plain list of children is an AND. <c>and</c> and <c>or</c> introduce nested
    /// groups. <c>has X</c> is "X is non-zero", <c>not X</c> is "X == 0", and
    /// <c>never</c> is a literal false used to switch content off.
    ///
    /// INCOMPLETE, tracked rather than dropped: the <c>min</c>/<c>max</c> function
    /// operators, and engine-derived conditions.
    /// </remarks>
    public class ConditionSet
    {
        private enum Combine { And, Or }

        private readonly List<ConditionSet> _children = new List<ConditionSet>();
        private readonly List<string> _tokens = new List<string>();
        private Combine _combine = Combine.And;

        /// <summary>An empty set passes: content with no conditions is always available.</summary>
        public bool IsEmpty => _tokens.Count == 0 && _children.Count == 0;

        /// <summary>Diagnostics from parsing, for surfacing bad content rather than failing silently.</summary>
        public List<string> Diagnostics { get; } = new List<string>();

        public static ConditionSet Load(DataNode node)
        {
            var set = new ConditionSet();
            set.LoadInto(node);
            return set;
        }

        private void LoadInto(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                string key = child.Token(0);

                if (key == "and" || key == "or")
                {
                    var group = new ConditionSet { _combine = key == "or" ? Combine.Or : Combine.And };
                    group.LoadInto(child);
                    Diagnostics.AddRange(group.Diagnostics);
                    _children.Add(group);
                    continue;
                }

                if (key == "never")
                {
                    // A literal-false CHILD, not a flag on the enclosing set. Upstream
                    // turns it into a LIT-0 term, so under an "or" it is simply one
                    // false alternative among others. Short-circuiting the whole set
                    // would silently disable the live alternatives beside it.
                    _children.Add(new ConditionSet { _tokens = { "0" } });
                    continue;
                }

                var leaf = new ConditionSet();

                if (key == "has" && child.Size == 2)
                {
                    // "has X" is just X, tested for truthiness.
                    leaf._tokens.Add(child.Token(1));
                }
                else if (key == "not" && child.Size == 2)
                {
                    // "not X" is X == 0.
                    leaf._tokens.Add(child.Token(1));
                    leaf._tokens.Add("==");
                    leaf._tokens.Add("0");
                }
                else
                {
                    for (int i = 0; i < child.Size; i++)
                        leaf._tokens.Add(child.Token(i));
                }

                if (leaf._tokens.Count == 0)
                {
                    Diagnostics.Add(Where(child) + ": empty condition");
                    continue;
                }

                _children.Add(leaf);
            }
        }

        /// <summary>Whether the conditions currently satisfy this set.</summary>
        public bool Test(Conditions conditions)
        {
            if (conditions is null) throw new ArgumentNullException(nameof(conditions));

            if (_tokens.Count > 0)
                return ConditionExpression.Evaluate(_tokens, conditions) != 0L;

            if (_children.Count == 0)
                return true;

            if (_combine == Combine.Or)
            {
                foreach (ConditionSet child in _children)
                {
                    if (child.Test(conditions))
                        return true;
                }
                return false;
            }

            foreach (ConditionSet child in _children)
            {
                if (!child.Test(conditions))
                    return false;
            }
            return true;
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
