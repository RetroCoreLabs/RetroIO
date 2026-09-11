//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using NUnit.Framework;
using RetroIO.Storage.Ndfs.Elements;

namespace RetroIO.Tests
{
    /// <summary>
    /// Tests for the packed 32-bit SINTRAN/ND-100 date codec. The algorithm was verified against
    /// an independent legacy C# reference implementation (Emulated.Lib/DateHelper.cs), an
    /// independent TypeScript port (ndfs-ts/nd-time.ts, executed live during the same investigation),
    /// and a real disk-dump sample — see [[retrocommander-ndfs-metadata-export]] session notes.
    /// </summary>
    [TestFixture]
    public class NdDateTimeTests
    {
        [Test]
        public void FromNdDate_KnownValue_DecodesCorrectly()
        {
            // 0xB9469AE4 / 3108412132: a real value captured from a live-exported NDFS .xat
            // sidecar, cross-verified against ndfs-ts/nd-time.ts (executed live via Node) to
            // decode to exactly 1996-05-03 09:43:36.
            DateTime? result = NdDateTime.FromNdDate(3108412132u);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Value, Is.EqualTo(new DateTime(1996, 5, 3, 9, 43, 36)));
        }

        [Test]
        public void ToNdDate_KnownValue_EncodesCorrectly()
        {
            uint raw = NdDateTime.ToNdDate(new DateTime(1996, 5, 3, 9, 43, 36));

            Assert.That(raw, Is.EqualTo(3108412132u));
        }

        [Test]
        public void FromNdDate_Zero_ReturnsNull()
        {
            Assert.That(NdDateTime.FromNdDate(0), Is.Null);
        }

        [Test]
        public void RoundTrip_EpochBoundaries_Survive()
        {
            var min = new DateTime(1950, 1, 1, 0, 0, 0);
            var max = new DateTime(2013, 12, 31, 23, 59, 59);

            Assert.That(NdDateTime.FromNdDate(NdDateTime.ToNdDate(min)), Is.EqualTo(min));
            Assert.That(NdDateTime.FromNdDate(NdDateTime.ToNdDate(max)), Is.EqualTo(max));
        }

        [Test]
        public void ToNdDate_YearOutOfRange_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => NdDateTime.ToNdDate(new DateTime(1949, 1, 1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => NdDateTime.ToNdDate(new DateTime(2014, 1, 1)));
        }

        [Test]
        public void ToNdDateOrUnset_YearOutOfRange_ReturnsZeroInsteadOfThrowing()
        {
            // The real-world "now" is permanently out of range from 2014 onward (the format's
            // year field is only 6 bits, 1950-2013) — this is the normal case for any code
            // stamping a creation/modified timestamp today, not a rare edge case, so it must
            // never throw. Regression test: NdfsFileSystem.CreateNewFile originally called the
            // throwing ToNdDate directly with DateTime.Now, which broke every NDFS write once run
            // in a year past 2013 (caught by the full RetroIO.Tests suite going red after the
            // date-decode feature first landed).
            Assert.That(NdDateTime.ToNdDateOrUnset(new DateTime(2026, 7, 2)), Is.EqualTo(0u));
            Assert.That(NdDateTime.ToNdDateOrUnset(new DateTime(1949, 1, 1)), Is.EqualTo(0u));
        }

        [Test]
        public void ToNdDateOrUnset_YearInRange_MatchesToNdDate()
        {
            var dt = new DateTime(1996, 5, 3, 9, 43, 36);
            Assert.That(NdDateTime.ToNdDateOrUnset(dt), Is.EqualTo(NdDateTime.ToNdDate(dt)));
        }

        [Test]
        public void FromNdDate_HourField24To31_ClampsToZero()
        {
            // Bits 16-12 (hour) can legally hold 0-31; the source system is known to encode
            // 24-31 in this field. Build a raw word with hour=24 the same way ToNdDate would if
            // it didn't validate hour, to exercise the decode-side clamp directly.
            var withHour0 = new DateTime(1996, 5, 3, 0, 43, 36);
            uint raw = NdDateTime.ToNdDate(withHour0) | (24u << 12);

            DateTime? decoded = NdDateTime.FromNdDate(raw);

            Assert.That(decoded, Is.Not.Null);
            Assert.That(decoded!.Value.Hour, Is.EqualTo(0));
        }

        [Test]
        public void FromNdDate_InvalidMonth_ReturnsNull()
        {
            // Month field (bits 25-22) can hold 0-15; 0 and 13-15 are not valid calendar months.
            uint raw = 0u | (0u << 22); // month = 0, everything else zero -> still fails (year=1950 ok, month=0 invalid)
            raw |= (1u << 17); // day = 1, to isolate the month failure
            Assert.That(NdDateTime.FromNdDate(raw), Is.Null);
        }
    }
}
