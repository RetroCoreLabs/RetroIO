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
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.PacketLayer;
using RetroIO.Protocols.X25.Transport;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The router alone, with a fake line: <see cref="X25Router.DecideIncomingCall"/> is the one
    /// decision for a call that came in on any line, and it only answers - it never sends anything on
    /// the line itself. The line clears a refused call with the cause and diagnostic it is given.
    /// </summary>
    /// <remarks>
    /// The causes are from X.25 (10/96) Table C.1 (see <see cref="X25CallDecision"/>): no route is Not
    /// obtainable 0x0D with diagnostic 67 (row 24), an address the line's own list bars is Access barred
    /// 0x0B with diagnostic 70 (row 25). Until 2026-11-18 the router refused a call with no route itself,
    /// through the transport's RejectCall, with 0x0B. The transport here is a fake that counts the calls
    /// made on it.
    /// </remarks>
    [TestFixture]
    public class X25RouterIncomingCallTests
    {
        private sealed class CountingTransport : ITransportInterface
        {
            public readonly List<byte> RejectCauses = new List<byte>();
            public readonly List<X25CallRequest> Rejected = new List<X25CallRequest>();
            public int Accepts;

            public string Name => "fake0";

            public TransportType Type => TransportType.Serial;

            public TransportStatus Status => TransportStatus.Active;

            public bool PlaceCall(X25CallRequest callRequest) => false;

            public bool AcceptCall(X25CallRequest callRequest)
            {
                Accepts++;
                return true;
            }

            public bool RejectCall(X25CallRequest callRequest, byte cause)
            {
                Rejected.Add(callRequest);
                RejectCauses.Add(cause);
                return true;
            }

            public bool Start() => true;

            public void Stop()
            {
            }

            public TransportStatistics GetStatistics() => new TransportStatistics();

            public void ResetStatistics()
            {
            }

            public void Raise(X25CallRequest call) => IncomingCall?.Invoke(this, call);

            public event EventHandler<X25CallRequest>? IncomingCall;

            public event EventHandler<TransportStatusEventArgs>? StatusChanged
            {
                add { }
                remove { }
            }

            public event EventHandler<TransportDataEventArgs>? DataReceived
            {
                add { }
                remove { }
            }
        }

        private static X25Router RouterWithRouteFor(string pattern, bool serviceExists = true)
        {
            X25Router router = new X25Router(NullLogger.Instance);
            if (pattern.Length > 0)
            {
                router.AddRoute(new X25Route
                {
                    Name = "Local",
                    X121AddressPattern = pattern,
                    Type = RouteType.Local,
                    Destination = "LocalTAD",
                    Direction = RouteDirection.Bidirectional,
                    Enabled = true,
                });
            }

            router.LocalServiceAvailable = (called, cud) => serviceExists;
            return router;
        }

        private static void AssertNoRoute(X25CallDecision decision)
        {
            Assert.That(decision.Action, Is.EqualTo(X25CallAction.Refuse));
            Assert.That(decision.Cause, Is.EqualTo((byte)0x0D), "X.25 Table C.1 row 24, unknown number: not obtainable");
            Assert.That(decision.Diagnostic, Is.EqualTo((byte)67), "Table C.1 row 24: # 67, invalid called DTE address");
        }

        [Test]
        public void NoInboundRoute_IsNotObtainable_AndTheRouterSendsNothingItself()
        {
            X25Router router = RouterWithRouteFor(string.Empty);
            CountingTransport transport = new CountingTransport();
            router.RegisterInterface(transport);

            X25CallDecision decision = router.DecideIncomingCall(new X25CallRequest("23430200", "23430001"), transport, programWaits: false);

            AssertNoRoute(decision);
            Assert.That(transport.Rejected.Count, Is.EqualTo(0), "the line sends the clear, not the router");
            Assert.That(transport.Accepts, Is.EqualTo(0));
        }

        [Test]
        public void InboundRoute_AndALocalService_IsDeliveredLocally_AndNotAcceptedByTheRouter()
        {
            X25Router router = RouterWithRouteFor("^23430200$");
            CountingTransport transport = new CountingTransport();

            X25CallDecision decision = router.DecideIncomingCall(new X25CallRequest("23430200", "23430001"), transport, programWaits: false);

            Assert.That(decision.Action, Is.EqualTo(X25CallAction.DeliverLocally));
            Assert.That(transport.Rejected.Count, Is.EqualTo(0));
            Assert.That(transport.Accepts, Is.EqualTo(0), "the router does not accept; whoever owns the call next does");
        }

        [Test]
        public void InboundRoute_ButNoLocalServiceForTheAddress_IsNotObtainable()
        {
            X25Router router = RouterWithRouteFor("^23430200$", serviceExists: false);

            AssertNoRoute(router.DecideIncomingCall(new X25CallRequest("23430200", "23430001"), new CountingTransport(), programWaits: false));
        }

        [Test]
        public void NoServiceLookupSet_IsNotObtainable()
        {
            X25Router router = RouterWithRouteFor("^23430200$");
            router.LocalServiceAvailable = null;

            AssertNoRoute(router.DecideIncomingCall(new X25CallRequest("23430200", "23430001"), new CountingTransport(), programWaits: false));
        }

        [Test]
        public void AProgramWaiting_WithARoute_IsDeliveredLocally_WithoutAService()
        {
            X25Router router = RouterWithRouteFor("^23430200$", serviceExists: false);

            X25CallDecision decision = router.DecideIncomingCall(new X25CallRequest("23430200", "23430001"), new CountingTransport(), programWaits: true);

            Assert.That(decision.Action, Is.EqualTo(X25CallAction.DeliverLocally), "the waiting program is the local destination");
        }

        [Test]
        public void AProgramWaiting_WithNoRoute_IsNotObtainable()
        {
            X25Router router = RouterWithRouteFor(string.Empty);

            AssertNoRoute(router.DecideIncomingCall(new X25CallRequest("23430200", "23430001"), new CountingTransport(), programWaits: true));
        }

        [Test]
        public void ARouteForAnotherAddress_IsNotObtainable()
        {
            X25Router router = RouterWithRouteFor("^23430200$");

            AssertNoRoute(router.DecideIncomingCall(new X25CallRequest("23439999", "23430001"), new CountingTransport(), programWaits: false));
        }

        [Test]
        public void AnOutboundOnlyRoute_DoesNotLetAnIncomingCallThrough()
        {
            X25Router router = new X25Router(NullLogger.Instance);
            router.AddRoute(new X25Route
            {
                Name = "OutOnly",
                X121AddressPattern = "^23430200$",
                Type = RouteType.Local,
                Destination = "LocalTAD",
                Direction = RouteDirection.Outbound,
                Enabled = true,
            });
            router.LocalServiceAvailable = (called, cud) => true;

            AssertNoRoute(router.DecideIncomingCall(new X25CallRequest("23430200", "23430001"), new CountingTransport(), programWaits: false));
        }

        [Test]
        public void AnAddressTheXotServerEntryDoesNotTake_IsAccessBarred_EvenWithARoute()
        {
            X25Router router = RouterWithRouteFor("^23430200$");
            XotServerConfig config = new XotServerConfig { Name = "xot0" };
            config.AcceptedX121Ranges.Add("^99999999$");
            XotServerTransport server = new XotServerTransport(NullLogger.Instance, config);

            X25CallDecision decision = router.DecideIncomingCall(new X25CallRequest("23430200", "23430001"), server, programWaits: false);

            Assert.That(decision.Action, Is.EqualTo(X25CallAction.Refuse));
            Assert.That(decision.Cause, Is.EqualTo((byte)0x0B), "X.25 Table C.1 row 25, incoming call barred: access barred");
            Assert.That(decision.Diagnostic, Is.EqualTo((byte)70), "Table C.1 row 25: # 70");
        }

        [Test]
        public void ALinkWithNoTransport_IsDecidedTheSameWay()
        {
            // A link manager link has no ITransportInterface; the router decides its calls all the same.
            Assert.That(RouterWithRouteFor("^23430200$").DecideIncomingCall(new X25CallRequest("23430200", "23430001"), null, programWaits: false).Action,
                Is.EqualTo(X25CallAction.DeliverLocally));
            AssertNoRoute(RouterWithRouteFor(string.Empty).DecideIncomingCall(new X25CallRequest("23430200", "23430001"), null, programWaits: false));
        }

        [Test]
        public void TheTransportEvent_IsNotHeardByTheRouter()
        {
            // The router answers only when it is asked (DecideIncomingCall). It must not also listen
            // to the transport's IncomingCall event: that event is the program's offer, raised only
            // after the router passed the call, and a second answer from the router there would
            // refuse calls the program is deciding on.
            X25Router router = RouterWithRouteFor(string.Empty);
            CountingTransport transport = new CountingTransport();
            router.RegisterInterface(transport);

            transport.Raise(new X25CallRequest("23430200", "23430001"));

            Assert.That(transport.Rejected.Count, Is.EqualTo(0));
        }
    }
}