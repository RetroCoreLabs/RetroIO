//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Buffers;
using System.IO;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FFS.Structures;

namespace RetroIO.Storage.Local.FFS.Core
{
    /// <summary>
    /// Locates the FFS/UFS primary superblock inside a raw disk image whose filesystem
    /// does NOT begin at byte 0.
    ///
    /// WHY THIS EXISTS
    /// ---------------
    /// The FFS superblock always sits at SBLOCK_OFFSET (8192 bytes) from the start of the
    /// *filesystem*, not from the start of the *image*. Normally a partition table (BSD
    /// disklabel, Sun VTOC, MBR) tells us where the filesystem starts. But some images are
    /// flat raw dumps with no partition table at all, and yet still carry a non-zero gap
    /// in front of the filesystem.
    ///
    /// Concrete example — NDIX (Norsk Data UNIX) on a di70 pack:
    /// The NDIX disk driver (kernel/MASTER/io/di.c) maps NDIX logical block 0 onto a
    /// physical sector one whole cylinder in:
    ///
    ///     else
    ///         // map logical block 0
    ///         blkzero[DIUNIT(dev)] = dist[i].nspc;
    ///
    /// and every transfer then computes:
    ///
    ///     daddr = bp->b_blkno + blkzero[...] + (cyloff * st->nspc);
    ///
    /// so blkzero = nspc = nsect * ntrak = 18 * 5 = 90 sectors for a di70. Physical
    /// cylinder 0 is skipped because on a native Norsk Data pack that cylinder belongs to
    /// SINTRAN — disk label, boot info and the bad-track/maintenance area — and NDIX must
    /// not clobber it. (The SCSI branch of the same code sets blkzero = 0, because a SCSI
    /// disk has no such ND reserved cylinder, so that case needs no skip.)
    ///
    /// Because the gap is derived from the disk GEOMETRY (nsect * ntrak) it differs per ND
    /// disk type. There is therefore no constant we could hardcode, and no on-disk table to
    /// read it from — which is precisely why we scan for the superblock instead.
    ///
    /// HOW THE SCAN WORKS
    /// ------------------
    /// A filesystem based at byte B puts its magic at B + SBLOCK_OFFSET + MAGIC_FIELD_OFFSET
    /// (= B + 8192 + 1372 = B + 9564). We only consider block-aligned bases, so we test
    /// B = 0, 512, 1024, ... and check 4 bytes at each corresponding magic position. A hit is
    /// only accepted once the full superblock parses, self-validates, and the filesystem it
    /// describes actually fits inside the image.
    /// </summary>
    public static class FfsSuperblockLocator
    {
        /// <summary>
        /// Byte offset of the fs_magic field within the FFS superblock structure.
        /// </summary>
        public const int MAGIC_FIELD_OFFSET = 1372;

        /// <summary>
        /// Distance from the filesystem base to the fs_magic field
        /// (superblock at +8192, magic at +1372 within it).
        /// </summary>
        public const int MAGIC_FROM_BASE = FfsConstants.SBLOCK_OFFSET + MAGIC_FIELD_OFFSET;

        /// <summary>
        /// Granularity of candidate filesystem bases. Bases are assumed to be
        /// 512-byte-sector aligned, which holds for every FFS image we know of
        /// (the NDIX di70 gap of 90 * 1024 bytes is 512-aligned too).
        /// </summary>
        public const int BASE_ALIGNMENT = 512;

        /// <summary>
        /// Default limit on how far into the image we look for a filesystem base.
        /// 1 MB comfortably covers any realistic pre-filesystem gap (the NDIX di70 gap is
        /// 90 KB) while keeping the scan cheap enough to run during driver detection.
        /// </summary>
        public const long DEFAULT_MAX_SCAN_BYTES = 1024 * 1024;

        /// <summary>
        /// Reads <paramref name="count"/> bytes at absolute byte <paramref name="offset"/>.
        /// Returns false if the range is not fully readable.
        /// </summary>
        private delegate bool ReadAtFunc(long offset, byte[] buffer, int count);

