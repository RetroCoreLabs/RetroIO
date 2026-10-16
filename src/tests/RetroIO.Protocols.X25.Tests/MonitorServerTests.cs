using System;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;
using RetroIO.Protocols.X25.Monitor;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Integration test for <see cref="MonitorServer"/> over a real loopback TCP connection: a viewer client
    /// connects, the server publishes a decoded capture record, and the client receives it framed over the
    /// wire — the full server-decode-and-push path a remote GUI relies on.
    /// </summary>
    [TestFixture]
    public class MonitorServerTests
    {
        [Test]
        public void Publish_ReachesConnectedClient_OverTcp()
        {
            using MonitorServer server = new MonitorServer("127.0.0.1", 0);
            Assert.That(server.Start(), Is.True, "the server should bind");

            using TcpClient tcp = new TcpClient();
            tcp.Connect("127.0.0.1", server.ListenPort);
            NetworkStream stream = tcp.GetStream();

            CaptureRecord? received = null;
            ManualResetEventSlim got = new ManualResetEventSlim(false);
            MonitorFrameReader reader = new MonitorFrameReader();
            reader.MessageReceived += m =>
            {
                if (string.Equals(m.Type, MonitorProtocol.TypeCapture, StringComparison.Ordinal))
                {
                    received = m.Capture;
                    got.Set();
                }
            };

            Thread readThread = new Thread(() =>
            {
                byte[] buffer = new byte[4096];
                while (true)
                {
                    int n;
                    try
                    {
                        n = stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (Exception)
                    {
                        break;
                    }

                    if (n <= 0)
                    {
                        break;
                    }

                    reader.ProcessBytes(buffer.AsSpan(0, n));
                }
            })
            {
                IsBackground = true
            };
            readThread.Start();

            // Wait for the server to register the accepted connection before publishing.
            Assert.That(SpinWait.SpinUntil(() => server.ClientCount == 1, 3000), Is.True,
                "the server should register the client");

            // Under X25MON/1 the server ships the raw frame; the viewer decodes it.
            byte[] packet = X25PacketBuilder.CreateCallRequest(1, "23410001", "23400001", null, null);
            CaptureRecord record = new CaptureRecord
            {
                Seq = 1,
                Port = 0,
                Direction = CaptureDirection.Rx,
                TimestampMs = 999,
                Frame = packet,
                TotalLength = packet.Length
            };
            server.Publish(record);

            Assert.That(got.Wait(3000), Is.True, "the client should receive the published capture");
            Assert.That(received, Is.Not.Null);
            Assert.That(received!.Frame, Is.EqualTo(packet), "the raw frame crosses the wire intact");

            // The viewer decodes the raw frame into the display fields.
            CaptureRecord decoded = X25CaptureDecoder.Decode(received.Frame, received.Port,
                received.Direction, received.Seq, received.TimestampMs);
            Assert.That(decoded.PacketType, Is.EqualTo("CallRequest"));
            Assert.That(decoded.Called, Is.EqualTo("23410001"));
            Assert.That(decoded.Layers.Count, Is.EqualTo(1));
        }
    }
}
