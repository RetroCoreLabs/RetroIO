using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Packet;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Checks that the XSGSY routing builders put a REAL checksum in header word 6.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect this guards</b></para>
    /// Word 6 is a ones-complement checksum over words 0-5, carved from the kernel and confirmed on
    /// 3595/3595 captured frames. <c>SintranHeader.ProtocolId</c> and <c>SintranHeader.Counter</c>
    /// are compatibility views over its HIGH and LOW bytes, so a builder that sets both has
    /// FABRICATED word 6 rather than computed it. A wrong one kills D100 with
    /// <c>XMSG ERROR CODE 24</c>; the same defect on the file-access path cost most of a day, and
    /// <c>TadSession</c> had it too.
    /// <para><b>Why the node numbers are large</b></para>
    /// With both nodes under 256 the checksum sum's high half contributes nothing, so a fabricated
    /// value and a real one can agree by accident - which is exactly how this hid on the file-access
    /// path until node 19999 appeared. These use numbers over 255 so the two readings must differ.
    /// </remarks>
    public sealed class ListRoutingHeaderChecksumTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public ListRoutingHeaderChecksumTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Recomputes the carved checksum for a parsed frame's header.
        /// </summary>
        /// <param name="header">
        /// The header to check.
        /// </param>
        /// <returns>
        /// The checksum word 6 should carry.
        /// </returns>
        private static ushort Carved(SintranHeader header)
        {
            return XmsgEnvelope.ComputeHeaderChecksum(
                (ushort)((header.Marker1 << 8) | header.Marker2),
                (ushort)((header.PacketType << 8) | (byte)header.Subtype),
                header.DestinationNode,
                header.SourceNode,
                header.Flags1,
                header.Flags2);
        }

        /// <summary>
        /// An XSGSY request carries the carved checksum.
        /// </summary>
        [Fact]
        public void AnXsgsyRequestCarriesTheCarvedHeaderChecksum()
        {
            ListRoutingClient client = new ListRoutingClient();

            byte[] info = client.BuildRequest(
                destinationNode: 19999,
                sourceNode: 103,
                querySystem: 100,
                destinationSystem: 100,
                destinationPort: 0x044D,
                sourceSystem: 103,
                sourcePort: 0x0211,
                flags1: 0x0042);

            XmsgFrame frame = XmsgFrame.Parse(info);
            SintranHeader header = frame.Header!;

            _output.WriteLine($"word 6 on the frame : 0x{header.Checksum:X4}");
            _output.WriteLine($"carved checksum     : 0x{Carved(header):X4}");

            Assert.Equal(Carved(header), header.Checksum);
        }

        /// <summary>
        /// An XSGSY response carries the carved checksum.
        /// </summary>
        /// <remarks>
        /// The response is built by answering a request, so this drives the client first and hands
        /// the parsed frame back - the same way the server is used for real.
        /// </remarks>
        [Fact]
        public void AnXsgsyResponseCarriesTheCarvedHeaderChecksum()
        {
            ListRoutingClient client = new ListRoutingClient();
            byte[] requestInfo = client.BuildRequest(
                destinationNode: 19999,
                sourceNode: 103,
                querySystem: 100,
                destinationSystem: 100,
                destinationPort: 0x044D,
                sourceSystem: 103,
                sourcePort: 0x0211,
                flags1: 0x0042);

            XmsgFrame request = XmsgFrame.Parse(requestInfo);

            ListRoutingServer server = new ListRoutingServer();
            RoutingTableEntry entry = new RoutingTableEntry(100, XroutConnectionType.Neighbour, 0, 0, 0);

            byte[] responseInfo = server.BuildResponse(request, entry, flags1: 0x0043);

            XmsgFrame response = XmsgFrame.Parse(responseInfo);
            SintranHeader header = response.Header!;

            _output.WriteLine($"word 6 on the frame : 0x{header.Checksum:X4}");
            _output.WriteLine($"carved checksum     : 0x{Carved(header):X4}");

            Assert.Equal(Carved(header), header.Checksum);
        }
    }
}
