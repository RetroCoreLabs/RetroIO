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
    /// <see cref="PcapAdapterChoice"/> on made-up adapter lists shaped like a Windows host's
    /// (the pcap names are Npcap's <c>\Device\NPF_{GUID}</c> form).
    /// </summary>
    [TestFixture]
    public sealed class PcapAdapterChoiceTests
    {
        private static PcapAdapter[] Host()
        {
            return new PcapAdapter[]
            {
                new PcapAdapter("\\Device\\NPF_{E5360122-3474-432B-B03A-D427D99415A2}", "VirtualBox Host-Only Ethernet Adapter", "Ethernet 2", new[] { "192.168.56.1" }),
                new PcapAdapter("\\Device\\NPF_{286B7E72-117D-4187-9C6D-C05A5240173B}", "Hyper-V Virtual Ethernet Adapter #4", "vEthernet (External)", new[] { "192.168.1.180" }),
                new PcapAdapter("\\Device\\NPF_{77EA1A90-3A90-43EA-9DE2-CCCE67E9AA49}", "Microsoft KM-TEST Loopback Adapter", "ND-Loopback", new[] { "192.168.199.1" }),
            };
        }

        [Test]
        public void NoSelector_TakesNdLoopback_WhenItExists()
        {
            int i = PcapAdapterChoice.Choose(Host(), null, out string reason);
            Assert.That(i, Is.EqualTo(2));
            Assert.That(reason, Does.Contain("ND-Loopback"));
            Assert.That(reason, Does.Contain("default"));
        }

        [Test]
        public void NoSelector_TakesTheFirst_WhenThereIsNoNdLoopback()
        {
            PcapAdapter[] host = Host();
            PcapAdapter[] without = { host[0], host[1] };
            int i = PcapAdapterChoice.Choose(without, "", out string reason);
            Assert.That(i, Is.EqualTo(0));
            Assert.That(reason, Does.Contain("first"));
        }

        [Test]
        public void ConnectionName_ChoosesTheAdapter()
        {
            Assert.That(PcapAdapterChoice.Choose(Host(), "vEthernet (External)", out string reason), Is.EqualTo(1));
            Assert.That(reason, Does.Contain("connection name"));
            Assert.That(PcapAdapterChoice.Choose(Host(), "nd-loopback", out _), Is.EqualTo(2));
            Assert.That(PcapAdapterChoice.Choose(Host(), "ND-Loopback: Microsoft KM-TEST Loopback Adapter  [192.168.199.1]", out _), Is.EqualTo(2), "a whole label from the list");
        }

        [Test]
        public void IpPrefix_ChoosesTheAdapter_OnWholeNumbersOnly()
        {
            Assert.That(PcapAdapterChoice.Choose(Host(), "select:192.168.1", out string reason), Is.EqualTo(1), reason);
            Assert.That(PcapAdapterChoice.Choose(Host(), "select:192.168.199", out _), Is.EqualTo(2));
            Assert.That(PcapAdapterChoice.Choose(Host(), "select:192.168.", out _), Is.EqualTo(0));
            Assert.That(PcapAdapterChoice.Choose(Host(), "select:10.0", out string none), Is.EqualTo(-1));
            Assert.That(none, Does.Contain("10.0"));
        }

        [Test]
        public void OlderSelectors_StillWork()
        {
            Assert.That(PcapAdapterChoice.Choose(Host(), "192.168.56.1", out _), Is.EqualTo(0), "exact address");
            Assert.That(PcapAdapterChoice.Choose(Host(), "KM-TEST", out _), Is.EqualTo(2), "part of the description");
            Assert.That(PcapAdapterChoice.Choose(Host(), "\\Device\\NPF_{286B7E72-117D-4187-9C6D-C05A5240173B}", out _), Is.EqualTo(1), "pcap name");
            Assert.That(PcapAdapterChoice.Choose(Host(), "Hyper-V Virtual Ethernet Adapter #4 [192.168.1.180]", out _), Is.EqualTo(1), "dropdown label");
        }

        [Test]
        public void UnknownName_ChoosesNothing_AndSaysSo()
        {
            Assert.That(PcapAdapterChoice.Choose(Host(), "no-such-adapter", out string reason), Is.EqualTo(-1));
            Assert.That(reason, Does.Contain("no-such-adapter"));
        }

        [Test]
        public void ConnectionNameFor_FindsTheWindowsNameByTheGuid()
        {
            string[] ids = { "{E5360122-3474-432B-B03A-D427D99415A2}", "{77EA1A90-3A90-43EA-9DE2-CCCE67E9AA49}" };
            string[] names = { "Ethernet 2", "ND-Loopback" };
            Assert.That(PcapAdapterChoice.ConnectionNameFor("\\Device\\NPF_{77ea1a90-3a90-43ea-9de2-ccce67e9aa49}", ids, names), Is.EqualTo("ND-Loopback"));
            Assert.That(PcapAdapterChoice.ConnectionNameFor("\\Device\\NPF_{00000000-0000-0000-0000-000000000000}", ids, names), Is.Null);
        }

        [Test]
        public void ConnectionNameFor_UsesTheInterfaceName_OffWindows()
        {
            string[] ids = { "eth0", "lo" };
            string[] names = { "eth0", "lo" };
            Assert.That(PcapAdapterChoice.ConnectionNameFor("eth0", ids, names), Is.EqualTo("eth0"));
        }
    }
}
