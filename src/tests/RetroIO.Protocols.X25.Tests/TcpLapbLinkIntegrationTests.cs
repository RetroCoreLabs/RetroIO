//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

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
    /// Integration tests for TcpLapbLink - loopback communication tests
    /// </summary>
    [TestFixture]
    public class TcpLapbLinkIntegrationTests
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
            _logger = _loggerFactory.CreateLogger<TcpLapbLinkIntegrationTests>();
        }

        [TearDown]
        public void TearDown()
        {
            _loggerFactory?.Dispose();
        }

        #region Connection Tests

        [Test]
        public void TcpLapbLink_ServerMode_StartsListening()
        {
            // Arrange
            var config = new TcpLapbLinkConfig
            {
                Name = "Server0",
                Mode = TcpMode.Server,
                Port = 54321,
                RemoteMachineId = "REMOTE"
            };

            using var server = new TcpLapbLink(_logger, config);

            // Act
            bool started = server.Start();

            // Assert
            Assert.That(started, Is.True);
            Assert.That(server.Status, Is.EqualTo(LinkStatus.Active));
        }

        [Test]
        public void TcpLapbLink_ClientServerLoopback_Connects()
        {
            // Arrange
            var serverConfig = new TcpLapbLinkConfig
            {
                Name = "Server0",
                Mode = TcpMode.Server,
                Port = 54322,
                RemoteMachineId = "SERVER"
            };

            var clientConfig = new TcpLapbLinkConfig
            {
                Name = "Client0",
                Mode = TcpMode.Client,
                RemoteAddress = "127.0.0.1",
                Port = 54322,
                ConnectTimeoutMs = 5000,
                RemoteMachineId = "CLIENT"
            };

            using var server = new TcpLapbLink(_logger, serverConfig);
            using var client = new TcpLapbLink(_logger, clientConfig);

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
        public void TcpLapbLink_ClientServerLoopback_LapbConnects()
        {
            // Arrange
            var serverConfig = new TcpLapbLinkConfig
            {
                Name = "Server0",
                Mode = TcpMode.Server,
                Port = 54323,
                RemoteMachineId = "SERVER",
                T1TimeoutMs = 1000,
                T2TimeoutMs = 500
            };

            var clientConfig = new TcpLapbLinkConfig
            {
                Name = "Client0",
                Mode = TcpMode.Client,
                RemoteAddress = "127.0.0.1",
                Port = 54323,
                ConnectTimeoutMs = 5000,
                RemoteMachineId = "CLIENT",
                T1TimeoutMs = 1000,
                T2TimeoutMs = 500
            };

            using var server = new TcpLapbLink(_logger, serverConfig);
            using var client = new TcpLapbLink(_logger, clientConfig);

            LapbState? serverLapbState = null;
            LapbState? clientLapbState = null;

            server.LapbStateChanged += (s, e) => serverLapbState = e.NewState;
            client.LapbStateChanged += (s, e) => clientLapbState = e.NewState;

            // Act
            server.Start();
            Thread.Sleep(100);

            client.Start();
            Thread.Sleep(500); // Allow TCP connection

            // Initiate LAPB connection from client
            client.Connect();
            Thread.Sleep(1000); // Allow LAPB handshake

            // Assert
            Assert.That(client.LapbState, Is.EqualTo(LapbState.DataTransfer));
            Assert.That(server.LapbState, Is.EqualTo(LapbState.DataTransfer));
        }

        #endregion

        #region Data Transfer Tests

        [Test]
        public void TcpLapbLink_DataTransfer_SendsAndReceives()
        {
            // Arrange
            var serverConfig = new TcpLapbLinkConfig
            {
                Name = "Server0",
                Mode = TcpMode.Server,
                Port = 54324,
                RemoteMachineId = "SERVER",
                T1TimeoutMs = 1000,
                T2TimeoutMs = 500
            };

            var clientConfig = new TcpLapbLinkConfig
            {
                Name = "Client0",
                Mode = TcpMode.Client,
                RemoteAddress = "127.0.0.1",
                Port = 54324,
                ConnectTimeoutMs = 5000,
                RemoteMachineId = "CLIENT",
                T1TimeoutMs = 1000,
                T2TimeoutMs = 500
            };

            using var server = new TcpLapbLink(_logger, serverConfig);
            using var client = new TcpLapbLink(_logger, clientConfig);

            X25Packet? receivedPacket = null;
            var packetReceived = new ManualResetEvent(false);

            server.PayloadReceived += (link, payload, length) =>
            {
                receivedPacket = new X25Packet(payload);
                packetReceived.Set();
            };

            // Act - Start and connect
            server.Start();
            Thread.Sleep(100);

            client.Start();
            Thread.Sleep(500);

            client.Connect();
            Thread.Sleep(1000);

            // Verify LAPB is connected
            Assert.That(client.LapbState, Is.EqualTo(LapbState.DataTransfer), "Client LAPB should be connected");

            // Create a simple X.25 Data packet
            // GFI=0x10 (modulo 8), LCN=1, PTI=0x00 (Data P(S)=0, P(R)=0)
            byte[] x25Packet = new byte[] { 0x10, 0x01, 0x00, 0x48, 0x65, 0x6C, 0x6C, 0x6F }; // "Hello"
            bool sent = client.SendData(x25Packet);

            // Wait for packet
            bool received = packetReceived.WaitOne(2000);

            // Assert
            Assert.That(sent, Is.True, "Packet should be sent");
            Assert.That(received, Is.True, "Packet should be received within timeout");
            Assert.That(receivedPacket, Is.Not.Null, "Received packet should not be null");
            Assert.That(receivedPacket!.LCN, Is.EqualTo(1), "LCN should match");
        }

        #endregion

        #region Sequence Number Tests

        [Test]
        public void TcpLapbLink_MultiplePackets_SequenceNumbersIncrement()
        {
            // Arrange
            var serverConfig = new TcpLapbLinkConfig
            {
                Name = "Server0",
                Mode = TcpMode.Server,
                Port = 54325,
                RemoteMachineId = "SERVER",
                T1TimeoutMs = 1000
            };

            var clientConfig = new TcpLapbLinkConfig
            {
                Name = "Client0",
                Mode = TcpMode.Client,
                RemoteAddress = "127.0.0.1",
                Port = 54325,
                ConnectTimeoutMs = 5000,
                RemoteMachineId = "CLIENT",
                T1TimeoutMs = 1000
            };

            using var server = new TcpLapbLink(_logger, serverConfig);
            using var client = new TcpLapbLink(_logger, clientConfig);

            // Act - Connect
            server.Start();
            Thread.Sleep(100);
            client.Start();
            Thread.Sleep(500);
            client.Connect();
            Thread.Sleep(1000);

            // Get initial sequence numbers
            var (initialVS, _, _) = client.GetSequenceNumbers();

            // Send multiple packets
            byte[] x25Packet = new byte[] { 0x10, 0x01, 0x00, 0x41 };
            client.SendData(x25Packet);
            client.SendData(x25Packet);
            client.SendData(x25Packet);

            Thread.Sleep(100);

            // Get final sequence numbers
            var (finalVS, _, _) = client.GetSequenceNumbers();

            // Assert - V(S) should have incremented by 3
            byte expectedVS = (byte)((initialVS + 3) % 8);
            Assert.That(finalVS, Is.EqualTo(expectedVS), "V(S) should increment for each I-frame sent");
        }

        #endregion

        #region LAPB Statistics Tests

        [Test]
        public void TcpLapbLink_AfterDataTransfer_StatisticsUpdated()
        {
            // Arrange
            var serverConfig = new TcpLapbLinkConfig
            {
                Name = "Server0",
                Mode = TcpMode.Server,
                Port = 54326,
                RemoteMachineId = "SERVER"
            };

            var clientConfig = new TcpLapbLinkConfig
            {
                Name = "Client0",
                Mode = TcpMode.Client,
                RemoteAddress = "127.0.0.1",
                Port = 54326,
                ConnectTimeoutMs = 5000,
                RemoteMachineId = "CLIENT"
            };

            using var server = new TcpLapbLink(_logger, serverConfig);
            using var client = new TcpLapbLink(_logger, clientConfig);

            // Act
            server.Start();
            Thread.Sleep(100);
            client.Start();
            Thread.Sleep(500);
            client.Connect();
            Thread.Sleep(1000);

            // Send a packet
            byte[] x25Packet = new byte[] { 0x10, 0x01, 0x00, 0x42 };
            client.SendData(x25Packet);
            Thread.Sleep(500);

            // Get statistics
            var clientStats = client.GetLapbStatistics();
            var serverStats = server.GetLapbStatistics();

            // Assert
            Assert.That(clientStats.IFramesSent, Is.GreaterThan(0), "Client should have sent I-frames");
            Assert.That(serverStats.IFramesReceived, Is.GreaterThan(0), "Server should have received I-frames");
        }

        #endregion
    }
}
