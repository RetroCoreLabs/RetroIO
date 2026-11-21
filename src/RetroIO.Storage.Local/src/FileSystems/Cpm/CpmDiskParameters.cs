//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Storage.Local.FileSystems.Cpm
{
    /// <summary>
    /// The part of a CP/M Disk Parameter Block (DPB) the filesystem needs: block size, number of
    /// blocks, directory size and extent mask.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CP/M keeps no description of the disk on the disk itself. Each machine's BIOS holds a DPB for
    /// every format it knows, so a layout is data that comes from that machine's documents. Digital
    /// Research's CP/M 3 System Guide describes the DPB; the CP/M 3 macro library CPM3.LIB has a
    /// <c>dpb</c> macro that computes it from six numbers, and <see cref="FromCpm3Macro"/> runs the
    /// same steps, so a layout can be written exactly as a machine's BIOS source writes it.
    /// </para>
    /// <para>
    /// The fields kept here, with their DPB names:
    ///  - <see cref="BlockSize"/>: 128 shifted left by BSH.
    ///  - <see cref="BlockCount"/>: DSM + 1.
    ///  - <see cref="DirectoryEntries"/>: DRM + 1.
    ///  - <see cref="ExtentMask"/>: EXM.
    ///  - <see cref="ReservedTracks"/>: OFF.
    /// </para>
    /// </remarks>
    public sealed class CpmDiskParameters
    {
        /// <summary>
        /// Creates a set of disk parameters from values already worked out.
        /// </summary>
        /// <param name="blockSize">
        /// Allocation block size in bytes: 1024, 2048, 4096, 8192 or 16384.
        /// </param>
        /// <param name="blockCount">
        /// Number of allocation blocks on the disk (DSM + 1).
        /// </param>
        /// <param name="directoryEntries">
        /// Number of 32-byte directory entries (DRM + 1).
        /// </param>
        /// <param name="extentMask">
        /// The extent mask EXM: how many 16 KB logical extents one directory entry holds, minus one.
        /// </param>
        /// <param name="reservedTracks">
        /// Tracks before the data area (OFF), used by the boot loader and the system.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="blockSize"/> is not a CP/M block size, or a count is not positive.
        /// </exception>
        /// <param name="directoryBlocks">
        /// Blocks from block 0 kept for the directory: the number of set bits, from the top, in AL0 and
        /// AL1. Leave it 0 to take just the blocks the directory entries fill, as the CP/M 3
        /// <c>dpb</c> macro does. A BIOS may keep more: the Kaypro II keeps four 1 KB blocks for 64
        /// entries.
        /// </param>
        public CpmDiskParameters(int blockSize, int blockCount, int directoryEntries, int extentMask, int reservedTracks, int directoryBlocks = 0)
        {
            if (blockSize != 1024 && blockSize != 2048 && blockSize != 4096 && blockSize != 8192 && blockSize != 16384)
            {
                throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, "CP/M block sizes are 1024 to 16384 bytes, powers of two");
            }

            if (blockCount <= 0 || directoryEntries <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(blockCount), "the block count and the directory size must be positive");
            }

            BlockSize = blockSize;
            BlockCount = blockCount;
            DirectoryEntries = directoryEntries;
            ExtentMask = extentMask;
            ReservedTracks = reservedTracks;
            DirectoryBlocks = directoryBlocks > 0 ? directoryBlocks : (directoryEntries * 32 + blockSize - 1) / blockSize;
        }

        /// <summary>
        /// Gets the allocation block size in bytes.
        /// </summary>
        public int BlockSize { get; }

        /// <summary>
        /// Gets the number of allocation blocks on the disk (DSM + 1).
        /// </summary>
        public int BlockCount { get; }

        /// <summary>
        /// Gets the number of 32-byte directory entries (DRM + 1).
        /// </summary>
        public int DirectoryEntries { get; }

        /// <summary>
        /// Gets the extent mask EXM.
        /// </summary>
        public int ExtentMask { get; }

        /// <summary>
        /// Gets the number of reserved tracks before the data area (OFF).
        /// </summary>
        public int ReservedTracks { get; }

        /// <summary>
        /// Gets a value indicating whether a directory entry holds 16-bit block numbers.
        /// </summary>
        /// <remarks>
        /// CP/M uses one byte per block number when DSM is below 256 and two bytes (low byte first)
        /// otherwise, so an entry lists 16 or 8 blocks.
        /// </remarks>
        public bool WideBlockNumbers => BlockCount - 1 > 255;

        /// <summary>
        /// Gets the number of blocks kept for the directory, counted from block 0 (the set bits of
        /// AL0 and AL1). No file is given these blocks.
        /// </summary>
        public int DirectoryBlocks { get; }

        /// <summary>
        /// Builds the parameters from the bytes of a Disk Parameter Block as a BIOS or ROM holds it.
        /// </summary>
        /// <remarks>
        /// The 15 DPB bytes (CP/M 2.2 Alteration Guide, section 10): SPT (2 bytes, low first), BSH,
        /// BLM, EXM, DSM (2), DRM (2), AL0, AL1, CKS (2), OFF (2). BLM must be 2 to the power BSH,
        /// minus 1, and AL0/AL1 must be set bits from the top with no gap; anything else is not a DPB.
        /// </remarks>
        /// <param name="dpb">
        /// At least 15 bytes, starting at SPT.
        /// </param>
        /// <returns>
        /// The parameters the DPB describes.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when the bytes are not a consistent DPB.
        /// </exception>
        public static CpmDiskParameters FromDpbBytes(ReadOnlySpan<byte> dpb)
        {
            if (dpb.Length < 15)
            {
                throw new ArgumentException("A DPB is 15 bytes", nameof(dpb));
            }

            int bsh = dpb[2];
            int blm = dpb[3];
            int exm = dpb[4];
            int dsm = dpb[5] | (dpb[6] << 8);
            int drm = dpb[7] | (dpb[8] << 8);
            int allocation = (dpb[9] << 8) | dpb[10];
            int off = dpb[13] | (dpb[14] << 8);
            if (bsh < 3 || bsh > 7 || blm != (1 << bsh) - 1)
            {
                throw new ArgumentException("BSH " + bsh + " and BLM " + blm + " do not match", nameof(dpb));
            }

            int directoryBlocks = 0;
            while (directoryBlocks < 16 && (allocation & (0x8000 >> directoryBlocks)) != 0)
            {
                directoryBlocks++;
            }

            if (directoryBlocks == 0 || (allocation & ~(0xFFFF << (16 - directoryBlocks)) & 0xFFFF) != 0)
            {
                throw new ArgumentException("AL0/AL1 " + allocation.ToString("X4") + " are not directory bits from the top", nameof(dpb));
            }

            return new CpmDiskParameters(128 << bsh, dsm + 1, drm + 1, exm, off, directoryBlocks);
        }

        /// <summary>
        /// Works out the disk parameters the way the CP/M 3 <c>dpb</c> macro (CPM3.LIB) does.
        /// </summary>
        /// <remarks>
        /// The macro's steps, followed here one by one:
        ///  - SPT = physical sectors per track times (sector size / 128).
        ///  - DSM = ((tracks - OFF) * SPT) / (block size / 128) - 1.
        ///  - EXM = block size / 1024, halved when DSM is above 255, then minus 1.
        ///  - DRM = directory entries - 1.
        /// The macro also computes the directory allocation bits (AL0, AL1) and the check size
        /// (CKS); the reader works them out from the directory size instead, so they are not kept.
        /// </remarks>
        /// <param name="physicalSectorSize">
        /// Physical sector size in bytes (the macro's first argument).
        /// </param>
        /// <param name="physicalSectorsPerTrack">
        /// Physical sectors per track (second argument).
        /// </param>
        /// <param name="tracks">
        /// Number of tracks (third argument).
        /// </param>
        /// <param name="blockSize">
        /// Allocation block size in bytes (fourth argument).
        /// </param>
        /// <param name="directoryEntries">
        /// Number of directory entries (fifth argument).
        /// </param>
        /// <param name="reservedTracks">
        /// Track offset OFF (sixth argument).
        /// </param>
        /// <returns>
        /// The parameters the macro would put in the BIOS.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when 1 KB blocks are used on a disk with more than 256 blocks, which the macro
        /// rejects with an error.
        /// </exception>
        public static CpmDiskParameters FromCpm3Macro(
            int physicalSectorSize, int physicalSectorsPerTrack, int tracks, int blockSize, int directoryEntries, int reservedTracks)
        {
            int recordsPerTrack = physicalSectorsPerTrack * (physicalSectorSize / 128);
            int size = (tracks - reservedTracks) * recordsPerTrack;
            int dsm = size / (blockSize / 128) - 1;
            int exm = blockSize / 1024;
            if (dsm > 255)
            {
                if (blockSize == 1024)
                {
                    throw new ArgumentException("CP/M does not allow 1 KB blocks on a disk with more than 256 blocks", nameof(blockSize));
                }

                exm /= 2;
            }

            exm -= 1;
            return new CpmDiskParameters(blockSize, dsm + 1, directoryEntries, exm, reservedTracks);
        }
    }
}
