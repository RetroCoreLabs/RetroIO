//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Storage.Ndfs.BlockDevices;

namespace RetroIO.Storage.Ndfs.Structures
{
    /// <summary>
    /// Represents the NDFS bit file (allocation bitmap).
    /// Each bit represents one page on the disk - 0 = free, 1 = used.
    /// </summary>
    public class BitFile
    {
        /// <summary>
        /// Pointer to the bit file index block.
        /// </summary>
        public BlockPointer? IndexPointer { get; set; }

        /// <summary>
        /// Number of blocks used for the bitmap.
        /// </summary>
        public uint BitBlocks { get; set; }

        /// <summary>
        /// The actual bitmap data.
        /// </summary>
        private byte[]? _bitmap;

        /// <summary>
        /// Total number of pages tracked by this bitmap. SINTRAN sizes the bitmap to the
        /// physical device geometry (e.g. 38400 pages on PACK-ONE), NOT to the directory's
        /// declared capacity — verified: PACK-ONE's popcount over 38400 bits matches
        /// <c>ndtool -i</c> exactly.
        /// </summary>
        public uint TotalPages { get; private set; }

        /// <summary>
        /// Highest allocatable page + 1 — the descending allocator's starting ceiling.
        /// SINTRAN bounds the free/allocatable window by the directory's declared capacity
        /// (<c>pages_available</c>, extended-info words 1756B–1757B), NOT by the physical
        /// device size. On PACK-ONE capacity = 36945, so the highest allocatable page is
        /// 36944; pages 36945..38399 are a deliberate free-but-unreachable gap (spare /
        /// round-off), left free in the bitmap yet never handed out. When capacity is
        /// unknown this defaults to <see cref="TotalPages"/>. (Verified empirically from
        /// PACK-ONE's bitmap boundary: page 36944 = used, 36945+ = all free.)
        /// </summary>
        public uint AllocCeiling { get; set; }

        /// <summary>
        /// Initializes the bit file with the specified size.
        /// </summary>
        public void Initialize(uint totalPages)
        {
            TotalPages = totalPages;

            // Default the allocation ceiling to the full device until the caller supplies
            // the directory's declared capacity (pages_available).
            AllocCeiling = totalPages;

            // Calculate bitmap size (1 bit per page)
            // Rounded up to a whole number of 16-bit WORDS. The bit file is word addressed
            // (see BitPosition), so an odd byte count would leave the last word half present
            // and its high-byte pages past the end of the buffer — a 616-page ND floppy loses
            // pages 608..615 exactly that way.
            int bitmapBytes = (int)(((totalPages + 7) / 8 + 1) & ~1u);
            _bitmap = new byte[bitmapBytes];

            // Calculate number of blocks needed for bitmap
            BitBlocks = (uint)((bitmapBytes + NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE - 1) /
                              NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);
        }

        /// <summary>
        /// Loads bitmap data from raw bytes.
        /// </summary>
        public void LoadBitmap(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            _bitmap = new byte[data.Length];
            Array.Copy(data, _bitmap, data.Length);
        }

        /// <summary>
        /// Byte index and bit mask of a page in the on-disk bitmap.
        /// </summary>
        /// <param name="blockId">
        /// The page number.
        /// </param>
        /// <param name="byteIndex">
        /// Receives the index into the bitmap byte array.
        /// </param>
        /// <param name="mask">
        /// Receives the single-bit mask within that byte.
        /// </param>
        /// <remarks>
        /// <para><b>Page N is bit N%16 of the 16-bit WORD N/16, counting from the LSB.</b></para>
        /// AUTHORITY: SINTRAN III System Supervisor, ND-30.003.007 EN, appendix F.2
        /// "Bit-File":
        /// <code>
        /// PAGE = BLOCK*400B + WORD*20B + BIT
        /// </code>
        /// <c>20B</c> is 16 decimal, so ONE 16-BIT WORD MAPS 16 PAGES — not one byte mapping 8.
        /// And because the formula ADDS the bit number, page x sits at bit 0, the least
        /// significant bit. The Norwegian edition (ND-30.003.7 NO) carries the identical
        /// formula, and SINTRAN's own allocator agrees: <c>TPAGF</c> at 51043B forms the word
        /// index with <c>SHA ZIN SHR 4</c>, i.e. page/16.
        /// <para>
        /// The image is big-endian, so the word at index w occupies bytes w*2 (high) and w*2+1
        /// (low). Page N therefore lands in the OTHER byte of the pair from where a naive
        /// "byte N/8, bit N%8" scheme would put it, which reduces to flipping the low bit of
        /// the byte index.
        /// </para>
        /// <para><b>Why this matters</b></para>
        /// This library previously used the naive byte scheme. Being wrong in both reader and
        /// writer, it was self-consistent and every round-trip test passed — but on a pack
        /// written by real SINTRAN it reads the allocation map with every byte pair swapped.
        /// Measured on three genuine ND media (a 78 MB pack and two ND distribution floppies),
        /// the naive scheme reports 32, 4 and 3 pages that demonstrably hold file data as FREE.
        /// Handing those pages out would have overwritten live files.
        /// </remarks>
        private static void BitPosition(uint blockId, out int byteIndex, out int mask)
        {
            byteIndex = (int)((blockId >> 3) ^ 1u);
            mask = 1 << (int)(blockId & 7u);
        }

