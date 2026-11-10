//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.ListNames;
using RetroIO.Protocols.Xmsg.Node.Services;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// A node with a name table answers a real SINTRAN LIST-NAMES request: the secure ACK, then
    /// the reply, both as D100 sent them.
    /// </summary>
    /// <remarks>
    /// Vectors from the 2026-11-10 COSMOS Ethernet hub capture of D102 walking D100's table, from
    /// SINTRAN byte 0.
    /// </remarks>
    public sealed class ListNamesNodeTests
    {
        // D102's first request (Flags 1 0x0012).
        private const string FirstRequest =
            "2113000e0064006600120100dd022100868400640000006606b201000145000401020000";

        // D100's ACK of it.
        private const string FirstAck = "21130003006600640012" + "0001de0c";

        // D100's reply to it, at D100's own Flags 1 0x0011.
        private const string FirstReply =
            "2113000e0066006400110100dd0321008660006606b2006606b201000100000c010400640001fe0444313030";

        // D102's second request (Flags 1 0x0013).
        private const string SecondRequest =
            "2113000e0064006600130100dd012100868400640000006606b2010001450006010400640002";

        private static InMemoryNameTable SmallD100Table()
        {
            List<NameTableEntry> rows = new List<NameTableEntry>();
            rows.Add(new NameTableEntry(0x00640001u, "D100"));
            rows.Add(new NameTableEntry(0x006401DAu, "*XM-FIDO"));
            return new InMemoryNameTable(rows);
        }

        /// <summary>
        /// Without a server host, the ACK and reply equal the capture once the node's own count
        /// stands where D100's did (0x0011).
        /// </summary>
        [Fact]
        public void Node_acks_and_answers_the_captured_request()
        {
            XmsgNode node = new XmsgNode(nodeNumber: 100, ackCounter: 0x00);
            node.NameTable = SmallD100Table();
            while (node.OutgoingDatagramSequence < 0x0011)
            {
                node.NextDatagramSequence();
            }

            IReadOnlyList<XmsgFrame> sent = node.HandleFrames(XmsgFrame.Parse(Convert.FromHexString(FirstRequest)));

            Assert.Equal(2, sent.Count);
            Assert.Equal(Convert.FromHexString(FirstAck), sent[0].ToArray());
            Assert.Equal(Convert.FromHexString(FirstReply), sent[1].ToArray());
            Assert.Equal((ushort)0x0012, node.OutgoingDatagramSequence);
        }

        /// <summary>
        /// With a server host, the reply's Flags 1 is the host's own per-node count, one step per
        /// reply, and never the request's number.
        /// </summary>
        [Fact]
        public void Node_with_server_host_spends_the_host_count()
        {
            XmsgNode node = new XmsgNode(nodeNumber: 100, ackCounter: 0x00);
            node.NameTable = SmallD100Table();
            node.ServerHost = new XmsgServerHost(100);

            IReadOnlyList<XmsgFrame> first = node.HandleFrames(XmsgFrame.Parse(Convert.FromHexString(FirstRequest)));
            IReadOnlyList<XmsgFrame> second = node.HandleFrames(XmsgFrame.Parse(Convert.FromHexString(SecondRequest)));

            Assert.Equal(2, first.Count);
            Assert.Equal(2, second.Count);
            Assert.Equal(Convert.FromHexString(FirstAck), first[0].ToArray());
            Assert.Equal(SintranPacketSubtype.Ack, second[0].Header.Subtype);
            Assert.Equal((ushort)0x0013, second[0].Header.Flags1);

            ushort firstReply = first[1].Header.Flags1;
            Assert.Equal((ushort)(firstReply + 1), second[1].Header.Flags1);
            Assert.NotEqual((ushort)0x0012, firstReply);

            ListNamesClient client = new ListNamesClient();
            Assert.True(client.TryParseResponse(second[1], out bool end, out NameTableEntry row));
            Assert.False(end);
            Assert.Equal("*XM-FIDO", row.Name);
        }

        /// <summary>
        /// Without a name table the node does not answer LIST-NAMES itself.
        /// </summary>
        [Fact]
        public void Node_without_name_table_sends_no_reply()
        {
            XmsgNode node = new XmsgNode(nodeNumber: 100, ackCounter: 0x00);
            node.AcknowledgeData = false;

            IReadOnlyList<XmsgFrame> sent = node.HandleFrames(XmsgFrame.Parse(Convert.FromHexString(FirstRequest)));

            Assert.Empty(sent);
        }
    }
}
