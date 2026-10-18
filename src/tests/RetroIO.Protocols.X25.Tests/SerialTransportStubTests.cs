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
    /// The serial transport of the old runtime has no send path any more. These tests pin that
    /// PlaceCall, AcceptCall and RejectCall say so, instead of logging "would accept", counting the
    /// call and returning true with nothing sent.
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

        [Test]
        public void AcceptCall_IsNotImplemented_AndSaysSo()
        {
            SerialTransport transport = NewTransport();

            NotSupportedException? ex = Assert.Throws<NotSupportedException>(() => transport.AcceptCall(NewCall()));

            Assert.That(ex!.Message, Does.Contain("not implemented"));
            Assert.That(transport.GetStatistics().InboundCallsAccepted, Is.EqualTo(0), "a call that was not answered must not be counted as accepted");
        }

        [Test]
        public void RejectCall_IsNotImplemented_AndSaysSo()
        {
            SerialTransport transport = NewTransport();

            NotSupportedException? ex = Assert.Throws<NotSupportedException>(() => transport.RejectCall(NewCall(), 0x0B));

            Assert.That(ex!.Message, Does.Contain("not implemented"));
            Assert.That(transport.GetStatistics().InboundCallsRejected, Is.EqualTo(0), "a call that was not cleared must not be counted as rejected");
        }

        [Test]
        public void PlaceCall_IsNotImplemented_AndSaysSo()
        {
            SerialTransport transport = NewTransport();

            NotSupportedException? ex = Assert.Throws<NotSupportedException>(() => transport.PlaceCall(NewCall()));

            Assert.That(ex!.Message, Does.Contain("not implemented"));
            Assert.That(transport.GetStatistics().OutboundCallsSucceeded, Is.EqualTo(0), "a call that was not sent must not be counted as succeeded");
        }
    }
}
