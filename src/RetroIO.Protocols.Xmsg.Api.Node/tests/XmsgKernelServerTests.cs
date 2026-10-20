//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Api.Node;
using RetroIO.Protocols.Xmsg.Node.Services;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Api.Node.Tests
{
    /// <summary>
    /// Proves the bridge carries traffic both ways between the manual-shaped API and the node's
    /// frame-shaped world.
    /// </summary>
    public sealed class XmsgKernelServerTests
    {
        /// <summary>
        /// A datagram arriving from the node becomes a message on the addressed kernel port, with
        /// the sender's magic number recoverable exactly as the manual says.
        /// </summary>
        [Fact]
        public void Inbound_DatagramBecomesAMessageOnThePort()
        {
            XmsgKernel kernel = new XmsgKernel(102, 0x1111, null);
            XmsgKernelServer bridge = new XmsgKernelServer(kernel, "*DEMO", 4);

            XmsgPortNumber port;
            kernel.OpenPort(out port);
            XmsgMagicNumber mine;
            kernel.ConvertPortToMagic(port, out mine);
            bridge.RegisterPort(mine.PortWord);

            XmsgMagicNumber theirs = XmsgMagicNumber.Create(100, 5, 43);
            XmsgFrame incoming = BuildFrame(theirs, mine, Encoding.ASCII.GetBytes("HELLO"));

            IReadOnlyList<XmsgFrame> replies = bridge.Handle(incoming, new FakeTransport());

            Assert.Empty(replies);

            XmsgReceiveResult received = kernel.Receive(port, XmsgWaitOptions.None);
            Assert.True(received.Received);
            Assert.Equal(5, received.Length);
            Assert.Equal(theirs, kernel.GetMessageStatus(received.Message).Sender);

            byte[] body = new byte[5];
            int read;
            kernel.Read(received.Message, body, 0, out read);
            Assert.Equal("HELLO", Encoding.ASCII.GetString(body));
        }

        /// <summary>
        /// A kernel send to another system leaves as a datagram addressed to that system's node,
        /// carrying our port as the source.
        /// </summary>
        [Fact]
        public void Outbound_KernelSendBecomesADatagram()
        {
            // Create wires the kernel and the bridge to each other in the one order that works.
            XmsgKernelServer wiredBridge = XmsgKernelServer.Create(102, 0x1111, "*DEMO", 4);
            XmsgKernel wired = wiredBridge.Kernel;

            XmsgPortNumber port;
            wired.OpenPort(out port);
            XmsgMagicNumber mine;
            wired.ConvertPortToMagic(port, out mine);

            FakeTransport transport = new FakeTransport();

            // A datagram must arrive first so the bridge has a transport to build replies with.
            XmsgMagicNumber remote = XmsgMagicNumber.Create(100, 5, 43);
            wiredBridge.RegisterPort(mine.PortWord);
            wiredBridge.Handle(BuildFrame(remote, mine, Array.Empty<byte>()), transport);
            wired.Receive(port, XmsgWaitOptions.None);

            XmsgMessageIdentifier message;
            wired.ReserveBuffer(4, XmsgBufferOptions.None, out message);
            int written;
            wired.Write(message, Encoding.ASCII.GetBytes("PONG"), 0, false, out written);

            Assert.False(wired.Send(remote, port, XmsgSendFlags.None).IsError);

            IReadOnlyList<XmsgFrame> pending = wiredBridge.DrainPending(transport);
            Assert.Single(pending);
            Assert.Equal(100, transport.LastRemoteNode);
            Assert.Equal(remote.PortWord, transport.LastClientPort);
            Assert.Equal(mine.PortWord, transport.LastSourcePort);
            Assert.Equal("PONG", Encoding.ASCII.GetString(transport.LastPayload));
        }

        /// <summary>
        /// A datagram the kernel queued before a datagram arrived is not lost when the arrival is
        /// handled: it is still handed out, once.
        /// </summary>
        [Fact]
        public void Handle_DoesNotDropDatagramsQueuedEarlier()
        {
            XmsgKernelServer bridge = XmsgKernelServer.Create(102, 0x1111, "*DEMO", 4);
            XmsgKernel kernel = bridge.Kernel;
            FakeTransport transport = new FakeTransport();
            bridge.AttachTransport(transport);

            XmsgPortNumber port;
            kernel.OpenPort(out port);
            XmsgMagicNumber mine;
            kernel.ConvertPortToMagic(port, out mine);
            bridge.RegisterPort(mine.PortWord);
            XmsgMagicNumber remote = XmsgMagicNumber.Create(100, 5, 43);

            // Queue one outgoing datagram and do NOT drain it.
            XmsgMessageIdentifier message;
            kernel.ReserveBuffer(4, XmsgBufferOptions.None, out message);
            int written;
            kernel.Write(message, Encoding.ASCII.GetBytes("KEEP"), 0, false, out written);
            Assert.False(kernel.Send(remote, port, XmsgSendFlags.None).IsError);

            // A datagram arrives and is handled before the queue was drained.
            IReadOnlyList<XmsgFrame> fromHandle = bridge.Handle(
                BuildFrame(remote, mine, Encoding.ASCII.GetBytes("HI")), transport);
            IReadOnlyList<XmsgFrame> fromDrain = bridge.DrainPending(transport);

            // Exactly one copy of the queued datagram comes out, from one of the two calls.
            Assert.Equal(1, fromHandle.Count + fromDrain.Count);
        }

        /// <summary>
        /// Only registered ports are routed here, so the node does not hand this server traffic
        /// belonging to another one.
        /// </summary>
        [Fact]
        public void OwnsPort_OnlyReportsRegisteredPorts()
        {
            XmsgKernel kernel = new XmsgKernel(102, 0x1111, null);
            XmsgKernelServer bridge = new XmsgKernelServer(kernel, "*DEMO", 4);

            Assert.False(bridge.OwnsPort(1218));

            bridge.RegisterPort(1218);
            Assert.True(bridge.OwnsPort(1218));

            bridge.UnregisterPort(1218);
            Assert.False(bridge.OwnsPort(1218));
        }

        /// <summary>
        /// Without a transport there is no way off this system, and the send says so rather than
        /// silently dropping the message.
        /// </summary>
        [Fact]
        public void Outbound_BeforeAnyTransport_Fails()
        {
            XmsgKernelServer bridge = new XmsgKernelServer(
                new XmsgKernel(102, 0x1111, null), "*DEMO", 4);

            XmsgStatus status = bridge.Send(
                XmsgMagicNumber.Create(100, 5, 43),
                XmsgMagicNumber.Create(102, 1, 7),
                Encoding.ASCII.GetBytes("X"),
                XmsgSendFlags.None);

            Assert.True(status.IsError);
            Assert.Equal(XmsgError.XENRU, status.Error);
        }

        /// <summary>
        /// The server's registry identity is what the node advertises.
        /// </summary>
        [Fact]
        public void Identity_ReportsNameAndPorts()
        {
            XmsgKernelServer bridge = new XmsgKernelServer(
                new XmsgKernel(102, 0x1111, null), "*DEMO", 4);

            Assert.Equal("*DEMO", bridge.Name);
            Assert.Equal(4, bridge.LogicalPort);
            // The wire port is the low word of a magic number: the high nine bits are the logical
            // port, the low seven bits are the random part. Spec: XMSG-WIRE-PORT-IS-MAGIC-LOW-WORD.
            Assert.Equal(4, bridge.WirePort >> 7);
            Assert.True(XmsgRandomGenerator.IsMintable(bridge.WirePort & 0x7F));
            Assert.False(bridge.AdvancesOutputOnAck);
        }

        /// <summary>
        /// The low seven bits of the wire port are the first draw of the generator the server was
        /// seeded with, and the high nine bits are the logical port.
        /// </summary>
        [Fact]
        public void WirePort_HasLogicalPortHighAndGeneratorDrawLow()
        {
            XmsgKernelServer bridge = new XmsgKernelServer(
                new XmsgKernel(102, 0x1111, null), "*DEMO", 4, 0x4321);

            int expectedRandom = new XmsgRandomGenerator(0x4321).Next();

            Assert.Equal(4, bridge.WirePort >> 7);
            Assert.Equal(expectedRandom, bridge.WirePort & 0x7F);
        }

        /// <summary>
        /// The wire port is drawn once, so it does not change while the server lives.
        /// </summary>
        [Fact]
        public void WirePort_IsFixedForTheLifeOfTheServer()
        {
            XmsgKernelServer bridge = new XmsgKernelServer(
                new XmsgKernel(102, 0x1111, null), "*DEMO", 4, 0x4321);

            ushort first = bridge.WirePort;
            Assert.Equal(first, bridge.WirePort);
            Assert.Equal(first, bridge.WirePort);
        }

        /// <summary>
        /// Different generator seeds give different random parts, so the part really comes from
        /// the generator and is not a constant.
        /// </summary>
        [Fact]
        public void WirePort_DiffersBetweenSeeds()
        {
            XmsgKernelServer a = new XmsgKernelServer(
                new XmsgKernel(102, 0x1111, null), "*DEMO", 4, 0x0001);
            XmsgKernelServer b = new XmsgKernelServer(
                new XmsgKernel(102, 0x1111, null), "*DEMO", 4, 0x0002);

            Assert.NotEqual(a.WirePort, b.WirePort);
            Assert.Equal(a.WirePort >> 7, b.WirePort >> 7);
        }

        /// <summary>
        /// For every logical port from 1 to 511, the high nine bits of the wire port are that port and the low
        /// seven bits are a value the generator can mint. No port overflows into another port's bits.
        /// </summary>
        [Fact]
        public void WirePort_EveryLogicalPort_HasPortHighAndMintableRandomLow()
        {
            for (int logical = 1; logical <= 511; logical++)
            {
                XmsgKernelServer bridge = new XmsgKernelServer(
                    new XmsgKernel(102, 0x1111, null), "*DEMO", logical, 0x4321);

                Assert.Equal(logical, bridge.WirePort >> 7);
                Assert.True(XmsgRandomGenerator.IsMintable(bridge.WirePort & 0x7F), "logical port " + logical);
            }
        }

        /// <summary>
        /// The three-argument constructor uses DefaultWirePortSeed: its random part is the first draw of a
        /// generator started from that seed.
        /// </summary>
        [Fact]
        public void WirePort_ThreeArgumentConstructor_UsesTheDefaultSeed()
        {
            XmsgKernelServer bridge = new XmsgKernelServer(
                new XmsgKernel(102, 0x1111, null), "*DEMO", 9);

            int expectedRandom = new XmsgRandomGenerator(XmsgKernelServer.DefaultWirePortSeed).Next();

            Assert.Equal(9, bridge.WirePort >> 7);
            Assert.Equal(expectedRandom, bridge.WirePort & 0x7F);
        }

        /// <summary>
        /// The same inputs give the same wire port, so a restarted node registers on the same port.
        /// </summary>
        [Fact]
        public void WirePort_SameSeedAndPort_GiveTheSameWirePort()
        {
            XmsgKernelServer a = new XmsgKernelServer(
                new XmsgKernel(102, 0x1111, null), "*DEMO", 4, 0x4321);
            XmsgKernelServer b = new XmsgKernelServer(
                new XmsgKernel(103, 0x2222, null), "*OTHER", 4, 0x4321);

            Assert.Equal(a.WirePort, b.WirePort);
        }

        /// <summary>
        /// Create hands its port seed to the wire port as well as to the kernel, so the random part comes from
        /// the seed the caller gave and not from the default.
        /// </summary>
        [Fact]
        public void Create_PassesThePortSeedToTheWirePort()
        {
            XmsgKernelServer bridge = XmsgKernelServer.Create(102, 0x4321, "*DEMO", 4);

            int expectedRandom = new XmsgRandomGenerator(0x4321).Next();

            Assert.Equal(4, bridge.WirePort >> 7);
            Assert.Equal(expectedRandom, bridge.WirePort & 0x7F);
        }

        /// <summary>
        /// Two Create calls with different port seeds give different wire ports for the same logical port.
        /// </summary>
        [Fact]
        public void Create_DifferentPortSeeds_GiveDifferentWirePorts()
        {
            XmsgKernelServer a = XmsgKernelServer.Create(102, 0x0001, "*DEMO", 4);
            XmsgKernelServer b = XmsgKernelServer.Create(102, 0x0002, "*DEMO", 4);

            Assert.NotEqual(a.WirePort, b.WirePort);
        }

        private static XmsgFrame BuildFrame(XmsgMagicNumber from, XmsgMagicNumber to, byte[] payload)
        {
            XmsgFrame frame = new XmsgFrameBuilder()
                .Between(to.SystemNumber, from.SystemNumber)
                .WithDatagramSequence(1)
                .WithProtocol(SintranProtocolId.Tad)
                .WithFlags2(0x0108)
                .WithSubHeaderControl(0x10, 0x86, 0x84)
                .WithEndpoints(to.SystemNumber, to.PortWord, from.SystemNumber, from.PortWord)
                .WithControlService(0x01080000)
                .Build();

            // The builder has no trailer method - the payload is assigned on the built frame.
            frame.TrailingBytes = payload;
            return frame;
        }

        private sealed class FakeTransport : IXmsgServerTransport
        {
            private ushort _nextPort = 0x0211;
            private int _nextSession = 1;

            internal ushort LastRemoteNode { get; private set; }

            internal ushort LastClientPort { get; private set; }

            internal ushort LastSourcePort { get; private set; }

            internal byte[] LastPayload { get; private set; } = Array.Empty<byte>();

            /// <summary>
            /// The Flags 1 the last send site said it was answering, or
            /// <see cref="XmsgAnsweredFlags1.None"/> when it said it was originating.
            /// </summary>
            internal int LastAnsweredFlags1 { get; private set; } = XmsgAnsweredFlags1.None;

            public ushort NodeNumber
            {
                get { return 102; }
            }

            public ushort AllocateSessionPort()
            {
                return _nextPort++;
            }

            /// <summary>
            /// Answers that every node is reachable.
            /// </summary>
            /// <param name="remoteNode">
            /// Ignored; these tests are about the kernel server, not about link state.
            /// </param>
            /// <returns>
            /// Always <c>true</c>.
            /// </returns>
            public bool CanReach(ushort remoteNode)
            {
                return true;
            }

            public int AllocateSessionNumber()
            {
                return _nextSession++;
            }

            /// <summary>
            /// Builds a datagram whose message body goes on the wire verbatim at offset 28.
            /// </summary>
            /// <param name="remoteNode">
            /// The node to send to.
            /// </param>
            /// <param name="clientSystem">
            /// The peer's system number.
            /// </param>
            /// <param name="clientPort">
            /// The peer's port.
            /// </param>
            /// <param name="sourcePort">
            /// Our source port.
            /// </param>
            /// <param name="xmcsm">
            /// XMCSM, the word at wire 26-27.
            /// </param>
            /// <param name="frameFlags">
            /// The sub-header frame-flags byte.
            /// </param>
            /// <param name="role">
            /// The sub-header role byte.
            /// </param>
            /// <param name="body">
            /// The message body from wire offset 28.
            /// </param>
            /// <param name="answeredFlags1">
            /// The Flags 1 answered, or XmsgAnsweredFlags1.None.
            /// </param>
            /// <returns>
            /// The assembled datagram.
            /// </returns>
            public XmsgFrame BuildBodyDatagram(
                ushort remoteNode,
                ushort clientSystem,
                ushort clientPort,
                ushort sourcePort,
                ushort xmcsm,
                byte frameFlags,
                byte role,
                byte[] body,
                int answeredFlags1)
            {
                LastRemoteNode = remoteNode;
                LastAnsweredFlags1 = answeredFlags1;
                LastClientPort = clientPort;
                LastSourcePort = sourcePort;
                LastPayload = body;

                XmsgFrame built = new XmsgFrame();
                built.Header.Marker1 = SintranHeader.Marker1Value;
                built.Header.Marker2 = SintranHeader.Marker2Normal;
                built.Header.Subtype = SintranPacketSubtype.Data;
                built.Header.DestinationNode = remoteNode;
                built.Header.SourceNode = NodeNumber;
                built.Header.Flags1 = 1;
                built.Header.Flags2 = xmcsm;

                XmsgSubHeader sub = new XmsgSubHeader();
                sub.FrameFlags = frameFlags;
                sub.Role = role;
                sub.DestinationSystem = clientSystem;
                sub.DestinationPort = clientPort;
                sub.SourceSystem = NodeNumber;
                sub.SourcePort = sourcePort;
                sub.Xmcsm = xmcsm;

                built.SubHeader = sub;
                built.TrailingBytes = body;
                return built;
            }

            /// <summary>
            /// Builds the datagram carrying one message body, declaring its own length.
            /// </summary>
            /// <param name="remoteNode">
            /// The node to send to.
            /// </param>
            /// <param name="clientSystem">
            /// The peer's system number.
            /// </param>
            /// <param name="clientPort">
            /// The peer's port.
            /// </param>
            /// <param name="sourcePort">
            /// Our source port.
            /// </param>
            /// <param name="frameFlags">
            /// The sub-header frame-flags byte.
            /// </param>
            /// <param name="role">
            /// The sub-header role byte.
            /// </param>
            /// <param name="body">
            /// The message body from wire offset 28.
            /// </param>
            /// <param name="answeredFlags1">
            /// The Flags 1 answered, or <see cref="XmsgAnsweredFlags1.None"/>.
            /// </param>
            /// <returns>
            /// The one assembled datagram.
            /// </returns>
            /// <remarks>
            /// This fake never fragments. The kernel server does not send messages anywhere near
            /// the split point, and a fake that reproduced the split would be asserting the frame
            /// layer's behaviour from a test that is not about it.
            /// </remarks>
            public IReadOnlyList<XmsgFrame> BuildFragmentedBodyDatagram(
                ushort remoteNode,
                ushort clientSystem,
                ushort clientPort,
                ushort sourcePort,
                byte frameFlags,
                byte role,
                byte[] body,
                int answeredFlags1)
            {
                List<XmsgFrame> frames = new List<XmsgFrame>(1);
                frames.Add(BuildBodyDatagram(
                    remoteNode, clientSystem, clientPort, sourcePort, (ushort)body.Length,
                    frameFlags, role, body, answeredFlags1));
                return frames;
            }

            public XmsgFrame BuildDatagram(
                ushort remoteNode,
                ushort clientSystem,
                ushort clientPort,
                ushort sourcePort,
                uint controlService,
                byte frameFlags,
                byte role,
                byte[] payload,
                int answeredFlags1)
            {
                LastRemoteNode = remoteNode;
                LastAnsweredFlags1 = answeredFlags1;
                LastClientPort = clientPort;
                LastSourcePort = sourcePort;
                LastPayload = payload;

                XmsgFrame built = new XmsgFrameBuilder()
                    .Between(remoteNode, NodeNumber)
                    .WithDatagramSequence(1)
                    .WithProtocol(SintranProtocolId.Tad)
                    .WithFlags2((ushort)(controlService >> 16))
                    .WithSubHeaderControl(0x10, frameFlags, role)
                    .WithEndpoints(clientSystem, clientPort, NodeNumber, sourcePort)
                    .WithControlService(controlService)
                    .Build();

                built.TrailingBytes = payload;
                return built;
            }
        }
    }
}
