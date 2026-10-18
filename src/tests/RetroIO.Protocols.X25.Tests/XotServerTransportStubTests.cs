//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Transport;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The XOT server transport has no way to answer a call on its TCP connection yet. These tests pin
    /// that AcceptCall and RejectCall say so, instead of counting a call and returning true.
    /// </summary>
    [TestFixture]
    public class XotServerTransportStubTests
    {
        private static readonly ILogger Logger = NullLogger.Instance;

        private static XotServerTransport NewTransport()
        {
            return new XotServerTransport(Logger, new XotServerConfig
            {
                Name = "stub",
                ListenAddress = "127.0.0.1",
                ListenPort = 0,
            });
        }

        private static X25CallRequest NewCall()
        {
            return new X25CallRequest("200", "100");
        }

        [Test]
        public void AcceptCall_IsNotImplemented_AndSaysSo()
        {
            XotServerTransport transport = NewTransport();

            NotSupportedException? ex = Assert.Throws<NotSupportedException>(() => transport.AcceptCall(NewCall()));

            Assert.That(ex!.Message, Does.Contain("not implemented"));
            Assert.That(transport.GetStatistics().InboundCallsAccepted, Is.EqualTo(0), "a call that was not answered must not be counted as accepted");
        }

        [Test]
        public void RejectCall_IsNotImplemented_AndSaysSo()
        {
            XotServerTransport transport = NewTransport();

            NotSupportedException? ex = Assert.Throws<NotSupportedException>(() => transport.RejectCall(NewCall(), 0x0B));

            Assert.That(ex!.Message, Does.Contain("not implemented"));
            Assert.That(transport.GetStatistics().InboundCallsRejected, Is.EqualTo(0), "a call that was not cleared must not be counted as rejected");
        }
    }
}
