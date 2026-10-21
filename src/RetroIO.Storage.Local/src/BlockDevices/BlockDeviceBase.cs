//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Streams;

namespace RetroIO.Storage.Local.BlockDevices
{
    /// <summary>
    /// Abstract base class for all block device implementations.
    /// Provides common functionality and helper methods for addressing mode conversions.
    /// Supports LBA, Track/Sector, and Cylinder/Head/Sector addressing.
    /// </summary>
    /// <remarks>
    /// <para><b>ADDRESSING MODES EXPLAINED:</b></para>
    ///
    /// <para><b>1. LBA (Logical Block Address)</b></para>
    ///  - Sequential block numbering: 0, 1, 2, 3...
    ///  - Modern, simple, device-independent
    ///  - Used by: Modern hard drives, USB drives, ProDOS (Apple II)
    ///  - Best for: Devices with uniform, simple geometry
    ///  - Example: Block 0, Block 1, Block 2...
    ///
    /// <para><b>2. Track/Sector</b></para>
    ///  - Two-dimensional addressing: Track (cylinder) + Sector within track
    ///  - Reflects single-sided or logically-organized floppy disks
    ///  - Sectors-per-track may VARY (Zone Bit Recording - ZBR)
    ///  - Used by: Commodore 1541 (D64), Apple II DOS 3.3, BBC Micro DFS
    ///  - Best for: Devices with variable sectors-per-track
    ///  - Example: Track 18, Sector 0 (Commodore directory track)
    ///  - Note: Track numbering is usually 1-indexed, Sector is 0-indexed
    ///
    /// <para><b>3. CHS (Cylinder/Head/Sector)</b></para>
    ///  - Three-dimensional addressing: Cylinder + Head (side) + Sector
    ///  - Reflects physical geometry of multi-sided disks
    ///  - Sectors-per-track is UNIFORM across all cylinders/heads
    ///  - Used by: Amiga ADF, Dragon/CoCo VDK, PC floppy disks, hard drives
    ///  - Best for: Multi-sided disks with uniform geometry
    ///  - Example: Cylinder 0, Head 0, Sector 1 (Amiga boot block)
    ///  - Formula: LBA = (Cylinder × Heads + Head) × SectorsPerTrack + Sector
    ///
    /// <para><b>WHEN TO USE WHICH:</b></para>
    /// Each entry is a device type, then the recommended mode:
    ///  - Commodore D64/D71/D81: Track/Sector (D64 has ZBR: 21/19/18/17 sectors per track)
    ///  - Amiga ADF: CHS (80 cylinders × 2 heads × 11 sectors = uniform)
    ///  - Dragon/CoCo VDK: CHS (40/80 cylinders × 1/2 heads × 18 sectors = uniform)
    ///  - Apple II ProDOS: LBA (280 blocks, sequential)
    ///  - BBC Micro DFS: Track/Sector (40/80 tracks × 10 sectors = uniform, but logically Track/Sector)
    ///
    /// <para><b>HOW TO IMPLEMENT YOUR BLOCK DEVICE:</b></para>
    ///  - Determine your device's native addressing (Track/Sector, CHS, or LBA)
    ///  - Implement ReadBlock/WriteBlock using native addressing
    ///  - Use helper methods to convert from other modes to your native mode
    ///  - Example: D64 native is Track/Sector, use LBAToTrackSector() for LBA requests
    ///  - Example: Amiga native is CHS, use LBAToCHS() for LBA requests
    ///
    /// <para><b>HELPER METHODS PROVIDED:</b></para>
    ///  - LBAToTrackSector() / TrackSectorToLBA() - For variable geometry devices
    ///  - LBAToCHS() / CHSToLBA() - For uniform geometry multi-sided devices
    ///  - GetTrackSectorOffset() - Calculate byte offset for Track/Sector with ZBR
    ///  - GetCHSOffset() - Calculate byte offset for CHS with uniform geometry
    ///
    /// <para><b>HOW THE IMAGE IS HELD AND WRITTEN BACK:</b></para>
    ///  - The image is held in pages of 4096 bytes. Mount reads the bytes the stream really holds; for a <c>PaddedSubStream</c> that is its
    ///    real length, so the zero padding behind a short file is never read or held.
    ///  - A page that holds no byte of the file and was never written is not held; reading it gives zero bytes.
    ///  - Every block written is marked changed. Flush writes only the changed blocks, in rising order, and then forgets the marks. A block
    ///    behind the end of a short file therefore grows the file only as far as that block (the <c>PaddedSubStream</c> fills the gap with
    ///    zero bytes first), instead of making the file as long as the whole volume.
    /// </remarks>
    public abstract class BlockDeviceBase : IBlockDevice
    {
        // Size of one page of the image in memory: 4096 bytes, so 16 sectors of 256 bytes or 8 blocks of 512.
        private const int PageShift = 12;
        private const int PageSize = 1 << PageShift;

