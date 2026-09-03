using System;

using RetroIO.Protocols.Xmsg.Sintran;

namespace RetroIO.Protocols.Xmsg.Ndfs
{
    /// <summary>
    /// Builds the 42-byte SINTRAN directory entry that a remote listing returns for the pack.
    /// </summary>
    /// <remarks>
    /// <para><b>What this record is for</b></para>
    /// A file is named <c>D100.(PACK-ONE:SYSTEM)SINTRAN:DATA;1</c>, so a listing cannot be printed
    /// without knowing the directory it came from. The client asks for this record as the SECOND
    /// entry of the walk, between the first and second file entries, and it consumes no walk
    /// ordinal - file entries count <c>0, 1, 2</c> unbroken across it.
    /// <para><b>Why it exists in this code base</b></para>
    /// We never sent it. Live against D100 on 2026-08-05, serving two files and then three, the
    /// client asked for exactly two entries either time and then abandoned the conversation without
    /// a ReleaseFileEntry - it stopped at the point this record should have arrived, and the
    /// stopping point did not move when the file count did.
    /// <para><b>Layout, from the Monitor Calls manual appendix C</b></para>
    /// The manual's byte-indexed table for MON 244B (GDIEN):
    ///  - <c>0:1</c> state bits and the open-file count, see <see cref="FaDirectoryFlags"/>.
    ///  - <c>2:3</c> unit number in bits 15-12, logical device number in bits 11-0.
    ///  - <c>4:5</c> subunit in bits 11-8, index into the name table in bits 7-0.
    ///  - <c>6:7</c> directory lock.
    ///  - <c>8:9</c> logical device number of the directory semaphore, for tape and floppy only.
    ///  - <c>10:25</c> directory name, 16 bytes, terminated by <c>0x27</c> if shorter.
    ///  - <c>26:29</c> object file pointer.
    ///  - <c>30:33</c> user file pointer.
    ///  - <c>34:37</c> bit file pointer.
    ///  - <c>38:41</c> number of unreserved pages in the directory.
    /// <para><b>Bytes 10-41 are the on-disk label</b></para>
    /// The manual notes that bytes 10:41 are a copy of the directory entry stored on disc, so only
    /// the first ten bytes are live kernel state that SINTRAN prepends.
    /// <para><b>What is SYNTHESISED here</b></para>
    /// We serve a Windows folder, not a pack, so there is no real directory to copy. The name is
    /// real; the three block pointers and the unreserved-page count are INVENTED, because a listing
    /// header needs a directory NAME and nothing we can see suggests the client reads the pointers.
    /// Values are taken from the captured <c>PACK-ONE</c> so they are at least self-consistent and
    /// of the right shape. If a client ever turns out to follow one of these pointers, that is where
    /// this will break.
    /// </remarks>
    public static class FaDirectoryEntry
    {
        /// <summary>
        /// The length of the record in bytes.
        /// </summary>
        public const int RecordLength = 42;

        /// <summary>
        /// The maximum length of a directory name in bytes.
        /// </summary>
        public const int NameLength = 16;

        /// <summary>
        /// The offset of the directory name within the record.
        /// </summary>
        public const int NameOffset = 10;

        /// <summary>
        /// Masks the open-file count out of the state word, bits 9-0.
        /// </summary>
        public const ushort OpenFileCountMask = 0x03FF;

        /// <summary>
        /// The byte that terminates a SINTRAN name shorter than its field.
        /// </summary>
        /// <remarks>
        /// The convention itself lives in <see cref="SintranName"/>; this is here so callers of this
        /// record do not have to reach across for it.
        /// </remarks>
        public const byte NameTerminator = SintranName.Terminator;

        /// <summary>
        /// The state word written for a served directory.
        /// </summary>
        /// <remarks>
        /// Entered, main and default, with no files open - which is what the captured
        /// <c>PACK-ONE</c> carries apart from its one open file. These three bits are the ones a
        /// client is most likely to check before trusting the rest of the record.
        /// </remarks>
        public const FaDirectoryFlags DefaultFlags =
            FaDirectoryFlags.Entered | FaDirectoryFlags.MainDirectory | FaDirectoryFlags.DefaultDirectory;

        /// <summary>
        /// The unit and logical-device word, record bytes 2-3.
        /// </summary>
        /// <remarks>
        /// Unit 0 and logical device <c>0x240</c>, which the Monitor Calls device table names as ECC
        /// disk controller 1, data field. Copied from the captured <c>PACK-ONE</c>: we have no disc,
        /// so any value here is a fiction, and a real one is a better fiction than zero.
        /// </remarks>
        public const ushort UnitAndDeviceWord = 0x0240;

