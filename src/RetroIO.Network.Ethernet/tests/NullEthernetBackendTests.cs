//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;

namespace RetroIO.Network.Ethernet.Tests
{
    /// <summary>
    /// Verifies the default <see cref="NullEthernetBackend"/>: transmits are discarded
    /// by default, the optional loopback mode round-trips a frame through
    /// <see cref="IEthernetBackend.OnFrameReceived"/>, and the running state gates both.
    /// </summary>
    [TestFixture]
    public class NullEthernetBackendTests
    {
        private static byte[] SampleFrame()
        {
            // A minimal Ethernet-ish frame: dest MAC, src MAC, ethertype, payload.
            return new byte[]
            {
                0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
                0x02, 0x00, 0x11, 0x22, 0x33, 0x44,
                0x08, 0x00,
                0xDE, 0xAD, 0xBE, 0xEF,
            };
        }

        [Test]
        public void Default_DropsTransmit_NoReceive()
        {
            NullEthernetBackend backend = new NullEthernetBackend();
            int received = 0;
            backend.OnFrameReceived += (f, o, l) => received++;

            backend.Start();
            byte[] frame = SampleFrame();
            backend.Send(frame, 0, frame.Length);

            Assert.That(received, Is.EqualTo(0), "the default backend must not deliver any frame");
            Assert.That(backend.Loopback, Is.False);
        }

        [Test]
        public void Loopback_EchoesTransmit_BackAsReceive()
        {
            NullEthernetBackend backend = new NullEthernetBackend(loopback: true);

            byte[]? got = null;
            int gotLength = 0;
            backend.OnFrameReceived += (f, o, l) =>
            {
                got = new byte[l];
                System.Array.Copy(f, o, got, 0, l);
                gotLength = l;
            };

            backend.Start();
            byte[] frame = SampleFrame();
            backend.Send(frame, 0, frame.Length);

            Assert.That(got, Is.Not.Null, "loopback must echo the transmit back as a receive");
            Assert.That(gotLength, Is.EqualTo(frame.Length));
            Assert.That(got, Is.EqualTo(frame));
        }

        [Test]
        public void Loopback_HonoursRunningState()
        {
            NullEthernetBackend backend = new NullEthernetBackend(loopback: true);
            int received = 0;
            backend.OnFrameReceived += (f, o, l) => received++;

            byte[] frame = SampleFrame();

            // Not started → nothing echoes.
            backend.Send(frame, 0, frame.Length);
            Assert.That(received, Is.EqualTo(0));
            Assert.That(backend.IsRunning, Is.False);

            backend.Start();
            Assert.That(backend.IsRunning, Is.True);
            backend.Send(frame, 0, frame.Length);
            Assert.That(received, Is.EqualTo(1));

            // Stopped again → no more echoes.
            backend.Stop();
            backend.Send(frame, 0, frame.Length);
            Assert.That(received, Is.EqualTo(1));
        }

        [Test]
        public void Send_IgnoresEmptyOrNull()
        {
            NullEthernetBackend backend = new NullEthernetBackend(loopback: true);
            int received = 0;
            backend.OnFrameReceived += (f, o, l) => received++;
            backend.Start();

            backend.Send(null!, 0, 0);
            backend.Send(SampleFrame(), 0, 0);

            Assert.That(received, Is.EqualTo(0));
        }
    }
}
