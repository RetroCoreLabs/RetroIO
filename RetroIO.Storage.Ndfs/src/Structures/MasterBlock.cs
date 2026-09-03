//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Text;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Boot;

namespace RetroIO.Storage.Ndfs.Structures
{
    /// <summary>
    /// Checksum validation states for Extended Info block.
    /// </summary>
    /// <remarks>
    /// The SINTRAN kernel writes and compares the FULL 16-bit checksum (writer WXDIR
    /// 37702B, validator CHDSI 37763B): there is no kernel path that accepts a match
    /// on the low byte alone. The former <c>CrcValidLowByteOnly</c> state was a
    /// reader-side heuristic that SINTRAN never produces, so it has been removed.
    /// </remarks>
    public enum ChecksumValidationState
    {
        /// <summary>Full 16-bit checksum is valid.</summary>
        CrcValid,

        /// <summary>Checksum does not match.</summary>
        CrcInvalid
    }

    /// <summary>
    /// Represents the NDFS master block, which contains the directory entry
    /// and pointers to the three main filesystem structures.
    /// </summary>
    public class MasterBlock
    {
        /// <summary>
        /// Byte offset of the 8-word (16-byte) Extended Info block within page 0.
        /// Word 1750B (octal) = byte 2000 decimal = 0x07D0, immediately preceding the
        /// 32-byte master block at 0x07E0.
        /// </summary>
        public const int EXT_INFO_OFFSET = 0x07D0;

        /// <summary>
        /// Flag-word bit 15 = "directory entered / in use" — a cross-system mount
        /// interlock. The kernel sets it when a directory is entered (CHDSI 37763B,
        /// <c>BSET ONE 170 DA</c> at 040124, where 170 octal = bit 15) and clears it on
        /// release (REENB 40162B, <c>BSET ZRO 170 DA</c> at 040201). A stored 0x8000
        /// therefore means "left entered by system N", where N is <see cref="ExtLastSystemNumber"/>.
        /// Bits 0–14 have no kernel-assigned meaning on this SINTRAN revision and are
        /// preserved verbatim.
        /// </summary>
        public const ushort EXT_FLAG_ENTERED = 0x8000;

        /// <summary>
        /// Directory name (max 16 characters).
        /// </summary>
        public string DirectoryName { get; set; } = string.Empty;

        /// <summary>
        /// Pointer to the object file index.
        /// </summary>
        public BlockPointer? ObjectFilePointer { get; set; }

        /// <summary>
        /// Pointer to the user file index.
        /// </summary>
        public BlockPointer? UserFilePointer { get; set; }

        /// <summary>
        /// Pointer to the bit file (allocation bitmap).
        /// </summary>
        public BlockPointer? BitFilePointer { get; set; }

        /// <summary>
        /// Number of unreserved pages in the filesystem.
        /// </summary>
        public uint UnreservedPages { get; set; }

        /// <summary>
        /// Total size of the filesystem in pages.
        /// </summary>
        public uint ImageSize { get; set; }

        /// <summary>
        /// Size of the directory structures in pages.
        /// </summary>
        public uint DirectorySize { get; set; }

        // Extended information (if present)
        // Located at negative offsets from master block (addresses 1750-1757 octal)
        public uint ExtPagesAvailable { get; set; }       // -1/-2: Pages available (32-bit, words 1757-1756)
        public uint ExtLastSystemNumber { get; set; }     // -3: System number last entering (word 1755)
        public uint ExtFlagWord { get; set; }             // -4: Flag word (word 1754)
        public ushort ExtReserved1 { get; set; }          // -5: Reserved (word 1753)
        public ushort ExtReserved2 { get; set; }          // -6: Reserved (word 1752)
        public ushort ExtReserved3 { get; set; }          // -7: Reserved (word 1751)
        public ushort ExtCheckSum { get; set; }           // -10: Checksum (word 1750)
        // REMOVED: ExtCpuNumber ("CPU serial"). The extended-info block is exactly 8 words and all
        // of them are accounted for — checksum (1750B), reserved 1-3 (1751B-1753B), flag word
        // (1754B), system number (1755B), pages-available (1756B-1757B). There is no word left for
        // a separate "CPU serial", and nothing ever parsed one: the old field was declared but
        // never assigned, so it always read 0 and surfaced as a phantom "N/A" in the UI. The system
        // number (ExtLastSystemNumber) is the only system identifier NDFS stores.
        public bool ExtValid { get; set; }
        public ushort ExtCalculatedChecksum { get; set; } // Calculated checksum for validation
        public ChecksumValidationState ChecksumState { get; set; } // Checksum validation state
        public bool HasFlomon { get; set; }               // True if FLOMON signature detected (floppy disk)

