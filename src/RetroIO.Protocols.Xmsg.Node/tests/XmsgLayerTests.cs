//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Node;
using RetroIO.Protocols.Xmsg.Codec;
using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Node.Seam;
using RetroIO.Protocols.Xmsg.Node.Tad;
using RetroIO.Protocols.Xmsg.Packet;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Phase 4 gate for <see cref="XmsgLayer"/>: driven with captured incoming packets through the
    /// real codec seam, the layer must emit the byte-exact responses the live-verified path produces
    /// - reachability reply, list-route (XSGSY) reply, TAD connect ACK + accept, and port-assign -
    /// and raise the matching up-events. This proves the seam packaging did not alter any wire byte.
    /// </summary>
    public sealed class XmsgLayerTests
    {
        // Records every information field the layer sends down through the codec.
        private sealed class RecordingTransport : IXmsgTransport
        {
            public List<byte[]> Sent { get; } = new List<byte[]>();

            public void Send(ReadOnlySpan<byte> bytes)
            {
                Sent.Add(bytes.ToArray());
            }
        }

        private const string ConnectRequestHex =
            "2113000E0067006400000400DA13210086E400670000006402F7040000410010FF072A54414441444D00FE0444313033";

        private const string SessionSetupHex =
            "2113000E0067006400010400DA122100868400670156006402F704000000000906001B001C0100FF00";

        private static (XmsgCodec codec, RecordingTransport transport, XmsgLayer layer) BuildSeam(ushort node)
        {
            RecordingTransport transport = new RecordingTransport();
            XmsgCodec codec = new XmsgCodec("hdlc:test", transport);
            XmsgLayer layer = new XmsgLayer(codec, node, 0x00);
            return (codec, transport, layer);
        }

        [Fact]
        public void Reachability_EmitsByteExactReply()
        {
            (XmsgCodec codec, RecordingTransport transport, XmsgLayer layer) = BuildSeam(102);

            // Reachability request 100 -> 102 (trailing 0x08); expect reply 102 -> 100 trailing 0x0E.
            codec.ProcessBytes(Convert.FromHexString("2113001900660064FFFF0001DE08"));

            Assert.Single(transport.Sent);
            Assert.Equal(
                Convert.FromHexString("2113001300640066FFFF0001DE0E"),
                transport.Sent[0]);
        }

        [Fact]
        public void TadConnect_EmitsAckThenAccept_AndRaisesSessionOpened()
        {
            (XmsgCodec codec, RecordingTransport transport, XmsgLayer layer) = BuildSeam(103);
            layer.TadResponder = new TadTerminalResponder(103, () => new DateTime(2026, 7, 2));
            layer.AcknowledgeTadFrames = true;

            ushort openedSystem = 0;
            ushort openedPort = 0;
            int opened = 0;
            layer.SessionOpened += delegate (string linkId, ushort clientSystem, ushort clientPort)
            {
                opened++;
                openedSystem = clientSystem;
                openedPort = clientPort;
            };

            codec.ProcessBytes(Convert.FromHexString(ConnectRequestHex));

            // Response order: secure ACK on DE (connect DA + 4) trailing 0x1D, then the connect-accept.
            Assert.Equal(2, transport.Sent.Count);
            Assert.Equal(
                Convert.FromHexString("211300030064006700000001DE1D"),
                transport.Sent[0]);
            // Accept ECHOES the connect: channel DA, f1 0x0000, counter 0x13 (the form 100 accepts).
            byte[] expectedAccept = Convert.FromHexString(
                "2113000E00640067" + "0000" + "0400" + "DA" + "13" + "2100" + "86" + "40"
                + "0064" + "02F7" + "0067" + "0156" + "04000041" + "00" + "08"
                + "01020000" + "0202000A");
            Assert.Equal(expectedAccept, transport.Sent[1]);

            // The up-event fired once with the connecting endpoint (system 100, its asking port).
            Assert.Equal(1, opened);
            Assert.Equal(0x0064, openedSystem);
            Assert.Equal(0x02F7, openedPort);
        }

        [Fact]
        public void TadSessionSetup_EmitsAckThenPortAssign()
        {
            (XmsgCodec codec, RecordingTransport transport, XmsgLayer layer) = BuildSeam(103);
            layer.TadResponder = new TadTerminalResponder(103, () => new DateTime(2026, 7, 2));
            layer.AcknowledgeTadFrames = true;

            // Establish the session first (connect), then feed the session-setup 100 sends next.
            codec.ProcessBytes(Convert.FromHexString(ConnectRequestHex));
            transport.Sent.Clear();

            codec.ProcessBytes(Convert.FromHexString(SessionSetupHex));

            // Response order: secure ACK, then the port-assignment frame.
            Assert.Equal(2, transport.Sent.Count);
            // Port-assign ECHOES the setup: channel DA, f1 0x0001, counter 0x12.
            byte[] expectedAssign = Convert.FromHexString(
                "2113000E00640067" + "0001" + "0400" + "DA" + "12" + "2100" + "86" + "40"
                + "0064" + "02F7" + "0067" + "0156" + "04000000" + "00" + "18"
                + "00" + "0705" + "00006702" + "11" + "1F03" + "4C0000"
                + "00" + "0B02" + "0300" + "1502" + "0108" + "FF00");
            Assert.Equal(expectedAssign, transport.Sent[1]);
        }

        [Fact]
        public void ListRoute_MatchesProvenNodePath()
        {
            // The exact 100 -> 103 XSGSY list-route request captured on the wire.
            byte[] reqInfo = Convert.FromHexString(
                "2113000E0067006400010100DD12210086C400670000006402A60100014B000401020067");
            RoutingTableEntry[] entries =
            {
                new RoutingTableEntry(103, XroutConnectionType.Local, 103, 0, 0),
            };

            // Reference: the proven XmsgNode path.
            XmsgNode reference = new XmsgNode(103, 0x00);
            reference.AcknowledgeData = false;
            reference.RoutingTable = new InMemoryRoutingTable(entries);
            XmsgFrame? refReply = reference.HandleFrame(XmsgFrame.Parse(reqInfo));
            Assert.NotNull(refReply);

            // Through the seam: the layer must produce byte-identical XSGSY reply bytes.
            (XmsgCodec codec, RecordingTransport transport, XmsgLayer layer) = BuildSeam(103);
            layer.RoutingTable = new InMemoryRoutingTable(entries);
            codec.ProcessBytes(reqInfo);

            Assert.Single(transport.Sent);
            Assert.Equal(refReply!.ToArray(), transport.Sent[0]);
        }
    }
}
