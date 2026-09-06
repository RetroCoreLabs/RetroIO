using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Link;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// X.25 over a LinkManager link, end to end through the runtime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the fence for stage 3 of Phase C2. Before it, link traffic went into a single
    /// process-wide engine whose outlet asked <c>IsLocalConnection</c>, which answered true for
    /// every channel, so nothing the engine produced ever reached a link. A call arriving on a link
    /// was therefore never answered on that link, and no test said so.
    /// </para>
    /// <para>
    /// Each link now owns an <c>X25Endpoint</c>: the engine sends through
    /// <c>LinkManager.SendViaLink</c>, and an incoming call is routed by
    /// <c>X25RoutingLayer.RouteIncomingCall</c>, which allocates the local channel number and binds
    /// the service but puts nothing on the wire - the engine sends the answer.
    /// </para>
    /// <para>
    /// The link is a <c>TestLink</c>, so the LAPB below is real: the packet is carried in an I
    /// frame and the answer comes back out as one.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class ProtocolRuntimeLinkEndpointTests
    {
        private const string LocalMachineId = "200";
        private const string CallingAddress = "300";

        private ProtocolRuntime _runtime = null!;
        private TestLink _link = null!;
        private readonly object _lock = new object();
        private List<byte[]> _outbound = null!;

        [SetUp]
        public void SetUp()
        {
            _outbound = new List<byte[]>();

            RuntimeConfig config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = LocalMachineId },
                TestMode = true,
            };

            _runtime = new ProtocolRuntime(NullLogger.Instance, config);
            _link = _runtime.DefaultTestLink;

            // Every I frame the runtime puts on the link, with its information field kept: that is
            // the X.25 packet the engine produced.
            _link.FrameTransmitted += OnFrameTransmitted;

            _link.SetDataTransferState();
        }

        [TearDown]
        public void TearDown()
        {
            if (_link != null)
            {
                _link.FrameTransmitted -= OnFrameTransmitted;
            }

            _runtime?.Dispose();
        }

        private void OnFrameTransmitted(object? sender, LapbFrameEventArgs e)
        {
            if (e.FrameType != LapbFrameType.Information || e.Information == null || e.Information.Length < 3)
            {
                return;
            }

            lock (_lock)
            {
                _outbound.Add(e.Information);
            }
        }

        private List<byte[]> Outbound()
        {
            lock (_lock)
            {
                return new List<byte[]>(_outbound);
            }
        }

        /// <summary>
        /// Hands a complete X.25 packet to the link as the information field of an I frame, the way
        /// a peer would send it.
        /// </summary>
        /// <param name="packet">
        /// The X.25 packet.
        /// </param>
        /// <param name="ns">
        /// N(S) for the I frame.
        /// </param>
        private void DeliverToLink(byte[] packet, int ns)
        {
            // Address B (X.25 2.4.2, a command from the peer acting as DTE), I frame with N(R)=0.
            byte[] frame = new byte[2 + packet.Length];
            frame[0] = 0x01;
            frame[1] = (byte)(ns << 1);
            Buffer.BlockCopy(packet, 0, frame, 2, packet.Length);
            _link.InjectFrame(frame, frame.Length);
        }

        /// <summary>
        /// The packet type identifier of an X.25 packet: the third octet (clause 5.1).
        /// </summary>
        private static byte TypeOf(byte[] packet) => packet.Length > 2 ? packet[2] : (byte)0;

        [Test]
        public void ACallArrivingOnALink_IsAnsweredOnThatLink()
        {
            byte[] call = X25PacketBuilder.CreateCallRequest(
                lcn: 5,
                calledAddress: LocalMachineId,
                callingAddress: CallingAddress);

            DeliverToLink(call, ns: 0);

            List<byte[]> sent = Outbound();
            bool sawCallAccepted = false;
            for (int i = 0; i < sent.Count; i++)
            {
                // Call Accepted / Call Connected is 0x0F (X.25 Table 5-1).
                if (TypeOf(sent[i]) == 0x0F)
                {
                    sawCallAccepted = true;
                    Assert.That(sent[i][1], Is.EqualTo((byte)5), "the answer must carry the channel the call came in on");
                }
            }

            Assert.That(sawCallAccepted, Is.True,
                "a call to the local machine's own address must be accepted back out of the link it arrived on");
        }

        [Test]
        public void ACallToAnUnknownAddress_IsClearedOnThatLink()
        {
            byte[] call = X25PacketBuilder.CreateCallRequest(
                lcn: 6,
                calledAddress: "999",
                callingAddress: CallingAddress);

            DeliverToLink(call, ns: 0);

            List<byte[]> sent = Outbound();
            bool sawClear = false;
            for (int i = 0; i < sent.Count; i++)
            {
                // Clear Request / Clear Indication is 0x13 (Table 5-1).
                if (TypeOf(sent[i]) == 0x13)
                {
                    sawClear = true;
                    Assert.That(sent[i][1], Is.EqualTo((byte)6));
                }
            }

            Assert.That(sawClear, Is.True, "an unroutable call must be cleared, not left open");
        }

        [Test]
        public void TheLinkGetsItsOwnEngine_NotTheProcessWideOne()
        {
            byte[] call = X25PacketBuilder.CreateCallRequest(
                lcn: 7,
                calledAddress: LocalMachineId,
                callingAddress: CallingAddress);

            DeliverToLink(call, ns: 0);

            // Nothing at all came back before this change, because the single engine's replies were
            // routed in-process and the link branch was dead.
            Assert.That(Outbound().Count, Is.GreaterThan(0),
                "the engine's answer must reach the link the call arrived on");
        }
    }
}
