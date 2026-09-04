using System;
using System.Collections.Generic;
using System.Text;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Codec;
using RetroIO.Protocols.Xmsg.Node.Seam;
using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Node.Tad;
using RetroIO.Protocols.Xmsg.Packet;
using RetroIO.Protocols.Xmsg.Servers.Tad;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Why a second command on one session renders nothing, and what releases it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This decides something that matters rather than something cosmetic. If the output window is
    /// held only because the peer never acknowledged, it is a limit of the test client and a real
    /// terminal is fine. If it were held for any other reason, a real user would meet it on their
    /// SECOND command - which would be a serious fault hiding behind a test-harness excuse.
    /// </para>
    /// <para><b>Where it is held</b></para>
    /// <para>
    /// <c>DrainSegmentedOutput</c> opens with
    /// <c>if (session.OutstandingOutputCount > 0) return;</c>, and the FINAL frame of a reply is
    /// tracked as outstanding like any other. So the next command builds its burst and is then
    /// held until the previous reply is acknowledged.
    /// </para>
    /// <para><b>What releases it - and what does not</b></para>
    /// <para>
    /// Telling the server directly, <c>NotifyAck(node, flags1)</c>, DOES release it: the second
    /// reply goes out in full. Sending the same acknowledgement as a FRAME from the test client
    /// does not. So the gate is understood and the remaining gap is in the client acknowledgement
    /// or its routing, which is a much smaller question than the one this file started with.
    /// </para>
    /// <para><b>A wrong answer this file already produced</b></para>
    /// <para>
    /// An earlier version scanned the frame body for a <c>07 03 length</c> BDAT header, found
    /// nothing, and reported that even <c>NotifyAck</c> failed to release the window. The reply had
    /// in fact been sent, in full, and the scanner could not read it. That wrong reading was
    /// written up and had to be withdrawn. A diagnostic that can only fail one way is worse than
    /// no diagnostic - the text extraction here is now deliberately crude and framing-independent.
    /// </para>
    /// </remarks>
    public sealed class OutputWindowDiagnosticTests
    {
        private sealed class Pipe : IXmsgTransport
        {
            public Action<byte[]>? Target { get; set; }

            public void Send(ReadOnlySpan<byte> bytes)
            {
                Target?.Invoke(bytes.ToArray());
            }
        }

        private static DateTime FixedClock()
        {
            return new DateTime(1998, 8, 11, 9, 0, 0, DateTimeKind.Utc);
        }

        /// <summary>
        /// With nothing acknowledging the previous reply, a second command renders nothing.
        /// </summary>
        /// <remarks>
        /// The proven half. It is why a chat command that works looked broken under test, and it
        /// is a limit of this client rather than of the server - see the class remarks for the part
        /// that is still open.
        /// </remarks>
        [Fact]
        public void WithNoAcknowledgementTheSecondCommandRendersNothing()
        {
            bool acknowledge = false;
            StringBuilder screen = new StringBuilder();
            List<ushort> serverDataFlags1 = new List<ushort>();

            Pipe serverToClient = new Pipe();
            Pipe clientToServer = new Pipe();

            XmsgCodec serverCodec = new XmsgCodec("server", serverToClient);
            XmsgLayer serverLayer = new XmsgLayer(serverCodec, 102, 0x00);
            serverLayer.AcknowledgeData = false;
            serverLayer.AcknowledgeTadFrames = true;

            XmsgServerHost host = new XmsgServerHost(102);
            TadServer server = new TadServer(FixedClock, null);
            host.Register(server);
            serverLayer.ServerHost = host;

            XmsgCodec clientCodec = new XmsgCodec("client", clientToServer);
            TadConnectClient client = new TadConnectClient(100, 102, 0x0283, seed: 0x14);

            clientCodec.PacketReceived += delegate (string linkId, XmsgPacketInfo packet)
            {
                if (packet.Frame.Header == null)
                {
                    return;
                }

                if (packet.Frame.Header.Subtype == SintranPacketSubtype.Data)
                {
                    serverDataFlags1.Add(packet.Frame.Header.Flags1);
                    AppendBdatText(screen, packet.Frame);
                }
            };

            clientToServer.Target = bytes => serverCodec.ProcessBytes(bytes);
            serverToClient.Target = bytes => clientCodec.ProcessBytes(bytes);

            clientCodec.SendPacket(new XmsgPacket(client.BuildConnect("D102")));
            clientCodec.SendPacket(new XmsgPacket(client.BuildInput("SYSTEM")));
            clientCodec.SendPacket(new XmsgPacket(client.BuildInput("SYSTEM")));

            clientCodec.SendPacket(new XmsgPacket(client.BuildInput("who")));
            Assert.NotEmpty(serverDataFlags1);

            if (acknowledge)
            {
                // Exactly what a real 100 does with the reply it has just displayed. Told to the
                // server directly, so the experiment measures the WINDOW and not the wire.
                ushort last = serverDataFlags1[serverDataFlags1.Count - 1];
                server.NotifyAck(100, last);
            }

            screen.Clear();
            clientCodec.SendPacket(new XmsgPacket(client.BuildInput("stat")));

            if (acknowledge)
            {
                Assert.NotEqual(string.Empty, screen.ToString());
            }
            else
            {
                Assert.Equal(string.Empty, screen.ToString());
            }
        }


        /// <summary>
        /// Acknowledging the previous reply DOES release the window and the second reply is sent.
        /// </summary>
        /// <remarks>
        /// The other half of the pair. Together with the no-acknowledgement case these two say
        /// exactly what the gate is, and they are what corrected the earlier claim that nothing
        /// released it.
        /// </remarks>
        [Fact]
        public void AcknowledgingThePreviousReplyReleasesTheWindow()
        {
            StringBuilder screen = new StringBuilder();
            List<ushort> seen = new List<ushort>();

            Pipe serverToClient = new Pipe();
            Pipe clientToServer = new Pipe();

            XmsgCodec serverCodec = new XmsgCodec("server", serverToClient);
            XmsgLayer serverLayer = new XmsgLayer(serverCodec, 102, 0x00);
            serverLayer.AcknowledgeData = false;
            serverLayer.AcknowledgeTadFrames = true;

            XmsgServerHost host = new XmsgServerHost(102);
            TadServer server = new TadServer(FixedClock, null);
            host.Register(server);
            serverLayer.ServerHost = host;

            XmsgCodec clientCodec = new XmsgCodec("client", clientToServer);
            TadConnectClient client = new TadConnectClient(100, 102, 0x0283, seed: 0x14);

            bool afterSecond = false;
            string afterHex = "(none)";
            clientCodec.PacketReceived += delegate (string linkId, XmsgPacketInfo packet)
            {
                if (packet.Frame.Header == null)
                {
                    return;
                }

                if (afterSecond)
                {
                    afterHex = (afterHex == "(none)" ? string.Empty : afterHex)
                        + " [" + packet.Frame.Header.Subtype + " f1=" + packet.Frame.Header.Flags1
                        + " body=" + Convert.ToHexString(packet.Frame.GetBodyBytes()) + "]";
                }

                if (packet.Frame.Header.Subtype == SintranPacketSubtype.Data)
                {
                    seen.Add(packet.Frame.Header.Flags1);
                    AppendBdatText(screen, packet.Frame);
                }
            };

            clientToServer.Target = bytes => serverCodec.ProcessBytes(bytes);
            serverToClient.Target = bytes => clientCodec.ProcessBytes(bytes);

            clientCodec.SendPacket(new XmsgPacket(client.BuildConnect("D102")));
            clientCodec.SendPacket(new XmsgPacket(client.BuildInput("SYSTEM")));
            clientCodec.SendPacket(new XmsgPacket(client.BuildInput("SYSTEM")));
            clientCodec.SendPacket(new XmsgPacket(client.BuildInput("who")));

            // Every candidate at once: zero and every Flags 1 the client actually saw. If the ack
            // index were the gate, one of these would have to release it.
            server.NotifyAck(100, 0);
            for (int i = 0; i < seen.Count; i++)
            {
                server.NotifyAck(100, seen[i]);
            }
            screen.Clear();
            afterSecond = true;
            clientCodec.SendPacket(new XmsgPacket(client.BuildInput("stat")));
            bool zeroReleased = screen.ToString().Length != 0;

            Assert.True(
                zeroReleased,
                "the second reply did not render. Frames after the second command: " + afterHex);
        }

        /// <summary>
        /// Appends every printable ASCII run in a frame body.
        /// </summary>
        /// <param name="screen">
        /// The text being accumulated.
        /// </param>
        /// <param name="frame">
        /// The frame the server sent.
        /// </param>
        /// <remarks>
        /// Deliberately crude - it does not parse the message framing at all. An earlier version
        /// scanned for a "07 03 length" BDAT header, found nothing, and made a reply that HAD been
        /// sent look like silence. A diagnostic that can only fail one way is worse than none.
        /// </remarks>
        private static void AppendBdatText(StringBuilder screen, XmsgFrame frame)
        {
            byte[] body = frame.GetBodyBytes();
            for (int i = 0; i < body.Length; i++)
            {
                byte c = body[i];
                if (c >= 0x20 && c < 0x7F)
                {
                    screen.Append((char)c);
                }
            }
        }
    }
}