        // The image in pages of PageSize bytes, or null when not mounted. A null page holds only zero bytes.
        private byte[]?[]? _pages;

        // Length of the image as the stream shows it (for a PaddedSubStream, including the padding).
        private long _imageLength;

        // One bit per block: set when the block was written since the last flush.
        private ulong[]? _changed;

        /// <summary>
        /// True when the device was mounted read-only.
        /// </summary>
        protected bool _readOnly;
        /// <summary>
        /// The stream the image was loaded from and is written back to, or null when not mounted.
        /// </summary>
        protected Stream? _imageStream;

        /// <summary>
        /// Gets whether the device is mounted and ready (IBlockDevice interface).
        /// </summary>
        public bool IsMounted => _pages != null;

        /// <summary>
        /// Gets the number of bytes of the image this device holds in memory: whole pages of 4096 bytes for the part of the file that was
        /// read at mount and for every page written since.
        /// </summary>
        public long HeldBytes
        {
            get
            {
                if (_pages == null)
                {
                    return 0;
                }

                long held = 0;
                for (int i = 0; i < _pages.Length; i++)
                {
                    if (_pages[i] != null)
                    {
                        held += PageSize;
                    }
                }

                return held;
            }
        }

        /// <summary>
        /// Gets whether the device is read-only (IBlockDevice interface).
        /// </summary>
        public bool IsReadOnly => _readOnly;

        /// <summary>
        /// Block size in bytes (IBlockDevice interface).
        /// Must be implemented by concrete device.
        /// </summary>
        public abstract int BlockSize { get; }

        /// <summary>
        /// Total number of blocks on this device (IBlockDevice interface).
        /// Must be implemented by concrete device.
        /// </summary>
        public abstract uint TotalBlocks { get; }

        /// <summary>
        /// True if this device supports variable geometry (IBlockDevice interface).
        /// Must be implemented by concrete device.
        /// </summary>
        public abstract bool SupportsVariableGeometry { get; }

        /// <summary>
        /// Mounts a disk image from the provided stream.
        /// </summary>
        public virtual void Mount(Stream imageStream, bool readOnly)
        {
            if (imageStream == null)
            {
                throw new ArgumentNullException(nameof(imageStream));
            }

            _readOnly = readOnly;
            _imageStream = imageStream; // Keep reference for writing back

            // Read the part of the image the file really holds into memory. A PaddedSubStream shows zero bytes behind its real length;
            // those are not read, so a short file of a large volume costs only the memory of the file.
            long length = imageStream.Length;
            long stored = imageStream is PaddedSubStream padded ? padded.RealLength : length;
            byte[]?[] pages = new byte[]?[(length + PageSize - 1) >> PageShift];
            imageStream.Position = 0;
            for (long offset = 0; offset < stored; offset += PageSize)
            {
                byte[] page = new byte[PageSize];
                imageStream.ReadExactly(page, 0, (int)Math.Min(PageSize, stored - offset));
                pages[offset >> PageShift] = page;
            }

            _imageLength = length;
            _pages = pages;
            long blocks = (length + BlockSize - 1) / BlockSize;
            _changed = new ulong[(blocks + 63) / 64];

            // Validate geometry (device-specific)
            ValidateGeometry(length);
        }

        /// <summary>
        /// Copies bytes of the image into a buffer. Bytes of a page that is not held read as zero.
        /// </summary>
        /// <param name="offset">
        /// Position in the image of the first byte.
        /// </param>
        /// <param name="destination">
        /// Receives the bytes; its length is the number of bytes copied.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the bytes reach outside the image.
        /// </exception>
        protected void ReadImage(long offset, Span<byte> destination)
        {
            EnsureMounted();
            CheckRange(offset, destination.Length);
            int done = 0;
            while (done < destination.Length)
            {
                long at = offset + done;
                int inPage = (int)(at & (PageSize - 1));
                int count = Math.Min(PageSize - inPage, destination.Length - done);
                byte[]? page = _pages![at >> PageShift];
                if (page == null)
                {
                    destination.Slice(done, count).Clear();
                }
                else
                {
                    page.AsSpan(inPage, count).CopyTo(destination.Slice(done, count));
                }

                done += count;
            }
        }

