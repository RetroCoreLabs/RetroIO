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
    /// What the two XOT transports do with a header that is not a header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RFC 1613 gives XOT a four-octet header and nothing else: no flag byte, no marker, nothing to
    /// find the next frame boundary with. So a wrong version or an impossible length is the end of
    /// the connection. Both transports used to log a warning and carry on round the loop, reading
    /// the next four octets as another header. In the over-length case those four octets were the
    /// start of a payload that had never been consumed, so the stream was read from the wrong offset
    /// for the rest of the connection's life and every packet after it was rubbish.
    /// </para>
    /// <para>
    /// These tests were written as pins that said the old behaviour was wrong in their names, and
    /// rewritten when Phase E3 fixed it. Real sockets on 127.0.0.1 and a free port picked by the
    /// operating system; each test is bounded by a short wait and disposes what it opened.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class XotTransportHeaderTests
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
        public void Server_BadVersionHeader_ClosesTheConnection()
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

                // The good frame that followed the bad header is NOT delivered. That is the point:
                // after a wrong header nothing in the stream is at a known offset, so anything read
                // from it would be a guess. The connection goes instead.
                Assert.That(received, Is.EqualTo(0), "nothing may be delivered from a stream that has lost its framing");
                Assert.That(IsStillOpen(client), Is.False, "the connection must be closed on the first bad header");
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
        public void Client_BadVersionHeader_ClosesTheConnection()
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

                // Same rule on the client side. It also has to actually close the socket: leaving
                // the read loop without closing left the connection up with nobody reading it, so
                // the peer saw a live connection that would never answer again.
                Assert.That(received, Is.EqualTo(0), "nothing may be delivered from a stream that has lost its framing");
                Assert.That(IsStillOpen(peer), Is.False, "the connection must be closed on the first bad header");
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
