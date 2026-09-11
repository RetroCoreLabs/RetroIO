//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Storage.Ndfs.Elements;

namespace RetroIO.Tests
{
    [TestFixture]
    public class NdfsUserEntryRoundTripTest
    {
        [Test]
        public void UserEntry_RoundTrips_DefaultAccessAndFriends()
        {
            var u = new UserEntry
            {
                UserName = "BUILD",
                UserIndex = 9,
                EnterCount = 3,
                Password = 0x1234,
                PagesReserved = 500,
                PagesUsed = 42,
                DirectoryIndex = 0,
                DefaultFileAccess = 0x03FF,
                DateCreated = 0x96269685,
                LastDateEntered = 0x9A08A18B,
            };
            u.Friends[0].Bits = 0x8701;
            u.Friends[1].Bits = 0x8707;

            var bytes = u.ToBytes();

            // Verified on-disk offsets: default access @40, friends @48.
            Assert.That((bytes[40] << 8) | bytes[41], Is.EqualTo(0x03FF));
            Assert.That((bytes[48] << 8) | bytes[49], Is.EqualTo(0x8701));
            Assert.That(bytes[38], Is.EqualTo(0)); // 38-39 unused

            var v = UserEntry.FromBytes(bytes, 0);
            Assert.That(v, Is.Not.Null);
            Assert.That(v!.UserName, Is.EqualTo("BUILD"));
            Assert.That(v.EnterCount, Is.EqualTo((byte)3));
            Assert.That(v.PagesReserved, Is.EqualTo((uint)500));
            Assert.That(v.PagesUsed, Is.EqualTo((uint)42));
            Assert.That(v.DefaultFileAccess, Is.EqualTo((ushort)0x03FF));
            Assert.That(v.DateCreated, Is.EqualTo((uint)0x96269685));
            Assert.That(v.LastDateEntered, Is.EqualTo((uint)0x9A08A18B));
            Assert.That(v.Friends[0].Bits, Is.EqualTo((ushort)0x8701));
            Assert.That(v.Friends[1].Bits, Is.EqualTo((ushort)0x8707));
        }

        /// <summary>
        /// A real pack may set bits in the flags byte beyond the 0x81 pair that FromBytes tests on.
        /// ToBytes used to hardcode 0x81, so any read/modify/write silently destroyed them; the
        /// whole byte must now survive the round trip. Mirrors the ObjectEntry.Header fix.
        /// </summary>
        [Test]
        public void UserEntry_RoundTrips_UnmodelledFlagBitsInByte0()
        {
            // 0xC1 = valid user (0x81) plus an extra bit 6 this class does not model.
            var raw = new byte[64];
            raw[0] = 0xC1;
            raw[2] = (byte)'S';
            raw[3] = (byte)'Y';
            raw[4] = 0x27; // name terminator
            raw[37] = 7;   // user index

            var entry = UserEntry.FromBytes(raw, 0);
            Assert.That(entry, Is.Not.Null);
            Assert.That(entry!.UF, Is.EqualTo((byte)0xC1), "the whole flags byte must be captured");

            var written = entry.ToBytes();
            Assert.That(written[0], Is.EqualTo((byte)0xC1), "bit 6 must not be cleared by a rewrite");
        }

        /// <summary>
        /// An entry built from scratch must still be marked valid, otherwise FromBytes rejects the
        /// slot on the next mount and the user vanishes.
        /// </summary>
        [Test]
        public void UserEntry_CreatedFromScratch_IsMarkedValid()
        {
            var u = new UserEntry { UserName = "FRESH", UserIndex = 3 };

            var bytes = u.ToBytes();

            Assert.That(bytes[0] & UserEntry.UfValidUser, Is.EqualTo(UserEntry.UfValidUser));
            Assert.That(UserEntry.FromBytes(bytes, 0), Is.Not.Null);
        }
    }
}
