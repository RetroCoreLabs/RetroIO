//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Link;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Integration tests for TcpLinkTransport over a localhost TCP connection.
    /// </summary>
    /// <remarks>
    /// Validates the raw byte transport contract (open, accept, write, receive, close) on real sockets
    /// using an OS-assigned ephemeral port to avoid port conflicts, and event-driven waits with timeouts
    /// plus byte accumulation to tolerate TCP segmentation. This exercises the real transport that
    /// CodecLink runs SLIP/HDLC over; the framing/LAPB logic itself is covered by the fake-transport tests.
    /// </remarks>
    [TestFixture]
    public class TcpLinkTransportTests
    {
        private ILoggerFactory _factory = null!;
        private ILogger _logger = null!;

        [SetUp]
        public void Setup()
        {
            _factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            _logger = _factory.CreateLogger<TcpLinkTransportTests>();
        }

        [TearDown]
        public void TearDown()
        {
            _factory?.Dispose();
        }

        [Test]
        public void TcpLinkTransport_Loopback_ExchangesBytesBothDirections()
        {
            object gate = new object();
            List<byte> serverBytes = new List<byte>();
            List<byte> clientBytes = new List<byte>();
            ManualResetEventSlim serverAccepted = new ManualResetEventSlim(false);
            ManualResetEventSlim serverData = new ManualResetEventSlim(false);
            ManualResetEventSlim clientData = new ManualResetEventSlim(false);

            using TcpLinkTransport server = new TcpLinkTransport(_logger, "srv", isServer: true, "127.0.0.1", 0);
            server.Opened += name => serverAccepted.Set();
            server.DataReceived += (name, data) =>
            {
                lock (gate)
                {
                    Append(serverBytes, data);
                }
                serverData.Set();
            };

            Assert.That(server.Open(), Is.True, "server should start listening");
            int port = server.Port;

            using TcpLinkTransport client = new TcpLinkTransport(_logger, "cli", isServer: false, "127.0.0.1", port);
            client.DataReceived += (name, data) =>
            {
                lock (gate)
                {
                    Append(clientBytes, data);
                }
                clientData.Set();
            };

            Assert.That(client.Open(), Is.True, "client should connect");
            Assert.That(serverAccepted.Wait(TimeSpan.FromSeconds(5)), Is.True, "server should accept the connection");

            // client -> server (bytes include SLIP/HDLC control octets to prove the transport is opaque).
            byte[] toServer = new byte[] { 0x7E, 0xC0, 0x01, 0x02, 0xDB };
            client.Write(toServer);
            Assert.That(WaitForBytes(gate, serverBytes, toServer.Length, serverData, 5000), Is.True,
                "server should receive all bytes");
            lock (gate)
            {
                Assert.That(serverBytes.ToArray(), Is.EqualTo(toServer));
            }

            // server -> client
            byte[] toClient = new byte[] { 0xAA, 0xBB, 0xCC };
            server.Write(toClient);
            Assert.That(WaitForBytes(gate, clientBytes, toClient.Length, clientData, 5000), Is.True,
                "client should receive all bytes");
            lock (gate)
            {
                Assert.That(clientBytes.ToArray(), Is.EqualTo(toClient));
            }
        }

        private static void Append(List<byte> target, ReadOnlySpan<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                target.Add(data[i]);
            }
        }

        // Waits until 'target' holds at least 'expected' bytes, re-checking on each data signal.
        private static bool WaitForBytes(object gate, List<byte> target, int expected, ManualResetEventSlim signal, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                lock (gate)
                {
                    if (target.Count >= expected)
                    {
                        return true;
                    }
                }

                int remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0)
                {
                    return false;
                }

                signal.Wait(remaining);
                signal.Reset();
            }
        }
    }
}
