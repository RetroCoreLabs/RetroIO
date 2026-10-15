//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Text;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Storage.Ndfs.Elements
{
    /// <summary>
    /// Represents an object (file) entry in the NDFS object file.
    /// Contains file metadata, access control, versioning, and tracking information.
    /// </summary>
    public class ObjectEntry
    {
        /// <summary>
        /// 16-bit header holding the entry status flags.
        /// </summary>
        /// <value>
        /// The whole big-endian header word as read from disk, not just the bits modelled below.
        /// </value>
        /// <remarks>
        /// Modelled bits:
        ///  - Bit 15 — (U) entry used.
        ///  - Bit 14 — (W) currently opened for write.
        ///  - Bit 13 — (R) reserved.
        ///  - Bit 12 — (M) file modified, set when opened for write.
        ///  - Bit 8 — (F) user/object entry discriminator, clear for an object entry.
        ///  - Bits 10-0 — terminal number of the last user that opened the file, <c>0</c> for an RT
        ///    program.
        /// Bits 10-0 overlap bit 8 because the terminal number occupies the low 11 bits of the same
        /// word; read the discriminator only when distinguishing an object entry from a user entry.
        /// </remarks>
        public uint Header { get; set; }

        /// <summary>
        /// Bit 15 of <see cref="Header"/>, the "entry used" flag, as a whole 16-bit value.
        /// </summary>
        /// <remarks>
        /// <para><b>Value</b></para>
        /// <c>0x8000</c>. <see cref="Header"/> is a 16-bit big-endian word, so the used flag is
        /// <c>0x8000</c>, not <c>0x80</c>.
        /// <para><b>Why it matters</b></para>
        /// A newly created entry must have this set, or SINTRAN reads the slot as free and the file
        /// disappears. Use this constant when building an entry from scratch rather than writing a
        /// bare <c>0x80</c> byte.
        /// <para><b>Why the whole word round-trips</b></para>
        /// Confusing the two is what the old <see cref="ToBytes"/> did. A real entry recorded off an
        /// ND-100 by the COSMOS remote file access work in the NDInsight repository carries
        /// <c>0x90</c> in byte 0, which is this flag plus "file modified" — proof that the high byte
        /// holds more than just "used" and must be preserved whole.
        /// </remarks>
        public const uint HeaderEntryUsed = 0x8000;

        /// <summary>
        /// Index of this object entry.
        /// </summary>
        public int ObjectIndex { get; set; }

        /// <summary>
        /// Gets or sets the file number SINTRAN reports for this file - the <c>n</c> in
        /// <c>FILE n</c> - which is NOT the same as <see cref="ObjectIndex"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="ObjectIndex"/> is the raw PHYSICAL position in the object file
        /// (<c>page * 32 + slotInPage</c>) and is the write-back key. The number the user sees is
        /// per-user and stable for the life of the file.
        /// </para>
        /// <para>
        /// The two coincide only inside a user's FIRST object block. A user may own up to 16 blocks
        /// of 256 files, and block <c>n</c> sits at object-file pages <c>n*512 + user*8</c>, so
        /// beyond the first block the physical position encodes both the block and the slot. Use
        /// <see cref="ComputeFileNumber"/> to convert.
        /// </para>
        /// <para>
        /// Worked example from a verified pack: <c>F0500</c> is <c>FILE 307</c> owned by user 8, and
        /// its physical position is 18483. Reading 18483 as the old model does gives file number
        /// <c>18483 &amp; 0xFF</c> = 51 and owner <c>18483 &gt;&gt; 8</c> = 72 - a user that does not
        /// exist. Both are wrong; only <see cref="ComputeFileNumber"/> gives 307.
        /// </para>
        /// </remarks>
        public int FileNumber { get; set; }

        /// <summary>
        /// Converts a physical object-file position into the file number SINTRAN reports.
        /// </summary>
        /// <param name="objectIndex">
        /// The physical position, <c>page * 32 + slotInPage</c>.
        /// </param>
        /// <param name="userIndex">
        /// The owning user, taken from byte 34 of the entry.
        /// </param>
        /// <returns>
        /// The per-user file number, or <c>-1</c> when the position does not lie inside any object
        /// block belonging to <paramref name="userIndex"/>.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Returning <c>-1</c> for a position outside the user's region is deliberate: a caller
        /// spotting a mismatch is far better than silently receiving a plausible but wrong number.
        /// </para>
        /// <para>
        /// Doc: <c>NDInsight SINTRAN/XMSG/DOC/NDFS-OBJECT-BLOCKS-DECODED-2026-08-01.md</c>.
        /// </para>
        /// </remarks>
        public static int ComputeFileNumber(int objectIndex, int userIndex)
        {
            const int entriesPerPage = 32;
            const int pagesPerObjectBlock = 8;
            const int pagesPerIndexBlock = 512;
            const int filesPerObjectBlock = pagesPerObjectBlock * entriesPerPage;

            int page = objectIndex / entriesPerPage;
            int slotInPage = objectIndex % entriesPerPage;

            int block = page / pagesPerIndexBlock;
            int pageWithinBlockRow = page % pagesPerIndexBlock;
            int firstPageOfUser = userIndex * pagesPerObjectBlock;

            int offsetPages = pageWithinBlockRow - firstPageOfUser;
            if (offsetPages < 0 || offsetPages >= pagesPerObjectBlock)
            {
                return -1;
            }

            int slot = (offsetPages * entriesPerPage) + slotInPage;
            return (block * filesPerObjectBlock) + slot;
        }

        /// <summary>
        /// Object/file name (max 16 characters).
        /// </summary>
        public string ObjectName { get; set; } = string.Empty;

        /// <summary>
        /// File type/extension (max 4 characters).
        /// </summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Owner user name.
        /// </summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>
        /// User index of the owner.
        /// </summary>
        public int UserIndex { get; set; }

        /// <summary>
        /// Pointer to next version of this file (for versioning).
        /// </summary>
        public uint ObjectEntryNextVersion { get; set; }

        /// <summary>
        /// Pointer to previous version of this file (for versioning).
        /// </summary>
        public uint ObjectEntryPrevVersion { get; set; }

        /// <summary>
        /// Access permission bits (15 bits total).
        /// Bits 14-9: Public access permissions (6 bits)
        /// Bits 9-4: Friend access permissions (6 bits)
        /// Bits 4-0: Own access permissions (5 bits)
        /// </summary>
        public uint AccessBits { get; set; }

        /// <summary>
        /// File type flags (Terminal, Peripheral, Spooling, Indexed, Contiguous, Allocated, MagneticTape, Library).
        /// </summary>
        public FileTypes FileTypeFlags { get; set; } = FileTypes.IndexedFile;

        /// <summary>
        /// Device number for device files.
        /// </summary>
        public uint DeviceNumber { get; set; }

        /// <summary>
        /// User index of user who reserved this file.
        /// </summary>
        public uint UserIndexOfReservingUser { get; set; }

        /// <summary>
        /// Main-directory index of the reserving user, byte 32 of the entry.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The high half of the word at bytes 32-33, whose low half is
        /// <see cref="UserIndexOfReservingUser"/>. Together they identify the last user that reserved
        /// the file; byte 0 bit 13 is the matching "file is reserved" flag.
        /// </para>
        /// <para>
        /// <b>Byte 32 is NOT a file-type code.</b> <see cref="FileType"/> reads and writes this same
        /// byte, so it round-trips and nothing is corrupted, but the name is wrong: neither the
        /// Monitor Calls manual nor the System Supervisor manual has a file-type code anywhere in the
        /// entry, and an annotated dump accounts for all 32 words with none left over. The file's type
        /// is the TEXT at bytes 18-21, which <see cref="Type"/> already carries. Removing
        /// <see cref="FileType"/> is a public API change and has not been done.
        /// </para>
        /// </remarks>
        public uint ReservingUserDirectoryIndex { get; set; }

        /// <summary>
        /// Number of times file is currently open.
        /// </summary>
        public uint CurrentOpenCount { get; set; }

        /// <summary>
        /// Total number of times file has been opened.
        /// </summary>
        public uint TotalOpenCount { get; set; }

        /// <summary>
        /// Date file was created (ND date format).
        /// </summary>
        public uint DateCreated { get; set; }

        /// <summary>
        /// Last date file was opened for read (ND date format).
        /// </summary>
        public uint LastDateOpenedForRead { get; set; }

        /// <summary>
        /// Last date file was opened for write (ND date format).
        /// </summary>
        public uint LastDateOpenedForWrite { get; set; }

        /// <summary>
        /// Pointer to the first data block or index block.
        /// Contains both block ID and pointer type (Contiguous/Indexed/SubIndexed).
        /// </summary>
        public BlockPointer? FilePointer { get; set; }

        /// <summary>
        /// Number of pages allocated for this file.
        /// </summary>
        public uint PagesInFile { get; set; }

        /// <summary>
        /// Actual size in bytes.
        /// </summary>
        public ulong BytesInFile { get; set; }

        /// <summary>
        /// File type as numeric code (legacy field).
        /// </summary>
        public int FileType { get; set; }

        /// <summary>
        /// Gets the file type as text.
        /// </summary>
        public string FileTypeAsText
        {
            get
            {
                return FileType switch
                {
                    0 => "DATA",
                    1 => "PROG",
                    2 => "SYMB",
                    3 => "TEXT",
                    _ => $"TYPE{FileType}"
                };
            }
        }

        /// <summary>
        /// Verbatim on-disk 64 bytes, used as the base when re-serializing so
        /// fields not modelled here (e.g. byte 33, low byte of the object-index
        /// word, the mxobl/acobl byte) survive a round trip. Null for new entries.
        /// </summary>
        private byte[]? _raw;

        /// <summary>
        /// Parses an object entry from raw bytes.
        /// </summary>
        /// <remarks>
        /// The original on-disk bytes are retained, so bytes this class does not model survive a
        /// later <see cref="ToBytes"/> call.
        /// </remarks>
        /// <param name="data">
        /// Buffer holding the raw entry.
        /// </param>
        /// <param name="offset">
        /// Byte position in <paramref name="data"/> at which the 64-byte entry starts.
        /// </param>
        /// <returns>
        /// The parsed entry, or <c>null</c> when the slot is free — that is, when the
        /// <see cref="HeaderEntryUsed"/> bit is clear.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="data"/> is null, or holds fewer than 64 bytes from
        /// <paramref name="offset"/>.
        /// </exception>
        public static ObjectEntry? FromBytes(byte[] data, int offset)
        {
            if (data == null || data.Length < offset + 64)
                throw new ArgumentException("Insufficient data for object entry");

            var entry = new ObjectEntry();
            entry._raw = new byte[64];
            Array.Copy(data, offset, entry._raw, 0, 64);

            // Based on C code analysis:
            // Byte 0: 0x80 bit indicates "in use"
            // Bytes 2-17: File name (terminated by ')
            // Bytes 18-21: File type
            // Byte 34: User index
            // Bytes 52-55: Pages (big-endian)
            // Bytes 56-59: Bytes (big-endian) + 1
            // Bytes 60-63: File pointer (big-endian)

            // Check if in use
            if ((data[offset] & 0x80) == 0)
                return null;

            // Read object name (16 bytes at offset+2, terminated by ')
            int nameLength = 0;
            for (int i = 0; i < 16; i++)
            {
                if (data[offset + 2 + i] == 0x27) break; // ' is the terminator
                if (data[offset + 2 + i] == 0) break;
                nameLength++;
            }
            entry.ObjectName = Encoding.ASCII.GetString(data, offset + 2, nameLength);

            // Read file type (4 bytes at offset+18, terminated by ')
            int typeLength = 0;
            for (int i = 0; i < 4; i++)
            {
                if (data[offset + 18 + i] == 0x27 || data[offset + 18 + i] == 0) break;
                typeLength++;
            }
            if (typeLength > 0)
                entry.Type = Encoding.ASCII.GetString(data, offset + 18, typeLength);
            else
                entry.Type = string.Empty; // preserve empty type as-is — do NOT default to "DATA"

            // User index (byte 34)
            entry.UserIndex = data[offset + 34];

            // Pages in file (bytes 52-55, big-endian)
            entry.PagesInFile = (uint)((data[offset + 52] << 24) |
                                       (data[offset + 53] << 16) |
                                       (data[offset + 54] << 8) |
                                       data[offset + 55]);

            // Bytes in file (bytes 56-59, big-endian) + 1. The field is 32 bits wide, so a stored
            // 0xFFFFFFFF (empty file) wraps to 0. uint + int would widen to long and give 4294967296.
            entry.BytesInFile = unchecked((uint)((data[offset + 56] << 24) |
                                                 (data[offset + 57] << 16) |
                                                 (data[offset + 58] << 8) |
                                                 data[offset + 59]) + 1u);

            // File pointer (bytes 60-63, big-endian) - stored as BlockPointer with type info
            entry.FilePointer = BlockPointer.FromBytes(data, offset + 60);

            // Read file type (1 byte)
            entry.FileType = data[offset + 32];

            // Previously-dropped fields at offsets 22-51 (see RetroCore
            // NDDiskBlock decoder). All big-endian.
            ushort BE16(int o) => (ushort)((data[offset + o] << 8) | data[offset + o + 1]);
            uint BE32(int o) => (uint)((data[offset + o] << 24) | (data[offset + o + 1] << 16) |
                                       (data[offset + o + 2] << 8) | data[offset + o + 3]);

            entry.Header = BE16(0);
            entry.ObjectEntryNextVersion = BE16(22);
            entry.ObjectEntryPrevVersion = BE16(24);
            entry.AccessBits = BE16(26);
            entry.FileTypeFlags = (FileTypes)BE16(28);
            entry.DeviceNumber = BE16(30);
            // NOT a separate on-disk field. This reads the SAME byte as UserIndex above, so the two
            // The reserving user lives in the word at bytes 32-33: byte 32 is the reserving user's
            // MAIN DIRECTORY index and byte 33 is the USER index within it.
            //
            // CORRECTED 2026-07-30. This used to read byte 34, which is the OWNING user, so the two
            // properties came back equal and a comment here asserted that no reserving-user field
            // existed at all. Both were wrong.
            //
            // Sources: ND-860228-2 Monitor Calls appendix C gives a byte-indexed layout with
            // "User index in main directory of reserving user" at 32:33; ND-30.003.007 System
            // Supervisor appendix F.6 names the same word LAST RESERVING USER and shows it split at
            // 15/8/7/0; and an annotated @DUMP-OBJECT-ENTRY listing in that manual labels every one
            // of the 32 words. Byte 0 bit 13 is the matching "file is reserved" flag. Which half of
            // the word is the directory and which the user is INFERRED, from the same manual packing
            // a directory/user pair the same way for MON 215B - left byte directory, right byte user.
            //
            // It records the LAST reserving user, so it is a stale record rather than a live lock.
            // When SINTRAN clears it is UNKNOWN.
            //
            // Full write-up:
            // E:\Dev\Ronny\NDInsight\SINTRAN\Filesystem\on-disk-format\OBJECT-ENTRY-CARVE-BYTES-32-35-2026-07-30.md
            entry.ReservingUserDirectoryIndex = data[offset + 32];
            entry.UserIndexOfReservingUser = data[offset + 33];
            entry.CurrentOpenCount = BE16(36);
            entry.TotalOpenCount = BE16(38);
            entry.DateCreated = BE32(40);
            entry.LastDateOpenedForRead = BE32(44);
            entry.LastDateOpenedForWrite = BE32(48);

            return entry;
        }

        /// <summary>
        /// Converts the object entry to raw bytes for writing.
        /// </summary>
        /// <param name="buffer">
        /// Destination buffer, which must hold 64 bytes from <paramref name="offset"/> onwards.
        /// </param>
        /// <param name="offset">
        /// Byte position in <paramref name="buffer"/> at which the 64-byte entry starts.
        /// </param>
        /// <remarks>
        /// <para>
        /// When the entry came from <see cref="FromBytes"/> the original on-disk bytes are copied
        /// first, so bytes this class does not model still survive the round trip. Only the modelled
        /// fields are then written over the top.
        /// </para>
        /// <para>
        /// <b>Round-tripping this record matters beyond the local disk.</b> SINTRAN's COSMOS remote
        /// file access server sends this exact 64-byte record over the network, unchanged, one per
        /// directory entry, when another machine lists a directory on this pack. So a field lost here
        /// is a field lost to every remote client as well as to the pack. Keep this method
        /// byte-symmetric with <see cref="FromBytes"/>; the header word is the field that already got
        /// this wrong once, and the comment beside that write records how it was caught.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="buffer"/> is null, or too short to hold 64 bytes from
        /// <paramref name="offset"/>.
        /// </exception>
        public void ToBytes(byte[] buffer, int offset)
        {
            if (buffer == null || buffer.Length < offset + 64)
                throw new ArgumentException("Insufficient buffer for object entry");

            // Base on original on-disk bytes when available so unmodelled
            // bytes survive; otherwise start from a cleared area.
            if (_raw != null && _raw.Length == 64)
                Array.Copy(_raw, 0, buffer, offset, 64);
            else
                Array.Clear(buffer, offset, 64);

            // Versioning, access, flags, device, open counts, dates (22-51).
            void W16(int o, uint v) { buffer[offset + o] = (byte)(v >> 8); buffer[offset + o + 1] = (byte)v; }
            void W32(int o, uint v)
            {
                buffer[offset + o] = (byte)(v >> 24); buffer[offset + o + 1] = (byte)(v >> 16);
                buffer[offset + o + 2] = (byte)(v >> 8); buffer[offset + o + 3] = (byte)v;
            }
            // The full 16-bit header, symmetric with FromBytes, which reads it as BE16(0).
            //
            // This used to write a bare `buffer[offset] = 0x80`, which threw away the low byte and
            // every high-byte flag except "used", so a read/modify/write cycle silently cleared the
            // "file modified" flag (bit 12) on every entry it touched.
            //
            // HOW THAT WAS FOUND, and why it is not only a local disk concern:
            //
            // This same 64-byte object entry is what SINTRAN's COSMOS remote file access server
            // (*FA-SERVER) puts ON THE NETWORK, unchanged, one per directory entry, when another
            // machine runs LIST-FILES or FILE-STATISTICS against this pack. The server does not
            // reformat it - the on-disk record IS the wire record.
            //
            // The bug was caught by decoding a recorded conversation between two real ND-100s: the
            // entry for SINTRAN:DATA arrives with 0x90 in byte 0, which is "used" (bit 15) plus
            // "file modified" (bit 12). Feeding that captured record through FromBytes and back out
            // through ToBytes returned 0x80, losing bit 12. So any COSMOS-style server answering
            // out of RetroFS would have handed remote clients entries with the modified flag
            // stripped, and any local read/modify/write did the same to the pack itself.
            //
            // The decode and the byte-level evidence live in the NDInsight repository under
            // SINTRAN/XMSG (see XMSG-LIST-FILES-ON-THE-WIRE-2026-07-29.md). The regression test is
            // RetroIO.Protocols.RetroIO.Protocols.Xmsg.Ndfs.Tests.FaDirectoryListingTests.HeaderWord_SurvivesTheRoundTrip, which replays
            // the captured 64 bytes through this class and requires all of them back unchanged.
            //
            // Callers building an entry from scratch must set Header (see HeaderEntryUsed); a zero
            // header marks the slot free and the file vanishes. The old hardcoded 0x80 was masking
            // exactly that in the create path.
            W16(0, Header);

            W16(22, ObjectEntryNextVersion);
            W16(24, ObjectEntryPrevVersion);
            W16(26, AccessBits);
            W16(28, (uint)FileTypeFlags);
            W16(30, DeviceNumber);
            W16(36, CurrentOpenCount);
            W16(38, TotalOpenCount);
            W32(40, DateCreated);
            W32(44, LastDateOpenedForRead);
            W32(48, LastDateOpenedForWrite);

            // Object name (16 bytes at offset+2, terminated by ')
            byte[] nameBytes = Encoding.ASCII.GetBytes(ObjectName);
            int nameLen = Math.Min(nameBytes.Length, 16);
            Array.Copy(nameBytes, 0, buffer, offset + 2, nameLen);
            if (nameLen < 16)
                buffer[offset + 2 + nameLen] = 0x27; // ' terminator

            // File type (4 bytes at offset+18, terminated by ')
            byte[] typeBytes = Encoding.ASCII.GetBytes(Type);
            int typeLen = Math.Min(typeBytes.Length, 4);
            Array.Copy(typeBytes, 0, buffer, offset + 18, typeLen);
            if (typeLen < 4)
                buffer[offset + 18 + typeLen] = 0x27; // ' terminator

            // File type code (byte 32)
            // Bytes 32-33 are the reserving user: byte 32 the main-directory index, byte 33 the user
            // index. FileType reads and writes byte 32 under a wrong name - see the remarks on
            // ReservingUserDirectoryIndex - so it is left writing there to keep behaviour identical
            // for anything that sets it.
            buffer[offset + 32] = (byte)FileType;

            // Byte 33 was never written before 2026-07-30, so the reserving user's index was dropped
            // on any entry built from scratch. As with byte 35, it is only written when there are no
            // raw bytes to preserve, so a disk-read entry keeps whatever the pack had rather than
            // being rewritten from a field this class may never have been told about.
            if (_raw == null || _raw.Length != 64)
            {
                buffer[offset + 33] = (byte)UserIndexOfReservingUser;
            }

            // Object-index word (bytes 34-35), stored big-endian as [owning user | file slot].
            //
            // Byte 35 was never written before 2026-07-30, so an entry built from scratch went to
            // disk with a zero slot regardless of where it actually sat. Entries READ from disk were
            // spared, because the raw bytes are copied in above and byte 35 survived untouched -
            // which is why nothing noticed. Only the create path was affected, and only a SINTRAN
            // machine reading the result would care.
            //
            // So byte 35 is written ONLY when there are no raw bytes to preserve. This library
            // differs from the C, Python and TypeScript ports here: they hold a diskObjectIndex read
            // straight from bytes 34-35 and write it back verbatim, whereas this ObjectIndex is
            // DERIVED from the entry's physical position during the directory walk (see
            // NdfsFileSystem, where it is set to ((subindex * 512) + i) * 32 + j). The two agree on
            // a well-formed pack - the object file is partitioned so user U owns slots U*256 to
            // U*256+255, making ObjectIndex equal to (UserIndex << 8) | slot - but on a pack where
            // the stored word and the physical position disagree they would not. Preserving the raw
            // byte keeps this method faithful rather than silently rewriting the entry to match our
            // own idea of where it lives.
            //
            // CORRECTION 2026-08-01: "ObjectIndex equals (UserIndex << 8) | slot" holds ONLY inside
            // a user's FIRST object block. A user may own up to 16 blocks of 256 files, and block n
            // sits at object-file pages n*512 + U*8, so past the first block the physical position
            // encodes the block as well as the slot and this identity breaks. That is precisely why
            // preserving the raw byte matters more than the comment above realised - and why byte 35
            // (written below as ObjectIndex & 0xFF) cannot express a number above 255. Use
            // ComputeFileNumber for the user-visible number; see FileNumber.
            //
            // Layout verified against real disk bytes in the NDInsight repository,
            // SINTRAN/Filesystem/on-disk-format/object-entry.md section 4.5, which marks byte 34
            // (owning user) VERIFIED and byte 35 (file slot) INFERRED.
            buffer[offset + 34] = (byte)UserIndex;
            if (_raw == null || _raw.Length != 64)
            {
                buffer[offset + 35] = (byte)(ObjectIndex & 0xFF);
            }

            // Pages in file (bytes 52-55, big-endian)
            buffer[offset + 52] = (byte)(PagesInFile >> 24);
            buffer[offset + 53] = (byte)(PagesInFile >> 16);
            buffer[offset + 54] = (byte)(PagesInFile >> 8);
            buffer[offset + 55] = (byte)PagesInFile;

            // Bytes in file - 1 (bytes 56-59, big-endian)
            ulong bytesMinusOne = BytesInFile > 0 ? BytesInFile - 1 : 0;
            buffer[offset + 56] = (byte)(bytesMinusOne >> 24);
            buffer[offset + 57] = (byte)(bytesMinusOne >> 16);
            buffer[offset + 58] = (byte)(bytesMinusOne >> 8);
            buffer[offset + 59] = (byte)bytesMinusOne;

            // File pointer (bytes 60-63, big-endian)
            if (FilePointer != null)
            {
                uint pointerValue = FilePointer.Native;
                buffer[offset + 60] = (byte)(pointerValue >> 24);
                buffer[offset + 61] = (byte)(pointerValue >> 16);
                buffer[offset + 62] = (byte)(pointerValue >> 8);
                buffer[offset + 63] = (byte)pointerValue;
            }
        }

    }
}
