using System.Globalization;
using EndlessSky.Data;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Numeric output fidelity. Upstream's DataWriter sets <c>precision(8)</c> on its
    /// stream, so numbers are written with eight significant digits in C's
    /// <c>%g</c> style.
    /// </summary>
    [TestFixture]
    public class DataWriterTests
    {
        [Test]
        public void WholeNumbersHaveNoDecimalPoint()
        {
            Assert.AreEqual("5", DataWriter.Number(5.0));
            Assert.AreEqual("-42", DataWriter.Number(-42.0));
            Assert.AreEqual("0", DataWriter.Number(0.0));
        }

        [Test]
        public void BinaryRepresentationNoiseIsNotWrittenOut()
        {
            // The reason this matters. 0.1 + 0.2 is 0.30000000000000004 as a double,
            // and round-trip formatting writes every one of those digits. Upstream's
            // eight significant digits write 0.3, so a file saved here stayed textually
            // different from the same file saved by upstream - and the noise compounds
            // on every load-and-save cycle.
            Assert.AreEqual("0.3", DataWriter.Number(0.1 + 0.2));
            Assert.AreEqual("0.1", DataWriter.Number(0.1));
            Assert.AreEqual("2.675", DataWriter.Number(2.675));
        }

        [Test]
        public void ValuesAreRoundedToEightSignificantDigits()
        {
            Assert.AreEqual("1.2345679", DataWriter.Number(1.23456789012345));
            Assert.AreEqual("0.00012345679", DataWriter.Number(0.000123456789012));
        }

        [Test]
        public void TrailingZerosAreStripped()
        {
            Assert.AreEqual("1.5", DataWriter.Number(1.5000000000));
            Assert.AreEqual("0.25", DataWriter.Number(0.25));
        }

        [Test]
        public void ExponentsUseCStyleLowerCase()
        {
            // .NET writes "1E+20"; C's %g, and therefore every upstream data file,
            // writes "1e+20".
            string big = DataWriter.Number(1e20);
            string small = DataWriter.Number(1.5e-12);

            Assert.IsFalse(big.Contains("E"), $"expected a lower-case exponent, got {big}");
            Assert.IsFalse(small.Contains("E"), $"expected a lower-case exponent, got {small}");
            Assert.AreEqual("1e+20", big);
        }

        [Test]
        public void EverythingWrittenParsesBackToTheSameValueAtThisPrecision()
        {
            // Eight digits is a deliberate loss of precision, so the guarantee is not
            // bit-exact round-tripping - it is that a written file reloads to the same
            // value the file itself claims.
            double[] values =
            {
                0.1, 0.1 + 0.2, 1.0 / 3.0, 1234.5678, -0.000875, 6.02e23, 1e-9, 99.999999,
            };

            foreach (double value in values)
            {
                string written = DataWriter.Number(value);
                double reparsed = double.Parse(written, NumberStyles.Float, CultureInfo.InvariantCulture);

                Assert.AreEqual(reparsed, double.Parse(DataWriter.Number(reparsed),
                    NumberStyles.Float, CultureInfo.InvariantCulture), 0.0,
                    $"writing {written} again must be stable");
                Assert.AreEqual(value, reparsed, System.Math.Abs(value) * 1e-7,
                    $"{value} written as {written}");
            }
        }

        [Test]
        public void NumbersSurviveARoundTripThroughAWrittenNode()
        {
            var writer = new DataWriter();
            writer.Write("outfit", "Test Widget");
            writer.BeginChild();
            writer.Write("mass", 0.1 + 0.2);
            writer.EndChild();

            string text = writer.ToString();
            StringAssert.Contains("mass 0.3", text);
            StringAssert.DoesNotContain("0.30000000000000004", text);

            DataNode node = new DataFile(text, "test.txt").Nodes[0];
            Assert.AreEqual(0.3, node.Children[0].Value(1), 1e-9);
        }

        [TestCase(100.0, "100")]
        [TestCase(0.3, "0.3")]
        [TestCase(2.5, "2.5")]
        [TestCase(-1234.5, "-1234.5")]
        public void ModestNumbersAreWrittenPlainly(double value, string expected)
        {
            Assert.AreEqual(expected, DataWriter.Number(value));
        }

        [TestCase(1e8, "1e+08")]
        [TestCase(123456789.0, "1.2345679e+08")]
        [TestCase(1e15, "1e+15")]
        public void LargeValuesGoScientificExactlyWhereUpstreamDoes(double value, string expected)
        {
            // Upstream sets out.precision(8) and writes doubles through the default
            // float format, i.e. C's %.8g -- which switches to scientific notation once
            // the decimal exponent reaches the precision. An integer short-circuit
            // printed these in full instead, so a file written here and a file written
            // by upstream stopped matching byte for byte at exactly 1e8.
            Assert.AreEqual(expected, DataWriter.Number(value));
        }

        [Test]
        public void NegativeZeroKeepsItsSign()
        {
            // %.8g of -0.0 is "-0". Casting to a long threw the sign away, which is the
            // one bit distinguishing a value that approached zero from below.
            Assert.AreEqual("-0", DataWriter.Number(-0.0));
            Assert.AreEqual("0", DataWriter.Number(0.0));
        }

        [Test]
        public void IntegersPassedAsIntegersAreStillWrittenInFull()
        {
            // Upstream's writer is templated on the static type: an integral type goes
            // to the stream unaffected by precision, so a large credit balance is not
            // rounded away. Only doubles get %.8g.
            var writer = new DataWriter();
            writer.Write("credits", 123456789L);

            StringAssert.Contains("123456789", writer.ToString());
        }
    }
}
