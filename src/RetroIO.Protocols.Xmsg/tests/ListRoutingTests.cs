using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.ListRouting;

using System.Collections.Generic;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Verifies the XROUT <c>XSGSY</c> list-routing client and server against the
    /// canonical captured request/response frames (XMSG-PROTOCOL.md section 9.1).
    /// </summary>
    public sealed class ListRoutingTests
    {
        // Canonical captured REQUEST info field (102 -> 100, query system 100), 36 bytes.
        private const string CapturedRequestHex =
            "21 13 00 0e 00 64 00 66 01 2b 01 00 db " +   // SINTRAN header
            "e9 21 00 86 84 00 64 00 00 00 66 02 a5 " +   // XMSG sub-header (part)
            "01 00 01 4b 00 04 " +                        // XMCSM + pad + XMLEN
            "01 02 00 64";                                // param#1 = system 100

        // Canonical captured RESPONSE info field (100 -> 102), 48 bytes.
        private const string CapturedResponseHex =
            "21 13 00 0e 00 66 00 64 00 f4 01 00 dc " +   // SINTRAN header
            "20 21 00 86 60 00 66 02 a5 00 66 02 a5 " +   // XMSG sub-header (part)
            "01 00 01 00 00 10 " +                        // XMCSM + pad + XMLEN
            "01 02 00 64 02 02 00 04 03 02 00 64 04 02 00 00"; // p1..p4

        [Fact]
        public void Client_rebuilds_captured_request_byte_identical()
        {
            byte[] expected = TestHex.Parse(CapturedRequestHex);

            ListRoutingClient client = new ListRoutingClient();
            byte[] actual = client.BuildRequest(
                querySystem: 100,
                destinationNode: 0x0064,
                sourceNode: 0x0066,
                destinationSystem: 0x0064,
                destinationPort: 0x0000,
                sourceSystem: 0x0066,
                sourcePort: 0x02A5,
                flags1: 0x012B,
                flags2: 0x0100,
                frameFlags: 0x86,
                role: 0x84,
                controlService: ListRoutingClient.XmcsmXsgsyRequest);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void Server_rebuilds_captured_response_byte_identical()
        {
            byte[] expected = TestHex.Parse(CapturedResponseHex);

            XmsgFrame request = XmsgFrame.Parse(TestHex.Parse(CapturedRequestHex));
            RoutingTableEntry entry = new RoutingTableEntry(
                system: 100,
                connectionType: XroutConnectionType.Local,
                extraInfo: 100,
                hops: 0,
                wans: 0);

            ListRoutingServer server = new ListRoutingServer();
            byte[] actual = server.BuildResponse(
                request,
                entry,
                flags1: 0x00F4,
                flags2: 0x0100,
                frameFlags: 0x86,
                role: 0x60,
                controlService: ListRoutingServer.XmcsmXsgsyReply);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void Client_parses_response()
        {
            XmsgFrame response = XmsgFrame.Parse(TestHex.Parse(CapturedResponseHex));

            ListRoutingClient client = new ListRoutingClient();
            RoutingTableEntry entry = client.ParseResponse(response);

            Assert.Equal((ushort)100, entry.System);
            Assert.Equal(XroutConnectionType.Local, entry.ConnectionType);
            Assert.Equal((ushort)100, entry.ExtraInfo);
            Assert.Equal((byte)0, entry.Hops);
            Assert.Equal((byte)0, entry.Wans);
        }

        [Fact]
        public void Client_parses_request_query()
        {
            XmsgFrame request = XmsgFrame.Parse(TestHex.Parse(CapturedRequestHex));

            ListRoutingClient client = new ListRoutingClient();
            ushort query = client.ParseRequestQuery(request);

            Assert.Equal((ushort)100, query);
        }

        [Fact]
        public void Server_handle_from_table()
        {
            // A table with a single Local entry for system 100.
            List<RoutingTableEntry> entries = new List<RoutingTableEntry>();
            entries.Add(new RoutingTableEntry(100, XroutConnectionType.Local, 100, 0, 0));
            InMemoryRoutingTable table = new InMemoryRoutingTable(entries);

            XmsgFrame request = XmsgFrame.Parse(TestHex.Parse(CapturedRequestHex));

            ListRoutingServer server = new ListRoutingServer();
            ListRoutingClient client = new ListRoutingClient();

            // Hit: query for 100 resolves to the Local entry.
            byte[] hitBytes = server.Handle(request, table, flags1: 0x00F4);
            XmsgFrame hitFrame = XmsgFrame.Parse(hitBytes);
            RoutingTableEntry hit = client.ParseResponse(hitFrame);

            Assert.Equal((ushort)100, hit.System);
            Assert.Equal(XroutConnectionType.Local, hit.ConnectionType);
            Assert.Equal((ushort)100, hit.ExtraInfo);
            Assert.Equal((byte)0, hit.Hops);
            Assert.Equal((byte)0, hit.Wans);

            // Miss: a table whose only entry (system 50) is below the query 100.
            List<RoutingTableEntry> lowEntries = new List<RoutingTableEntry>();
            lowEntries.Add(new RoutingTableEntry(50, XroutConnectionType.Local, 0, 0, 0));
            InMemoryRoutingTable lowTable = new InMemoryRoutingTable(lowEntries);

            byte[] missBytes = server.Handle(request, lowTable, flags1: 0x00F4);
            XmsgFrame missFrame = XmsgFrame.Parse(missBytes);
            RoutingTableEntry miss = client.ParseResponse(missFrame);

            Assert.Equal((ushort)0, miss.System);
            Assert.Equal(XroutConnectionType.Unavailable, miss.ConnectionType);
        }
    }
}
