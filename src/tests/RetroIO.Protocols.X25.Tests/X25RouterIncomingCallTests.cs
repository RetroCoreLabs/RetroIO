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
using RetroIO.Protocols.X25.Transport;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The router answers an incoming call first: with no inbound route it refuses the call at once
    /// on the transport the call came in on, with cause 0x0B (access barred, X.25 Table 5-7); with a
    /// route it leaves the call alone, so it can go on to the program or the local service.
    /// </summary>
    /// <remarks>
    /// X.25 4.1.4: a called DTE that does not accept a call refuses it "by a clear request packet as
    /// described in 4.1.7". Until 2026-11-17 the router's refusal only wrote a log line and nothing
    /// went on the wire. The transport here is a fake that counts the calls made on it.
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

        private static X25Router RouterWithRouteFor(string pattern)
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

            return router;
        }

        [Test]
        public void NoInboundRoute_RejectsOnTheTransportOnce_WithAccessBarred()
        {
            X25Router router = RouterWithRouteFor(string.Empty);
            CountingTransport transport = new CountingTransport();
            router.RegisterInterface(transport);
            X25CallRequest call = new X25CallRequest("23430200", "23430001");

            bool passed = router.AnswerIncomingCall(call, transport);

            Assert.That(passed, Is.False, "a call with no inbound route must not go on");
            Assert.That(transport.Rejected.Count, Is.EqualTo(1), "the refusal must go to the transport exactly once");
            Assert.That(transport.Rejected[0], Is.SameAs(call), "the transport is handed the call it raised");
            Assert.That(transport.RejectCauses[0], Is.EqualTo((byte)0x0B), "X.25 Table 5-7: 0x0B access barred");
            Assert.That(transport.Accepts, Is.EqualTo(0));
        }

        [Test]
        public void InboundRoute_IsNotRejected_AndNotAcceptedByTheRouter()
        {
            X25Router router = RouterWithRouteFor("^23430200$");
            CountingTransport transport = new CountingTransport();
            router.RegisterInterface(transport);
            X25CallRequest call = new X25CallRequest("23430200", "23430001");

            bool passed = router.AnswerIncomingCall(call, transport);

            Assert.That(passed, Is.True, "a call with a route goes on to the program or the service");
            Assert.That(transport.Rejected.Count, Is.EqualTo(0));
            Assert.That(transport.Accepts, Is.EqualTo(0), "the router does not accept; whoever owns the call next does");
        }

        [Test]
        public void ARouteForAnotherAddress_DoesNotLetTheCallThrough()
        {
            X25Router router = RouterWithRouteFor("^23430200$");
            CountingTransport transport = new CountingTransport();
            X25CallRequest call = new X25CallRequest("23439999", "23430001");

            Assert.That(router.AnswerIncomingCall(call, transport), Is.False);
            Assert.That(transport.RejectCauses, Is.EqualTo(new List<byte> { 0x0B }));
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
            CountingTransport transport = new CountingTransport();

            Assert.That(router.AnswerIncomingCall(new X25CallRequest("23430200", "23430001"), transport), Is.False);
            Assert.That(transport.RejectCauses, Is.EqualTo(new List<byte> { 0x0B }));
        }

        [Test]
        public void TheTransportEvent_IsNotHeardByTheRouter()
        {
            // The router answers only when it is asked (AnswerIncomingCall). It must not also listen
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