        /// <summary>
        /// Copies bytes into the image and marks every block they touch as changed, so the next flush writes those blocks.
        /// </summary>
        /// <param name="offset">
        /// Position in the image of the first byte.
        /// </param>
        /// <param name="source">
        /// The bytes to store.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the bytes reach outside the image.
        /// </exception>
        protected void WriteImage(long offset, ReadOnlySpan<byte> source)
        {
            EnsureMounted();
            CheckRange(offset, source.Length);
            if (source.Length == 0)
            {
                return;
            }

            int done = 0;
            while (done < source.Length)
            {
                long at = offset + done;
                int inPage = (int)(at & (PageSize - 1));
                int count = Math.Min(PageSize - inPage, source.Length - done);
                long index = at >> PageShift;
                byte[]? page = _pages![index];
                if (page == null)
                {
                    page = new byte[PageSize];
                    _pages[index] = page;
                }

                source.Slice(done, count).CopyTo(page.AsSpan(inPage, count));
                done += count;
            }

            long first = offset / BlockSize;
            long last = (offset + source.Length - 1) / BlockSize;
            for (long block = first; block <= last; block++)
            {
                _changed![block >> 6] |= 1UL << (int)(block & 63);
            }
        }

        // Refuses a range that does not lie inside the image.
        private void CheckRange(long offset, int count)
        {
            if (offset < 0 || offset + count > _imageLength)
            {
                throw new ArgumentOutOfRangeException(nameof(offset),
                    $"Bytes {offset} to {offset + count - 1} are outside the image of {_imageLength} bytes");
            }
        }

        /// <summary>
        /// Validates that the image size matches the expected geometry.
        /// Must be implemented by concrete device.
        /// </summary>
        /// <param name="imageSize">
        /// Size of the disk image in bytes
        /// </param>
        protected abstract void ValidateGeometry(long imageSize);

        /// <summary>
        /// Reads a block from the device (IBlockDevice interface).
        /// Must be implemented by concrete device.
        /// </summary>
        public abstract int ReadBlock(BlockAddress address, Span<byte> buffer);

        /// <summary>
        /// Writes a block to the device (IBlockDevice interface).
        /// Must be implemented by concrete device.
        /// </summary>
        public abstract void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data);

        /// <summary>
        /// Gets block device information (IBlockDevice interface).
        /// Must be implemented by concrete device.
        /// </summary>
        public abstract BlockDeviceInfo GetBlockDeviceInfo();

        /// <summary>
        /// Gets the number of blocks per track (IBlockDevice interface).
        /// Must be implemented by concrete device.
        /// </summary>
        public abstract int GetBlocksPerTrack(int track);

        /// <summary>
        /// Flushes any pending changes to the underlying stream: every block written since the last flush, and nothing else.
        /// </summary>
        /// <remarks>
        /// Runs of changed blocks are written in rising order, each run with one seek. A run behind the end of a short file (a
        /// <c>PaddedSubStream</c>) grows the file to the end of that run, and the stream fills any gap before it with zero bytes. The part of
        /// the last block that lies past the end of the image (the image is not a whole number of blocks) is not written.
        /// </remarks>
        public virtual void Flush()
        {
            if (_pages == null || _imageStream == null || _changed == null)
            {
                return; // Not mounted, nothing to flush
            }

            if (_readOnly)
            {
                return; // Read-only, nothing to flush
            }

            if (!_imageStream.CanWrite)
            {
                return; // Stream not writable
            }

            long blocks = (_imageLength + BlockSize - 1) / BlockSize;
            long block = 0;
            while (block < blocks)
            {
                ulong word = _changed[block >> 6] >> (int)(block & 63);
                if (word == 0)
                {
                    // Nothing changed in the rest of this word: go to the start of the next one.
                    block = ((block >> 6) + 1) << 6;
                    continue;
                }

                block += System.Numerics.BitOperations.TrailingZeroCount(word);
                long end = block;
                while (end < blocks && (_changed[end >> 6] & (1UL << (int)(end & 63))) != 0)
                {
                    _changed[end >> 6] &= ~(1UL << (int)(end & 63));
                    end++;
                }

                WriteRun(block * BlockSize, Math.Min(end * BlockSize, _imageLength));
                block = end;
            }

            _imageStream.Flush();
        }

