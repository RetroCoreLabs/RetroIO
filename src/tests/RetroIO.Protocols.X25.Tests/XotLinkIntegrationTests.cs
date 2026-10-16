using System;
using System.Threading;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Lapb;
using RetroIO.Protocols.X25.Layers;
using RetroIO.Protocols.X25.Runtime.Layers;
using RetroIO.Link;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Integration tests for XotLink - supports both RFC 1613 mode (raw X.25)
    /// and Cisco mode (LAPB framing over TCP)
    /// </summary>
    [TestFixture]
    public class XotLinkIntegrationTests
    {
        private ILogger _logger = null!;
        private ILoggerFactory _loggerFactory = null!;

        [SetUp]
        public void Setup()
        {
            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });
            _logger = _loggerFactory.CreateLogger<XotLinkIntegrationTests>();
        }

        [TearDown]
        public void TearDown()
        {
            _loggerFactory?.Dispose();
        }

        #region Connection Tests

        [Test]
        public void XotLink_ServerMode_StartsListening()
        {
            // Arrange
            var config = new XotLinkConfig
            {
                Name = "XotServer0",
                Mode = TcpMode.Server,
                Port = 55321,
                RemoteMachineId = "REMOTE"
            };

            using var server = new XotLink(_logger, config);

            // Act
            bool started = server.Start();

            // Assert
            Assert.That(started, Is.True);
            Assert.That(server.Status, Is.EqualTo(LinkStatus.Active));
        }

        [Test]
        public void XotLink_ClientServerLoopback_Connects()
        {
            // Arrange
            var serverConfig = new XotLinkConfig
            {
                Name = "XotServer0",
                Mode = TcpMode.Server,
                Port = 55322,
                RemoteMachineId = "SERVER"
            };

            var clientConfig = new XotLinkConfig
            {
                Name = "XotClient0",
                Mode = TcpMode.Client,
                RemoteAddress = "127.0.0.1",
                Port = 55322,
                ConnectTimeoutMs = 5000,
                RemoteMachineId = "CLIENT"
            };

            using var server = new XotLink(_logger, serverConfig);
            using var client = new XotLink(_logger, clientConfig);

            // Act
            server.Start();
            Thread.Sleep(100); // Allow server to start listening

            client.Start();
            Thread.Sleep(500); // Allow connection to establish

            // Assert
            Assert.That(server.Status, Is.EqualTo(LinkStatus.Active));
            Assert.That(client.Status, Is.EqualTo(LinkStatus.Active));
        }

        [Test]
        public void XotLink_AfterTcpConnect_LapbHandshakeCompletes()
        {
            // Arrange - Cisco mode with LAPB framing
            var serverConfig = new XotLinkConfig
            {
                Name = "XotServer0",
                Mode = TcpMode.Server,
                Port = 55328,
                RemoteMachineId = "SERVER",
                UseLapbFraming = true,
                IsPrimary = false // Server waits for SABM
            };

            var clientConfig = new XotLinkConfig
            {
                Name = "XotClient0",
                Mode = TcpMode.Client,
                RemoteAddress = "127.0.0.1",
                Port = 55328,
                ConnectTimeoutMs = 5000,
                RemoteMachineId = "CLIENT",
                UseLapbFraming = true,
                IsPrimary = true // Client initiates
            };

            using var server = new XotLink(_logger, serverConfig);
            using var client = new XotLink(_logger, clientConfig);

            var serverConnected = new ManualResetEvent(false);
            var clientConnected = new ManualResetEvent(false);

            server.LapbStateChanged += (s, e) =>
            {
                if (e.NewState == LapbState.DataTransfer)
                    serverConnected.Set();
            };

            client.LapbStateChanged += (s, e) =>
            {
                if (e.NewState == LapbState.DataTransfer)
                    clientConnected.Set();
            };

            // Act
            server.Start();
            Thread.Sleep(100);

            client.Start();
            Thread.Sleep(300); // Allow TCP connect

            // Initiate LAPB handshake
            client.Connect();

            // Wait for LAPB to connect
            bool serverOk = serverConnected.WaitOne(3000);
            bool clientOk = clientConnected.WaitOne(3000);

            // Assert
            Assert.That(serverOk, Is.True, "Server LAPB should reach DataTransfer");
            Assert.That(clientOk, Is.True, "Client LAPB should reach DataTransfer");
            Assert.That(server.LapbState, Is.EqualTo(LapbState.DataTransfer));
            Assert.That(client.LapbState, Is.EqualTo(LapbState.DataTransfer));
        }

        #endregion

        #region Data Transfer Tests

        [Test]
        public void XotLink_DataTransfer_SendsAndReceives()
        {
            // Arrange - Cisco mode with LAPB framing
            var serverConfig = new XotLinkConfig
            {
                Name = "XotServer0",
                Mode = TcpMode.Server,
                Port = 55323,
                RemoteMachineId = "SERVER",
                UseLapbFraming = true,
                IsPrimary = false
            };

            var clientConfig = new XotLinkConfig
            {
                Name = "XotClient0",
                Mode = TcpMode.Client,
                RemoteAddress = "127.0.0.1",
                Port = 55323,
                ConnectTimeoutMs = 5000,
                RemoteMachineId = "CLIENT",
                UseLapbFraming = true,
                IsPrimary = true
            };

            using var server = new XotLink(_logger, serverConfig);
            using var client = new XotLink(_logger, clientConfig);

            X25Packet? receivedPacket = null;
            var packetReceived = new ManualResetEvent(false);
            var lapbConnected = new ManualResetEvent(false);

            server.PayloadReceived += (link, payload, length) =>
            {
                receivedPacket = new X25Packet(payload);
                packetReceived.Set();
            };

            client.LapbStateChanged += (s, e) =>
            {
                if (e.NewState == LapbState.DataTransfer)
                    lapbConnected.Set();
            };

            // Act - Connect TCP
            server.Start();
            Thread.Sleep(100);

            client.Start();
            Thread.Sleep(300);

            // Initiate LAPB handshake
            client.Connect();
            bool lapbOk = lapbConnected.WaitOne(3000);
            Assert.That(lapbOk, Is.True, "LAPB should connect");

            // Create a simple X.25 Call Request packet
            // GFI=0x10 (modulo 8), LCN=1, PTI=0x0B (Call Request)
            byte[] x25Packet = new byte[] { 0x10, 0x01, 0x0B, 0x21, 0x32, 0x30, 0x30 }; // Call to "200"
            bool sent = client.SendData(x25Packet);

            // Wait for packet
            bool received = packetReceived.WaitOne(2000);

            // Assert
            Assert.That(sent, Is.True, "Packet should be sent");
            Assert.That(received, Is.True, "Packet should be received within timeout");
            Assert.That(receivedPacket, Is.Not.Null, "Received packet should not be null");
            Assert.That(receivedPacket!.LCN, Is.EqualTo(1), "LCN should match");
            Assert.That(receivedPacket.PacketType, Is.EqualTo(X25PacketType.CallRequest), "Should be Call Request");
        }

        [Test]
        public void XotLink_MultiplePackets_AllReceived()
        {
            // Arrange - Cisco mode with LAPB framing
            var serverConfig = new XotLinkConfig
            {
                Name = "XotServer0",
                Mode = TcpMode.Server,
                Port = 55324,
                RemoteMachineId = "SERVER",
                UseLapbFraming = true,
                IsPrimary = false
            };

            var clientConfig = new XotLinkConfig
            {
                Name = "XotClient0",
                Mode = TcpMode.Client,
                RemoteAddress = "127.0.0.1",
                Port = 55324,
                ConnectTimeoutMs = 5000,
                RemoteMachineId = "CLIENT",
                UseLapbFraming = true,
                IsPrimary = true
            };

            using var server = new XotLink(_logger, serverConfig);
            using var client = new XotLink(_logger, clientConfig);

            int receivedCount = 0;
            var allReceived = new ManualResetEvent(false);
            var lapbConnected = new ManualResetEvent(false);

            server.PayloadReceived += (link, payload, length) =>
            {
                receivedCount++;
                if (receivedCount >= 5)
                {
                    allReceived.Set();
                }
            };

            client.LapbStateChanged += (s, e) =>
            {
                if (e.NewState == LapbState.DataTransfer)
                    lapbConnected.Set();
            };

            // Act
            server.Start();
            Thread.Sleep(100);
            client.Start();
            Thread.Sleep(300);

            // Establish LAPB
            client.Connect();
            bool lapbOk = lapbConnected.WaitOne(3000);
            Assert.That(lapbOk, Is.True, "LAPB should connect");

            // Send 5 packets
            byte[] x25Packet = new byte[] { 0x10, 0x01, 0x00, 0x41 }; // Data packet
            for (int i = 0; i < 5; i++)
            {
                client.SendData(x25Packet);
                Thread.Sleep(50); // Give time for LAPB processing
            }

            // Wait for all packets.
            //
            // The budget has to cover LAPB recovering by itself, not just the happy
            // path. T1 defaults to 3000 ms (LinkConfig.T1TimeoutMs), so ONE lost
            // frame costs a full 3 s before it is retransmitted - and the old 5 s
            // budget left barely 2 s of slack once the five 50 ms send gaps and
            // scheduling delay were taken out. This test asserts that all five
            // packets arrive, not that they arrive without a retransmission, so it
            // must allow at least a couple of T1 cycles.
            //
            // Measured 2026-09-05: failed once in a full-suite run, then passed 10
            // of 10 running the fixture alone and 3 of 3 running the single test
            // alone. That pattern - only under full-suite CPU load - is what a
            // too-tight timeout looks like. The cause is NOT proven; if this ever
            // fails again with a generous budget, the fault is in XotLink or LAPB
            // and not in the clock.
            bool received = allReceived.WaitOne(15000);

            // Assert
            Assert.That(received, Is.True, "All packets should be received");
            Assert.That(receivedCount, Is.EqualTo(5), "Should receive exactly 5 packets");
        }

        #endregion

        #region Statistics Tests

        [Test]
        public void XotLink_AfterDataTransfer_StatisticsUpdated()
        {
            // Arrange - Cisco mode with LAPB framing
            var serverConfig = new XotLinkConfig
            {
                Name = "XotServer0",
                Mode = TcpMode.Server,
                Port = 55325,
                RemoteMachineId = "SERVER",
                UseLapbFraming = true,
                IsPrimary = false
            };

            var clientConfig = new XotLinkConfig
            {
                Name = "XotClient0",
                Mode = TcpMode.Client,
                RemoteAddress = "127.0.0.1",
                Port = 55325,
                ConnectTimeoutMs = 5000,
                RemoteMachineId = "CLIENT",
                UseLapbFraming = true,
                IsPrimary = true
            };

            using var server = new XotLink(_logger, serverConfig);
            using var client = new XotLink(_logger, clientConfig);

            // The link delivers opaque payloads (X.25 classification happens above the link now).
            int serverPayloads = 0;
            server.PayloadReceived += (link, payload, length) => System.Threading.Interlocked.Increment(ref serverPayloads);

            var lapbConnected = new ManualResetEvent(false);
            client.LapbStateChanged += (s, e) =>
            {
                if (e.NewState == LapbState.DataTransfer)
                    lapbConnected.Set();
            };

            // Act
            server.Start();
            Thread.Sleep(100);
            client.Start();
            Thread.Sleep(300);

            // Establish LAPB
            client.Connect();
            bool lapbOk = lapbConnected.WaitOne(3000);
            Assert.That(lapbOk, Is.True, "LAPB should connect");

            // Send packets
            byte[] x25Packet = new byte[] { 0x10, 0x01, 0x00, 0x42 };
            client.SendData(x25Packet);
            client.SendData(x25Packet);
            client.SendData(x25Packet);
            Thread.Sleep(500);

            // Get statistics
            var clientStats = client.GetStatistics();
            var serverStats = server.GetStatistics();

            // Assert - Note: With LAPB, there are also I-frame headers and SABM/UA exchanges
            // With LAPB the wire also carries SABM/UA/RR, so frame count is at least the 3 I-frames
            Assert.That(clientStats.FramesTransmitted, Is.GreaterThanOrEqualTo(3), "Client should have sent 3 payloads");
            Assert.That(clientStats.BytesTransmitted, Is.GreaterThan(0), "Client should have transmitted bytes");

            Assert.That(serverPayloads, Is.EqualTo(3), "Server should have received 3 payloads");
            Assert.That(serverStats.BytesReceived, Is.GreaterThan(0), "Server should have received bytes");
        }

        #endregion

        #region XOT Header Tests

        [Test]
        public void XotLink_LargePacket_HandlesCorrectly()
        {
            // Arrange - Cisco mode with LAPB framing
            var serverConfig = new XotLinkConfig
            {
                Name = "XotServer0",
                Mode = TcpMode.Server,
                Port = 55326,
                RemoteMachineId = "SERVER",
                UseLapbFraming = true,
                IsPrimary = false
            };

            var clientConfig = new XotLinkConfig
            {
                Name = "XotClient0",
                Mode = TcpMode.Client,
                RemoteAddress = "127.0.0.1",
                Port = 55326,
                ConnectTimeoutMs = 5000,
                RemoteMachineId = "CLIENT",
                UseLapbFraming = true,
                IsPrimary = true
            };

            using var server = new XotLink(_logger, serverConfig);
            using var client = new XotLink(_logger, clientConfig);

            X25Packet? receivedPacket = null;
            var packetReceived = new ManualResetEvent(false);
            var lapbConnected = new ManualResetEvent(false);

            server.PayloadReceived += (link, payload, length) =>
            {
                receivedPacket = new X25Packet(payload);
                packetReceived.Set();
            };

            client.LapbStateChanged += (s, e) =>
            {
                if (e.NewState == LapbState.DataTransfer)
                    lapbConnected.Set();
            };

            // Act
            server.Start();
            Thread.Sleep(100);
            client.Start();
            Thread.Sleep(300);

            // Establish LAPB
            client.Connect();
            bool lapbOk = lapbConnected.WaitOne(3000);
            Assert.That(lapbOk, Is.True, "LAPB should connect");

            // Create a large X.25 packet (1000 bytes of data)
            byte[] largePacket = new byte[1003]; // 3 header + 1000 data
            largePacket[0] = 0x10; // GFI
            largePacket[1] = 0x01; // LCN
            largePacket[2] = 0x00; // PTI (Data)
            for (int i = 3; i < largePacket.Length; i++)
            {
                largePacket[i] = (byte)(i & 0xFF);
            }

            bool sent = client.SendData(largePacket);
            bool received = packetReceived.WaitOne(3000);

            // Assert
            Assert.That(sent, Is.True, "Large packet should be sent");
            Assert.That(received, Is.True, "Large packet should be received");
            Assert.That(receivedPacket, Is.Not.Null);
            Assert.That(receivedPacket!.FullPacket.Length, Is.EqualTo(1003), "Packet size should match");
        }

        #endregion

        #region Cisco XOT with LAPB Tests

        [Test]
        public void XotLink_ImplementsILapbCapable()
        {
            // Arrange
            var config = new XotLinkConfig
            {
                Name = "XotTest",
                Mode = TcpMode.Server,
                Port = 55327,
                RemoteMachineId = "TEST"
            };

            using var link = new XotLink(_logger, config);

            // Assert - Cisco XOT link IS ILapbCapable (carries LAPB frames)
            Assert.That(link, Is.InstanceOf<ILapbCapable>());
            Assert.That(link.Type, Is.EqualTo(LinkType.Xot));
            Assert.That(link.LapbState, Is.EqualTo(LapbState.Disconnected));
        }

        [Test]
        public void XotLink_LapbSequenceNumbers_StartAtZero()
        {
            // Arrange - Cisco mode with LAPB framing
            var config = new XotLinkConfig
            {
                Name = "XotTest",
                Mode = TcpMode.Server,
                Port = 55329,
                RemoteMachineId = "TEST",
                UseLapbFraming = true
            };

            using var link = new XotLink(_logger, config);
            link.Start();
            Thread.Sleep(100);

            // Act
            var (vs, vr, va) = link.GetSequenceNumbers();

            // Assert
            Assert.That(vs, Is.EqualTo(0), "V(S) should start at 0");
            Assert.That(vr, Is.EqualTo(0), "V(R) should start at 0");
            Assert.That(va, Is.EqualTo(0), "V(A) should start at 0");
        }

        [Test]
        public void XotLink_GetLapbStatistics_ReturnsValidStats()
        {
            // Arrange - Cisco mode with LAPB framing
            var config = new XotLinkConfig
            {
                Name = "XotTest",
                Mode = TcpMode.Server,
                Port = 55330,
                RemoteMachineId = "TEST",
                UseLapbFraming = true
            };

            using var link = new XotLink(_logger, config);
            link.Start();
            Thread.Sleep(100);

            // Act
            var stats = link.GetLapbStatistics();

            // Assert
            Assert.That(stats, Is.Not.Null);
            Assert.That(stats.IFramesSent, Is.EqualTo(0));
            Assert.That(stats.IFramesReceived, Is.EqualTo(0));
        }

        #endregion

        #region RFC 1613 Mode Tests (Raw X.25 over TCP)

        [Test]
        public void XotLink_Rfc1613Mode_SendsRawX25Packets()
        {
            // Arrange - RFC 1613 mode (default, no LAPB framing)
            var serverConfig = new XotLinkConfig
            {
                Name = "XotServer0",
                Mode = TcpMode.Server,
                Port = 55340,
                RemoteMachineId = "SERVER",
                UseLapbFraming = false // RFC 1613 mode
            };

            var clientConfig = new XotLinkConfig
            {
                Name = "XotClient0",
                Mode = TcpMode.Client,
                RemoteAddress = "127.0.0.1",
                Port = 55340,
                ConnectTimeoutMs = 5000,
                RemoteMachineId = "CLIENT",
                UseLapbFraming = false // RFC 1613 mode
            };

            using var server = new XotLink(_logger, serverConfig);
            using var client = new XotLink(_logger, clientConfig);

            X25Packet? receivedPacket = null;
            var packetReceived = new ManualResetEvent(false);

            server.PayloadReceived += (link, payload, length) =>
            {
                receivedPacket = new X25Packet(payload);
                packetReceived.Set();
            };

            // Act - Connect TCP (no LAPB handshake needed)
            server.Start();
            Thread.Sleep(100);

            client.Start();
            Thread.Sleep(500); // Allow TCP connection

            // Send raw X.25 packet - no LAPB Connect needed in RFC 1613 mode
            byte[] x25Packet = new byte[] { 0x10, 0x01, 0x0B, 0x21, 0x32, 0x30, 0x30 }; // Call to "200"
            bool sent = client.SendData(x25Packet);

            // Wait for packet
            bool received = packetReceived.WaitOne(3000);

            // Assert
            Assert.That(sent, Is.True, "Packet should be sent");
            Assert.That(received, Is.True, "Packet should be received within timeout");
            Assert.That(receivedPacket, Is.Not.Null, "Received packet should not be null");
            Assert.That(receivedPacket!.LCN, Is.EqualTo(1), "LCN should match");
            Assert.That(receivedPacket.PacketType, Is.EqualTo(X25PacketType.CallRequest), "Should be Call Request");
        }

        [Test]
        public void XotLink_Rfc1613Mode_MultiplePackets()
        {
            // Arrange - RFC 1613 mode
            var serverConfig = new XotLinkConfig
            {
                Name = "XotServer0",
                Mode = TcpMode.Server,
                Port = 55341,
                RemoteMachineId = "SERVER",
                UseLapbFraming = false
            };

            var clientConfig = new XotLinkConfig
            {
                Name = "XotClient0",
                Mode = TcpMode.Client,
                RemoteAddress = "127.0.0.1",
                Port = 55341,
                ConnectTimeoutMs = 5000,
                RemoteMachineId = "CLIENT",
                UseLapbFraming = false
            };

            using var server = new XotLink(_logger, serverConfig);
            using var client = new XotLink(_logger, clientConfig);

            int receivedCount = 0;
            var allReceived = new ManualResetEvent(false);

            server.PayloadReceived += (link, payload, length) =>
            {
                receivedCount++;
                if (receivedCount >= 5)
                {
                    allReceived.Set();
                }
            };

            // Act
            server.Start();
            Thread.Sleep(100);
            client.Start();
            Thread.Sleep(500);

            // Send 5 packets - no LAPB handshake needed
            byte[] x25Packet = new byte[] { 0x10, 0x01, 0x00, 0x41 }; // Data packet
            for (int i = 0; i < 5; i++)
            {
                client.SendData(x25Packet);
                Thread.Sleep(50);
            }

            // Wait for all packets
            bool received = allReceived.WaitOne(5000);

            // Assert
            Assert.That(received, Is.True, "All packets should be received");
            Assert.That(receivedCount, Is.EqualTo(5), "Should receive exactly 5 packets");
        }

        [Test]
        public void XotLink_Rfc1613Mode_ConnectIsIgnored()
        {
            // Arrange - RFC 1613 mode
            var config = new XotLinkConfig
            {
                Name = "XotTest",
                Mode = TcpMode.Server,
                Port = 55342,
                RemoteMachineId = "TEST",
                UseLapbFraming = false // RFC 1613 mode
            };

            using var link = new XotLink(_logger, config);
            link.Start();
            Thread.Sleep(100);

            // Act - Connect should be ignored in RFC 1613 mode
            link.Connect();

            // Assert - LAPB state should remain Disconnected (no LAPB in RFC 1613)
            Assert.That(link.LapbState, Is.EqualTo(LapbState.Disconnected));
        }

        #endregion
    }
}
