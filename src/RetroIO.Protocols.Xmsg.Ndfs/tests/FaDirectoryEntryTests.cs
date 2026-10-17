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
    /// Covers the 42-byte SINTRAN directory entry the listing walk returns for the pack.
    /// </summary>
    /// <remarks>
    /// <para><b>What these assertions are checked against</b></para>
    /// The field layout is quoted from the SINTRAN III Monitor Calls manual appendix C, the
    /// byte-indexed table for MON 244B (GDIEN). The values come from the one such record in
    /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-list-files.txt</c>, which describes
    /// <c>PACK-ONE</c> and which the System Supervisor manual's own
    /// <c>@DUMP-DIRECTORY-ENTRY</c> example reproduces pointer for pointer.
    /// <para><b>What is NOT proven here</b></para>
    /// That a real client accepts a record we built. Only the machine can show that.
    /// </remarks>
    public sealed class FaDirectoryEntryTests
    {
        /// <summary>
        /// Reads a big-endian 16-bit value out of the record.
        /// </summary>
        /// <param name="record">
        /// The record to read.
        /// </param>
        /// <param name="offset">
        /// The byte offset to read at.
        /// </param>
        /// <returns>
        /// The value at <paramref name="offset"/>.
        /// </returns>
        private static ushort WordAt(byte[] record, int offset)
        {
            return (ushort)((record[offset] << 8) | record[offset + 1]);
        }

        /// <summary>
        /// Reads a big-endian 32-bit value out of the record.
        /// </summary>
        /// <param name="record">
        /// The record to read.
        /// </param>
        /// <param name="offset">
        /// The byte offset to read at.
        /// </param>
        /// <returns>
        /// The value at <paramref name="offset"/>.
        /// </returns>
        private static uint DoubleWordAt(byte[] record, int offset)
        {
            return (uint)((record[offset] << 24) | (record[offset + 1] << 16) |
                          (record[offset + 2] << 8) | record[offset + 3]);
        }

        /// <summary>
        /// The record is 42 bytes and its fields sit where the manual puts them.
        /// </summary>
        /// <remarks>
        /// The length is the whole reason this record is distinguishable from a file entry: both are
        /// carried by the same <c>B0</c> tag, and only the length says which kind it is.
        /// </remarks>
        [Fact]
        public void TheRecordMatchesTheManualLayout()
        {
            byte[] record = FaDirectoryEntry.BuildRecord("PACK-ONE");

            Assert.Equal(FaDirectoryEntry.RecordLength, record.Length);
            Assert.Equal(42, record.Length);

            // Bytes 0-1: entered, main directory, default directory, no files open.
            Assert.Equal(0xD000, WordAt(record, 0));
            Assert.Equal(0, WordAt(record, 0) & FaDirectoryEntry.OpenFileCountMask);

            // Bytes 2-3 and 6-7: the unit/device word and the directory lock, both from the capture.
            Assert.Equal(0x0240, WordAt(record, 2));
            Assert.Equal(0x0540, WordAt(record, 6));

            // Bytes 4-5 and 8-9: subunit and name-table index, then the tape/floppy semaphore.
            Assert.Equal(0x0000, WordAt(record, 4));
            Assert.Equal(0x0000, WordAt(record, 8));

            // Bytes 26-41: the four double words of the on-disc half.
            Assert.Equal(0x400048FCu, DoubleWordAt(record, 26));
            Assert.Equal(0x400048FEu, DoubleWordAt(record, 30));
            Assert.Equal(0x00004824u, DoubleWordAt(record, 34));
            Assert.Equal(0x0000347Eu, DoubleWordAt(record, 38));
        }

        /// <summary>
        /// The name sits at byte 10, is upper-cased, and is terminated the SINTRAN way.
        /// </summary>
        /// <remarks>
        /// A name shorter than the 16-byte field is followed by <c>0x27</c> and then zeros, which is
        /// exactly how <c>PACK-ONE</c> appears on the wire.
        /// </remarks>
        [Fact]
        public void TheNameIsWrittenWhereTheManualPutsIt()
        {
            byte[] record = FaDirectoryEntry.BuildRecord("pack-one");

            Assert.Equal(
                "PACK-ONE",
                System.Text.Encoding.ASCII.GetString(record, FaDirectoryEntry.NameOffset, 8));

            Assert.Equal(FaDirectoryEntry.NameTerminator, record[FaDirectoryEntry.NameOffset + 8]);

            // Everything after the terminator, up to the object file pointer, must be zero.
            for (int i = FaDirectoryEntry.NameOffset + 9; i < 26; i++)
            {
                Assert.Equal(0, record[i]);
            }
        }

        /// <summary>
        /// A name that fills the field exactly carries no terminator.
        /// </summary>
        /// <remarks>
        /// The 16-character file names in the object-entry capture run right to the end of their
        /// field with no <c>0x27</c>, so the terminator is a pad, not a delimiter.
        /// </remarks>
        [Fact]
        public void AFullLengthNameIsNotTerminated()
        {
            byte[] record = FaDirectoryEntry.BuildRecord("ABCDEFGHIJKLMNOP");

            Assert.Equal(
                "ABCDEFGHIJKLMNOP",
                System.Text.Encoding.ASCII.GetString(record, FaDirectoryEntry.NameOffset, 16));

            // Byte 26 is the first byte of the object file pointer, not a terminator.
            Assert.Equal(0x40, record[26]);
        }

        /// <summary>
        /// An empty name is refused rather than silently producing a nameless directory.
        /// </summary>
        [Fact]
        public void AnEmptyNameIsRefused()
        {
            Assert.Throws<ArgumentException>(() => FaDirectoryEntry.BuildRecord(string.Empty));
        }

        /// <summary>
        /// The state word decodes to the three bits the capture sets.
        /// </summary>
        [Fact]
        public void TheStateWordDecodesToEnteredMainAndDefault()
        {
            byte[] record = FaDirectoryEntry.BuildRecord("PACK-ONE");

            FaDirectoryFlags flags = (FaDirectoryFlags)(WordAt(record, 0) & ~FaDirectoryEntry.OpenFileCountMask);

            Assert.True(flags.HasFlag(FaDirectoryFlags.Entered));
            Assert.True(flags.HasFlag(FaDirectoryFlags.MainDirectory));
            Assert.True(flags.HasFlag(FaDirectoryFlags.DefaultDirectory));

            Assert.False(flags.HasFlag(FaDirectoryFlags.TapeDirectory));
            Assert.False(flags.HasFlag(FaDirectoryFlags.ReadOnly));
        }
    }
}
