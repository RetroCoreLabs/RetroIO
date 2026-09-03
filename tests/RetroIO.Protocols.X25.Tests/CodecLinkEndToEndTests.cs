using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Link.Lapb;
using RetroIO.Protocols.X25.Layers;
using RetroIO.Protocols.X25.Runtime.Layers;
using RetroIO.Link;
using RetroIO.Link.Framing;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// End-to-end tests for a framing codec plus CodecLinkBridge plus LapbInstance over an in-memory loopback.
    /// </summary>
    /// <remarks>
    /// Proves the relocated FCS model works through the real LAPB engine: a SLIP-framed link performs the
    /// SABM/UA handshake and transfers I-frame data both directions, with the CRC-16 FCS appended by the
    /// bridge on transmit and validated/stripped on receive. Delivery is synchronous (the loopback hands a
    /// codec's framed bytes straight to the peer codec's de-framer), so no timers are exercised.
    /// </remarks>
    [TestFixture]
    public class CodecLinkEndToEndTests
    {
        // Delivers a codec's framed output straight into the peer codec's receive path (synchronous wire).
        private sealed class LoopbackTransport : IFrameTransport
        {
            public IFrameCodec? Peer;

            public void Send(ReadOnlySpan<byte> bytes)
            {
                Peer?.ProcessBytes(bytes);
            }
        }

        // Records the framed bytes a codec emits, for inspection/corruption in tests.
        private sealed class CapturingTransport : IFrameTransport
        {
            public byte[] Last = Array.Empty<byte>();

            public void Send(ReadOnlySpan<byte> bytes)
            {
                Last = bytes.ToArray();
            }
        }

        private ILoggerFactory _factory = null!;
        private ILogger _logger = null!;

        [SetUp]
        public void Setup()
        {
            _factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            _logger = _factory.CreateLogger<CodecLinkEndToEndTests>();
        }

        [TearDown]
        public void TearDown()
        {
            _factory?.Dispose();
        }

        [Test]
        public void Slip_LapbHandshakeAndDataTransfer_EndToEnd()
        {
            // Two SLIP endpoints joined by an in-memory loopback.
            LoopbackTransport wireA = new LoopbackTransport();
            LoopbackTransport wireB = new LoopbackTransport();
            SlipFrameCodec codecA = new SlipFrameCodec("A", wireA);
            SlipFrameCodec codecB = new SlipFrameCodec("B", wireB);
            wireA.Peer = codecB; // A's framed bytes arrive at B's de-framer
            wireB.Peer = codecA;

            using LapbInstance lapbA = new LapbInstance(_logger, "A", isPrimary: true);
            using LapbInstance lapbB = new LapbInstance(_logger, "B", isPrimary: false);
            using CodecLinkBridge bridgeA = new CodecLinkBridge("A", codecA, lapbA, _logger);
            using CodecLinkBridge bridgeB = new CodecLinkBridge("B", codecB, lapbB, _logger);

            List<byte[]> receivedByA = new List<byte[]>();
            List<byte[]> receivedByB = new List<byte[]>();
            lapbA.DataReceived += (sender, data) => receivedByA.Add(data);
            lapbB.DataReceived += (sender, data) => receivedByB.Add(data);

            // LAPB link establishment (SABM/UA) carried over SLIP framing with FCS.
            lapbA.Connect();
            Assert.That(lapbA.State, Is.EqualTo(LapbState.DataTransfer), "A should be connected");
            Assert.That(lapbB.State, Is.EqualTo(LapbState.DataTransfer), "B should be connected");

            // A -> B data transfer.
            byte[] message = Encoding.ASCII.GetBytes("HELLO-SLIP");
            lapbA.SendData(message);
            Assert.That(receivedByB.Count, Is.EqualTo(1), "B should receive one I-frame payload");
            Assert.That(receivedByB[0], Is.EqualTo(message));

            // B -> A data transfer; payload deliberately contains SLIP END (0xC0) and ESC (0xDB) bytes
            // to prove they survive framing escapes inside a real LAPB information field.
            byte[] reply = new byte[] { 0xC0, 0xDB, 0x00, 0x7E, 0xAA, 0x55 };
            lapbB.SendData(reply);
            Assert.That(receivedByA.Count, Is.EqualTo(1), "A should receive one I-frame payload");
            Assert.That(receivedByA[0], Is.EqualTo(reply));
        }

        [Test]
        public void Slip_CorruptedFcs_FrameIsDropped()
        {
            // Encode a valid SABM+FCS frame to SLIP wire bytes, then flip a bit inside it.
            CapturingTransport capture = new CapturingTransport();
            SlipFrameCodec encoder = new SlipFrameCodec("enc", capture);
            encoder.SendFrame(BuildFrameWithFcs(new byte[] { 0x01, 0x3F }));
            byte[] framed = capture.Last;
            framed[1] ^= 0x01; // corrupt the address byte (index 1, just after the leading END)

            // Receiver: codec + bridge + LAPB. The bridge must reject the bad FCS before LAPB sees it.
            using LapbInstance lapb = new LapbInstance(_logger, "R", isPrimary: false);
            SlipFrameCodec receiverCodec = new SlipFrameCodec("R", new LoopbackTransport());
            using CodecLinkBridge bridge = new CodecLinkBridge("R", receiverCodec, lapb, _logger);

            int framesToLapb = 0;
            lapb.FrameReceived += (sender, args) => framesToLapb++;

            receiverCodec.ProcessBytes(framed);

            Assert.That(framesToLapb, Is.EqualTo(0), "a frame with a bad FCS must be dropped before LAPB");
            Assert.That(lapb.State, Is.EqualTo(LapbState.Disconnected), "no SABM should have been accepted");
        }

        private static byte[] BuildFrameWithFcs(byte[] data)
        {
            byte[] frame = new byte[data.Length + 2];
            Array.Copy(data, frame, data.Length);
            Fcs16.WriteFcs(data, frame.AsSpan(data.Length));
            return frame;
        }
    }
}