        /// <summary>
        /// Boot format detected in page 0 (BPUN / FLOMON / raw Binary bootstrap / None). A genuine
        /// raw bootstrap starts with PIOF (octal 150405) or IOF (octal 150401) — see NdfsBootLoader.
        /// Used as corroborating evidence during filesystem detection.
        /// </summary>
        public NdfsBootFormat BootFormat { get; set; } = NdfsBootFormat.None;

        /// <summary>
        /// True when flag-word bit 15 is set — the directory was "entered" (mounted) by
        /// <see cref="ExtLastSystemNumber"/> and not cleanly released. See
        /// <see cref="EXT_FLAG_ENTERED"/>. Kernel: CHDSI 37763B sets it, REENB 40162B clears it.
        /// </summary>
        public bool IsDirectoryEntered => (ExtFlagWord & EXT_FLAG_ENTERED) != 0;

        /// <summary>
        /// Computes the Extended Info checksum the way the SINTRAN kernel does: a plain
        /// 16-bit additive sum of the seven words that FOLLOW the checksum slot
        /// (words 1751B–1757B), truncated to 16 bits. Both the writer WXDIR (37702B) and
        /// the enter-directory validator CHDSI (37763B) run the identical
        /// <c>ADD ,X 0</c> accumulation loop over these words — there is no XOR and the
        /// system number (word 1755B) is just one of the seven summed words.
        /// (Kernel: WXDIR loop 037714–037721; CHDSI loop 040007–040014.)
        /// </summary>
        public static ushort ComputeExtChecksum(
            ushort reserved3, ushort reserved2, ushort reserved1,
            ushort flagWord, ushort systemNumber,
            ushort pagesHi, ushort pagesLo)
        {
            // Reserved words 1751B–1753B, flag 1754B, system# 1755B, pages-hi 1756B, pages-lo 1757B.
            // Plain two's-complement 16-bit add; overflow past bit 15 is discarded.
            uint sum = (uint)reserved3 + reserved2 + reserved1 + flagWord + systemNumber + pagesHi + pagesLo;
            return (ushort)(sum & 0xFFFF);
        }