        /// <summary>
        /// Scans a seekable stream for an FFS filesystem base.
        /// </summary>
        /// <param name="stream">Image stream. Must be seekable. Position is not preserved.</param>
        /// <param name="baseOffsetBytes">On success, the byte offset at which the filesystem begins.</param>
        /// <param name="superblock">On success, the parsed and validated superblock.</param>
        /// <param name="maxScanBytes">How far to scan. Defaults to <see cref="DEFAULT_MAX_SCAN_BYTES"/>.</param>
        /// <returns>True if a valid filesystem base was found.</returns>
        public static bool TryLocate(
            Stream stream,
            out long baseOffsetBytes,
            out FfsSuperblock? superblock,
            long maxScanBytes = DEFAULT_MAX_SCAN_BYTES)
        {
            baseOffsetBytes = -1;
            superblock = null;

            if (stream == null || !stream.CanRead || !stream.CanSeek)
            {
                return false;
            }

            long length = stream.Length;

            ReadAtFunc reader = (long offset, byte[] buffer, int count) =>
            {
                if (offset < 0 || offset + count > length)
                {
                    return false;
                }

                stream.Position = offset;

                // ReadExactly throws on short read; the caller treats any failure as "no match".
                try
                {
                    stream.ReadExactly(buffer, 0, count);
                    return true;
                }
                catch (EndOfStreamException)
                {
                    return false;
                }
            };

            return Scan(reader, length, maxScanBytes, out baseOffsetBytes, out superblock);
        }

        /// <summary>
        /// Scans a block device for an FFS filesystem base.
        /// </summary>
        /// <param name="device">Block device to scan.</param>
        /// <param name="baseOffsetBytes">On success, the byte offset at which the filesystem begins.</param>
        /// <param name="superblock">On success, the parsed and validated superblock.</param>
        /// <param name="maxScanBytes">How far to scan. Defaults to <see cref="DEFAULT_MAX_SCAN_BYTES"/>.</param>
        /// <returns>True if a valid filesystem base was found.</returns>
        public static bool TryLocate(
            IBlockDevice device,
            out long baseOffsetBytes,
            out FfsSuperblock? superblock,
            long maxScanBytes = DEFAULT_MAX_SCAN_BYTES)
        {
            baseOffsetBytes = -1;
            superblock = null;

            if (device == null)
            {
                return false;
            }

            int blockSize = device.BlockSize;
            if (blockSize <= 0)
            {
                return false;
            }

            long length = (long)device.TotalBlocks * blockSize;

            ReadAtFunc reader = (long offset, byte[] buffer, int count) =>
            {
                if (offset < 0 || offset + count > length)
                {
                    return false;
                }

                // Reads are not necessarily block-aligned, so read the covering block range
                // into a scratch buffer and copy the requested window out of it.
                long firstBlock = offset / blockSize;
                long lastBlock = (offset + count - 1) / blockSize;
                int blockCount = (int)(lastBlock - firstBlock + 1);
                int scratchSize = blockCount * blockSize;

                byte[] scratch = ArrayPool<byte>.Shared.Rent(scratchSize);
                try
                {
                    for (int i = 0; i < blockCount; i++)
                    {
                        Span<byte> target = scratch.AsSpan(i * blockSize, blockSize);
                        int read = device.ReadBlock(BlockAddress.FromLBA((uint)(firstBlock + i)), target);
                        if (read < blockSize)
                        {
                            return false;
                        }
                    }

                    int within = (int)(offset - firstBlock * blockSize);
                    Array.Copy(scratch, within, buffer, 0, count);
                    return true;
                }
                catch
                {
                    // Any read failure simply means "no filesystem here".
                    return false;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(scratch);
                }
            };

            return Scan(reader, length, maxScanBytes, out baseOffsetBytes, out superblock);
        }

        /// <summary>
        /// Verifies that a filesystem starts at a specific, caller-supplied byte offset.
        /// Used by the explicit SetFilesystemOffset() path, where we must not guess.
        /// </summary>
        /// <param name="device">Block device to read.</param>
        /// <param name="baseOffsetBytes">Candidate filesystem base in bytes.</param>
        /// <param name="superblock">On success, the parsed and validated superblock.</param>
        /// <returns>True if a valid FFS superblock lives at that base.</returns>
        public static bool TryReadAtBase(IBlockDevice device, long baseOffsetBytes, out FfsSuperblock? superblock)
        {
            superblock = null;

            if (device == null || baseOffsetBytes < 0)
            {
                return false;
            }

            int blockSize = device.BlockSize;
            if (blockSize <= 0)
            {
                return false;
            }

            long length = (long)device.TotalBlocks * blockSize;

            ReadAtFunc reader = (long offset, byte[] buffer, int count) =>
            {
                if (offset < 0 || offset + count > length)
                {
                    return false;
                }

                long firstBlock = offset / blockSize;
                long lastBlock = (offset + count - 1) / blockSize;
                int blockCount = (int)(lastBlock - firstBlock + 1);
                int scratchSize = blockCount * blockSize;

                byte[] scratch = ArrayPool<byte>.Shared.Rent(scratchSize);
                try
                {
                    for (int i = 0; i < blockCount; i++)
                    {
                        Span<byte> target = scratch.AsSpan(i * blockSize, blockSize);
                        int read = device.ReadBlock(BlockAddress.FromLBA((uint)(firstBlock + i)), target);
                        if (read < blockSize)
                        {
                            return false;
                        }
                    }

                    int within = (int)(offset - firstBlock * blockSize);
                    Array.Copy(scratch, within, buffer, 0, count);
                    return true;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(scratch);
                }
            };

            return TryAcceptBase(reader, length, baseOffsetBytes, out superblock);
        }

