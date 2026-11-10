//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Node.Services;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// A node with a routing table answers a list-route (<c>XSGSY</c>, service 75) request the way
    /// real machines do: it ACKs the request, then replies at its OWN next Flags 1, and a repeated
    /// request is answered again at the same number.
    /// </summary>
    /// <remarks>
    /// Vectors from the 102-asks-100 capture that <c>ListRoutingTests</c> also rebuilds: request
    /// Flags 1 <c>0x012B</c>, answered by D100 at <c>0x00F4</c>.
    /// </remarks>
    public sealed class ListRouteNodeTests
    {
        // D102's request (Flags 1 0x012B), asking about system 100.
        private const string CapturedRequest =
            "2113000e00640066012b0100dbe92100868400640000006602a50100014b000401020064";

        // D100's reply (Flags 1 0x00F4).
        private const string CapturedResponse =
            "2113000e0066006400f40100dc2021008660006602a5006602a501000100001001020064020200040302006404020000";

        private static InMemoryRoutingTable D100Routes()
        {
            List<RoutingTableEntry> entries = new List<RoutingTableEntry>();
            entries.Add(new RoutingTableEntry(100, XroutConnectionType.Local, 100, 0, 0));
            return new InMemoryRoutingTable(entries);
        }

        private static XmsgNode NodeAt(ushort nextFlags1)
        {
            XmsgNode node = new XmsgNode(nodeNumber: 100, ackCounter: 0x00);
            node.RoutingTable = D100Routes();
            while (node.OutgoingDatagramSequence < nextFlags1)
            {
                node.NextDatagramSequence();
            }

            return node;
        }

        /// <summary>
        /// The node ACKs the request (Flags 1 0x012B), then sends the captured reply byte for byte,
        /// at its own count (0x00F4), not the request's number.
        /// </summary>
        [Fact]
        public void Node_acks_and_answers_at_its_own_number()
        {
            XmsgNode node = NodeAt(0x00F4);

            IReadOnlyList<XmsgFrame> sent = node.HandleFrames(XmsgFrame.Parse(Convert.FromHexString(CapturedRequest)));

            Assert.Equal(2, sent.Count);
            Assert.Equal(SintranPacketSubtype.Ack, sent[0].Header!.Subtype);
            Assert.Equal((ushort)0x012B, sent[0].Header!.Flags1);
            Assert.Equal((ushort)0x0066, sent[0].Header!.DestinationNode);
            Assert.Equal(Convert.FromHexString(CapturedResponse), sent[1].ToArray());
            Assert.Equal((ushort)0x00F5, node.OutgoingDatagramSequence);
        }

        /// <summary>
        /// A repeat of the same request (same Flags 1) is ACKed again and answered at the SAME reply
        /// number; no new number is spent.
        /// </summary>
        [Fact]
        public void Repeated_request_is_answered_at_the_same_number()
        {
            XmsgNode node = NodeAt(0x00F4);
            byte[] request = Convert.FromHexString(CapturedRequest);

            IReadOnlyList<XmsgFrame> first = node.HandleFrames(XmsgFrame.Parse(request));
            IReadOnlyList<XmsgFrame> repeat = node.HandleFrames(XmsgFrame.Parse(request));

            Assert.Equal(2, repeat.Count);
            Assert.Equal(SintranPacketSubtype.Ack, repeat[0].Header!.Subtype);
            Assert.Equal(first[1].ToArray(), repeat[1].ToArray());
            Assert.Equal((ushort)0x00F5, node.OutgoingDatagramSequence);
        }

        /// <summary>
        /// With a server host attached, the reply's number comes from the host's per-node count, one
        /// step per new request, and a repeat spends none.
        /// </summary>
        [Fact]
        public void Node_with_server_host_spends_the_host_count()
        {
            XmsgNode node = new XmsgNode(nodeNumber: 100, ackCounter: 0x00);
            node.RoutingTable = D100Routes();
            node.ServerHost = new XmsgServerHost(100);

            byte[] request = Convert.FromHexString(CapturedRequest);
            XmsgFrame second = XmsgFrame.Parse(request);
            second.Header!.Flags1 = 0x012C;

            IReadOnlyList<XmsgFrame> a = node.HandleFrames(XmsgFrame.Parse(request));
            IReadOnlyList<XmsgFrame> again = node.HandleFrames(XmsgFrame.Parse(request));
            IReadOnlyList<XmsgFrame> b = node.HandleFrames(second);

            ushort firstReply = a[1].Header!.Flags1;
            Assert.NotEqual((ushort)0x012B, firstReply);
            Assert.Equal(firstReply, again[1].Header!.Flags1);
            Assert.Equal((ushort)(firstReply + 1), b[1].Header!.Flags1);
            Assert.Equal((ushort)0x012C, b[0].Header!.Flags1);
        }
    }
}
