//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

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
            Build(withRoute: true);
        }

        /// <summary>
        /// Builds the runtime on its default test link. A call on a link manager link is decided by the
        /// router like a call on any other line (2026-11-18), so the local machine's address gets an
        /// inbound route to the local TAD unless a test wants none.
        /// </summary>
        private void Build(bool withRoute)
        {
            if (_runtime != null)
            {
                if (_link != null)
                {
                    _link.FrameTransmitted -= OnFrameTransmitted;
                }

                _runtime.Dispose();
            }

            _outbound = new List<byte[]>();

            RuntimeConfig config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = LocalMachineId },
                TestMode = true,
            };

            if (withRoute)
            {
                config.Routing.RoutingTable.Add(new X25RouteEntry
                {
                    Name = "LocalTAD",
                    X121AddressPattern = "^" + LocalMachineId + "$",
                    Type = RetroIO.Protocols.X25.Runtime.RouteType.Local,
                    Destination = "LocalTAD",
                    Priority = 200,
                    Enabled = true,
                });
            }

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

            // NUnit keeps one fixture object for every test; Build must not dispose this one again.
            _runtime = null!;
            _link = null!;
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

        /// <summary>
        /// The clear of a call on channel <paramref name="lcn"/>, or null when none was sent.
        /// </summary>
        private byte[]? ClearOn(byte lcn)
        {
            List<byte[]> sent = Outbound();
            for (int i = 0; i < sent.Count; i++)
            {
                // Clear Request / Clear Indication is 0x13 (Table 5-1): cause in octet 4, diagnostic in octet 5.
                if (TypeOf(sent[i]) == 0x13 && sent[i][1] == lcn && sent[i].Length >= 5)
                {
                    return sent[i];
                }
            }

            return null;
        }

        [Test]
        public void ACallToAnUnknownAddress_IsRefusedByTheRouter_WithNotObtainable()
        {
            DeliverToLink(X25PacketBuilder.CreateCallRequest(lcn: 6, calledAddress: "999", callingAddress: CallingAddress), ns: 0);

            byte[]? clear = ClearOn(6);
            Assert.That(clear, Is.Not.Null, "an unroutable call must be cleared on the link it came in on");
            // The router decides Not obtainable, diagnostic 67 (Table C.1 row 24). The runtime is the DTE
            // on a link manager link (EndpointForLink), and a DTE's Clear Request may carry only "DTE
            // originated" or a cause with bit 8 set (Table 5-7 note a, text line 8006), so the engine puts
            // 0x00 in the cause and the router's diagnostic goes on unchanged (lines 8022-8024).
            Assert.That(clear![3], Is.EqualTo(RetroIO.Protocols.X25.PacketLayer.X25Cause.DteOriginated), "a DTE clears with DTE originated");
            Assert.That(clear[4], Is.EqualTo(RetroIO.Protocols.X25.PacketLayer.X25Diagnostic.InvalidCalledAddress), "the router's diagnostic, # 67");
        }

        [Test]
        public void ACallToTheLocalAddressWithNoRoute_IsRefusedByTheRouter_NotAnsweredByTheService()
        {
            // The local TAD takes calls to the machine's own address. With no inbound route the router
            // refuses the call before the service is asked, the same as on every other line. Until
            // 2026-11-18 a link manager link skipped the router and the service accepted the call.
            Build(withRoute: false);

            DeliverToLink(X25PacketBuilder.CreateCallRequest(lcn: 5, calledAddress: LocalMachineId, callingAddress: CallingAddress), ns: 0);

            List<byte[]> sent = Outbound();
            for (int i = 0; i < sent.Count; i++)
            {
                Assert.That(TypeOf(sent[i]), Is.Not.EqualTo((byte)0x0F), "a call with no route must not be accepted");
            }

            byte[]? clear = ClearOn(5);
            Assert.That(clear, Is.Not.Null, "the call must be cleared on the link");
            Assert.That(clear![3], Is.EqualTo(RetroIO.Protocols.X25.PacketLayer.X25Cause.DteOriginated), "a DTE clears with DTE originated (Table 5-7 note a)");
            Assert.That(clear[4], Is.EqualTo(RetroIO.Protocols.X25.PacketLayer.X25Diagnostic.InvalidCalledAddress), "the router's diagnostic, # 67");
        }

        /// <summary>
        /// A local service that refuses every call it is given.
        /// </summary>
        private sealed class RefusingService : RetroIO.Protocols.X25.Services.IProtocolService
        {
            public string ServiceName => "Refuser";

            public string[] LocalAddresses => new[] { "777" };

            public int Priority => 1000;

            public int Calls;

            public bool CanHandleCUD(byte[] cud) => true;

            public bool HandleIncomingCall(ushort lcn, string calledAddress, string callingAddress, byte[] cud, byte[] facilities)
            {
                Calls++;
                return false;
            }

            public void HandleData(ushort lcn, byte[] data)
            {
            }

            public void HandleClear(ushort lcn, byte cause, byte diagnostic)
            {
            }

            public void HandleInterrupt(ushort lcn, byte[] data)
            {
            }
        }

        [Test]
        public void ACallTheLocalServiceRefuses_IsClearedAsDteOriginated()
        {
            // The router passes the call (it has a route and a service takes the address); the service,
            // which is the called DTE here, then refuses it. X.25 4.1.7: "The called DTE may refuse an
            // incoming call by clearing it"; Table 5-7: DTE originated, 0x00. Until 2026-11-18 this was
            // cleared with 0x0D like a number that does not exist.
            Build(withRoute: true);
            _runtime.Router.AddRoute(new RetroIO.Protocols.X25.Transport.X25Route
            {
                Name = "Refuser",
                X121AddressPattern = "^777$",
                Type = RetroIO.Protocols.X25.Transport.RouteType.Local,
                Destination = "Refuser",
                Enabled = true,
            });
            RefusingService service = new RefusingService();
            _runtime.GetProtocolServiceRegistry().RegisterService(service);

            DeliverToLink(X25PacketBuilder.CreateCallRequest(lcn: 4, calledAddress: "777", callingAddress: CallingAddress), ns: 0);

            Assert.That(service.Calls, Is.EqualTo(1), "the service must have been asked");
            byte[]? clear = ClearOn(4);
            Assert.That(clear, Is.Not.Null);
            Assert.That(clear![3], Is.EqualTo(RetroIO.Protocols.X25.PacketLayer.X25Cause.DteOriginated), "4.1.7: the called side refused");
            Assert.That(clear[4], Is.EqualTo((byte)0));
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
