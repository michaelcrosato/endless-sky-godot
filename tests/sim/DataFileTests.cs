using System.Linq;
using EndlessSky.Data;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Parser conformance against the syntax rules in upstream DataFile.cpp.
    /// These are the rules real content actually relies on, so each case here
    /// corresponds to a pattern that appears in the shipped data files.
    /// </summary>
    public class DataFileTests
    {
        private static DataFile Parse(string text) => new DataFile(text, "test");

        [Test]
        public void TopLevelNodesAreSiblings()
        {
            DataFile file = Parse("ship Shuttle\nship Sparrow\n");

            Assert.AreEqual(2, file.Nodes.Count);
            Assert.AreEqual("ship", file.Nodes[0].Token(0));
            Assert.AreEqual("Shuttle", file.Nodes[0].Token(1));
            Assert.AreEqual("Sparrow", file.Nodes[1].Token(1));
        }

        [Test]
        public void IndentationCreatesChildren()
        {
            DataFile file = Parse("ship Shuttle\n\tattributes\n\t\tmass 80\n");

            DataNode ship = file.Nodes[0];
            Assert.AreEqual(1, ship.Children.Count, "attributes should be the only child");

            DataNode attributes = ship.Children[0];
            Assert.AreEqual("attributes", attributes.Token(0));
            Assert.AreEqual(1, attributes.Children.Count);
            Assert.AreEqual("mass", attributes.Children[0].Token(0));
            Assert.AreEqual(80.0, attributes.Children[0].Value(1));
        }

        [Test]
        public void DedentReturnsToOuterLevel()
        {
            DataFile file = Parse("a\n\tb\n\t\tc\n\td\ne\n");

            Assert.AreEqual(2, file.Nodes.Count);
            DataNode a = file.Nodes[0];
            Assert.AreEqual(2, a.Children.Count, "b and d are both children of a");
            Assert.AreEqual("b", a.Children[0].Token(0));
            Assert.AreEqual("d", a.Children[1].Token(0));
            Assert.AreEqual("c", a.Children[0].Children[0].Token(0));
            Assert.AreEqual("e", file.Nodes[1].Token(0));
        }

        [Test]
        public void DoubleQuotesGroupATokenWithSpaces()
        {
            DataFile file = Parse("ship \"Star Barge\"\n");

            Assert.AreEqual("Star Barge", file.Nodes[0].Token(1));
        }

        [Test]
        public void BacktickQuotesAllowEmbeddedDoubleQuotes()
        {
            // Upstream has no escape sequences; a backtick-quoted token is the only way
            // to carry a literal double quote, and real dialogue content relies on it.
            DataFile file = Parse("description `He said \"hello\" loudly`\n");

            Assert.AreEqual("He said \"hello\" loudly", file.Nodes[0].Token(1));
        }

        [Test]
        public void HashStartsACommentAndIsSkipped()
        {
            DataFile file = Parse("# a comment\nship Shuttle\n");

            Assert.AreEqual(1, file.Nodes.Count);
            Assert.AreEqual("Shuttle", file.Nodes[0].Token(1));
        }

        [Test]
        public void TrailingCommentIsStrippedFromALine()
        {
            DataFile file = Parse("mass 80 # tons\n");

            Assert.AreEqual(2, file.Nodes[0].Size, "the comment must not become a token");
            Assert.AreEqual(80.0, file.Nodes[0].Value(1));
        }

        [Test]
        public void HashInsideAQuotedTokenIsLiteral()
        {
            DataFile file = Parse("name \"Sector #7\"\n");

            Assert.AreEqual("Sector #7", file.Nodes[0].Token(1));
        }

        [Test]
        public void EmptyQuotedTokenIsPreserved()
        {
            DataFile file = Parse("name \"\" trailing\n");

            Assert.AreEqual(3, file.Nodes[0].Size);
            Assert.AreEqual(string.Empty, file.Nodes[0].Token(1));
            Assert.AreEqual("trailing", file.Nodes[0].Token(2));
        }

        [Test]
        public void BlankLinesDoNotBreakNesting()
        {
            // Real ship definitions have blank lines between outfit groups.
            DataFile file = Parse("ship X\n\toutfits\n\t\t\"A\"\n\n\t\t\"B\"\n");

            DataNode outfits = file.Nodes[0].Children[0];
            Assert.AreEqual(2, outfits.Children.Count);
            Assert.AreEqual("A", outfits.Children[0].Token(0));
            Assert.AreEqual("B", outfits.Children[1].Token(0));
        }

        [Test]
        public void FileWithoutTrailingNewlineStillParses()
        {
            DataFile file = Parse("ship Shuttle");

            Assert.AreEqual(1, file.Nodes.Count);
            Assert.AreEqual("Shuttle", file.Nodes[0].Token(1));
        }

        [Test]
        public void LineNumbersAreRecorded()
        {
            DataFile file = Parse("a\n\nb\n");

            Assert.AreEqual(1, file.Nodes[0].LineNumber);
            Assert.AreEqual(3, file.Nodes[1].LineNumber);
        }

        [TestCase("0", 0.0)]
        [TestCase("1", 1.0)]
        [TestCase("-1", -1.0)]
        [TestCase("+2", 2.0)]
        [TestCase(".77", 0.77)]
        [TestCase("13.545", 13.545)]
        [TestCase("-2.5", -2.5)]
        [TestCase("1e3", 1000.0)]
        [TestCase("1.5e-2", 0.015)]
        [TestCase("2E2", 200.0)]
        public void NumbersParseLikeUpstream(string token, double expected)
        {
            Assert.IsTrue(DataNode.IsNumber(token), $"\"{token}\" should be numeric");
            Assert.AreEqual(expected, DataNode.Value(token), 1e-12);
        }

        [TestCase("")]
        [TestCase("abc")]
        [TestCase("1.2.3")]
        [TestCase("1e2e3")]
        [TestCase("1.5e2.5")]
        [TestCase("12a")]
        public void NonNumbersAreRejected(string token)
        {
            Assert.IsFalse(DataNode.IsNumber(token), $"\"{token}\" should not be numeric");
        }

        [Test]
        public void NegativeZeroKeepsItsSign()
        {
            // copysign in upstream preserves -0.0; a naive multiply would not.
            double value = DataNode.Value("-0");
            Assert.IsTrue(double.IsNegative(value), "-0 should parse to negative zero");
        }

        [Test]
        public void RoundTripThroughWriterIsStable()
        {
            const string source =
                "ship \"Star Barge\"\n" +
                "\tattributes\n" +
                "\t\tcategory \"Light Freighter\"\n" +
                "\t\t\"drag\" 2.2\n" +
                "\tdescription `A \"quoted\" name`\n";

            DataFile first = Parse(source);
            string written = string.Concat(first.Nodes.Select(n => n.ToDataString()));
            DataFile second = Parse(written);

            Assert.AreEqual(
                string.Concat(first.Nodes.Select(n => n.ToDataString())),
                string.Concat(second.Nodes.Select(n => n.ToDataString())),
                "parse -> write -> parse must be a fixed point");
            Assert.AreEqual("A \"quoted\" name", second.Nodes[0].Children[1].Token(1));
        }

        [Test]
        public void SpaceIndentedFilesNestCorrectly()
        {
            DataFile file = Parse("a\n    b\n        c\n");

            Assert.AreEqual("b", file.Nodes[0].Children[0].Token(0));
            Assert.AreEqual("c", file.Nodes[0].Children[0].Children[0].Token(0));
        }

        [Test]
        public void MixedIndentationIsReportedNotSilent()
        {
            DataFile file = Parse("a\n\tb\n        c\n");

            Assert.IsNotEmpty(file.Diagnostics, "mixing tabs and spaces should warn");
        }

        [Test]
        public void UnterminatedQuoteIsReported()
        {
            DataFile file = Parse("name \"unclosed\n");

            Assert.IsNotEmpty(file.Diagnostics);
            Assert.AreEqual("unclosed", file.Nodes[0].Token(1), "the token still ends at the line break");
        }
    }
}
