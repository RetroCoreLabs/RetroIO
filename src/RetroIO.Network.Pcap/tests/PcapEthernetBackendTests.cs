//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;

namespace RetroIO.Network.Pcap.Tests
{
    /// <summary>
    /// Smoke tests for <see cref="PcapEthernetBackend"/> that DO NOT touch the native
    /// pcap layer (opening an adapter needs a host driver + elevated privileges and
    /// cannot run in CI). They cover the safe surface: the not-started state and that
    /// Send / Poll / Stop are no-ops before <c>Start</c>.
    /// </summary>
    [TestFixture]
    public class PcapEthernetBackendTests
    {
        [Test]
        public void NotStarted_IsNotRunning()
        {
            PcapEthernetBackend backend = new PcapEthernetBackend();
            Assert.That(backend.IsRunning, Is.False);
        }

        [Test]
        public void Send_BeforeStart_IsNoOp()
        {
            PcapEthernetBackend backend = new PcapEthernetBackend("no-such-adapter");
            byte[] frame = { 0x01, 0x02, 0x03, 0x04 };
            Assert.DoesNotThrow(() => backend.Send(frame, 0, frame.Length));
        }

        [Test]
        public void Poll_BeforeStart_RaisesNothing()
        {
            PcapEthernetBackend backend = new PcapEthernetBackend();
            int received = 0;
            backend.OnFrameReceived += (f, o, l) => received++;

            backend.Poll();

            Assert.That(received, Is.EqualTo(0));
        }

        [Test]
        public void Stop_BeforeStart_IsNoOp()
        {
            PcapEthernetBackend backend = new PcapEthernetBackend();
            Assert.DoesNotThrow(() => backend.Stop());
            Assert.That(backend.IsRunning, Is.False);
        }

        [Test]
        public void CreateFromSpec_ResolvesPcapSpecs_ElseNull()
        {
            // Construction does not open the adapter, so this stays off the native path.
            Assert.That(PcapEthernetBackend.CreateFromSpec("pcap"), Is.InstanceOf<PcapEthernetBackend>());
            Assert.That(PcapEthernetBackend.CreateFromSpec("pcap:Ethernet"), Is.InstanceOf<PcapEthernetBackend>());
            Assert.That(PcapEthernetBackend.CreateFromSpec("PCAP"), Is.InstanceOf<PcapEthernetBackend>());
            Assert.That(PcapEthernetBackend.CreateFromSpec("none"), Is.Null);
            Assert.That(PcapEthernetBackend.CreateFromSpec("slirp"), Is.Null);
            Assert.That(PcapEthernetBackend.CreateFromSpec(""), Is.Null);
        }
    }
}
