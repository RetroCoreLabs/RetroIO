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
    /// The guest address filter: which source addresses it learns, and the kernel capture
    /// filter text it builds (the text pcap compiles; a wrong expression would let the whole
    /// LAN through or block the guest).
    /// </summary>
    [TestFixture]
    public class GuestMacFilterTests
    {
        [Test]
        public void BeforeLearning_TheKernelFilterLetsOnlyBroadcastAndMulticastThrough()
        {
            GuestMacFilter filter = new GuestMacFilter();
            Assert.That(filter.BuildKernelFilter(), Is.EqualTo("ether broadcast or ether multicast"));
        }

        [Test]
        public void AfterLearning_TheKernelFilterNamesTheGuestAsDestinationAndRefusesItAsSource()
        {
            GuestMacFilter filter = new GuestMacFilter();
            filter.Learn(Frame(0x08, 0x00, 0x20, 0x0A, 0x0B, 0x0C));

            Assert.That(filter.BuildKernelFilter(), Is.EqualTo(
                "(ether dst 08:00:20:0a:0b:0c or ether broadcast or ether multicast) and not (ether src 08:00:20:0a:0b:0c)"));
        }

        [Test]
        public void TwoCardsGiveTwoAddresses()
        {
            GuestMacFilter filter = new GuestMacFilter();
            filter.Learn(Frame(0x08, 0x00, 0x20, 0x00, 0x00, 0x01));
            filter.Learn(Frame(0x08, 0x00, 0x20, 0x00, 0x00, 0x02));

            Assert.That(filter.Count, Is.EqualTo(2));
            Assert.That(filter.BuildKernelFilter(), Is.EqualTo(
                "(ether dst 08:00:20:00:00:01 or ether dst 08:00:20:00:00:02 or ether broadcast or ether multicast)"
                + " and not (ether src 08:00:20:00:00:01 or ether src 08:00:20:00:00:02)"));
        }

        [Test]
        public void TheSameAddressTwiceIsLearnedOnce_AndTheVersionMovesOnlyOnANewOne()
        {
            GuestMacFilter filter = new GuestMacFilter();
            int start = filter.Version;

            Assert.That(filter.Learn(Frame(0x08, 0x00, 0x20, 0x00, 0x00, 0x01)), Is.True);
            int afterFirst = filter.Version;
            Assert.That(filter.Learn(Frame(0x08, 0x00, 0x20, 0x00, 0x00, 0x01)), Is.False);

            Assert.That(afterFirst, Is.Not.EqualTo(start));
            Assert.That(filter.Version, Is.EqualTo(afterFirst));
            Assert.That(filter.Count, Is.EqualTo(1));
        }

        [Test]
        public void GroupAndAllZeroSourceAddressesAreNotLearned()
        {
            GuestMacFilter filter = new GuestMacFilter();
            Assert.That(filter.Learn(Frame(0x01, 0x00, 0x5E, 0x00, 0x00, 0x01)), Is.False);
            Assert.That(filter.Learn(Frame(0x00, 0x00, 0x00, 0x00, 0x00, 0x00)), Is.False);
            Assert.That(filter.Count, Is.EqualTo(0));
        }

        [Test]
        public void MoreCardsThanSlotsReplaceTheOldestAddress()
        {
            GuestMacFilter filter = new GuestMacFilter();
            for (int i = 1; i <= GuestMacFilter.MaxGuestMacs + 1; i++)
            {
                filter.Learn(Frame(0x08, 0x00, 0x20, 0x00, 0x00, (byte)i));
            }

            string macs = filter.DescribeMacs();
            Assert.That(filter.Count, Is.EqualTo(GuestMacFilter.MaxGuestMacs));
            Assert.That(macs, Does.Not.Contain("08:00:20:00:00:01"));
            Assert.That(macs, Does.Contain("08:00:20:00:00:05"));
        }

        [Test]
        public void AGivenAddress_PassesUnicastFramesBeforeTheGuestHasSentAnything()
        {
            GuestMacFilter filter = new GuestMacFilter();
            byte[] mac = { 0x08, 0x00, 0x20, 0x0A, 0x0B, 0x0C };
            byte[] reply = ToGuest(mac);
            Assert.That(filter.Classify(reply), Is.EqualTo(CapturedFrameVerdict.NotForGuest), "before");

            Assert.That(filter.Add(mac), Is.True);
            Assert.That(filter.Classify(reply), Is.EqualTo(CapturedFrameVerdict.Deliver), "after");
            Assert.That(filter.BuildKernelFilter(), Does.Contain("ether dst 08:00:20:0a:0b:0c"));

            // Learning the same address from a sent frame adds nothing.
            Assert.That(filter.Learn(Frame(0x08, 0x00, 0x20, 0x0A, 0x0B, 0x0C)), Is.False);
            Assert.That(filter.Count, Is.EqualTo(1));
        }

        [Test]
        public void AChangedAddress_ReplacesTheOldOne()
        {
            GuestMacFilter filter = new GuestMacFilter();
            byte[] old = { 0x08, 0x00, 0x20, 0x00, 0x00, 0x01 };
            byte[] fresh = { 0x08, 0x00, 0x20, 0x00, 0x00, 0x02 };
            filter.Add(old);
            int version = filter.Version;

            Assert.That(filter.Remove(old), Is.True);
            Assert.That(filter.Add(fresh), Is.True);
            Assert.That(filter.Version, Is.Not.EqualTo(version), "the kernel filter is rebuilt");
            Assert.That(filter.Classify(ToGuest(old)), Is.EqualTo(CapturedFrameVerdict.NotForGuest));
            Assert.That(filter.Classify(ToGuest(fresh)), Is.EqualTo(CapturedFrameVerdict.Deliver));
            Assert.That(filter.Remove(old), Is.False, "not held any more");
        }

        [Test]
        public void ARemovedSlot_IsReusedBeforeALiveAddressIsReplaced()
        {
            GuestMacFilter filter = new GuestMacFilter();
            for (byte i = 1; i <= GuestMacFilter.MaxGuestMacs; i++)
            {
                filter.Add(new byte[] { 0x08, 0x00, 0x20, 0x00, 0x00, i });
            }

            filter.Remove(new byte[] { 0x08, 0x00, 0x20, 0x00, 0x00, 3 });
            filter.Add(new byte[] { 0x08, 0x00, 0x20, 0x00, 0x00, 9 });

            Assert.That(filter.Count, Is.EqualTo(GuestMacFilter.MaxGuestMacs));
            Assert.That(filter.Classify(ToGuest(new byte[] { 0x08, 0x00, 0x20, 0x00, 0x00, 1 })), Is.EqualTo(CapturedFrameVerdict.Deliver), "the oldest stays");
        }

        [Test]
        public void GroupAddressesAndBadLengths_AreNotAdded()
        {
            GuestMacFilter filter = new GuestMacFilter();
            Assert.That(filter.Add(new byte[] { 0x01, 0x00, 0x5E, 0x00, 0x00, 0x01 }), Is.False);
            Assert.That(filter.Add(new byte[6]), Is.False);
            Assert.Throws<System.ArgumentException>(() => filter.Add(new byte[5]));
            Assert.Throws<System.ArgumentException>(() => filter.Remove(new byte[7]));
        }

        [Test]
        public void TheBackend_TakesAGivenAddressIntoItsKernelFilter()
        {
            PcapEthernetBackend backend = new PcapEthernetBackend();
            RetroIO.Network.Ethernet.IEthernetGuestAddresses addresses = backend;
            Assert.That(addresses.AddGuestAddress(new byte[] { 0x08, 0x00, 0x20, 0x0A, 0x0B, 0x0C }), Is.True);
            Assert.That(backend.KernelFilter, Does.Contain("ether dst 08:00:20:0a:0b:0c"));
            Assert.That(addresses.RemoveGuestAddress(new byte[] { 0x08, 0x00, 0x20, 0x0A, 0x0B, 0x0C }), Is.True);
            Assert.That(backend.KernelFilter, Is.EqualTo("ether broadcast or ether multicast"));
        }

        /// <summary>
        /// A unicast frame from another machine to the given address.
        /// </summary>
        private static byte[] ToGuest(byte[] mac)
        {
            return new byte[]
            {
                mac[0], mac[1], mac[2], mac[3], mac[4], mac[5],
                0x00, 0x15, 0x5D, 0x01, 0x02, 0x03,
                0x08, 0x00,
            };
        }

        /// <summary>
        /// A broadcast frame whose source is the given address.
        /// </summary>
        private static byte[] Frame(byte s0, byte s1, byte s2, byte s3, byte s4, byte s5)
        {
            return new byte[]
            {
                0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
                s0, s1, s2, s3, s4, s5,
                0x08, 0x06,
            };
        }
    }
}