        /// <summary>
        /// The directory-lock word, record bytes 6-7.
        /// </summary>
        /// <remarks>
        /// <c>0x0540</c> is <c>2500</c> octal, which the Monitor Calls semaphore table names as
        /// "directory entry number 0, directory semaphore" - consistent with the name-table index of
        /// zero written at bytes 4-5.
        /// </remarks>
        public const ushort DirectoryLock = 0x0540;

        /// <summary>
        /// The object file pointer, record bytes 26-29.
        /// </summary>
        /// <remarks>
        /// Type bits 01, indexed. Taken from the captured <c>PACK-ONE</c>; SYNTHESISED, see the type
        /// remarks.
        /// </remarks>
        public const uint ObjectFilePointer = 0x400048FC;

        /// <summary>
        /// The user file pointer, record bytes 30-33.
        /// </summary>
        /// <remarks>
        /// Type bits 01, indexed. Taken from the captured <c>PACK-ONE</c>; SYNTHESISED.
        /// </remarks>
        public const uint UserFilePointer = 0x400048FE;

        /// <summary>
        /// The bit file pointer, record bytes 34-37.
        /// </summary>
        /// <remarks>
        /// Type bits 00, contiguous - unlike the two pointers above, which is why this dword looks
        /// different on the wire. Taken from the captured <c>PACK-ONE</c>; SYNTHESISED.
        /// </remarks>
        public const uint BitFilePointer = 0x00004824;

        /// <summary>
        /// The number of unreserved pages in the directory, record bytes 38-41.
        /// </summary>
        /// <remarks>
        /// The only field of the on-disc half that is known to change: the three pointers are
        /// identical across every recording we hold while this one reads <c>0x347E</c>,
        /// <c>0x3FD2</c> and <c>0x2CA4</c> at different times. SYNTHESISED.
        /// </remarks>
        public const uint UnreservedPages = 0x0000347E;

        /// <summary>
        /// Builds the 42-byte directory entry for a served folder.
        /// </summary>
        /// <param name="directoryName">
        /// The directory name to report, for example <c>PACK-ONE</c>. Letters are upper-cased and
        /// the name is truncated to <see cref="NameLength"/> bytes.
        /// </param>
        /// <returns>
        /// The 42-byte record, ready to place in a listing reply.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="directoryName"/> is null or empty.
        /// </exception>
        public static byte[] BuildRecord(string directoryName)
        {
            if (string.IsNullOrEmpty(directoryName))
            {
                throw new ArgumentException(
                    "A directory entry must carry a name.",
                    nameof(directoryName));
            }

            byte[] record = new byte[RecordLength];

            WriteWord(record, 0, (ushort)DefaultFlags);
            WriteWord(record, 2, UnitAndDeviceWord);

            // Bytes 4-5: subunit 0 in bits 11-8, name-table index 0 in bits 7-0. Both zero, and
            // the index of zero is what makes the directory lock below consistent.
            WriteWord(record, 4, 0x0000);

            WriteWord(record, 6, DirectoryLock);

            // Bytes 8-9: the semaphore's logical device number, used for tape and floppy only.
            WriteWord(record, 8, 0x0000);

            SintranName.Write(new Span<byte>(record, NameOffset, NameLength), directoryName);

            WriteDoubleWord(record, 26, ObjectFilePointer);
            WriteDoubleWord(record, 30, UserFilePointer);
            WriteDoubleWord(record, 34, BitFilePointer);
            WriteDoubleWord(record, 38, UnreservedPages);

            return record;
        }

        /// <summary>
        /// Writes a big-endian 16-bit value into the record.
        /// </summary>
        /// <param name="record">
        /// The record being built.
        /// </param>
        /// <param name="offset">
        /// The byte offset to write at.
        /// </param>
        /// <param name="value">
        /// The value to write.
        /// </param>
        private static void WriteWord(byte[] record, int offset, ushort value)
        {
            record[offset] = (byte)(value >> 8);
            record[offset + 1] = (byte)value;
        }

        /// <summary>
        /// Writes a big-endian 32-bit value into the record.
        /// </summary>
        /// <param name="record">
        /// The record being built.
        /// </param>
        /// <param name="offset">
        /// The byte offset to write at.
        /// </param>
        /// <param name="value">
        /// The value to write.
        /// </param>
        private static void WriteDoubleWord(byte[] record, int offset, uint value)
        {
            record[offset] = (byte)(value >> 24);
            record[offset + 1] = (byte)(value >> 16);
            record[offset + 2] = (byte)(value >> 8);
            record[offset + 3] = (byte)value;
        }
    }
}