        /// <summary>
        /// Checks if a specific block/page is used.
        /// </summary>
        public bool IsBlockUsed(uint blockId)
        {
            if (_bitmap == null || blockId >= TotalPages)
                return false;

            BitPosition(blockId, out int byteIndex, out int mask);
            return (_bitmap[byteIndex] & mask) != 0;
        }

        /// <summary>
        /// Marks a block as used.
        /// </summary>
        public void MarkBlockUsed(uint blockId)
        {
            if (_bitmap == null || blockId >= TotalPages)
                throw new ArgumentOutOfRangeException(nameof(blockId));

            BitPosition(blockId, out int byteIndex, out int mask);

            _bitmap[byteIndex] |= (byte)mask;
        }

        /// <summary>
        /// Marks a block as free.
        /// </summary>
        public void MarkBlockFree(uint blockId)
        {
            if (_bitmap == null || blockId >= TotalPages)
                throw new ArgumentOutOfRangeException(nameof(blockId));

            BitPosition(blockId, out int byteIndex, out int mask);

            _bitmap[byteIndex] &= (byte)~mask;
        }

        /// <summary>
        /// Calculates the total number of used pages.
        /// </summary>
        public uint CalcUsedPages()
        {
            if (_bitmap == null)
                return 0;

            uint usedCount = 0;
            for (uint i = 0; i < TotalPages; i++)
            {
                if (IsBlockUsed(i))
                    usedCount++;
            }

            return usedCount;
        }

        /// <summary>
        /// Gets the number of free pages.
        /// </summary>
        public uint GetFreePages()
        {
            return TotalPages - CalcUsedPages();
        }

        /// <summary>
        /// The first allocatable page: block 7 (blocks 0-6 are system-reserved — master
        /// block, alternate, and the object/user/bit-file roots). This is the block-7
        /// floor that SINTRAN's scanner (TESTP 51355B) stops at.
        /// </summary>
        private const uint FIRST_ALLOC_BLOCK = 7;

        /// <summary>
        /// Effective top of the descending scan: highest page index to consider (inclusive).
        /// Clamped to the bitmap size so a stale/oversized ceiling can never index past it.
        /// </summary>
        private uint TopScanIndex()
        {
            uint ceiling = (AllocCeiling == 0 || AllocCeiling > TotalPages) ? TotalPages : AllocCeiling;
            return ceiling == 0 ? 0 : ceiling - 1;
        }

        /// <summary>
        /// Finds a single free block, scanning HIGH → LOW to match genuine SINTRAN layout.
        /// </summary>
        /// <remarks>
        /// The real filesystem allocates from the TOP of the volume downward: the scanner
        /// TESTP (kernel addr 51355B) only ever decrements its bitmap word index
        /// (<c>AAX -1</c> at 51372B/51401B), bounded below by the block-7 floor. This
        /// matches the @CREATE-FILE rule "contiguous files positioned in the highest page
        /// addresses". The scan starts at <see cref="AllocCeiling"/> − 1 (the declared
        /// capacity, NOT the physical device size) so it never enters the free-but-
        /// unreachable gap above capacity.
        /// Blocks 0-6 are system blocks and are never handed out.
        /// </remarks>
        public uint? FindFirstFreeBlock()
        {
            for (uint i = TopScanIndex(); i >= FIRST_ALLOC_BLOCK; i--)
            {
                if (!IsBlockUsed(i))
                    return i;

                if (i == FIRST_ALLOC_BLOCK)
                    break; // avoid uint underflow at the floor
            }

            return null;
        }

