//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.X25.Common;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The D.12 segment rule, with every expected figure worked by hand from D.12 and X.25 4.3.5: 64-octet segments,
    /// counted per complete packet sequence, rounded up once per sequence.
    /// </summary>
    [TestFixture]
    public class SegmentCounterTests
    {
        private const int Full = 128;

        [TestCase(1, 1ul, TestName = "OneOctet_IsOneSegment")]
        [TestCase(63, 1ul, TestName = "SixtyThreeOctets_IsOneSegment")]
        [TestCase(64, 1ul, TestName = "ExactlyOneSegment_IsOne")]
        [TestCase(65, 2ul, TestName = "OneOctetOver_IsTwo")]
        [TestCase(128, 2ul, TestName = "AFullPacketAlone_IsTwo")]
        [TestCase(0, 0ul, TestName = "AnEmptyMessage_IsNothing")]
        public void OnePacketMessage(int octets, ulong segments)
        {
            SegmentCounter c = new SegmentCounter();
            c.Add(octets, mBit: false, dBit: false, Full);
            Assert.That(c.Segments, Is.EqualTo(segments));
        }

        /// <summary>
        /// A message is rounded up once, not packet by packet. With a packet size of 100 (not a multiple of 64, so the two
        /// readings give different answers): 100 (A) + 100 (A) + 56 (B) = 256 octets = 4 segments; rounded per packet it
        /// would be 2 + 2 + 1 = 5.
        /// </summary>
        [Test]
        public void AMessageIsRoundedOnce_NotPerPacket()
        {
            SegmentCounter c = new SegmentCounter();
            c.Add(100, mBit: true, dBit: false, fullSize: 100);
            c.Add(100, mBit: true, dBit: false, fullSize: 100);
            Assert.That(c.Segments, Is.EqualTo(0ul), "the message has not ended");
            c.Add(56, mBit: false, dBit: false, fullSize: 100);
            Assert.That(c.Segments, Is.EqualTo(4ul));
        }

        /// <summary>
        /// M set on a packet that is not full makes it category B (4.3.5): the message ends there.
        /// </summary>
        [Test]
        public void MSetOnAShortPacket_EndsTheMessage()
        {
            SegmentCounter c = new SegmentCounter();
            c.Add(10, mBit: true, dBit: false, Full);
            Assert.That(c.Segments, Is.EqualTo(1ul));
            c.Add(10, mBit: false, dBit: false, Full);
            Assert.That(c.Segments, Is.EqualTo(2ul), "two messages of 10 octets, one segment each");
        }

        /// <summary>
        /// D set on a full packet with M set makes it category B (4.3.5): the message ends there.
        /// </summary>
        [Test]
        public void DSetOnAFullPacket_EndsTheMessage()
        {
            SegmentCounter c = new SegmentCounter();
            c.Add(100, mBit: true, dBit: true, fullSize: 100);
            Assert.That(c.Segments, Is.EqualTo(2ul), "ceil(100 / 64)");
            c.Add(28, mBit: false, dBit: false, fullSize: 100);
            Assert.That(c.Segments, Is.EqualTo(3ul), "a new message of 28 octets; joined it would be ceil(128 / 64) = 2");
        }

        /// <summary>
        /// A sequence still open at the end of the call was never completed (4.3.5), so it is not a message and is not
        /// charged; what was counted before it stays.
        /// </summary>
        [Test]
        public void AnOpenMessage_IsDroppedOnClose()
        {
            SegmentCounter c = new SegmentCounter();
            c.Add(10, mBit: false, dBit: false, Full);
            c.Add(Full, mBit: true, dBit: false, Full);
            c.Add(Full, mBit: true, dBit: false, Full);
            Assert.That(c.Segments, Is.EqualTo(1ul), "only the finished 10-octet message");
            c.Close();
            Assert.That(c.Segments, Is.EqualTo(1ul), "the open 256 octets are not charged");
            c.Close();
            Assert.That(c.Segments, Is.EqualTo(1ul));
        }

        /// <summary>
        /// A message of no octets is 0 segments: zero rounded up is zero (D.12 (2)).
        /// </summary>
        [Test]
        public void AnEmptyMessage_IsZeroSegments()
        {
            SegmentCounter c = new SegmentCounter();
            c.Add(0, mBit: false, dBit: false, Full);
            Assert.That(c.Segments, Is.EqualTo(0ul));
        }

        /// <summary>
        /// Another segment size (X.25 7.3.8.3, the Administration's choice): with 100-octet segments, a 257-octet message
        /// (two full 128-octet packets and 1 octet) is ceil(257 / 100) = 3 segments; a 100-octet one exactly 1; 101 is 2.
        /// </summary>
        [Test]
        public void AnotherSegmentSize_RoundsUpToThatSize()
        {
            SegmentCounter c = new SegmentCounter(100);
            c.Add(Full, mBit: true, dBit: false, Full);
            c.Add(Full, mBit: true, dBit: false, Full);
            c.Add(1, mBit: false, dBit: false, Full);
            Assert.That(c.Segments, Is.EqualTo(3ul));
            c.Add(100, mBit: false, dBit: false, Full);
            Assert.That(c.Segments, Is.EqualTo(4ul));
            c.Add(101, mBit: false, dBit: false, Full);
            Assert.That(c.Segments, Is.EqualTo(6ul));
        }

        /// <summary>
        /// The loader takes <c>segment_size</c> from <c>[switch]</c>, 64 when absent, and refuses 0 and anything over 4096
        /// (as x25emu). Writing it back is checked in the save-and-reload test of <c>SwitchCliTests</c>.
        /// </summary>
        [Test]
        public void SegmentSize_IsLoaded_AndChecked()
        {
            Assert.That(Load("[switch]\ndnic = 2342\nsegment_size = 128\n").SegmentSize, Is.EqualTo(128));
            Assert.That(Load("[switch]\ndnic = 2342\n").SegmentSize, Is.EqualTo(64));
            Assert.Throws<System.IO.InvalidDataException>(() => Load("[switch]\ndnic = 2342\nsegment_size = 0\n"));
            Assert.Throws<System.IO.InvalidDataException>(() => Load("[switch]\ndnic = 2342\nsegment_size = 4097\n"));
        }

        private static SwitchConfig Load(string text)
        {
            return SwitchConfigLoader.Load(IniConfig.Parse(text.Split('\n')));
        }
    }
}