        // Writes the bytes from start up to end (not included) from the pages to the stream. Every page in the range is held, because a
        // block is only marked changed after its bytes were stored.
        private void WriteRun(long start, long end)
        {
            _imageStream!.Position = start;
            long at = start;
            while (at < end)
            {
                int inPage = (int)(at & (PageSize - 1));
                int count = (int)Math.Min(PageSize - inPage, end - at);
                byte[]? page = _pages![at >> PageShift];
                if (page == null)
                {
                    throw new InvalidOperationException($"Block at byte {at} is marked changed but its page is not held");
                }

                _imageStream.Write(page, inPage, count);
                at += count;
            }
        }

        /// <summary>
        /// Disposes the device and releases resources.
        /// Writes any changes back to the stream if not read-only.
        /// </summary>
        public virtual void Dispose()
        {
            // Flush any pending changes
            Flush();

            // Close the underlying stream. After Mount() this device OWNS the stream (the caller
            // hands it over and relies on the driver lifecycle to close it). Not disposing it here
            // leaked the file handle: the image stayed locked after unmount, so a subsequent
            // read-write remount could not acquire write access and silently fell back to read-only.
            // FileStream/MemoryStream.Dispose is idempotent, so a double-close by the caller is safe.
            _imageStream?.Dispose();

            _pages = null;
            _changed = null;
            _imageStream = null;
        }

        // ===================================================================
        // HELPER METHODS - LBA ↔ Track/Sector Conversion
        // ===================================================================

        /// <summary>
        /// Converts LBA (logical block address) to Track/Sector addressing.
        /// Used by devices with variable sectors-per-track (e.g., D64 with ZBR).
        /// </summary>
        /// <param name="lba">
        /// Logical block address
        /// </param>
        /// <param name="sectorsPerTrack">
        /// Array of sectors per track (1-indexed)
        /// </param>
        /// <returns>
        /// Track and sector coordinates
        /// </returns>
        protected (byte track, byte sector) LBAToTrackSector(uint lba, int[] sectorsPerTrack)
        {
            uint currentLBA = 0;

            for (int track = 1; track < sectorsPerTrack.Length; track++)
            {
                int sectorsInTrack = sectorsPerTrack[track];

                if (lba < currentLBA + sectorsInTrack)
                {
                    byte sector = (byte)(lba - currentLBA);
                    return ((byte)track, sector);
                }

                currentLBA += (uint)sectorsInTrack;
            }

            throw new ArgumentOutOfRangeException(nameof(lba), $"LBA {lba} out of range (max: {currentLBA - 1})");
        }

        /// <summary>
        /// Converts Track/Sector addressing to LBA.
        /// Used by devices with variable sectors-per-track (e.g., D64 with ZBR).
        /// </summary>
        /// <param name="track">
        /// Track number (1-indexed)
        /// </param>
        /// <param name="sector">
        /// Sector number (0-indexed)
        /// </param>
        /// <param name="sectorsPerTrack">
        /// Array of sectors per track (1-indexed)
        /// </param>
        /// <returns>
        /// Logical block address
        /// </returns>
        protected uint TrackSectorToLBA(byte track, byte sector, int[] sectorsPerTrack)
        {
            uint lba = 0;

            for (int t = 1; t < track; t++)
            {
                lba += (uint)sectorsPerTrack[t];
            }

            return lba + sector;
        }

        /// <summary>
        /// Calculates byte offset for Track/Sector addressing with variable geometry.
        /// Used by devices like D64 where sectors-per-track varies (Zone Bit Recording).
        /// </summary>
        /// <param name="track">
        /// Track number (1-indexed)
        /// </param>
        /// <param name="sector">
        /// Sector number (0-indexed)
        /// </param>
        /// <param name="sectorsPerTrack">
        /// Array of sectors per track (1-indexed)
        /// </param>
        /// <param name="blockSize">
        /// Size of each block in bytes
        /// </param>
        /// <returns>
        /// Byte offset within the image
        /// </returns>
        protected int GetTrackSectorOffset(byte track, byte sector, int[] sectorsPerTrack, int blockSize)
        {
            int offset = 0;

            // Sum sectors for all tracks before this one
            for (int t = 1; t < track; t++)
            {
                offset += sectorsPerTrack[t] * blockSize;
            }

            // Add sectors within this track
            offset += sector * blockSize;

            return offset;
        }

