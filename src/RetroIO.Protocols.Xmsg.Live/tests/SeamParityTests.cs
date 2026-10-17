//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Codec;
using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Live;
using RetroIO.Protocols.Xmsg.Live.Seam;
using RetroIO.Protocols.Xmsg.Node;
using RetroIO.Protocols.Xmsg.Node.Seam;
using RetroIO.Protocols.Xmsg.Node.Tad;
using Xunit;

namespace RetroIO.Protocols.Xmsg.Live.Tests
{
    /// <summary>
    /// Phase 5 offline parity gate: the restructured seam composition (LapbLayerAdapter + XmsgCodec +
    /// XmsgLayer) must produce byte-for-byte the SAME wire output as the proven legacy composition
    /// (LiveNode + XmsgNode) when driven with the identical inbound HDLC stream - a scripted
    /// connect-to handshake (peer SABM, connect request, session-setup). Byte-identical output here
    /// means the seam changed no wire behaviour; the remaining live half of the gate (against machine
    /// 100) is run by the user.
    /// </summary>
    public sealed class SeamParityTests
    {
        // The exact live connect request and session-setup 100 -> 103 (proto DA), as captured.
        private const string ConnectRequestHex =
            "2113000E0067006400000400DA13210086E400670000006402F7040000410010FF072A54414441444D00FE0444313033";
        private const string SessionSetupHex =
            "2113000E0067006400010400DA122100868400670156006402F704000000000906001B001C0100FF00";

        private static readonly Func<DateTime> FixedClock = () => new DateTime(2026, 7, 2);

        [Fact]
        public async Task SeamComposition_ProducesIdenticalWireBytes_AsLegacy()
        {
            byte[] inbound = BuildInboundHandshake();

            byte[] legacyWire = await RunLegacy(inbound);
            byte[] seamWire = await RunSeam(inbound);

            Assert.Equal(legacyWire, seamWire);
        }

        /// <summary>
        /// Runs the legacy LiveNode + XmsgNode path over the inbound stream; returns the wire bytes it writes.
        /// </summary>
        private static async Task<byte[]> RunLegacy(byte[] inbound)
        {
            InMemoryDuplex duplex = new InMemoryDuplex(inbound);
            LapbLayer link = new LapbLayer(ownNode: 103);
            XmsgNode node = new XmsgNode(103, 0x00);
            ConfigureNode(node);

            LiveNode live = new LiveNode(duplex, link, node);
            link.Connect(0);
            await live.RunWithoutTimersAsync(CancellationToken.None);
            return duplex.GetWrittenBytes();
        }

        /// <summary>
        /// Runs the seam LapbLayerAdapter + XmsgCodec + XmsgLayer path over the inbound stream; returns the wire bytes it writes.
        /// </summary>
        private static async Task<byte[]> RunSeam(byte[] inbound)
        {
            InMemoryDuplex duplex = new InMemoryDuplex(inbound);
            LapbLayer link = new LapbLayer(ownNode: 103);
            LapbLayerAdapter adapter = new LapbLayerAdapter("hdlc:test", duplex, link);
            LinkXmsgTransport codecTransport = new LinkXmsgTransport(adapter);
            XmsgCodec codec = new XmsgCodec("hdlc:test", codecTransport);
            XmsgLayer layer = new XmsgLayer(codec, 103, 0x00);
            ConfigureLayer(layer);

            adapter.PayloadReceived += delegate (ILink link, byte[] payload, int length)
            {
                codec.ProcessBytes(payload.AsSpan(0, length));
            };

            adapter.Initiate();
            await adapter.RunWithoutTimersAsync(CancellationToken.None);
            return duplex.GetWrittenBytes();
        }

        private static void ConfigureNode(XmsgNode node)
        {
            node.AcknowledgeData = false;
            node.RoutingTable = new InMemoryRoutingTable(RoutingEntries());
            node.TadResponder = new TadTerminalResponder(103, FixedClock);
            node.AcknowledgeTadFrames = true;
        }

        private static void ConfigureLayer(XmsgLayer layer)
        {
            layer.AcknowledgeData = false;
            layer.RoutingTable = new InMemoryRoutingTable(RoutingEntries());
            layer.TadResponder = new TadTerminalResponder(103, FixedClock);
            layer.AcknowledgeTadFrames = true;
        }

        private static RoutingTableEntry[] RoutingEntries()
        {
            return new RoutingTableEntry[]
            {
                new RoutingTableEntry(100, XroutConnectionType.Neighbour, 1, 1, 0),
                new RoutingTableEntry(102, XroutConnectionType.Via, 100, 2, 0),
                new RoutingTableEntry(103, XroutConnectionType.Local, 103, 0, 0),
            };
        }

        /// <summary>
        /// Builds the inbound HDLC byte stream: the peer (node 100) SABM, then two data I-frames
        /// carrying the connect request (N(S)=0) and the session-setup (N(S)=1).
        /// </summary>
        private static byte[] BuildInboundHandshake()
        {
            List<byte> inbound = new List<byte>();
            inbound.AddRange(HdlcEncoder.Encode(new byte[] { 0x01, 0x3F, 0x00, 0x64 }));   // peer SABM (node 100)
            // The peer's UA answering OUR SABM. Spec 3.1: both directions must complete a
            // SABM -> UA exchange before the link is UP, and a station MUST answer a received SABM
            // with UA. Omitting it modelled a non-conformant peer; the adapter now declines to
            // report such a link usable. See LapbLinkUpGatingTests.
            inbound.AddRange(HdlcEncoder.Encode(new byte[] { 0x01, 0x73, 0x00, 0x64 }));   // peer UA
            inbound.AddRange(HdlcEncoder.Encode(IFrame(sendSeq: 0, receiveSeq: 0, Convert.FromHexString(ConnectRequestHex))));
            inbound.AddRange(HdlcEncoder.Encode(IFrame(sendSeq: 1, receiveSeq: 0, Convert.FromHexString(SessionSetupHex))));
            return inbound.ToArray();
        }

        /// <summary>
        /// Builds a LAPB data I-frame body: addr 0x09, control from N(S)/N(R), then info.
        /// </summary>
        private static byte[] IFrame(int sendSeq, int receiveSeq, byte[] info)
        {
            byte control = (byte)((receiveSeq << 5) | (sendSeq << 1));
            byte[] body = new byte[2 + info.Length];
            body[0] = 0x09;
            body[1] = control;
            Array.Copy(info, 0, body, 2, info.Length);
            return body;
        }
    }
}