        /// <summary>
        /// Parses the master block from raw page data.
        /// The master block is located in the last 32 bytes of the first page.
        /// </summary>
        public static MasterBlock FromBytes(byte[] pageData)
        {
            if (pageData == null || pageData.Length < NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE)
                throw new ArgumentException("Invalid page data for master block");

            var masterBlock = new MasterBlock();
            int offset = NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET;

            // Read directory name (16 bytes, terminated by ' not null)
            int nameLength = 0;
            for (int i = 0; i < 16; i++)
            {
                if (pageData[offset + i] == 0x27) break; // ' is the terminator in NDFS
                if (pageData[offset + i] == 0) break;
                nameLength++;
            }
            masterBlock.DirectoryName = Encoding.ASCII.GetString(pageData, offset, nameLength);

            // Read object file pointer (4 bytes)
            masterBlock.ObjectFilePointer = BlockPointer.FromBytes(pageData, offset + 0x10);

            // Read user file pointer (4 bytes)
            masterBlock.UserFilePointer = BlockPointer.FromBytes(pageData, offset + 0x14);

            // Read bit file pointer (4 bytes)
            masterBlock.BitFilePointer = BlockPointer.FromBytes(pageData, offset + 0x18);

            // Read unreserved pages (4 bytes, big-endian)
            masterBlock.UnreservedPages = (uint)((pageData[offset + 0x1C] << 24) |
                                                 (pageData[offset + 0x1D] << 16) |
                                                 (pageData[offset + 0x1E] << 8) |
                                                 pageData[offset + 0x1F]);

            // Extended Information Block (words 1750-1757 octal = bytes 2000-2015 decimal = 0x07D0-0x07DF)
            // Located 16 bytes before master block
            int extOffset = 0x07D0;  // Byte offset 2000

            // Read extended checksum (word 1750 octal, offset 0x07D0)
            masterBlock.ExtCheckSum = (ushort)((pageData[extOffset] << 8) | pageData[extOffset + 1]);

            // Read reserved fields (words 1751-1753 octal, offsets 0x07D2-0x07D6)
            masterBlock.ExtReserved1 = (ushort)((pageData[extOffset + 2] << 8) | pageData[extOffset + 3]);
            masterBlock.ExtReserved2 = (ushort)((pageData[extOffset + 4] << 8) | pageData[extOffset + 5]);
            masterBlock.ExtReserved3 = (ushort)((pageData[extOffset + 6] << 8) | pageData[extOffset + 7]);

            // Read extended flag word (word 1754 octal, offset 0x07D8)
            masterBlock.ExtFlagWord = (uint)((pageData[extOffset + 8] << 8) | pageData[extOffset + 9]);

            // Read last system number (word 1755 octal, offset 0x07DA)
            masterBlock.ExtLastSystemNumber = (uint)((pageData[extOffset + 10] << 8) | pageData[extOffset + 11]);

            // Read pages available (words 1756-1757 octal, offset 0x07DC-0x07DE)
            // 32-bit value: high word (1756) + low word (1757)
            masterBlock.ExtPagesAvailable = (uint)((pageData[extOffset + 12] << 24) |
                                                   (pageData[extOffset + 13] << 16) |
                                                   (pageData[extOffset + 14] << 8) |
                                                   pageData[extOffset + 15]);

            // Calculate checksum first (needed for validation).
            // KERNEL-CORRECTED (WXDIR 37702B / CHDSI 37763B): the checksum is a plain
            // 16-bit ADDITIVE SUM of the seven words that follow it (1751B–1757B), NOT
            // "XOR six words then add SystemNumber". The old XOR formula only reproduced
            // PACK-ONE by coincidence — see ComputeExtChecksum.
            ushort pagesLo = (ushort)(masterBlock.ExtPagesAvailable & 0xFFFF);
            ushort pagesHi = (ushort)((masterBlock.ExtPagesAvailable >> 16) & 0xFFFF);
            ushort calculated = ComputeExtChecksum(
                masterBlock.ExtReserved3, masterBlock.ExtReserved2, masterBlock.ExtReserved1,
                (ushort)masterBlock.ExtFlagWord, (ushort)masterBlock.ExtLastSystemNumber,
                pagesHi, pagesLo);

            masterBlock.ExtCalculatedChecksum = calculated;

            // Determine checksum validation state. The kernel compares the full 16-bit
            // value; there is no "low byte only" acceptance state.
            masterBlock.ChecksumState = (masterBlock.ExtCheckSum == calculated)
                ? ChecksumValidationState.CrcValid
                : ChecksumValidationState.CrcInvalid;

            // Detect FLOMON signature by parsing boot sector
            // FLOMON is detected by checking boot sector for [00 00 00 00 00 00] signature
            // (address, count, checksum all zero in BPUN format)
            var bootCode = NdfsBootLoader.LoadBootCodeFromBytes(pageData);
            masterBlock.BootFormat = bootCode?.Format ?? NdfsBootFormat.None;
            masterBlock.HasFlomon = (masterBlock.BootFormat == NdfsBootFormat.FloMon);

            // Determine if Extended Info is valid
            if (masterBlock.HasFlomon)
            {
                // FLOMON disks: Extended Info is invalid (floppy disk, not hard disk)
                masterBlock.ExtValid = false;
            }
            else
            {
                // Hard disk: Validate using checksum state (full 16-bit match only).
                bool checksumNotZero = (masterBlock.ExtCheckSum != 0);
                bool checksumValid = (masterBlock.ChecksumState == ChecksumValidationState.CrcValid);

                masterBlock.ExtValid = checksumNotZero && checksumValid;
            }

            return masterBlock;
        }

