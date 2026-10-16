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
    /// Integration test for <see cref="MonitorClient"/> against a real <see cref="MonitorServer"/> over
    /// loopback TCP: the client connects, the server publishes, and the client raises the decoded capture.
    /// </summary>
    [TestFixture]
    public class MonitorClientTests
    {
        [Test]
        public void Client_Connects_And_ReceivesPublishedCapture()
        {
            using MonitorServer server = new MonitorServer("127.0.0.1", 0);
            Assert.That(server.Start(), Is.True);

            using MonitorClient client = new MonitorClient();

            CaptureRecord? received = null;
            ManualResetEventSlim got = new ManualResetEventSlim(false);
            client.CaptureReceived += r =>
            {
                received = r;
                got.Set();
            };

            Assert.That(client.Connect("127.0.0.1", server.ListenPort), Is.True);
            Assert.That(SpinWait.SpinUntil(() => server.ClientCount == 1, 3000), Is.True);

            // The server ships the raw frame under X25MON/1; the client decodes it.
            byte[] packet = X25PacketBuilder.CreateCallRequest(1, "23410001", "23400001", null, null);
            server.Publish(new CaptureRecord
            {
                Seq = 1,
                Port = 0,
                Direction = CaptureDirection.Rx,
                TimestampMs = 100,
                Frame = packet,
                TotalLength = packet.Length
            });

            Assert.That(got.Wait(3000), Is.True, "the client should receive the published capture");
            Assert.That(received!.Frame, Is.EqualTo(packet));

            CaptureRecord decoded = X25CaptureDecoder.Decode(received.Frame, received.Port,
                received.Direction, received.Seq, received.TimestampMs);
            Assert.That(decoded.PacketType, Is.EqualTo("CallRequest"));
            Assert.That(decoded.Called, Is.EqualTo("23410001"));
        }
    }
}
