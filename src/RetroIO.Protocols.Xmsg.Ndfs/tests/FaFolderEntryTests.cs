//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Ndfs;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Ndfs.Tests
{
    /// <summary>
    /// Covers the fields of the 64-byte listing record that the ON-DISK object entry does not model,
    /// and which therefore have to be written by hand after
    /// <c>RetroIO.Storage.Ndfs.Elements.ObjectEntry.ToBytes</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The values asserted here were MEASURED on 2026-08-04 over the 49 directory records in
    /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-list-files.txt</c>, by comparing every
    /// 16-bit field across all of them to see which stay constant and which vary. That is what
    /// separated "structural, every record has it" from "content, differs per file".
    /// </para>
    /// <para>
    /// <b>Why this file exists at all:</b> <c>FaFolderEntry</c> had NO test. Several of the fields
    /// below were silently zero on every record we ever sent, and it took reading a capture to
    /// notice. A unit test cannot prove a real client accepts the record - only the machine can -
    /// but it can stop them regressing back to zero unnoticed.
    /// </para>
    /// <para>
    /// Two of the names here were CORRECTED on 2026-08-05 against
    /// <c>Reference-Manuals\ND-860228-2-EN SINTRAN III Monitor Calls.md</c> appendix C and the
    /// annotated <c>@DUMP-OBJECT-ENTRY</c> in
    /// <c>Operations\SINTRAN\ND-30.003.007 EN SINTRAN III System Supervisor.md</c>. Bytes 22-25 are
    /// the version pointers, not "the walk ordinal", and bytes 60-63 are NOT constant across the
    /// capture - only their low page id is.
    /// </para>
    /// </remarks>
    public sealed class FaFolderEntryTests
    {
        /// <summary>
        /// Builds a record for a plausible served file at a given position in the walk.
        /// </summary>
        /// <param name="ordinal">
        /// The entry's position in the walk.
        /// </param>
        /// <returns>
        /// The 64-byte record.
        /// </returns>
        private static byte[] BuildAt(int ordinal)
        {
            FaFileInfo file = new FaFileInfo(
                (ushort)(ordinal + 1),
                new FaFileName(null, "HELLO", "TXT"),
                19,
                new DateTime(1998, 8, 4, 12, 0, 0, DateTimeKind.Utc));

            return FaFolderEntry.BuildRecord(file, 0, ordinal);
        }

        /// <summary>
        /// Reads a big-endian 16-bit value out of the record.
        /// </summary>
        private static ushort WordAt(byte[] record, int offset)
        {
            return (ushort)((record[offset] << 8) | record[offset + 1]);
        }

        /// <summary>
        /// Bytes 22-25 are the next and previous VERSION pointers, and a single-version file points
        /// both at its own object index.
        /// </summary>
        /// <remarks>
        /// The System Supervisor manual's annotated <c>@DUMP-OBJECT-ENTRY</c> of object entry 025
        /// prints "000025 000025  POINTERS TO NEXT AND PREVIOUS VERSION", and every captured record
        /// repeats its own object index in both words. Before 2026-08-04 these were zero on every
        /// record we sent; they were then filled in under the wrong name ("the walk ordinal"), which
        /// gave the same bytes on a directory with no holes in it.
        /// </remarks>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(41)]
        public void Bytes22To25_AreTheVersionPointersAndPointAtThisEntry(int ordinal)
        {
            byte[] record = BuildAt(ordinal);

            Assert.Equal((ushort)ordinal, WordAt(record, 22));
            Assert.Equal((ushort)ordinal, WordAt(record, 24));

            // Both version pointers must agree with the object index they point at.
            Assert.Equal((ushort)ordinal, WordAt(record, 34));
        }

        /// <summary>
        /// Bytes 26-27 are the file ACCESS word, and must not be zero.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Named in <c>ND-860228-2-EN SINTRAN III Monitor Calls</c> appendix C and printed as
        /// "ACCESS WORD" by <c>@DUMP-OBJECT-ENTRY</c>. Three 5-bit tiers - bits 14-10 public,
        /// 9-5 friend, 4-0 own - with D=0x10, C=0x08, A=0x04, W=0x02, R=0x01 inside a tier.
        /// </para>
        /// <para>
        /// Zero means nobody may read, write or append the file, INCLUDING its owner, which is what
        /// we sent on every record until 2026-08-05. This test exists to stop that returning: the
        /// exact value is a judgement call, an unreadable file is not.
        /// </para>
        /// </remarks>
        [Fact]
        public void Bytes26To27_GrantAccessRatherThanNone()
        {
            byte[] record = BuildAt(0);

            ushort access = WordAt(record, 26);

            Assert.NotEqual(0, access);

            // Own tier, bits 4-0: read, write and append at the very least.
            Assert.Equal(0x07, access & 0x07);

            // The value 40 of the 49 captured records carry for an ordinary user file.
            Assert.Equal(FaFolderEntry.DefaultAccessWord, access);
        }

        /// <summary>
        /// The attribute word and the file pointer must agree about how the file is allocated.
        /// </summary>
        /// <remarks>
        /// Bytes 28-29 say indexed (bit 3); bytes 60-63 carry a 2-bit pointer type over a 30-bit
        /// page id, and indexed is type 01, so the record must read <c>0x4000nnnn</c>. In the
        /// capture the two never disagree. We used to declare indexed and then send a CONTIGUOUS
        /// pointer, <c>0x000078DA</c>.
        /// </remarks>
        [Fact]
        public void TheAttributeWordAndTheFilePointerAgreeOnIndexed()
        {
            byte[] first = BuildAt(0);
            byte[] later = BuildAt(7);

            // Bit 3 of the attribute word: an indexed file.
            Assert.Equal(0x0008, WordAt(first, 28));

            // Pointer type 01 in the top two bits, then the page id every captured record carries.
            Assert.Equal(0x4000, WordAt(first, 60));
            Assert.Equal(0x78DA, WordAt(first, 62));

            // The page id does not vary with the entry - in the capture it is the same on all 49.
            Assert.Equal(WordAt(first, 60), WordAt(later, 60));
            Assert.Equal(WordAt(first, 62), WordAt(later, 62));
        }

        /// <summary>
        /// A present-day file still gets a non-zero creation date.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The packed ND date cannot express any year from 2014 onwards, so every file in a Windows
        /// folder used to encode as zero. Counted over the 49 captured records, the creation date is
        /// non-zero on ALL of them - it is the one date field the wire never leaves blank.
        /// </para>
        /// <para>
        /// <c>2026-08-05</c> folds by one 64-year cycle to <c>1962-08-05</c>: same month, day and
        /// time, year congruent modulo the format's own span.
        /// </para>
        /// </remarks>
        [Fact]
        public void APresentDayFileStillGetsACreationDate()
        {
            DateTime modified = new DateTime(2026, 8, 5, 7, 20, 54, DateTimeKind.Utc);

            FaFileInfo file = new FaFileInfo(
                1,
                new FaFileName(null, "HELLO", "TXT"),
                19,
                modified);

            byte[] record = FaFolderEntry.BuildRecord(file, 0, 0);

            uint created = (uint)((record[40] << 24) | (record[41] << 16) |
                                  (record[42] << 8) | record[43]);

            Assert.NotEqual(0u, created);

            DateTime? decoded = RetroIO.Storage.Ndfs.Elements.NdDateTime.FromNdDate(created);

            Assert.NotNull(decoded);
            Assert.Equal(1962, decoded.Value.Year);
            Assert.Equal(modified.Month, decoded.Value.Month);
            Assert.Equal(modified.Day, decoded.Value.Day);
            Assert.Equal(modified.Hour, decoded.Value.Hour);
            Assert.Equal(modified.Minute, decoded.Value.Minute);
            Assert.Equal(modified.Second, decoded.Value.Second);
        }

        /// <summary>
        /// A date the format CAN hold is passed through untouched.
        /// </summary>
        /// <remarks>
        /// The fold must only engage when it has to - a 1998 file is inside the representable range
        /// and must keep its real year.
        /// </remarks>
        [Fact]
        public void ADateInRangeIsNotFolded()
        {
            DateTime modified = new DateTime(1998, 8, 4, 12, 0, 0, DateTimeKind.Utc);

            FaFileInfo file = new FaFileInfo(
                1,
                new FaFileName(null, "HELLO", "TXT"),
                19,
                modified);

            byte[] record = FaFolderEntry.BuildRecord(file, 0, 0);

            uint created = (uint)((record[40] << 24) | (record[41] << 16) |
                                  (record[42] << 8) | record[43]);

            DateTime? decoded = RetroIO.Storage.Ndfs.Elements.NdDateTime.FromNdDate(created);

            Assert.NotNull(decoded);
            Assert.Equal(1998, decoded.Value.Year);
        }

        /// <summary>
        /// "Last opened for read" stays zero, which is a value the wire really does use.
        /// </summary>
        /// <remarks>
        /// 17 of the 49 captured records carry zero here. A Windows folder has no read time to
        /// report, so zero is honest - unlike the creation date, where zero never occurs.
        /// </remarks>
        [Fact]
        public void LastOpenedForReadStaysZero()
        {
            byte[] record = BuildAt(0);

            uint lastRead = (uint)((record[44] << 24) | (record[45] << 16) |
                                   (record[46] << 8) | record[47]);

            Assert.Equal(0u, lastRead);
        }

        /// <summary>
        /// Packing the access tiers reproduces the exact word the capture carries.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this test exists</b></para>
        /// The access word became an enum composition on 2026-08-05. The assertion that the record
        /// carries <c>FaFolderEntry.DefaultAccessWord</c> is tautological on its own, so this pins
        /// the composition to the literal value measured on the wire instead.
        /// <para><b>The value</b></para>
        /// <c>0x04F7</c> is what 40 of the 49 captured records carry: own read, write, append and D;
        /// friend read, write and append; public read.
        /// </remarks>
        [Fact]
        public void PackingTheAccessTiersReproducesTheCapturedWord()
        {
            ushort packed = FaObjectEntryHeader.PackAccess(
                own: FaAccessRights.Read | FaAccessRights.Write | FaAccessRights.Append | FaAccessRights.Directory,
                friend: FaAccessRights.Read | FaAccessRights.Write | FaAccessRights.Append,
                @public: FaAccessRights.Read);

            Assert.Equal(0x04F7, packed);
            Assert.Equal(0x04F7, FaFolderEntry.DefaultAccessWord);

            // And it must round-trip back to the tiers it was built from.
            Assert.Equal(
                FaAccessRights.Read | FaAccessRights.Write | FaAccessRights.Append | FaAccessRights.Directory,
                FaObjectEntryHeader.UnpackAccess(packed, FaObjectEntryHeader.OwnTierShift));
            Assert.Equal(
                FaAccessRights.Read | FaAccessRights.Write | FaAccessRights.Append,
                FaObjectEntryHeader.UnpackAccess(packed, FaObjectEntryHeader.FriendTierShift));
            Assert.Equal(
                FaAccessRights.Read,
                FaObjectEntryHeader.UnpackAccess(packed, FaObjectEntryHeader.PublicTierShift));
        }

        /// <summary>
        /// The system files in the capture decode to the tiers SINTRAN prints for them.
        /// </summary>
        /// <remarks>
        /// <c>FILE-STATISTICS</c> on <c>SINTRAN:DATA</c> printed "PUBLIC ACCESS : NONE", "FRIEND
        /// ACCESS : NONE" and "OWN ACCESS : READ, WRITE, APPEND". That file's record carries
        /// <c>0x0007</c>, so the tier layout and the three low letters are checkable against ND's own
        /// output rather than against our own encoder.
        /// </remarks>
        [Fact]
        public void TheCapturedSystemFileWordDecodesToWhatFileStatisticsPrinted()
        {
            const ushort SintranDataAccess = 0x0007;

            Assert.Equal(
                FaAccessRights.Read | FaAccessRights.Write | FaAccessRights.Append,
                FaObjectEntryHeader.UnpackAccess(SintranDataAccess, FaObjectEntryHeader.OwnTierShift));
            Assert.Equal(
                FaAccessRights.None,
                FaObjectEntryHeader.UnpackAccess(SintranDataAccess, FaObjectEntryHeader.FriendTierShift));
            Assert.Equal(
                FaAccessRights.None,
                FaObjectEntryHeader.UnpackAccess(SintranDataAccess, FaObjectEntryHeader.PublicTierShift));
        }

        /// <summary>
        /// The header word must agree with the max byte pointer: a real byte count means bit 12.
        /// </summary>
        /// <remarks>
        /// Over all 49 captured records, bit 12 is set on the 40 with a real byte count and clear on
        /// the 9 whose max byte pointer is <c>0xFFFFFFFF</c>; no record mixes them. We used to send
        /// <c>0x8000</c> with a real byte count, a pairing that occurs nowhere in the capture.
        /// </remarks>
        [Fact]
        public void TheHeaderWordAgreesWithTheMaxBytePointer()
        {
            byte[] record = BuildAt(0);

            ushort header = WordAt(record, 0);

            // Entry used, or the record reads as a free slot and the file vanishes.
            Assert.Equal(0x8000, header & 0x8000);

            // And bit 12, because this record carries a real byte count.
            Assert.Equal(0x1000, header & 0x1000);

            Assert.Equal(0x9000, header);

            uint maxBytePointer = (uint)((record[56] << 24) | (record[57] << 16) |
                                         (record[58] << 8) | record[59]);

            Assert.NotEqual(0xFFFFFFFFu, maxBytePointer);
        }

        /// <summary>
        /// The record is exactly 64 bytes, and the fields written by hand do not disturb the name.
        /// </summary>
        /// <remarks>
        /// Bytes 22-25 sit between the name and the type, so a mistake there would corrupt one of
        /// them silently. This pins that they are still where the capture has them: the name from
        /// byte 2, the type from byte 18.
        /// </remarks>
        [Fact]
        public void TheHandWrittenFieldsDoNotDisturbTheNameOrType()
        {
            byte[] record = BuildAt(3);

            Assert.Equal(64, record.Length);

            // "HELLO" then the apostrophe terminator, exactly as a real short name is stored.
            Assert.Equal("HELLO'", System.Text.Encoding.ASCII.GetString(record, 2, 6));

            // "TXT" then its terminator.
            Assert.Equal("TXT'", System.Text.Encoding.ASCII.GetString(record, 18, 4));
        }
    }
}