        /// <summary>
        /// Validates that the master block contains valid NDFS data.
        /// </summary>
        public bool IsValid()
        {
            // Check that directory name is printable ASCII (allow empty for new filesystems)
            if (!string.IsNullOrEmpty(DirectoryName))
            {
                for (int i = 0; i < DirectoryName.Length; i++)
                {
                    char c = DirectoryName[i];
                    if (c < 0x20 || c > 0x7E)
                        return false;
                }
            }

            // Check that at least one pointer is valid (relaxed check for testing)
            // In a real NDFS system, all three should be valid
            bool hasValidPointer = false;

            if (ObjectFilePointer != null && ObjectFilePointer.IsValid())
                hasValidPointer = true;

            if (UserFilePointer != null && UserFilePointer.IsValid())
                hasValidPointer = true;

            if (BitFilePointer != null && BitFilePointer.IsValid())
                hasValidPointer = true;

            return hasValidPointer || !string.IsNullOrEmpty(DirectoryName);
        }

        /// <summary>
        /// Scores how strongly this page-0 looks like a genuine NDFS volume, on a 0.0–1.0 scale,
        /// using the kernel-verified invariants. Intended for filesystem auto-detection.
        /// </summary>
        /// <param name="devicePages">Device size in 2048-byte pages (file length / 2048).</param>
        /// <remarks>
        /// <para><see cref="IsValid"/> is far too permissive for detection: it accepts a page whose
        /// 32 bytes at offset 2016 merely contain a printable name OR any one 4-byte word that
        /// isn't zero/Reserved — so almost any 2048-aligned file can pass. This scorer instead
        /// demands the structure a real directory label must have, and then looks for kernel-grade
        /// corroboration.</para>
        ///
        /// <para><b>Structural core</b> (all required): the object, user and bit-file pointers must
        /// all be in-bounds for the device (see <see cref="BlockPointer.IsValidWithin"/>), the name
        /// must be printable ASCII (empty is allowed — a freshly created directory), and the
        /// user-file pointer must sit exactly 2 blocks above the object-file pointer
        /// (<c>user == object + 2</c>) — an invariant that holds on every real image examined
        /// (PACK-ONE: 18684/18686; floppy: 611/613).</para>
        ///
        /// <para><b>Corroboration tiers</b>:</para>
        /// <list type="bullet">
        /// <item>Hard disk: a VALID additive extended-info checksum (WXDIR 37702B / CHDSI 37763B)
        /// with reserved words 1751B–1753B all zero — near-certain.</item>
        /// <item>Floppy / bootable: a FLOMON signature, or a raw bootstrap whose first word is
        /// PIOF/IOF. FLOMON volumes legitimately have NO valid extended-info block, so they must
        /// never be penalised for lacking the checksum.</item>
        /// <item>Structure only, no corroboration — plausible but unproven.</item>
        /// </list>
        /// </remarks>
        public float DetectionScore(uint devicePages)
        {
            // --- Name must be printable ASCII (empty allowed: a new directory has no name yet) ---
            for (int i = 0; i < DirectoryName.Length; i++)
            {
                char c = DirectoryName[i];
                if (c < 0x20 || c > 0x7E)
                    return 0.0f;
            }

            // --- Pointer bounds ---
            bool objectOk = ObjectFilePointer != null && ObjectFilePointer.IsValidWithin(devicePages);
            bool userOk = UserFilePointer != null && UserFilePointer.IsValidWithin(devicePages);
            bool bitOk = BitFilePointer != null && BitFilePointer.IsValidWithin(devicePages);

            int inBounds = 0;
            if (objectOk) inBounds++;
            if (userOk) inBounds++;
            if (bitOk) inBounds++;

            if (inBounds == 0)
                return 0.0f; // no plausible pointer at all — not NDFS

            // --- Structural core ---
            // Beyond bounds, a real directory label has a very specific SHAPE. Verified empirically
            // across 53 real ND images (floppies 154/156/616/640 pages, a 38400-page SMD pack, and
            // a 36360-page Winchester pack):
            //   * the object and user files are always INDEXED (type 01),
            //   * the bit file is always CONTIGUOUS (type 00),
            //   * the object and user index blocks are ADJACENT — exactly 2 blocks apart.
            //
            // NOTE on the adjacency: the user pointer is NOT always object+2. It is object+2 on
            // floppies (611/613) and on SMD packs (18684/18686), but object-2 on the real Winchester
            // pack WD0.img (object=32771, user=32769) — the same layout the Winchester74MB template
            // encodes. So the invariant is the ABSOLUTE distance of 2, in either direction; testing
            // for "+2" alone would falsely reject every Winchester volume.
            //
            // The lone image (of 53) failing this shape is disk22.img, whose pointers are all zero —
            // a blank, unformatted floppy that SHOULD be rejected. Random data essentially never
            // reproduces this combination, which is what makes it a safe false-positive filter.
            bool structural = false;
            if (objectOk && userOk && bitOk &&
                ObjectFilePointer != null && UserFilePointer != null && BitFilePointer != null)
            {
                bool typesOk =
                    ObjectFilePointer.Type == BlockPointer.PointerType.Indexed &&
                    UserFilePointer.Type == BlockPointer.PointerType.Indexed &&
                    BitFilePointer.Type == BlockPointer.PointerType.Contiguous;

                // |user - object| == 2 (unsigned-safe).
                uint objId = ObjectFilePointer.BlockID;
                uint usrId = UserFilePointer.BlockID;
                uint distance = objId > usrId ? objId - usrId : usrId - objId;
                bool userAdjacentToObject = (distance == 2);

                structural = typesOk && userAdjacentToObject;
            }

            if (!structural)
            {
                // Some pointers look sane but the label does not have the shape a real directory
                // has. Weak evidence only — below any sensible auto-mount threshold.
                return 0.25f;
            }

            // --- Corroboration ---
            bool reservedClean = (ExtReserved1 == 0 && ExtReserved2 == 0 && ExtReserved3 == 0);

            // Hard disk with a kernel-valid additive checksum: the strongest signal there is.
            if (!HasFlomon && ExtValid && reservedClean)
                return 0.98f;

            // Floppy (FLOMON) or a genuine ND-100 bootstrap (PIOF/IOF): these have no usable
            // extended-info checksum, so the boot signature is the corroboration.
            if (HasFlomon || BootFormat == NdfsBootFormat.Binary || BootFormat == NdfsBootFormat.BPUN)
                return 0.95f;

            // Structure only — no checksum and no boot code. This is NOT a fringe case: 20 of the
            // 52 real images examined are genuine NDFS floppies with no boot code at all, and
            // floppies never carry a valid extended-info block. They must still win against other
            // filesystem drivers competing for the same 1.2MB floppy (e.g. FAT12), so the score
            // stays well clear of the mount threshold — the structural core above is specific
            // enough on its own to carry them.
            return 0.85f;
        }

