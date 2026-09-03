using NUnit.Framework;
using RetroIO.Protocols.X25.Nmc;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Tests that the NMC line parser decodes the switch management API responses into typed records,
    /// mirroring the byte-exact lines the switch emits (see docs/NMC_API_V1.md).
    /// </summary>
    [TestFixture]
    public class NmcLineParserTests
    {
        [Test]
        public void Parse_Info()
        {
            NmcParsedLine r = NmcLineParser.Parse("INFO dnic=2340|name=Switch Alpha|location=Local Lab|coords=12N,34E");
            Assert.That(r.Kind, Is.EqualTo(NmcLineKind.Info));
            Assert.That(r.Info!.Dnic, Is.EqualTo("2340"));
            Assert.That(r.Info.Name, Is.EqualTo("Switch Alpha"));
            Assert.That(r.Info.Location, Is.EqualTo("Local Lab"));
            Assert.That(r.Info.Coords, Is.EqualTo("12N,34E"));
        }

        [Test]
        public void Parse_Stats()
        {
            NmcParsedLine r = NmcLineParser.Parse(
                "STATS dnic=2340|ports=5|stitches=2|calls=12|pkts_in=100|pkts_out=97|bytes=4096|up=3600");
            Assert.That(r.Kind, Is.EqualTo(NmcLineKind.Stats));
            Assert.That(r.Stats!.Ports, Is.EqualTo(5));
            Assert.That(r.Stats.Stitches, Is.EqualTo(2));
            Assert.That(r.Stats.Calls, Is.EqualTo(12));
            Assert.That(r.Stats.PacketsIn, Is.EqualTo(100));
            Assert.That(r.Stats.PacketsOut, Is.EqualTo(97));
            Assert.That(r.Stats.Bytes, Is.EqualTo(4096));
            Assert.That(r.Stats.UptimeSeconds, Is.EqualTo(3600));
        }

        [Test]
        public void Parse_Neighbour_WithMgmt()
        {
            NmcParsedLine r = NmcLineParser.Parse("NEIGHBOUR name=Switch-B mgmt=234100 via=1 up=1");
            Assert.That(r.Kind, Is.EqualTo(NmcLineKind.Neighbour));
            Assert.That(r.Neighbour!.Name, Is.EqualTo("Switch-B"));
            Assert.That(r.Neighbour.MgmtAddr, Is.EqualTo("234100"));
            Assert.That(r.Neighbour.Via, Is.EqualTo(1));
            Assert.That(r.Neighbour.Up, Is.True);
        }

        [Test]
        public void Parse_Neighbour_NoMgmt_Down()
        {
            NmcParsedLine r = NmcLineParser.Parse("NEIGHBOUR name=WSL-Reference mgmt= via=2 up=0");
            Assert.That(r.Kind, Is.EqualTo(NmcLineKind.Neighbour));
            Assert.That(r.Neighbour!.Name, Is.EqualTo("WSL-Reference"));
            Assert.That(r.Neighbour.MgmtAddr, Is.EqualTo(string.Empty));
            Assert.That(r.Neighbour.Via, Is.EqualTo(2));
            Assert.That(r.Neighbour.Up, Is.False);
        }

        [Test]
        public void Parse_Route()
        {
            NmcParsedLine r = NmcLineParser.Parse("ROUTE type=prefix addr=2352 port=2 prefix=4");
            Assert.That(r.Kind, Is.EqualTo(NmcLineKind.Route));
            Assert.That(r.Route!.Type, Is.EqualTo("prefix"));
            Assert.That(r.Route.Address, Is.EqualTo("2352"));
            Assert.That(r.Route.Port, Is.EqualTo(2));
            Assert.That(r.Route.PrefixLen, Is.EqualTo(4));
        }

        [Test]
        public void Parse_Route_Default_EmptyAddr()
        {
            NmcParsedLine r = NmcLineParser.Parse("ROUTE type=default addr= port=1 prefix=0");
            Assert.That(r.Route!.Type, Is.EqualTo("default"));
            Assert.That(r.Route.Address, Is.EqualTo(string.Empty));
            Assert.That(r.Route.Port, Is.EqualTo(1));
        }

        [Test]
        public void Parse_Ok_And_Event_And_Pong()
        {
            Assert.That(NmcLineParser.Parse("OK subscribed").Kind, Is.EqualTo(NmcLineKind.Ok));
            Assert.That(NmcLineParser.Parse("EVENT call_setup calling=1 called=2").Kind, Is.EqualTo(NmcLineKind.Event));
            Assert.That(NmcLineParser.Parse("PONG").Kind, Is.EqualTo(NmcLineKind.Pong));
        }

        [Test]
        public void Parse_Unknown_And_Empty()
        {
            Assert.That(NmcLineParser.Parse("FROBNICATE now").Kind, Is.EqualTo(NmcLineKind.Unknown));
            Assert.That(NmcLineParser.Parse(string.Empty).Kind, Is.EqualTo(NmcLineKind.Unknown));
        }

        [Test]
        public void Field_MatchesAtTokenBoundary_NotSubstring()
        {
            // "port" must not match inside "out_port"; the space-delimited VC line has both.
            string payload = "in_port=0 out_port=3 port=9";
            Assert.That(NmcLineParser.Field(payload, "port", ' '), Is.EqualTo("9"));
            Assert.That(NmcLineParser.Field(payload, "out_port", ' '), Is.EqualTo("3"));
            Assert.That(NmcLineParser.Field(payload, "in_port", ' '), Is.EqualTo("0"));
        }
    }
}
