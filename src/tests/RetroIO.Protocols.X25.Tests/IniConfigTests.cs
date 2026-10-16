using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Link.Lapb;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Common;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Tests for <see cref="IniConfig"/>, verifying parity with the C parser (src/util/config.c):
    /// first-match lookup, comments, sections, first-<c>=</c> split with spaces kept, atoi semantics,
    /// and the entry cap.
    /// </summary>
    [TestFixture]
    public class IniConfigTests
    {
        private static IniConfig Parse(params string[] lines)
        {
            return IniConfig.Parse(new List<string>(lines));
        }

        [Test]
        public void Section_And_Key_Lookup()
        {
            IniConfig c = Parse("[switch]", "dnic = 2342", "name = Switch A");

            Assert.That(c.GetString("switch", "dnic", "?"), Is.EqualTo("2342"));
            Assert.That(c.GetString("switch", "name", "?"), Is.EqualTo("Switch A"), "interior spaces kept");
            Assert.That(c.HasSection("switch"), Is.True);
            Assert.That(c.HasSection("nope"), Is.False);
        }

        [Test]
        public void Missing_Key_ReturnsDefault()
        {
            IniConfig c = Parse("[switch]", "dnic = 2342");

            Assert.That(c.GetString("switch", "absent", "def"), Is.EqualTo("def"));
            Assert.That(c.Get("switch", "absent"), Is.Null);
            Assert.That(c.GetInt("switch", "absent", 15), Is.EqualTo(15));
        }

        [Test]
        public void Comments_And_BlankLines_Ignored()
        {
            IniConfig c = Parse("# comment", "; also comment", "", "[s]", "  k = v  ");

            Assert.That(c.Count, Is.EqualTo(1));
            Assert.That(c.GetString("s", "k", "?"), Is.EqualTo("v"), "key and value trimmed");
        }

        [Test]
        public void FirstEquals_Splits_ValueKeepsRest()
        {
            IniConfig c = Parse("[s]", "expr = a = b = c");

            Assert.That(c.GetString("s", "expr", "?"), Is.EqualTo("a = b = c"), "split on first = only");
        }

        [Test]
        public void FirstMatch_Wins_ForDuplicateKey()
        {
            IniConfig c = Parse("[s]", "k = first", "k = second");

            Assert.That(c.GetString("s", "k", "?"), Is.EqualTo("first"));
        }

        [Test]
        public void Atoi_LeadingDigits_ZeroOnNonNumeric()
        {
            Assert.That(IniConfig.Atoi("128"), Is.EqualTo(128));
            Assert.That(IniConfig.Atoi("9600baud"), Is.EqualTo(9600), "stops at first non-digit");
            Assert.That(IniConfig.Atoi("-7"), Is.EqualTo(-7));
            Assert.That(IniConfig.Atoi("abc"), Is.EqualTo(0), "non-numeric -> 0");
            Assert.That(IniConfig.Atoi(""), Is.EqualTo(0));
        }

        [Test]
        public void GetInt_UsesAtoi()
        {
            IniConfig c = Parse("[port.0]", "baud = 9600x", "packet_size = 128");

            Assert.That(c.GetInt("port.0", "baud", 0), Is.EqualTo(9600));
            Assert.That(c.GetInt("port.0", "packet_size", 0), Is.EqualTo(128));
        }

        [Test]
        public void MaxKeys_Cap_DropsExcess_ButSectionsStillAdvance()
        {
            // Cap of 2: only the first two key/value lines are retained; a later section still advances.
            IniConfig c = IniConfig.Parse(new List<string> { "[a]", "k1 = 1", "k2 = 2", "k3 = 3", "[b]", "k4 = 4" }, maxKeys: 2);

            Assert.That(c.Count, Is.EqualTo(2));
            Assert.That(c.GetString("a", "k1", "?"), Is.EqualTo("1"));
            Assert.That(c.GetString("a", "k2", "?"), Is.EqualTo("2"));
            Assert.That(c.Get("a", "k3"), Is.Null, "dropped past the cap");
            Assert.That(c.Get("b", "k4"), Is.Null, "dropped past the cap");
        }

        [Test]
        public void GetSection_EnumeratesInOrder()
        {
            IniConfig c = Parse("[hunt.0]", "members = 1,2,3", "[hunt.0]", "extra = x");

            List<KeyValuePair<string, string>> got = new List<KeyValuePair<string, string>>(c.GetSection("hunt.0"));
            Assert.That(got.Count, Is.EqualTo(2));
            Assert.That(got[0].Key, Is.EqualTo("members"));
            Assert.That(got[1].Key, Is.EqualTo("extra"));
        }
    }
}
