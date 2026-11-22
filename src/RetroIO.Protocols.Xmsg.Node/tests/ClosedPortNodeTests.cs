//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Node.Services;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Data for a port nobody owns is refused with an XEIMA error frame and is NOT acknowledged, as
    /// real ND machines do (RetroIO BUGS.md, closed port, 2026-11-10).
    /// </summary>
    /// <remarks>
    /// The frame is D100's 7WHO from the live TADA hang libxmsg recorded on 2026-11-10 at 09:44:39.232
    /// (libxmsg commit 0755090): node 100 to node 101, Flags 1 <c>0x004A</c>, to port <c>0x00EB</c>,
    /// which belonged to a session lost when the gateway rebooted. The answer real machines give is
    /// from 10 of 10 rejects in four captures (NDInsight TRIO-SEQUENCE-2026-08-11 trio.pcapng
    /// #9333 -> #9349 among them).
    /// </remarks>
    public sealed class ClosedPortNodeTests
    {
        private const string Live7WhoToDeadPort =
            "2113000E00650064004A0048DD83" + "21008214006500EB006409B600480000000002FC00";

        /// <summary>
        /// The answer is one bare 14-byte error frame: subtype 07, back to the sender, at the
        /// sender's Flags 1, Flags 2 FFED, with a correct header checksum - and no ACK.
        /// </summary>
        [Fact]
        public void DataToAClosedPort_GetsXeima_AndNoAck()
        {
            XmsgNode node = new XmsgNode(nodeNumber: 101, ackCounter: 0x00);
            node.ServerHost = new XmsgServerHost(101);
            node.AcknowledgeTadFrames = true;

            XmsgFrame incoming = XmsgFrame.Parse(Convert.FromHexString(Live7WhoToDeadPort));
            Assert.Equal((ushort)0x00EB, incoming.SubHeader!.DestinationPort);

            IReadOnlyList<XmsgFrame> sent = node.HandleFrames(incoming);

            Assert.Single(sent);
            byte[] bytes = sent[0].ToArray();
            Assert.Equal(14, bytes.Length);
            Assert.Equal(SintranPacketSubtype.NetworkError, sent[0].Header.Subtype);
            Assert.Equal((ushort)100, sent[0].Header.DestinationNode);
            Assert.Equal((ushort)101, sent[0].Header.SourceNode);
            Assert.Equal((ushort)0x004A, sent[0].Header.Flags1);
            Assert.Equal((ushort)0xFFED, sent[0].Header.Flags2);

            ushort checksum = Packet.XmsgEnvelope.ComputeHeaderChecksum(
                (ushort)((bytes[0] << 8) | bytes[1]),
                (ushort)((bytes[2] << 8) | bytes[3]),
                0x0064,
                0x0065,
                0x004A,
                0xFFED);
            Assert.Equal(checksum, (ushort)((bytes[12] << 8) | bytes[13]));
        }
    }
}
