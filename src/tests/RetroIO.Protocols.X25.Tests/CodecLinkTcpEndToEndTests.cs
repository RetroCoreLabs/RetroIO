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
    /// Full-stack test of a SLIP CodecLink over a real localhost TCP connection.
    /// </summary>
    /// <remarks>
    /// Composes the two independently-proven pieces — TcpLinkTransport (real sockets) and CodecLink
    /// (SLIP framing + FCS + LAPB) — to prove SLIP works over actual TCP end-to-end: the primary connects,
    /// LAPB establishes across the wire, and an X.25 packet is delivered to the peer. Uses an OS-assigned
    /// ephemeral port and event/timeout-driven synchronization to stay deterministic.
    /// </remarks>
    [TestFixture]
    public class CodecLinkTcpEndToEndTests
    {
        private ILoggerFactory _factory = null!;
        private ILogger _logger = null!;

        [SetUp]
        public void Setup()
        {
            _factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            _logger = _factory.CreateLogger<CodecLinkTcpEndToEndTests>();
        }

        [TearDown]
        public void TearDown()
        {
            _factory?.Dispose();
        }

        [Test]
        public void SlipOverRealTcp_EstablishesLapbAndDeliversX25()
        {
            object gate = new object();
            List<byte[]> x25OnServer = new List<byte[]>();
            ManualResetEventSlim gotPacket = new ManualResetEventSlim(false);

            // Server side (secondary): start first so it is listening; its port is then known.
            TcpLinkTransport serverTransport = new TcpLinkTransport(_logger, "B", isServer: true, "127.0.0.1", 0);
            CodecLinkConfig serverConfig = new CodecLinkConfig
            {
                Name = "B",
                Framing = FramingType.Slip,
                IsPrimary = false,
                LapbAutoConnect = false,
                ProtocolMode = new ProtocolModeConfig { Mode = ProtocolDetectionMode.AutoDetect }
            };
            using CodecLink serverLink = new CodecLink(_logger, serverConfig, serverTransport);
            serverLink.PayloadReceived += (link, payload, length) =>
            {
                lock (gate)
                {
                    x25OnServer.Add(payload);
                }
                gotPacket.Set();
            };

            Assert.That(serverLink.Start(), Is.True, "server link should start listening");
            int port = serverTransport.Port;

            // Client side (primary): connects and auto-establishes LAPB.
            TcpLinkTransport clientTransport = new TcpLinkTransport(_logger, "A", isServer: false, "127.0.0.1", port);
            CodecLinkConfig clientConfig = new CodecLinkConfig
            {
                Name = "A",
                Framing = FramingType.Slip,
                IsPrimary = true,
                LapbAutoConnect = true,
                ProtocolMode = new ProtocolModeConfig { Mode = ProtocolDetectionMode.AutoDetect }
            };
            using CodecLink clientLink = new CodecLink(_logger, clientConfig, clientTransport);
            Assert.That(clientLink.Start(), Is.True, "client link should connect");

            // SendX25Packet returns false until LAPB has established over the wire; retry until connected.
            byte[] x25 = new byte[] { 0x10, 0x01, 0x0B, 0xAA, 0xBB };
            long deadline = Environment.TickCount64 + 5000;
            bool sent = false;
            while (Environment.TickCount64 < deadline)
            {
                if (clientLink.SendData(x25))
                {
                    sent = true;
                    break;
                }

                Thread.Sleep(10);
            }

            Assert.That(sent, Is.True, "LAPB should establish over TCP so the packet can be sent");
            Assert.That(gotPacket.Wait(TimeSpan.FromSeconds(5)), Is.True, "server should receive the X.25 packet");
            lock (gate)
            {
                Assert.That(x25OnServer.Count, Is.EqualTo(1));
                Assert.That(x25OnServer[0], Is.EqualTo(x25));
            }
        }
    }
}
