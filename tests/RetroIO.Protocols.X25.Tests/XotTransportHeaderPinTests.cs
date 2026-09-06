using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Transport;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Pins what the two socket-driven XOT transports do with a bad XOT header. RFC 1613 section 3
    /// defines the header as a 2-octet version (always 0) and a 2-octet length; there is no frame
    /// delimiter and therefore no way to find the next frame after a bad header. The only sound
    /// reaction is to close the connection. Both transports instead <c>continue</c> to the next
    /// header read WITHOUT consuming the payload that follows the bad header, so the stream is read
    /// from the wrong offset for the rest of the connection's life. The one-stack plan (Phase E3)
    /// rewires both onto <c>XotFrameCodec</c>; these tests go red when that happens.
    /// </summary>
    /// <remarks>
    /// Real sockets on 127.0.0.1 and a free port picked by the OS; each test is bounded by a short
    /// wait and disposes everything it opened.
    /// </remarks>
    [TestFixture]
    public class XotTransportHeaderPinTests
    {
        private static readonly ILogger Logger = NullLogger.Instance;

        // Version 1 (bad), length 3, then 3 payload octets.
        private static readonly byte[] BadVersionFrame = { 0x00, 0x01, 0x00, 0x03, 0xAA, 0xBB, 0xCC };

        // Version 0, length 3: an RR for LCN 1 with P(R)=5.
        private static readonly byte[] GoodFrame = { 0x00, 0x00, 0x00, 0x03, 0x10, 0x01, 0xA1 };

        private static int FreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        [Test]
        public void Server_BadVersionHeader_DesyncsTheStreamAndKeepsItOpen_WHICH_IS_WRONG()
        {
            int port = FreePort();
            XotServerTransport server = new XotServerTransport(Logger, new XotServerConfig
            {
                Name = "pin",
                ListenAddress = "127.0.0.1",
                ListenPort = port,
            });

            int received = 0;
            server.DataReceived += (s, e) => Interlocked.Increment(ref received);
            Assert.That(server.Start(), Is.True);

            try
            {
                using TcpClient client = new TcpClient();
                client.Connect(IPAddress.Loopback, port);
                NetworkStream stream = client.GetStream();

                stream.Write(BadVersionFrame, 0, BadVersionFrame.Length);
                stream.Write(GoodFrame, 0, GoodFrame.Length);
                stream.Flush();
                Thread.Sleep(400);

                // MEASURED 2026-09-06 (XotServerTransport.cs, HandleConnectionLoop): version != 0
                // logs a warning and `continue`s. The next 4-octet header read therefore starts at
                // AA BB CC 00 - version 0xAABB, rejected again - and then at 00 00 03 10, which is
                // version 0 with length 0x0310 = 784: the loop now blocks waiting for 784 octets
                // that never come. The good frame is never delivered and the connection is never
                // closed. Correct: close on the first bad header (Phase E3).
                Assert.That(received, Is.EqualTo(0),
                    "behaviour changed: if the good frame is now delivered, or the connection is closed, look at Phase E3 and rewrite this test");
                Assert.That(IsStillOpen(client), Is.True,
                    "behaviour changed: closing the connection on a bad header is the FIX - rewrite this test");
            }
            finally
            {
                server.Stop();
            }
        }

        [Test]
        public void Server_GoodFrame_IsDelivered()
        {
            int port = FreePort();
            XotServerTransport server = new XotServerTransport(Logger, new XotServerConfig
            {
                Name = "pin",
                ListenAddress = "127.0.0.1",
                ListenPort = port,
            });

            int received = 0;
            byte[]? payload = null;
            server.DataReceived += (s, e) =>
            {
                payload = new byte[e.Length];
                Array.Copy(e.Data, payload, e.Length);
                Interlocked.Increment(ref received);
            };
            Assert.That(server.Start(), Is.True);

            try
            {
                using TcpClient client = new TcpClient();
                client.Connect(IPAddress.Loopback, port);
                NetworkStream stream = client.GetStream();
                stream.Write(GoodFrame, 0, GoodFrame.Length);
                stream.Flush();

                WaitFor(() => Volatile.Read(ref received) == 1, 2000);
                Assert.That(received, Is.EqualTo(1));
                Assert.That(payload, Is.EqualTo(new byte[] { 0x10, 0x01, 0xA1 }));
            }
            finally
            {
                server.Stop();
            }
        }

        [Test]
        public void Client_BadVersionHeader_DesyncsTheStreamAndKeepsItOpen_WHICH_IS_WRONG()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            XotClientTransport client = new XotClientTransport(Logger, new XotClientConfig { Name = "pin" });
            int received = 0;
            client.DataReceived += (s, e) => Interlocked.Increment(ref received);

            try
            {
                Assert.That(client.Start(), Is.True);
                Assert.That(client.ConnectToXotGateway("127.0.0.1", port), Is.True);

                using TcpClient peer = listener.AcceptTcpClient();
                NetworkStream stream = peer.GetStream();
                stream.Write(BadVersionFrame, 0, BadVersionFrame.Length);
                stream.Write(GoodFrame, 0, GoodFrame.Length);
                stream.Flush();
                Thread.Sleep(400);

                // MEASURED 2026-09-06 (XotClientTransport.cs, the receive loop): same `continue`
                // as the server, same consequence.
                Assert.That(received, Is.EqualTo(0),
                    "behaviour changed: if the good frame is now delivered, or the connection is closed, look at Phase E3 and rewrite this test");
                Assert.That(IsStillOpen(peer), Is.True,
                    "behaviour changed: closing the connection on a bad header is the FIX - rewrite this test");
            }
            finally
            {
                client.Stop();
                listener.Stop();
            }
        }

        /// <summary>
        /// True while the far end has not closed: a readable socket with zero bytes available
        /// means FIN was received.
        /// </summary>
        private static bool IsStillOpen(TcpClient c)
        {
            Socket s = c.Client;
            if (s.Poll(0, SelectMode.SelectRead))
            {
                return s.Available > 0;
            }

            return true;
        }

        private static void WaitFor(Func<bool> condition, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (!condition() && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10);
            }
        }
    }
}