        // ===================================================================
        // HELPER METHODS - LBA ↔ CHS (Cylinder/Head/Sector) Conversion
        // ===================================================================

        /// <summary>
        /// Converts LBA to Cylinder/Head/Sector (CHS) addressing.
        /// Used by devices with uniform geometry (e.g., Amiga ADF, Dragon VDK).
        /// </summary>
        /// <param name="lba">
        /// Logical block address
        /// </param>
        /// <param name="headsPerCylinder">
        /// Number of heads (sides) per cylinder
        /// </param>
        /// <param name="sectorsPerTrack">
        /// Number of sectors per track (uniform)
        /// </param>
        /// <returns>
        /// Cylinder, head, and sector coordinates
        /// </returns>
        protected (ushort cylinder, byte head, byte sector) LBAToCHS(uint lba, byte headsPerCylinder, byte sectorsPerTrack)
        {
            uint sectorsPerCylinder = (uint)(headsPerCylinder * sectorsPerTrack);
            ushort cylinder = (ushort)(lba / sectorsPerCylinder);
            uint remainder = lba % sectorsPerCylinder;
            byte head = (byte)(remainder / sectorsPerTrack);
            byte sector = (byte)(remainder % sectorsPerTrack);

            return (cylinder, head, sector);
        }

        /// <summary>
        /// Converts Cylinder/Head/Sector (CHS) addressing to LBA.
        /// Used by devices with uniform geometry (e.g., Amiga ADF, Dragon VDK).
        /// </summary>
        /// <param name="cylinder">
        /// Cylinder number
        /// </param>
        /// <param name="head">
        /// Head (side) number
        /// </param>
        /// <param name="sector">
        /// Sector number
        /// </param>
        /// <param name="headsPerCylinder">
        /// Number of heads per cylinder
        /// </param>
        /// <param name="sectorsPerTrack">
        /// Number of sectors per track
        /// </param>
        /// <returns>
        /// Logical block address
        /// </returns>
        protected uint CHSToLBA(ushort cylinder, byte head, byte sector, byte headsPerCylinder, byte sectorsPerTrack)
        {
            return (uint)((cylinder * headsPerCylinder + head) * sectorsPerTrack + sector);
        }

        /// <summary>
        /// Calculates byte offset for CHS addressing with uniform geometry.
        /// Used by devices like Amiga ADF, Dragon VDK where geometry is constant.
        /// </summary>
        /// <param name="cylinder">
        /// Cylinder number
        /// </param>
        /// <param name="head">
        /// Head (side) number
        /// </param>
        /// <param name="sector">
        /// Sector number
        /// </param>
        /// <param name="headsPerCylinder">
        /// Number of heads per cylinder
        /// </param>
        /// <param name="sectorsPerTrack">
        /// Number of sectors per track
        /// </param>
        /// <param name="blockSize">
        /// Size of each block in bytes
        /// </param>
        /// <returns>
        /// Byte offset within the image
        /// </returns>
        protected int GetCHSOffset(ushort cylinder, byte head, byte sector, byte headsPerCylinder, byte sectorsPerTrack, int blockSize)
        {
            uint lba = CHSToLBA(cylinder, head, sector, headsPerCylinder, sectorsPerTrack);
            return (int)(lba * blockSize);
        }

        // ===================================================================
        // HELPER METHODS - Utilities
        // ===================================================================

        /// <summary>
        /// Ensures the device is mounted before performing operations.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Device not mounted
        /// </exception>
        protected void EnsureMounted()
        {
            if (!IsMounted)
            {
                throw new InvalidOperationException("Device not mounted");
            }
        }

        /// <summary>
        /// Gets the total size of the image in bytes, as the stream showed it at mount (for a <c>PaddedSubStream</c>, including the padding).
        /// </summary>
        protected long GetImageSize() => _pages == null ? 0 : _imageLength;
    }
}