        /// <summary>
        /// Finds a contiguous range of free blocks, scanning HIGH → LOW so the run is
        /// placed in the highest available page addresses (matching SINTRAN's downward
        /// contiguous reserve via RSPAG 51120B → TESTP). Returns the LOWEST block id of
        /// the found run (its start), bounded by the block-7 floor and the capacity ceiling.
        /// </summary>
        public uint? FindFreeBlockRange(uint blocksNeeded)
        {
            if (blocksNeeded == 0 || blocksNeeded > TotalPages)
                return null;

            uint top = TopScanIndex();
            if (top < FIRST_ALLOC_BLOCK)
                return null;

            // Walk downward, counting a run of free blocks. Because we scan top-down, the
            // first run that reaches the required length ends at the highest possible page.
            uint consecutiveFree = 0;
            for (uint i = top; ; i--)
            {
                if (!IsBlockUsed(i))
                {
                    consecutiveFree++;
                    if (consecutiveFree >= blocksNeeded)
                        return i; // i is the lowest block of the run (its start)
                }
                else
                {
                    consecutiveFree = 0;
                }

                if (i == FIRST_ALLOC_BLOCK)
                    break; // reached the floor without underflowing
            }

            return null;
        }

        /// <summary>
        /// Allocates a range of blocks.
        /// </summary>
        public bool AllocateBlocks(uint startBlock, uint count)
        {
            // NEVER allocate system blocks (0-6)
            if (startBlock < 7)
                return false;

            if (startBlock + count > TotalPages)
                return false;

            // Check if all blocks are free
            for (uint i = startBlock; i < startBlock + count; i++)
            {
                if (IsBlockUsed(i))
                    return false;
            }

            // Mark all blocks as used
            for (uint i = startBlock; i < startBlock + count; i++)
            {
                MarkBlockUsed(i);
            }

            return true;
        }

        /// <summary>
        /// Frees a range of blocks.
        /// </summary>
        public void FreeBlocks(uint startBlock, uint count)
        {
            for (uint i = startBlock; i < startBlock + count && i < TotalPages; i++)
            {
                MarkBlockFree(i);
            }
        }

        /// <summary>
        /// Gets the raw bitmap data.
        /// </summary>
        public byte[] GetBitmapData()
        {
            if (_bitmap == null)
                return Array.Empty<byte>();

            var result = new byte[_bitmap.Length];
            Array.Copy(_bitmap, result, _bitmap.Length);
            return result;
        }

        /// <summary>
        /// Writes the BitFile to disk using the provided block device.
        /// Assumes Contiguous structure starting at the pointer block.
        /// </summary>
        public void WriteToDisk(RetroIO.Storage.Core.IBlockDevice device, BlockPointer pointer)
        {
            if (device == null || pointer == null || pointer.BlockID == 0)
                throw new ArgumentException("Invalid device or pointer");

            if (_bitmap == null)
                return;

            const int PAGE_SIZE = 2048;
            int pagesNeeded = (int)(((_bitmap.Length + PAGE_SIZE - 1) / PAGE_SIZE));

            // Write bitmap data to contiguous blocks
            for (int i = 0; i < pagesNeeded; i++)
            {
                var pageBuffer = new byte[PAGE_SIZE];
                int sourceOffset = i * PAGE_SIZE;
                int bytesToCopy = Math.Min(PAGE_SIZE, _bitmap.Length - sourceOffset);

                if (bytesToCopy > 0)
                    Array.Copy(_bitmap, sourceOffset, pageBuffer, 0, bytesToCopy);

                uint blockId = pointer.BlockID + (uint)i;
                device.WriteBlock(RetroIO.Storage.Core.BlockAddress.FromLBA(blockId), pageBuffer);
            }
        }
    }
}