        // =================================================================
        // CORE SCAN
        // =================================================================

        /// <summary>
        /// Walks aligned candidate bases looking for a superblock that validates.
        /// </summary>
        private static bool Scan(
            ReadAtFunc reader,
            long length,
            long maxScanBytes,
            out long baseOffsetBytes,
            out FfsSuperblock? superblock)
        {
            baseOffsetBytes = -1;
            superblock = null;

            // A filesystem needs at least its boot area plus a full superblock to exist at all.
            long minimumFilesystemBytes = FfsConstants.SBLOCK_OFFSET + FfsConstants.SBSIZE;
            if (length < minimumFilesystemBytes)
            {
                return false;
            }

            if (maxScanBytes < 0)
            {
                maxScanBytes = DEFAULT_MAX_SCAN_BYTES;
            }

            // Never scan past the point where a filesystem could no longer fit.
            long lastPossibleBase = length - minimumFilesystemBytes;
            long scanLimit = maxScanBytes < lastPossibleBase ? maxScanBytes : lastPossibleBase;

            // Read the whole scan window once rather than issuing a tiny read per candidate.
            // Window must extend far enough to hold the magic of the last candidate base.
            long windowLength = scanLimit + MAGIC_FROM_BASE + 4;
            if (windowLength > length)
            {
                windowLength = length;
            }

            if (windowLength > int.MaxValue)
            {
                windowLength = int.MaxValue;
            }

            int windowSize = (int)windowLength;
            byte[] window = ArrayPool<byte>.Shared.Rent(windowSize);
            try
            {
                if (!reader(0, window, windowSize))
                {
                    return false;
                }

                for (long candidateBase = 0; candidateBase <= scanLimit; candidateBase += BASE_ALIGNMENT)
                {
                    long magicPos = candidateBase + MAGIC_FROM_BASE;
                    if (magicPos + 4 > windowSize)
                    {
                        break;
                    }

                    int m = (int)magicPos;

                    // fs_magic is big-endian on every FFS image RetroFS supports.
                    uint magic = ((uint)window[m] << 24) |
                                 ((uint)window[m + 1] << 16) |
                                 ((uint)window[m + 2] << 8) |
                                 window[m + 3];

                    if (magic != FfsConstants.FS_MAGIC)
                    {
                        continue;
                    }

                    // Magic hit — now demand a superblock that parses, validates and fits.
                    if (TryAcceptBase(reader, length, candidateBase, out superblock))
                    {
                        baseOffsetBytes = candidateBase;
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(window);
            }
        }

        /// <summary>
        /// Parses the superblock for a candidate base and decides whether to accept it.
        /// Rejects a base whose filesystem would not fit inside the image, which is what
        /// stops a stray backup superblock (or random data) from being mistaken for the base.
        /// </summary>
        private static bool TryAcceptBase(
            ReadAtFunc reader,
            long length,
            long candidateBase,
            out FfsSuperblock? superblock)
        {
            superblock = null;

            byte[] sbBuffer = new byte[FfsConstants.SBSIZE];
            if (!reader(candidateBase + FfsConstants.SBLOCK_OFFSET, sbBuffer, FfsConstants.SBSIZE))
            {
                return false;
            }

            FfsSuperblock sb;
            try
            {
                sb = FfsSuperblock.Parse(sbBuffer);
            }
            catch
            {
                return false;
            }

            if (!sb.Validate())
            {
                return false;
            }

            // The filesystem must physically fit in the image from this base.
            // For the NDIX di70 image this is exact: 92160 + 7942 * 1024 == 8224768 == file size.
            long declaredBytes = (long)sb.fs_size * sb.fs_fsize;
            if (declaredBytes <= 0 || candidateBase + declaredBytes > length)
            {
                return false;
            }

            superblock = sb;
            return true;
        }
    }
}
