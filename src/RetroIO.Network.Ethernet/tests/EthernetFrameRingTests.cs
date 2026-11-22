//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using NUnit.Framework;

namespace RetroIO.Network.Ethernet.Tests
{
    /// <summary>
    /// The bounded receive queue: frames come out in order and unchanged, a full queue refuses
    /// and counts, and slots are reused (no buffer per frame).
    /// </summary>
    [TestFixture]
    public class EthernetFrameRingTests
    {
        [Test]
        public void FramesComeOutInOrderWithTheirLengths()
        {
            EthernetFrameRing ring = new EthernetFrameRing(4, 64);
            Write(ring, 0x11, 20);
            Write(ring, 0x22, 30);

            Assert.That(ring.Count, Is.EqualTo(2));
            Assert.That(ring.TryPeek(out byte[]? first, out int firstLength), Is.True);
            Assert.That(firstLength, Is.EqualTo(20));
            Assert.That(first![0], Is.EqualTo(0x11));
            ring.Release();

            Assert.That(ring.TryPeek(out byte[]? second, out int secondLength), Is.True);
            Assert.That(secondLength, Is.EqualTo(30));
            Assert.That(second![29], Is.EqualTo(0x22));
            ring.Release();

            Assert.That(ring.TryPeek(out _, out _), Is.False);
        }

        [Test]
        public void AFullQueueRefusesAndCountsEveryDrop()
        {
            EthernetFrameRing ring = new EthernetFrameRing(4, 64);
            for (int i = 0; i < 4; i++)
            {
                Write(ring, (byte)i, 16);
            }

            Assert.That(ring.TryReserve(16, out Span<byte> slot), Is.False);
            Assert.That(slot.Length, Is.EqualTo(0));
            Assert.That(ring.TryReserve(16, out _), Is.False);
            Assert.That(ring.FramesDroppedFull, Is.EqualTo(2));
            Assert.That(ring.Count, Is.EqualTo(4));

            // Reading one frees exactly one slot.
            ring.TryPeek(out _, out _);
            ring.Release();
            Assert.That(ring.TryReserve(16, out _), Is.True);
        }

        [Test]
        public void SlotsAreReusedNotAllocatedPerFrame()
        {
            EthernetFrameRing ring = new EthernetFrameRing(2, 64);
            Write(ring, 0x01, 10);
            ring.TryPeek(out byte[]? a, out _);
            ring.Release();
            Write(ring, 0x02, 10);
            ring.TryPeek(out _, out _);
            ring.Release();
            Write(ring, 0x03, 10);
            ring.TryPeek(out byte[]? c, out _);

            Assert.That(c, Is.SameAs(a), "the third frame must land in the first slot again");
        }

        [Test]
        public void TooLongOrEmptyIsRefusedWithoutCountingAsFull()
        {
            EthernetFrameRing ring = new EthernetFrameRing(2, 64);
            Assert.That(ring.TryReserve(65, out _), Is.False);
            Assert.That(ring.TryReserve(0, out _), Is.False);
            Assert.That(ring.FramesDroppedFull, Is.EqualTo(0));
        }

        [Test]
        public void SlotCountMustBeAPowerOfTwo()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new EthernetFrameRing(3, 64));
            Assert.Throws<ArgumentOutOfRangeException>(() => new EthernetFrameRing(1, 64));
        }

        /// <summary>
        /// Writes one frame filled with <paramref name="fill"/>.
        /// </summary>
        private static void Write(EthernetFrameRing ring, byte fill, int length)
        {
            Assert.That(ring.TryReserve(length, out Span<byte> slot), Is.True);
            slot.Fill(fill);
            ring.Commit();
        }
    }
}