        /// <summary>
        /// Converts the master block to raw bytes for writing.
        /// </summary>
        public void WriteToBytes(byte[] pageData)
        {
            if (pageData == null || pageData.Length < NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE)
                throw new ArgumentException("Invalid page buffer for master block");

            int offset = NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET;

            // Clear the master block area
            Array.Clear(pageData, offset, 32);

            // Write directory name (up to 16 bytes). SINTRAN terminates a name shorter than the
            // full 16-byte field with 0x27 ('); a full 16-char name has no terminator. The region
            // was just cleared, so anything after the terminator is already 0.
            var nameBytes = Encoding.ASCII.GetBytes(DirectoryName);
            int copyLength = Math.Min(nameBytes.Length, 16);
            Array.Copy(nameBytes, 0, pageData, offset, copyLength);
            if (copyLength < 16)
                pageData[offset + copyLength] = 0x27; // ' terminator

            // Write object file pointer
            if (ObjectFilePointer != null)
            {
                var pointerBytes = ObjectFilePointer.ToBytes();
                Array.Copy(pointerBytes, 0, pageData, offset + 0x10, 4);
            }

            // Write user file pointer
            if (UserFilePointer != null)
            {
                var pointerBytes = UserFilePointer.ToBytes();
                Array.Copy(pointerBytes, 0, pageData, offset + 0x14, 4);
            }

            // Write bit file pointer
            if (BitFilePointer != null)
            {
                var pointerBytes = BitFilePointer.ToBytes();
                Array.Copy(pointerBytes, 0, pageData, offset + 0x18, 4);
            }

            // Write unreserved pages (big-endian)
            pageData[offset + 0x1C] = (byte)(UnreservedPages >> 24);
            pageData[offset + 0x1D] = (byte)(UnreservedPages >> 16);
            pageData[offset + 0x1E] = (byte)(UnreservedPages >> 8);
            pageData[offset + 0x1F] = (byte)UnreservedPages;
        }

