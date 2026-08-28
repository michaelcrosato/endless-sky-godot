using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace EndlessSky.Data
{
    /// <summary>
    /// Serializes nodes back into Endless Sky data-file syntax. Port of upstream
    /// <c>DataWriter</c>. Round-tripping matters for save files and for verifying the
    /// parser against real content: parse -&gt; write -&gt; parse must be stable.
    /// </summary>
    public sealed class DataWriter
    {
        private readonly StringBuilder _out = new StringBuilder();
        private int _indent;
        private bool _atLineStart = true;

        /// <summary>
        /// Chooses quoting the way upstream does. Note the asymmetry: a token containing a
        /// double quote is wrapped in backticks, since the format has no escape sequences.
        /// </summary>
        public static string Quote(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }

            bool hasSpace = value.Length == 0 || value.Any(char.IsWhiteSpace);
            bool hasQuote = value.IndexOf('"') >= 0;
            bool hasBacktick = value.IndexOf('`') >= 0;

            if (hasQuote)
            {
                return "`" + value + "`";
            }

            if (hasSpace || hasBacktick)
            {
                return "\"" + value + "\"";
            }

            return value;
        }

        /// <summary>
        /// Formats a number the way upstream's DataWriter does.
        /// </summary>
        /// <remarks>
        /// Upstream calls <c>out.precision(8)</c> on its stream, so every number is
        /// written with eight significant digits in C's <c>%g</c> style: trailing
        /// zeros stripped, scientific notation only at the extremes.
        ///
        /// Round-tripping ("R") instead writes up to seventeen digits, which turns
        /// every value that is not exactly representable in binary into noise -
        /// 0.30000000000000004 where upstream writes 0.3, and 0.099999999999999998
        /// where it writes 0.1. Those files still parse, but they no longer match
        /// upstream's byte for byte, and the noise compounds every time a file is
        /// loaded and written back.
        /// </remarks>
        public static string Number(double value)
        {
            // Integers are written without a decimal point, matching upstream output.
            if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
            {
                return ((long)value).ToString(CultureInfo.InvariantCulture);
            }

            string text = value.ToString("G8", CultureInfo.InvariantCulture);

            // .NET spells the exponent "E+09"; C's %g spells it "e+09".
            return text.IndexOf('E') >= 0
                ? text.Replace("E", "e")
                : text;
        }

        public DataWriter Write(params object[] tokens)
        {
            foreach (object token in tokens)
            {
                WriteToken(token);
            }

            return EndLine();
        }

        public DataWriter WriteToken(object token)
        {
            if (!_atLineStart)
            {
                _out.Append(' ');
            }
            else
            {
                _out.Append('\t', _indent);
                _atLineStart = false;
            }

            _out.Append(token is double d ? Number(d)
                : Quote(Convert.ToString(token, CultureInfo.InvariantCulture) ?? string.Empty));
            return this;
        }

        public DataWriter EndLine()
        {
            if (!_atLineStart)
            {
                _out.Append('\n');
                _atLineStart = true;
            }

            return this;
        }

        public DataWriter BeginChild()
        {
            _indent++;
            return this;
        }

        public DataWriter EndChild()
        {
            _indent = Math.Max(0, _indent - 1);
            return this;
        }

        /// <summary>Writes an existing node subtree verbatim.</summary>
        public DataWriter Write(DataNode node)
        {
            _out.Append(node.ToDataString(_indent));
            _atLineStart = true;
            return this;
        }

        public override string ToString() => _out.ToString();
    }
}
