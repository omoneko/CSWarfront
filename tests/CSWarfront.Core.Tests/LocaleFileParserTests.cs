using CSWarfront.Core;
using Xunit;

namespace CSWarfront.Core.Tests
{
    public class LocaleFileParserTests
    {
        [Fact]
        public void Parses_key_value_lines_and_trims_whitespace()
        {
            var map = LocaleFileParser.Parse("A = Hello\n  B=World  \nC = a = b\n");
            Assert.Equal(3, map.Count);
            Assert.Equal("Hello", map["A"]);
            Assert.Equal("World", map["B"]);
            Assert.Equal("a = b", map["C"]); // only the FIRST '=' splits
        }

        [Fact]
        public void Skips_comments_blank_lines_and_malformed_lines()
        {
            var map = LocaleFileParser.Parse("# comment\n\n=no key\nnokey\nA=1\r\n");
            Assert.Single(map);
            Assert.Equal("1", map["A"]);
        }

        [Fact]
        public void Last_entry_wins_for_duplicate_keys()
        {
            var map = LocaleFileParser.Parse("A=first\nA=second\n");
            Assert.Equal("second", map["A"]);
        }

        [Fact]
        public void Unescapes_newlines_and_round_trips_through_escape()
        {
            var map = LocaleFileParser.Parse("A=line1\\nline2\n");
            Assert.Equal("line1\nline2", map["A"]);

            string original = "multi\nline \\ text";
            Assert.Equal(original, LocaleFileParser.Unescape(LocaleFileParser.Escape(original)));
        }

        [Fact]
        public void Empty_or_null_input_yields_empty_map()
        {
            Assert.Empty(LocaleFileParser.Parse(null));
            Assert.Empty(LocaleFileParser.Parse(""));
        }
    }
}
