using System;
using System.Collections.Generic;
using System.Text;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Api.Node;
using RetroIO.Protocols.Xmsg.Hdlc;
using RetroIO.Protocols.Xmsg.Live;
using RetroIO.Protocols.Xmsg.Node.Services;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Api.Node.Tests
{
    /// <summary>
    /// Runs two XMSG systems against each other over the real datagram path: kernel, bridge, node
    /// host, serialise, HDLC-frame, de-frame, parse, route into the other system.
    /// </summary>
    /// <remarks>
    /// This is what the bridge was built for. Everything below the API here is production code -
    /// the host assigns the per-link Flags 1 and derives the Counter and channel, the frame is
    /// serialised to its wire bytes, byte-stuffed into an HDLC frame, split back out and re-parsed.
    /// The only stand-in is the medium itself, deliberately a plain byte hand-off: LAPB sequencing
    /// and retransmission are covered by RetroIO.Protocols.Xmsg.Live.Tests and repeating them here would add nothing.
    /// </remarks>
    public sealed class TwoNodeKernelLinkTests
    {
        private const ushort NodeA = 100;
        private const ushort NodeB = 102;

        /// <summary>
        /// A message written on one system arrives in a port queue on the other, carrying the
        /// sender's magic number, after a full serialise, frame, de-frame and parse round trip.
        /// </summary>
        [Fact]
        public void Message_CrossesTheLink_AndArrivesWithItsSender()
        {
            Link link = new Link();

            XmsgMagicNumber senderMagic;
            XmsgPortNumber senderPort;
            link.OpenPortOn(link.A, out senderPort, out senderMagic);

            XmsgMagicNumber targetMagic;
            XmsgPortNumber targetPort;
            link.OpenPortOn(link.B, out targetPort, out targetMagic);

            XmsgMessageIdentifier message;
            link.A.Kernel.ReserveBuffer(16, XmsgBufferOptions.None, out message);
            int written;
            link.A.Kernel.Write(message, Encoding.ASCII.GetBytes("PING"), 0, false, out written);

            Assert.False(link.A.Kernel.Send(targetMagic, senderPort, XmsgSendFlags.None).IsError);

            int carried = link.PumpAToB();
            Assert.Equal(1, carried);

            XmsgReceiveResult arrived = link.B.Kernel.Receive(targetPort, XmsgWaitOptions.None);
            Assert.True(arrived.Received);
            Assert.Equal(4, arrived.Length);
            Assert.Equal(senderMagic, link.B.Kernel.GetMessageStatus(arrived.Message).Sender);

            byte[] body = new byte[4];
            int read;
            link.B.Kernel.Read(arrived.Message, body, 0, out read);
            Assert.Equal("PING", Encoding.ASCII.GetString(body));
        }

        /// <summary>
        /// The far side answers using only what arrived, which is the whole point of carrying the
        /// sender's magic number across the link.
        /// </summary>
        [Fact]
        public void Reply_UsesOnlyTheSenderLearnedFromTheMessage()
        {
            Link link = new Link();

            XmsgMagicNumber clientMagic;
            XmsgPortNumber clientPort;
            link.OpenPortOn(link.A, out clientPort, out clientMagic);

            XmsgMagicNumber serverMagic;
            XmsgPortNumber serverPort;
            link.OpenPortOn(link.B, out serverPort, out serverMagic);

            SendFrom(link.A, clientPort, serverMagic, "REQ");
            link.PumpAToB();

            XmsgReceiveResult request = link.B.Kernel.Receive(serverPort, XmsgWaitOptions.None);
            Assert.True(request.Received);

            // The server knows nothing about A except what the message status tells it.
            XmsgMagicNumber learned = link.B.Kernel.GetMessageStatus(request.Message).Sender;
            Assert.Equal(clientMagic, learned);

            SendFrom(link.B, serverPort, learned, "ANS");
            link.PumpBToA();

            XmsgReceiveResult reply = link.A.Kernel.Receive(clientPort, XmsgWaitOptions.None);
            Assert.True(reply.Received);

            byte[] body = new byte[3];
            int read;
            link.A.Kernel.Read(reply.Message, body, 0, out read);
            Assert.Equal("ANS", Encoding.ASCII.GetString(body));
        }

        /// <summary>
        /// Each datagram that crosses carries a distinct Flags 1, because the host owns one
        /// outgoing sequence per link shared by everything running on it.
        /// </summary>
        [Fact]
        public void EachDatagram_AdvancesTheLinkSequence()
        {
            Link link = new Link();

            XmsgPortNumber clientPort;
            XmsgMagicNumber clientMagic;
            link.OpenPortOn(link.A, out clientPort, out clientMagic);

            XmsgPortNumber serverPort;
            XmsgMagicNumber serverMagic;
            link.OpenPortOn(link.B, out serverPort, out serverMagic);

            List<ushort> sequences = new List<ushort>();
            for (int i = 0; i < 4; i++)
            {
                SendFrom(link.A, clientPort, serverMagic, "M" + i.ToString());
                link.PumpAToB(sequences);
            }

            Assert.Equal(4, sequences.Count);
            for (int i = 1; i < sequences.Count; i++)
            {
                Assert.NotEqual(sequences[i - 1], sequences[i]);
            }
        }

        /// <summary>
        /// Every frame that crosses is a well-formed XMSG datagram whose ports decode as
        /// magic-number low words.
        /// </summary>
        [Fact]
        public void CrossedFrames_AreWellFormedOnTheWire()
        {
            Link link = new Link();

            XmsgPortNumber clientPort;
            XmsgMagicNumber clientMagic;
            link.OpenPortOn(link.A, out clientPort, out clientMagic);

            XmsgPortNumber serverPort;
            XmsgMagicNumber serverMagic;
            link.OpenPortOn(link.B, out serverPort, out serverMagic);

            SendFrom(link.A, clientPort, serverMagic, "X");
            List<XmsgFrame> seen = new List<XmsgFrame>();
            link.PumpAToB(null, seen);

            Assert.Single(seen);
            XmsgFrame frame = seen[0];

            Assert.NotNull(frame.SubHeader);
            Assert.Equal(NodeB, frame.Header.DestinationNode);
            Assert.Equal(NodeA, frame.Header.SourceNode);
            Assert.Equal(serverMagic.PortWord, frame.SubHeader!.DestinationPort);
            Assert.Equal(clientMagic.PortWord, frame.SubHeader.SourcePort);

            // The port field decodes as a magic low word: a real port number and a random part the
            // kernel could have minted.
            int portNumber;
            int random;
            Assert.True(XmsgPortWordAllocator.TrySplit(frame.SubHeader.SourcePort, out portNumber, out random));
            Assert.Equal(clientMagic.PortNumber, portNumber);
            Assert.Equal(clientMagic.Random, random);
        }

        private static void SendFrom(XmsgKernelServer system, XmsgPortNumber from, XmsgMagicNumber to, string text)
        {
            XmsgMessageIdentifier message;
            system.Kernel.ReserveBuffer(text.Length, XmsgBufferOptions.None, out message);
            int written;
            system.Kernel.Write(message, Encoding.ASCII.GetBytes(text), 0, false, out written);
            system.Kernel.Send(to, from, XmsgSendFlags.None);
        }

        /// <summary>
        /// Two XMSG systems and the byte path between them.
        /// </summary>
        private sealed class Link
        {
            internal Link()
            {
                A = XmsgKernelServer.Create(NodeA, 0x1111, "*APP-A", 4);
                B = XmsgKernelServer.Create(NodeB, 0x2222, "*APP-B", 4);

                HostA = new XmsgServerHost(NodeA);
                HostB = new XmsgServerHost(NodeB);
                HostA.Register(A);
                HostB.Register(B);

                // Each bridge builds its datagrams with its own host's transport.
                A.AttachTransport(HostA);
                B.AttachTransport(HostB);

                // A host learns a link's seed from traffic it receives, so prime both directions.
                Prime(HostA, NodeB);
                Prime(HostB, NodeA);
            }

            internal XmsgKernelServer A { get; }

            internal XmsgKernelServer B { get; }

            internal XmsgServerHost HostA { get; }

            internal XmsgServerHost HostB { get; }

            internal void OpenPortOn(XmsgKernelServer system, out XmsgPortNumber port, out XmsgMagicNumber magic)
            {
                system.Kernel.OpenPort(out port);
                system.Kernel.ConvertPortToMagic(port, out magic);
                system.RegisterPort(magic.PortWord);
            }

            internal int PumpAToB(List<ushort>? sequences = null, List<XmsgFrame>? frames = null)
            {
                return Pump(A, HostA, HostB, sequences, frames);
            }

            internal int PumpBToA(List<ushort>? sequences = null, List<XmsgFrame>? frames = null)
            {
                return Pump(B, HostB, HostA, sequences, frames);
            }

            private static int Pump(
                XmsgKernelServer from,
                XmsgServerHost fromHost,
                XmsgServerHost toHost,
                List<ushort>? sequences,
                List<XmsgFrame>? frames)
            {
                IReadOnlyList<XmsgFrame> outgoing = from.DrainPending(fromHost);
                int carried = 0;

                for (int i = 0; i < outgoing.Count; i++)
                {
                    // Serialise, HDLC-frame, de-frame, re-parse: the real byte path.
                    byte[] wire = outgoing[i].ToArray();
                    byte[] framed = HdlcEncoder.Encode(wire);
                    IReadOnlyList<byte[]> split = HdlcDeframer.SplitFrames(framed);

                    for (int j = 0; j < split.Count; j++)
                    {
                        // The encoder appends the two FCS bytes; the datagram is what precedes them.
                        byte[] body = split[j];
                        byte[] datagram = new byte[body.Length - 2];
                        Array.Copy(body, datagram, datagram.Length);

                        XmsgFrame parsed = XmsgFrame.Parse(datagram);
                        if (sequences != null)
                        {
                            sequences.Add(parsed.Header.Flags1);
                        }

                        if (frames != null)
                        {
                            frames.Add(parsed);
                        }

                        toHost.Route(parsed);
                        carried++;
                    }
                }

                return carried;
            }

            private static void Prime(XmsgServerHost host, ushort remoteNode)
            {
                XmsgFrame primer = new XmsgFrameBuilder()
                    .Between(host.NodeNumber, remoteNode)
                    .WithDatagramSequence(1)
                    .WithProtocol(SintranProtocolId.Tad)
                    .WithFlags2(0x0108)
                    .WithSubHeaderControl(0x13, 0x86, 0x84)
                    .WithEndpoints(host.NodeNumber, 0, remoteNode, 0)
                    .WithControlService(0x01080000)
                    .Build();

                host.Route(primer);
            }
        }
    }
}
