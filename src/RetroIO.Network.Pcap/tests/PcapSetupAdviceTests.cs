//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using NUnit.Framework;

namespace RetroIO.Network.Pcap.Tests
{
    /// <summary>
    /// The per-system setup text, and that a failed start carries it. The old text said
    /// "Is Npcap installed and the host running as Administrator?" on every system.
    /// </summary>
    [TestFixture]
    public class PcapSetupAdviceTests
    {
        [Test]
        public void EachSystemGetsItsOwnSteps()
        {
            Assert.That(PcapSetupAdvice.For(PcapHostSystem.Windows), Does.Contain("Npcap"));
            Assert.That(PcapSetupAdvice.For(PcapHostSystem.Linux), Does.Contain("setcap cap_net_raw"));
            Assert.That(PcapSetupAdvice.For(PcapHostSystem.Linux), Does.Not.Contain("Npcap"));
            Assert.That(PcapSetupAdvice.For(PcapHostSystem.MacOS), Does.Contain("ChmodBPF"));
            Assert.That(PcapSetupAdvice.For(PcapHostSystem.FreeBsd), Does.Contain("devfs"));
        }

        [Test]
        public void EverySystemIsToldWhichAdapterIsTheDefault_AndHowToNameAnother()
        {
            PcapHostSystem[] systems = { PcapHostSystem.Windows, PcapHostSystem.Linux, PcapHostSystem.MacOS, PcapHostSystem.FreeBsd, PcapHostSystem.Other };
            for (int i = 0; i < systems.Length; i++)
            {
                string text = PcapSetupAdvice.For(systems[i]);
                Assert.That(text, Does.Contain("ND-Loopback"), systems[i].ToString());
                Assert.That(text, Does.Contain("select:"), systems[i].ToString());
            }
        }

        [Test]
        public void EverySystemIsToldWhatPcapCannotDo()
        {
            PcapHostSystem[] systems = (PcapHostSystem[])Enum.GetValues(typeof(PcapHostSystem));
            for (int i = 0; i < systems.Length; i++)
            {
                string text = PcapSetupAdvice.For(systems[i]);
                Assert.That(text, Does.Contain("Wi-Fi"), systems[i].ToString());
                Assert.That(text, Does.Contain("cannot reach the guest through the same adapter"), systems[i].ToString());
            }
        }

        [Test]
        public void AFailedStartSaysWhatThisSystemNeeds()
        {
            // No adapter has this name, so Start fails whether or not a pcap driver is
            // installed - without opening any adapter. Either way the message must carry the
            // steps for the system the test runs on.
            PcapEthernetBackend backend = new PcapEthernetBackend("no-such-adapter-7f3e9b2c");

            InvalidOperationException? ex = Assert.Throws<InvalidOperationException>(() => backend.Start());

            Assert.That(ex!.Message, Does.Contain(PcapSetupAdvice.ForThisSystem()));
            Assert.That(backend.IsRunning, Is.False);
        }
    }
}
