using System;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Node;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Layer 3 proof: the node answers a captured reachability request with the exact
    /// captured reply, and a captured data frame with the exact captured secure ACK.
    /// </summary>
    public sealed class XmsgNodeTests
    {
        /// <summary>
        /// The captured reachability request (100 -> 102) must yield the exact captured
        /// reply (102 -> 100) byte-for-byte.
        /// </summary>
        [Fact]
        public void ReachabilityRequest_ProducesExactCapturedReply()
        {
            // VERIFIED capture pair (XMSG-PROTOCOL.md section 5.1).
            byte[] requestBytes = LiveTestHex.Parse("21 13 00 19 00 66 00 64 FF FF 00 01 DE 08");
            byte[] expectedReply = LiveTestHex.Parse("21 13 00 13 00 64 00 66 FF FF 00 01 DE 0E");

            XmsgFrame request = XmsgFrame.Parse(requestBytes);
            XmsgNode node = new XmsgNode(nodeNumber: 102, ackCounter: 0x00);

            XmsgFrame? reply = node.HandleFrame(request);

            Assert.NotNull(reply);
            Assert.Equal(SintranPacketSubtype.ReachabilityReply, reply!.Header.Subtype);
            Assert.Equal(expectedReply, reply.ToArray());
        }

        /// <summary>
        /// A data frame in the 102 -> 103 direction must yield the exact captured secure
        /// ACK (103 -> 102) echoing Flags 1 and carrying the receiver's counter byte.
        /// </summary>
        [Fact]
        public void DataFrame_ProducesExactCapturedSecureAck()
        {
            // OBSERVED capture: ACK 21 13 00 03 00 66 00 67 00 04 00 01 DE 17.
            // That ACK answers a data frame 102->103 with datagram sequence 0x0004; the
            // node's own per-direction counter at that point is 0x17.
            byte[] expectedAck = LiveTestHex.Parse("21 13 00 03 00 66 00 67 00 04 00 01 DE 17");

            XmsgFrame data = new XmsgFrame();
            data.Header.Subtype = SintranPacketSubtype.Data;      // 0x0E
            data.Header.DestinationNode = 0x0067;                 // 103
            data.Header.SourceNode = 0x0066;                      // 102
            data.Header.Flags1 = 0x0004;                          // datagram sequence
            data.Header.Flags2 = 0x0400;                          // a data frame-class word
            data.Header.ProtocolId = SintranProtocolId.Routing;   // 0xDE
            data.ClearRawBytes();                                 // serialise the ACK from the model

            XmsgNode node = new XmsgNode(nodeNumber: 103, ackCounter: 0x17);

            bool delivered = false;
            ushort deliveredSeq = 0;
            node.OnDataDelivered += delegate (ushort seq) { delivered = true; deliveredSeq = seq; };

            XmsgFrame? ack = node.HandleFrame(data);

            Assert.NotNull(ack);
            Assert.True(delivered);
            Assert.Equal(0x0004, deliveredSeq);
            Assert.Equal(SintranPacketSubtype.Ack, ack!.Header.Subtype);
            Assert.Equal(0x0004, ack.Header.Flags1);              // echoes the datagram sequence
            Assert.Equal(expectedAck, ack.ToArray());
            // Counter decremented after producing the ACK.
            Assert.Equal(0x16, node.AckCounter);
        }

        /// <summary>
        /// The remote-name table resolves many aliases to one system number and rejects
        /// undefined names.
        /// </summary>
        [Fact]
        public void RemoteNameTable_ResolvesManyAliasesToOneSystem()
        {
            XmsgNode node = new XmsgNode(nodeNumber: 100, ackCounter: 0x00);
            node.DefineRemote("D102", 102);
            node.DefineRemote("main", 102);   // alias, same system as D102
            node.DefineRemote("D103", 103);

            Assert.True(node.TryResolveRemote("d102", out ushort s1));   // case-insensitive
            Assert.Equal(102, s1);
            Assert.True(node.TryResolveRemote("main", out ushort s2));
            Assert.Equal(102, s2);
            Assert.True(node.TryResolveRemote("D103", out ushort s3));
            Assert.Equal(103, s3);
            Assert.False(node.TryResolveRemote("unknown", out ushort s4));
            Assert.Equal(0, s4);
        }

        /// <summary>
        /// The outgoing datagram sequence advances monotonically per call.
        /// </summary>
        [Fact]
        public void OutgoingDatagramSequence_AdvancesPerCall()
        {
            XmsgNode node = new XmsgNode(nodeNumber: 100, ackCounter: 0x00);
            Assert.Equal(0, node.OutgoingDatagramSequence);
            Assert.Equal(0, node.NextDatagramSequence());
            Assert.Equal(1, node.NextDatagramSequence());
            Assert.Equal(2, node.OutgoingDatagramSequence);
        }
    }
}
