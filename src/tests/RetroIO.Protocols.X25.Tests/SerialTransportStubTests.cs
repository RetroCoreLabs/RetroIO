//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Transport;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The serial transport on a line with no X.25 data link. These tests pin that PlaceCall,
    /// AcceptCall and RejectCall refuse there, instead of logging "would accept", counting the call
    /// and returning true with nothing sent.
    /// </summary>
    [TestFixture]
    public class SerialTransportStubTests : TestBase
    {
        private SerialTransport NewTransport()
        {
            return new SerialTransport(Logger, Runtime, new SerialTransportConfig { Name = "stub" });
        }

        private static X25CallRequest NewCall()
        {
            return new X25CallRequest("200", "100");
        }

        // AcceptCall and RejectCall are built since 2026-11-17: they hand the program's answer to the
        // line's X.25 engine for a call the line offered (SerialIncomingCallTests.WaitMode_*). A call
        // that was never offered has no channel to answer on, so nothing is sent and false comes back.
        [Test]
        public void AcceptCall_ForACallThatWasNeverOffered_IsRefused()
        {
            SerialTransport transport = NewTransport();

            Assert.That(transport.AcceptCall(NewCall()), Is.False);
            Assert.That(transport.GetStatistics().InboundCallsAccepted, Is.EqualTo(0), "a call that was not answered must not be counted as accepted");
        }

        [Test]
        public void RejectCall_ForACallThatWasNeverOffered_IsRefused()
        {
            SerialTransport transport = NewTransport();

            Assert.That(transport.RejectCall(NewCall(), 0x0B), Is.False);
            Assert.That(transport.GetStatistics().InboundCallsRejected, Is.EqualTo(0), "a call that was not cleared must not be counted as rejected");
        }

        // PlaceCall is built since 2026-11-17 (through the line's X.25 engine, see
        // SerialIncomingCallTests.PlaceCall_*). A transport with no X.25 data link must refuse it, not
        // report success.
        [Test]
        public void PlaceCall_WithNoDataLink_IsRefused()
        {
            SerialTransport transport = NewTransport();

            Assert.That(transport.PlaceCall(NewCall()), Is.False);
            Assert.That(transport.GetStatistics().OutboundCallsSucceeded, Is.EqualTo(0), "a call that was not sent must not be counted as succeeded");
        }
    }
}
