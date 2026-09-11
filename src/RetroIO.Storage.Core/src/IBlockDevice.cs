//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Generic block device interface for retro computer disk images.
    /// Provides low-level block I/O abstraction independent of specific platform.
    /// Analogous to Linux kernel's block device layer.
    /// </summary>
    /// <remarks>
    /// This interface abstracts physical disk geometry and block-level access,
    /// allowing filesystems (IBlockFileSystem) to operate on any block device
    /// regardless of whether it's Commodore, Amiga, Dragon, Apple II, BBC Micro, etc.
    ///
    /// Supports multiple addressing modes:
    /// - LBA (Logical Block Address): Sequential block numbers (modern)
    /// - Track/Sector: Track and sector coordinates (Commodore, Apple II, BBC Micro)
    /// - Cylinder/Head/Sector: Full CHS addressing (Amiga, Dragon, hard drives)
    ///
    /// Design inspired by:
    /// - Linux VFS block layer (struct gendisk, struct bio)
    /// - SCSI block device model
    /// - ATA/ATAPI block addressing
    /// </remarks>
    public interface IBlockDevice : IDisposable
    {
        /// <summary>
        /// Block size in bytes (typically 256 or 512 for retro systems).
        /// </summary>
        int BlockSize { get; }

        /// <summary>
        /// Total number of blocks on this device.
        /// For LBA addressing, this is the maximum LBA + 1.
        /// For Track/Sector or CHS, this is the total calculated blocks.
        /// </summary>
        uint TotalBlocks { get; }

        /// <summary>
        /// True if the device is mounted read-only.
        /// </summary>
        bool IsReadOnly { get; }

        /// <summary>
        /// True if the device is mounted and ready for I/O operations.
        /// </summary>
        bool IsMounted { get; }

        /// <summary>
        /// True if this device supports variable geometry (e.g., different sectors per track).
        /// Examples: Commodore D64 (ZBR), some hard drive formats.
        /// False for uniform geometry (e.g., Dragon VDK, Amiga ADF).
        /// </summary>
        bool SupportsVariableGeometry { get; }

        /// <summary>
        /// Mounts a disk image from the provided stream.
        /// </summary>
        /// <param name="imageStream">Stream containing the disk image data</param>
        /// <param name="readOnly">True to mount read-only, false for read-write</param>
        /// <exception cref="ArgumentNullException">imageStream is null</exception>
        /// <exception cref="InvalidDataException">Image format is invalid or corrupted</exception>
        void Mount(Stream imageStream, bool readOnly);

        /// <summary>
        /// Reads a block from the device using the specified addressing mode.
        /// </summary>
        /// <param name="address">Block address (LBA, Track/Sector, or CHS)</param>
        /// <param name="buffer">Destination buffer (must be at least BlockSize bytes)</param>
        /// <returns>Number of bytes read (typically BlockSize)</returns>
        /// <exception cref="InvalidOperationException">Device not mounted</exception>
        /// <exception cref="ArgumentException">Invalid address or buffer too small</exception>
        int ReadBlock(BlockAddress address, Span<byte> buffer);

        /// <summary>
        /// Writes a block to the device using the specified addressing mode.
        /// </summary>
        /// <param name="address">Block address (LBA, Track/Sector, or CHS)</param>
        /// <param name="data">Source data (must be exactly BlockSize bytes)</param>
        /// <exception cref="InvalidOperationException">Device not mounted or is read-only</exception>
        /// <exception cref="ArgumentException">Invalid address or data size incorrect</exception>
        void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data);

        /// <summary>
        /// Gets block device information for emulator integration and metadata.
        /// </summary>
        /// <returns>Block device information structure</returns>
        /// <exception cref="InvalidOperationException">Device not mounted</exception>
        BlockDeviceInfo GetBlockDeviceInfo();

        /// <summary>
        /// Gets the number of blocks per track for variable geometry devices.
        /// For uniform geometry, returns the same value for all tracks.
        /// </summary>
        /// <param name="track">Track number (1-based or 0-based depending on device)</param>
        /// <returns>Number of blocks (sectors) on the specified track</returns>
        /// <exception cref="InvalidOperationException">Device not mounted</exception>
        /// <exception cref="ArgumentOutOfRangeException">Invalid track number</exception>
        int GetBlocksPerTrack(int track);

        /// <summary>
        /// Flushes any pending changes to the underlying storage.
        /// For memory-mapped devices, writes the in-memory buffer back to the stream.
        /// </summary>
        /// <exception cref="InvalidOperationException">Device not mounted or is read-only</exception>
        void Flush();
    }
}