        /// <summary>
        /// Serializes the 8-word Extended Info block (0x07D0) back into page 0 from the
        /// current field values, recomputing the additive checksum (word 1750B) so the
        /// block stays kernel-valid. This mirrors WXDIR (37702B): every field write is
        /// followed by a fresh checksum. Used by the enter/release flow (e.g.
        /// clearing the "entered" flag on unmount); never called for FLOMON floppies.
        /// </summary>
        public void WriteExtendedInfoToBytes(byte[] pageData)
        {
            if (pageData == null || pageData.Length < NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE)
                throw new ArgumentException("Invalid page buffer for extended info block");

            int ext = EXT_INFO_OFFSET;

            ushort pagesHi = (ushort)((ExtPagesAvailable >> 16) & 0xFFFF);
            ushort pagesLo = (ushort)(ExtPagesAvailable & 0xFFFF);
            ushort flag = (ushort)ExtFlagWord;
            ushort sysNum = (ushort)ExtLastSystemNumber;

            // Recompute the checksum from the (possibly just-modified) fields.
            ushort checksum = ComputeExtChecksum(
                ExtReserved3, ExtReserved2, ExtReserved1, flag, sysNum, pagesHi, pagesLo);
            ExtCheckSum = checksum;
            ExtCalculatedChecksum = checksum;
            ChecksumState = ChecksumValidationState.CrcValid;

            // Word 1750B: checksum
            pageData[ext + 0] = (byte)(checksum >> 8);
            pageData[ext + 1] = (byte)(checksum & 0xFF);
            // Words 1751B–1753B: reserved (preserved verbatim, normally 0)
            pageData[ext + 2] = (byte)(ExtReserved1 >> 8);
            pageData[ext + 3] = (byte)(ExtReserved1 & 0xFF);
            pageData[ext + 4] = (byte)(ExtReserved2 >> 8);
            pageData[ext + 5] = (byte)(ExtReserved2 & 0xFF);
            pageData[ext + 6] = (byte)(ExtReserved3 >> 8);
            pageData[ext + 7] = (byte)(ExtReserved3 & 0xFF);
            // Word 1754B: flag word
            pageData[ext + 8] = (byte)(flag >> 8);
            pageData[ext + 9] = (byte)(flag & 0xFF);
            // Word 1755B: system number
            pageData[ext + 10] = (byte)(sysNum >> 8);
            pageData[ext + 11] = (byte)(sysNum & 0xFF);
            // Words 1756B–1757B: pages available (32-bit big-endian)
            pageData[ext + 12] = (byte)((ExtPagesAvailable >> 24) & 0xFF);
            pageData[ext + 13] = (byte)((ExtPagesAvailable >> 16) & 0xFF);
            pageData[ext + 14] = (byte)((ExtPagesAvailable >> 8) & 0xFF);
            pageData[ext + 15] = (byte)(ExtPagesAvailable & 0xFF);
        }
    }
}
