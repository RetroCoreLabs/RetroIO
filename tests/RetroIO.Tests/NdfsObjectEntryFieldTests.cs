//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Storage.Ndfs.Elements;

namespace RetroIO.Tests
{
    /// <summary>
    /// Covers the object-entry fields corrected on 2026-07-30: the header word, the reserving user
    /// at bytes 32-33, and the object-index word at bytes 34-35.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The layout comes from ND's own documentation - ND-860228-2 Monitor Calls appendix C gives a
    /// byte-indexed table, ND-30.003.007 System Supervisor appendix F.6 repeats it word-indexed, and
    /// an annotated <c>@DUMP-OBJECT-ENTRY</c> listing in that manual labels all 32 words. It is
    /// cross-checked against bytes recorded off a live ND-100.
    /// </para>
    /// <para>
    /// These tests use a synthetic 64-byte entry rather than a disk image, so they pin the byte
    /// positions directly and fail loudly if an offset moves.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class NdfsObjectEntryFieldTests
    {
        /// <summary>
        /// Builds a 64-byte entry with a distinct value in each byte we care about.
        /// </summary>
        /// <returns>
        /// The raw entry bytes.
        /// </returns>
        private static byte[] BuildEntry()
        {
            byte[] raw = new byte[64];

            // Header word: used (bit 15) plus file-modified (bit 12), as a real entry carries.
            raw[0] = 0x90;
            raw[1] = 0x00;

            // Name and type, apostrophe terminated as SINTRAN writes them.
            byte[] name = System.Text.Encoding.ASCII.GetBytes("TESTFILE'");
            for (int i = 0; i < name.Length; i++) { raw[2 + i] = name[i]; }
            byte[] type = System.Text.Encoding.ASCII.GetBytes("DATA");
            for (int i = 0; i < type.Length; i++) { raw[18 + i] = type[i]; }

            raw[32] = 0x07;   // reserving user's main-directory index
            raw[33] = 0x2A;   // reserving user's user index
            raw[34] = 0x03;   // owning user index
            raw[35] = 0x11;   // object slot within the block

            return raw;
        }

        /// <summary>
        /// The reserving user is read from bytes 32 and 33, not from the owner's byte.
        /// </summary>
        /// <remarks>
        /// Before the correction this read byte 34, so it always equalled <c>UserIndex</c> and a
        /// comment in the parser asserted that no reserving-user field existed at all. The distinct
        /// values here would both have come back as 3.
        /// </remarks>
        [Test]
        public void ReservingUser_IsReadFromBytes32And33_NotTheOwnerByte()
        {
            ObjectEntry? entry = ObjectEntry.FromBytes(BuildEntry(), 0);

            Assert.That(entry, Is.Not.Null);
            Assert.That(entry!.ReservingUserDirectoryIndex, Is.EqualTo(7u), "byte 32, directory index");
            Assert.That(entry.UserIndexOfReservingUser, Is.EqualTo(0x2Au), "byte 33, user index");

            // The owner is a different field with a different value; the two must not be confused.
            Assert.That(entry.UserIndex, Is.EqualTo(3), "byte 34, owning user");
            Assert.That(entry.UserIndexOfReservingUser, Is.Not.EqualTo((uint)entry.UserIndex));
        }

        /// <summary>
        /// The header word keeps both of its flags through a round trip.
        /// </summary>
        /// <remarks>
        /// <c>ToBytes</c> used to write a hardcoded <c>0x80</c>, which stripped bit 12 - the
        /// file-modified flag - from every entry it touched.
        /// </remarks>
        [Test]
        public void HeaderWord_KeepsBothFlagsThroughARoundTrip()
        {
            byte[] raw = BuildEntry();
            ObjectEntry entry = ObjectEntry.FromBytes(raw, 0)!;

            Assert.That(entry.Header, Is.EqualTo(0x9000u));

            byte[] written = new byte[64];
            entry.ToBytes(written, 0);

            Assert.That(written[0], Is.EqualTo(0x90), "used plus file-modified must survive");
            Assert.That(written[1], Is.EqualTo(0x00));
        }

        /// <summary>
        /// Every byte of a parsed entry survives being written back out.
        /// </summary>
        /// <remarks>
        /// This is the check that matters for a server answering out of a pack: the 64-byte record is
        /// what SINTRAN's file-access server puts on the network unchanged, so any byte altered here
        /// is altered for every remote client too.
        /// </remarks>
        [Test]
        public void WholeEntry_SurvivesAReadWriteRoundTrip()
        {
            byte[] raw = BuildEntry();
            ObjectEntry entry = ObjectEntry.FromBytes(raw, 0)!;

            byte[] written = new byte[64];
            entry.ToBytes(written, 0);

            for (int i = 0; i < 64; i++)
            {
                Assert.That(written[i], Is.EqualTo(raw[i]), "byte " + i + " changed");
            }
        }

        /// <summary>
        /// An entry built from scratch writes the reserving user and the object slot.
        /// </summary>
        /// <remarks>
        /// Bytes 33 and 35 were never written at all before 2026-07-30, so a newly created entry went
        /// to disk with a zero reserving user and a zero slot. A disk-read entry was spared, because
        /// the retained raw bytes are copied in first - which is why nothing noticed.
        /// </remarks>
        [Test]
        public void NewEntry_WritesTheReservingUserAndTheObjectSlot()
        {
            ObjectEntry entry = new ObjectEntry
            {
                Header = ObjectEntry.HeaderEntryUsed,
                ObjectName = "NEWFILE",
                Type = "DATA",
                UserIndex = 3,
                ObjectIndex = (3 << 8) | 0x11,
                UserIndexOfReservingUser = 0x2A,
            };

            byte[] written = new byte[64];
            entry.ToBytes(written, 0);

            Assert.That(written[33], Is.EqualTo(0x2A), "byte 33, reserving user");
            Assert.That(written[34], Is.EqualTo(3), "byte 34, owning user");
            Assert.That(written[35], Is.EqualTo(0x11), "byte 35, object slot");
        }

        /// <summary>
        /// A slot whose used bit is clear is reported as free rather than parsed.
        /// </summary>
        [Test]
        public void EntryWithTheUsedBitClear_IsTreatedAsAFreeSlot()
        {
            byte[] raw = BuildEntry();
            raw[0] = 0x00;

            Assert.That(ObjectEntry.FromBytes(raw, 0), Is.Null);
        }
    }
}
