//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace RetroIO.Network.Pcap.Tests
{
    /// <summary>
    /// The receive path of <see cref="PcapEthernetBackend"/> without a real adapter: frames are
    /// fed in where the capture thread would hand them over
    /// (<see cref="PcapEthernetBackend.AcceptCapturedFrame"/>), the guest's address is learned
    /// through <see cref="PcapEthernetBackend.Send"/> exactly as at run time, and
    /// <see cref="PcapEthernetBackend.Poll"/> hands them to a listener. Opening a real adapter
    /// stays a hand test (decided 2026-11-12).
    /// </summary>
    [TestFixture]
    public class PcapReceivePathTests
    {
        /// <summary>
        /// The guest card's address.
        /// </summary>
        private static readonly byte[] GuestMac = { 0x08, 0x00, 0x20, 0x01, 0x02, 0x03 };

        /// <summary>
        /// Another machine on the LAN.
        /// </summary>
        private static readonly byte[] OtherMac = { 0x00, 0x1B, 0x21, 0x44, 0x55, 0x66 };

        /// <summary>
        /// A third machine on the LAN.
        /// </summary>
        private static readonly byte[] ThirdMac = { 0x00, 0x1B, 0x21, 0x77, 0x88, 0x99 };

        /// <summary>
        /// The broadcast address.
        /// </summary>
        private static readonly byte[] Broadcast = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        /// <summary>
        /// An IPv4 multicast address (224.0.0.251 mapped).
        /// </summary>
        private static readonly byte[] Multicast = { 0x01, 0x00, 0x5E, 0x00, 0x00, 0xFB };

        [Test]
        public void TheGuestsOwnTransmitCapturedBackIsDropped()
        {
            PcapEthernetBackend backend = new PcapEthernetBackend("no-adapter-in-a-unit-test");
            List<byte[]> got = Listen(backend);

            byte[] sent = Frame(Broadcast, GuestMac, 60);
            backend.Send(sent, 0, sent.Length);      // learns the guest's address
            backend.AcceptCapturedFrame(sent);       // pcap captures our own frame too
            backend.Poll();

            Assert.That(got.Count, Is.EqualTo(0), "the guest must not see its own transmit");
            Assert.That(backend.FramesDroppedSelfEcho, Is.EqualTo(1));
        }

        [Test]
        public void UnicastForAnotherMachineIsDropped_ForTheGuestIsDelivered()
        {
            PcapEthernetBackend backend = new PcapEthernetBackend("no-adapter-in-a-unit-test");
            List<byte[]> got = Listen(backend);
            LearnGuest(backend);

            backend.AcceptCapturedFrame(Frame(ThirdMac, OtherMac, 60));
            backend.AcceptCapturedFrame(Frame(GuestMac, OtherMac, 61));
            backend.Poll();

            Assert.That(got.Count, Is.EqualTo(1));
            Assert.That(got[0].Length, Is.EqualTo(61));
            Assert.That(backend.FramesDroppedNotForGuest, Is.EqualTo(1));
        }

        [Test]
        public void BroadcastAndMulticastAreDelivered()
        {
            PcapEthernetBackend backend = new PcapEthernetBackend("no-adapter-in-a-unit-test");
            List<byte[]> got = Listen(backend);
            LearnGuest(backend);

            backend.AcceptCapturedFrame(Frame(Broadcast, OtherMac, 60));
            backend.AcceptCapturedFrame(Frame(Multicast, OtherMac, 60));
            backend.Poll();

            Assert.That(got.Count, Is.EqualTo(2));
        }

        [Test]
        public void BeforeTheGuestHasSentAnything_OnlyBroadcastAndMulticastGetThrough()
        {
            PcapEthernetBackend backend = new PcapEthernetBackend("no-adapter-in-a-unit-test");
            List<byte[]> got = Listen(backend);

            backend.AcceptCapturedFrame(Frame(GuestMac, OtherMac, 60));
            backend.AcceptCapturedFrame(Frame(ThirdMac, OtherMac, 60));
            backend.AcceptCapturedFrame(Frame(Broadcast, OtherMac, 60));
            backend.Poll();

            Assert.That(got.Count, Is.EqualTo(1));
            Assert.That(got[0][0], Is.EqualTo(0xFF));
            Assert.That(backend.FramesDroppedNotForGuest, Is.EqualTo(2));
        }

        [Test]
        public void ACapturedHostFrameWithUnfilledChecksumsIsRepaired()
        {
            PcapEthernetBackend backend = new PcapEthernetBackend("no-adapter-in-a-unit-test");
            List<byte[]> got = Listen(backend);
            LearnGuest(backend);

            // The frame measured 2026-08-19: the host's own TCP segment, IPv4 checksum 0000
            // because the card fills it in only after the capture point. Readdressed to the guest.
            byte[] captured = new byte[]
            {
                0x08,0x00,0x20,0x01,0x02,0x03, 0x04,0x42,0x1A,0xEC,0xD0,0xF9, 0x08,0x00,
                0x45,0x00,0x00,0x28,0x32,0x0C,0x40,0x00,0x80,0x06,0x00,0x00,
                0xC0,0xA8,0x01,0xB4, 0xA0,0x4F,0x68,0x0A,
                0x5E,0xCF,0x01,0xBB,0xBD,0xFE,0xBE,0x11,0xD5,0xAF,0x07,0xAC,
                0x50,0x10,0x00,0xFF,0x00,0x00,0x00,0x00,
            };
            backend.AcceptCapturedFrame(captured);
            backend.Poll();

            Assert.That(got.Count, Is.EqualTo(1));
            Assert.That(Ones16(got[0], 14, 20), Is.EqualTo(0xFFFF), "the IPv4 header must verify");
            Assert.That(backend.FramesRepaired, Is.EqualTo(1));
        }

        [Test]
        public void PollHandsOverAtMostMaxFramesPerPoll()
        {
            PcapEthernetBackend backend = new PcapEthernetBackend("no-adapter-in-a-unit-test");
            List<byte[]> got = Listen(backend);
            int total = PcapEthernetBackend.MaxFramesPerPoll + 10;
            for (int i = 0; i < total; i++)
            {
                backend.AcceptCapturedFrame(Frame(Broadcast, OtherMac, 60));
            }

            backend.Poll();
            Assert.That(got.Count, Is.EqualTo(PcapEthernetBackend.MaxFramesPerPoll));

            backend.Poll();
            Assert.That(got.Count, Is.EqualTo(total), "the rest waits for the next poll");
            Assert.That(backend.FramesDelivered, Is.EqualTo(total));
        }

        [Test]
        public void TheReceiveQueueIsBoundedAndCountsWhatItDrops()
        {
            PcapEthernetBackend backend = new PcapEthernetBackend("no-adapter-in-a-unit-test");
            List<byte[]> got = Listen(backend);
            int extra = 7;
            for (int i = 0; i < PcapEthernetBackend.ReceiveQueueFrames + extra; i++)
            {
                backend.AcceptCapturedFrame(Frame(Broadcast, OtherMac, 60));
            }

            Assert.That(backend.FramesDroppedQueueFull, Is.EqualTo(extra));

            for (int i = 0; i < 20; i++)
            {
                backend.Poll();
            }
            Assert.That(got.Count, Is.EqualTo(PcapEthernetBackend.ReceiveQueueFrames));
        }

        [Test]
        public void TooLongAndTooShortFramesAreDroppedAndCounted()
        {
            PcapEthernetBackend backend = new PcapEthernetBackend("no-adapter-in-a-unit-test");
            List<byte[]> got = Listen(backend);

            backend.AcceptCapturedFrame(Frame(Broadcast, OtherMac, PcapEthernetBackend.MaxFrameBytes + 1));
            backend.AcceptCapturedFrame(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3 });
            backend.AcceptCapturedFrame(Frame(Broadcast, OtherMac, PcapEthernetBackend.MaxFrameBytes));
            backend.Poll();

            Assert.That(got.Count, Is.EqualTo(1));
            Assert.That(got[0].Length, Is.EqualTo(PcapEthernetBackend.MaxFrameBytes));
            Assert.That(backend.FramesDroppedTooLong, Is.EqualTo(1));
            Assert.That(backend.FramesDroppedTooShort, Is.EqualTo(1));
        }

        [Test]
        public void StatusShowsTheLearnedAddressAndTheDropCounts()
        {
            PcapEthernetBackend backend = new PcapEthernetBackend("no-adapter-in-a-unit-test");
            LearnGuest(backend);
            backend.AcceptCapturedFrame(Frame(ThirdMac, OtherMac, 60));

            string all = string.Join("\n", backend.DescribeStatus());

            Assert.That(all, Does.Contain("08:00:20:01:02:03"));
            Assert.That(all, Does.Contain("for other machines 1"));
        }

        // --- helpers ---------------------------------------------------------

        /// <summary>
        /// The guest sends one frame, as its card would, so the backend learns its address.
        /// </summary>
        private static void LearnGuest(PcapEthernetBackend backend)
        {
            byte[] arp = Frame(Broadcast, GuestMac, 60);
            backend.Send(arp, 0, arp.Length);
        }

        /// <summary>
        /// Collects a copy of every frame the backend hands over.
        /// </summary>
        private static List<byte[]> Listen(PcapEthernetBackend backend)
        {
            List<byte[]> got = new List<byte[]>();
            backend.OnFrameReceived += (frame, offset, length) =>
            {
                byte[] copy = new byte[length];
                Array.Copy(frame, offset, copy, 0, length);
                got.Add(copy);
            };
            return got;
        }

        /// <summary>
        /// A frame with the given addresses, EtherType 0x9000 (loopback test) and a counting payload.
        /// </summary>
        private static byte[] Frame(byte[] destination, byte[] source, int length)
        {
            byte[] f = new byte[length];
            Array.Copy(destination, 0, f, 0, 6);
            Array.Copy(source, 0, f, 6, 6);
            f[12] = 0x90;
            f[13] = 0x00;
            for (int i = 14; i < length; i++)
            {
                f[i] = (byte)i;
            }
            return f;
        }

        /// <summary>
        /// Folded one's-complement sum of a range; 0xFFFF means it verifies.
        /// </summary>
        private static int Ones16(byte[] data, int offset, int length)
        {
            uint sum = 0;
            for (int i = 0; i + 1 < length; i += 2)
            {
                sum += (uint)((data[offset + i] << 8) | data[offset + i + 1]);
            }
            while ((sum >> 16) != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }
            return (int)sum;
        }
    }
}
