//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Link.Physical;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Tests for <see cref="TCPServer.SendByte"/> over a real loopback connection.
    /// </summary>
    [TestFixture]
    public class TcpServerSendTests
    {
        // Asks the OS for a free port, then releases it so the server can bind it.
        private static int FreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        [Test]
        public void SendByte_AfterASuccessfulSend_ReturnsTrue_AndTheClientGetsTheByte()
        {
            // Regression: SendByte returned true only when Socket.Send reported 0 bytes sent,
            // so a successful one-byte send (Send returns 1) came back as false.
            int port = FreePort();
            TCPServer server = new TCPServer(NullLogger.Instance);
            Assert.That(server.StartServer("127.0.0.1", port), Is.True);

            using TcpClient client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);

            // The server accepts on its clock tick; give it a few ticks to see the connection.
            Stopwatch wait = Stopwatch.StartNew();
            bool sent = false;
            while (!sent && wait.ElapsedMilliseconds < 3000)
            {
                server.ClockTick();
                sent = server.SendByte(0x42);
            }

            Assert.That(sent, Is.True, "SendByte must report success when the byte was sent");

            client.ReceiveTimeout = 3000;
            byte[] got = new byte[1];
            int read = client.GetStream().Read(got, 0, 1);
            Assert.That(read, Is.EqualTo(1));
            Assert.That(got[0], Is.EqualTo(0x42));
        }

        [Test]
        public void SendByte_WithNoClientConnected_ReturnsFalse()
        {
            TCPServer server = new TCPServer(NullLogger.Instance);
            Assert.That(server.StartServer("127.0.0.1", FreePort()), Is.True);

            Assert.That(server.SendByte(0x42), Is.False);
        }
    }
}
