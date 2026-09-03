using NUnit.Framework;
using RetroIO.Protocols.X25.Routing;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Validates the routing decisions the cross-environment (C# ⇆ WSL) trunk relies on: a 3-digit DNIC-block
    /// prefix route for the peer network wins over the local default route, so peer calls go out the trunk
    /// while local calls stay internal.
    /// </summary>
    [TestFixture]
    public class CrossEnvRoutingTests
    {
        [Test]
        public void PeerPrefix_BeatsDefault_ForCrossEnvTrunk()
        {
            // Mirrors demo/switch-a.ini: 234x (WSL) -> trunk port 2, everything else -> default port 1.
            X121RoutingTable table = new X121RoutingTable();
            table.AddRoute(new X121Route { Type = X121RouteType.Prefix, Address = "234", PrefixLen = 3, DestPort = 2 });
            table.AddRoute(new X121Route { Type = X121RouteType.Default, DestPort = 1 });

            Assert.That(table.TryLookup("23420001", 0, out int wslPort), Is.True);
            Assert.That(wslPort, Is.EqualTo(2), "a WSL DNIC (234x) routes out the cross-env trunk");

            Assert.That(table.TryLookup("23520001", 0, out int localPort), Is.True);
            Assert.That(localPort, Is.EqualTo(1), "a C# DNIC (235x) falls through to the default (internal) route");
        }
    }
}
