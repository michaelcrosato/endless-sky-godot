using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EndlessSky.Data
{
    /// <summary>
    /// One line of an Endless Sky data file: whitespace-separated tokens plus any
    /// lines indented beneath it.
    ///
    /// Port of upstream <c>DataNode</c>. Numeric parsing deliberately reproduces
    /// upstream's hand-rolled routine rather than using <see cref="double.Parse"/>,
    /// because the two can disagree on rounding and simulation values are compared
    /// against upstream numbers.
    /// </summary>
    public sealed class DataNode : IEnumerable<DataNode>
    {
        private readonly List<string> _tokens = new List<string>();
        private readonly List<DataNode> _children = new List<DataNode>();
        private string _sourceFile;

        public DataNode(DataNode parent = null)
        {
            Parent = parent;
        }

        public DataNode Parent { get; internal set; }

        /// <summary>1-based line number in the source file; 0 if synthesized.</summary>
        public int LineNumber { get; internal set; }

        /// <summary>Source file this node came from, inherited from ancestors for error traces.</summary>
        public string SourceFile
        {
            get
            {
                for (DataNode n = this; n != null; n = n.Parent)
                {
                    if (n._sourceFile != null)
                    {
                        return n._sourceFile;
                    }
                }

                return null;
            }
            internal set => _sourceFile = value;
        }

        public IReadOnlyList<string> Tokens => _tokens;

        public IReadOnlyList<DataNode> Children => _children;

        public int Size => _tokens.Count;

        public bool HasChildren => _children.Count > 0;

        public IEnumerator<DataNode> GetEnumerator() => _children.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        internal void AddTokenInternal(string token) => _tokens.Add(token);

        internal DataNode AddChildInternal()
        {
            var child = new DataNode(this);
            _children.Add(child);
            return child;
        }

        public void AddChild(DataNode child)
        {
            child.Parent = this;
            _children.Add(child);
        }

        public void AddToken(string token) => _tokens.Add(token);

        /// <summary>Token at <paramref name="index"/>, or empty string when out of range.</summary>
        public string Token(int index)
        {
            return (uint)index < (uint)_tokens.Count ? _tokens[index] : string.Empty;
        }

        /// <summary>
        /// Numeric value of a token, or 0 when missing or non-numeric. Matches upstream:
        /// malformed values emit a diagnostic rather than throwing, so one bad line in a
        /// large content file cannot abort the whole load.
        /// </summary>
        public double Value(int index)
        {
            if ((uint)index >= (uint)_tokens.Count || _tokens[index].Length == 0)
            {
                Trace($"Requested token index ({index}) is out of bounds:");
                return 0.0;
            }

            if (!IsNumber(_tokens[index]))
            {
                Trace($"Cannot convert value \"{_tokens[index]}\" to a number:");
                return 0.0;
            }

            return Value(_tokens[index]);
        }

        public bool IsNumber(int index)
        {
            return (uint)index < (uint)_tokens.Count
                   && _tokens[index].Length != 0
                   && IsNumber(_tokens[index]);
        }

        public bool BoolValue(int index) => Value(index) != 0.0;

        /// <summary>
        /// Parses the upstream-permitted format "[+-]?[0-9]*[.]?[0-9]*([eE][+-]?[0-9]*)?"
        /// the way upstream does: accumulate an integer mantissa, then scale by a power of ten.
        /// </summary>
        public static double Value(string token)
        {
            if (!IsNumber(token))
            {
                return 0.0;
            }

            int i = 0;
            int length = token.Length;

            double sign = token[0] == '-' ? -1.0 : 1.0;
            if (token[0] == '-' || token[0] == '+')
            {
                i++;
            }

            long value = 0;
            while (i < length && token[i] >= '0' && token[i] <= '9')
            {
                value = value * 10 + (token[i++] - '0');
            }

            long power = 0;
            if (i < length && token[i] == '.')
            {
                i++;
                while (i < length && token[i] >= '0' && token[i] <= '9')
                {
                    value = value * 10 + (token[i++] - '0');
                    power--;
                }
            }

            if (i < length && (token[i] == 'e' || token[i] == 'E'))
            {
                i++;
                long expSign = i < length && token[i] == '-' ? -1 : 1;
                if (i < length && (token[i] == '-' || token[i] == '+'))
                {
                    i++;
                }

                long exponent = 0;
                while (i < length && token[i] >= '0' && token[i] <= '9')
                {
                    exponent = exponent * 10 + (token[i++] - '0');
                }

                power += expSign * exponent;
            }

            double magnitude = value * Math.Pow(10.0, power);
            return sign < 0.0 ? -magnitude : magnitude;
        }

        public static bool IsNumber(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            bool hasDecimalPoint = false;
            bool hasExponent = false;
            bool isLeading = true;

            foreach (char c in token)
            {
                if (isLeading)
                {
                    isLeading = false;
                    if (c == '-' || c == '+')
                    {
                        continue;
                    }
                }

                if (c == '.')
                {
                    if (hasDecimalPoint || hasExponent)
                    {
                        return false;
                    }

                    hasDecimalPoint = true;
                }
                else if (c == 'e' || c == 'E')
                {
                    if (hasExponent)
                    {
                        return false;
                    }

                    hasExponent = true;
                    isLeading = true;
                }
                else if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Diagnostic sink. The loader points this at its own collector so parse warnings
        /// are surfaced instead of silently swallowed.
        /// </summary>
        public static Action<string> OnDiagnostic;

        internal void Trace(string message)
        {
            OnDiagnostic?.Invoke($"{message} \"{this}\" ({SourceFile ?? "<memory>"}:{LineNumber})");
        }

        public override string ToString() => string.Join(" ", _tokens);

        /// <summary>Reconstructs this node and its subtree in data-file syntax.</summary>
        public string ToDataString(int indent = 0)
        {
            var sb = new StringBuilder();
            Write(sb, indent);
            return sb.ToString();
        }

        private void Write(StringBuilder sb, int indent)
        {
            sb.Append('\t', indent);
            for (int i = 0; i < _tokens.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(DataWriter.Quote(_tokens[i]));
            }

            sb.Append('\n');
            foreach (DataNode child in _children)
            {
                child.Write(sb, indent + 1);
            }
        }

        /// <summary>Culture-independent number formatting, for round-tripping.</summary>
        public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    }
}
