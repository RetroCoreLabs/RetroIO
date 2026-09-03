//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.BlockDevices
{
    /// <summary>
    /// Abstract base class for all block device implementations.
    /// Provides common functionality and helper methods for addressing mode conversions.
    /// Supports LBA, Track/Sector, and Cylinder/Head/Sector addressing.
    /// </summary>
    /// <remarks>
    /// <para><strong>ADDRESSING MODES EXPLAINED:</strong></para>
    ///
    /// <para><strong>1. LBA (Logical Block Address)</strong></para>
    /// <list type="bullet">
    /// <item><description>Sequential block numbering: 0, 1, 2, 3...</description></item>
    /// <item><description>Modern, simple, device-independent</description></item>
    /// <item><description>Used by: Modern hard drives, USB drives, ProDOS (Apple II)</description></item>
    /// <item><description>Best for: Devices with uniform, simple geometry</description></item>
    /// <item><description>Example: Block 0, Block 1, Block 2...</description></item>
    /// </list>
    ///
    /// <para><strong>2. Track/Sector</strong></para>
    /// <list type="bullet">
    /// <item><description>Two-dimensional addressing: Track (cylinder) + Sector within track</description></item>
    /// <item><description>Reflects single-sided or logically-organized floppy disks</description></item>
    /// <item><description>Sectors-per-track may VARY (Zone Bit Recording - ZBR)</description></item>
    /// <item><description>Used by: Commodore 1541 (D64), Apple II DOS 3.3, BBC Micro DFS</description></item>
    /// <item><description>Best for: Devices with variable sectors-per-track</description></item>
    /// <item><description>Example: Track 18, Sector 0 (Commodore directory track)</description></item>
    /// <item><description>Note: Track numbering is usually 1-indexed, Sector is 0-indexed</description></item>
    /// </list>
    ///
    /// <para><strong>3. CHS (Cylinder/Head/Sector)</strong></para>
    /// <list type="bullet">
    /// <item><description>Three-dimensional addressing: Cylinder + Head (side) + Sector</description></item>
    /// <item><description>Reflects physical geometry of multi-sided disks</description></item>
    /// <item><description>Sectors-per-track is UNIFORM across all cylinders/heads</description></item>
    /// <item><description>Used by: Amiga ADF, Dragon/CoCo VDK, PC floppy disks, hard drives</description></item>
    /// <item><description>Best for: Multi-sided disks with uniform geometry</description></item>
    /// <item><description>Example: Cylinder 0, Head 0, Sector 1 (Amiga boot block)</description></item>
    /// <item><description>Formula: LBA = (Cylinder × Heads + Head) × SectorsPerTrack + Sector</description></item>
    /// </list>
    ///
    /// <para><strong>WHEN TO USE WHICH:</strong></para>
    /// <list type="table">
    /// <listheader>
    /// <term>Device Type</term>
    /// <description>Recommended Mode</description>
    /// </listheader>
    /// <item>
    /// <term>Commodore D64/D71/D81</term>
    /// <description>Track/Sector (D64 has ZBR: 21/19/18/17 sectors per track)</description>
    /// </item>
    /// <item>
    /// <term>Amiga ADF</term>
    /// <description>CHS (80 cylinders × 2 heads × 11 sectors = uniform)</description>
    /// </item>
    /// <item>
    /// <term>Dragon/CoCo VDK</term>
    /// <description>CHS (40/80 cylinders × 1/2 heads × 18 sectors = uniform)</description>
    /// </item>
    /// <item>
    /// <term>Apple II ProDOS</term>
    /// <description>LBA (280 blocks, sequential)</description>
    /// </item>
    /// <item>
    /// <term>BBC Micro DFS</term>
    /// <description>Track/Sector (40/80 tracks × 10 sectors = uniform, but logically Track/Sector)</description>
    /// </item>
    /// </list>
    ///
    /// <para><strong>HOW TO IMPLEMENT YOUR BLOCK DEVICE:</strong></para>
    /// <list type="number">
    /// <item><description>Determine your device's native addressing (Track/Sector, CHS, or LBA)</description></item>
    /// <item><description>Implement ReadBlock/WriteBlock using native addressing</description></item>
    /// <item><description>Use helper methods to convert from other modes to your native mode</description></item>
    /// <item><description>Example: D64 native is Track/Sector, use LBAToTrackSector() for LBA requests</description></item>
    /// <item><description>Example: Amiga native is CHS, use LBAToCHS() for LBA requests</description></item>
    /// </list>
    ///
    /// <para><strong>HELPER METHODS PROVIDED:</strong></para>
    /// <list type="bullet">
    /// <item><description>LBAToTrackSector() / TrackSectorToLBA() - For variable geometry devices</description></item>
    /// <item><description>LBAToCHS() / CHSToLBA() - For uniform geometry multi-sided devices</description></item>
    /// <item><description>GetTrackSectorOffset() - Calculate byte offset for Track/Sector with ZBR</description></item>
    /// <item><description>GetCHSOffset() - Calculate byte offset for CHS with uniform geometry</description></item>
    /// </list>
    /// </remarks>
    public abstract class BlockDeviceBase : IBlockDevice
    {
        protected byte[]? _imageData;
        protected bool _readOnly;
        protected Stream? _imageStream;

        /// <summary>
        /// Gets whether the device is mounted and ready (IBlockDevice interface).
        /// </summary>
        public bool IsMounted => _imageData != null;

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

            // Read entire image into memory
            long length = imageStream.Length;
            _imageData = new byte[length];
            imageStream.Position = 0;
            imageStream.ReadExactly(_imageData, 0, (int)length);

            // Validate geometry (device-specific)
            ValidateGeometry(length);
        }

        /// <summary>
        /// Validates that the image size matches the expected geometry.
        /// Must be implemented by concrete device.
        /// </summary>
        /// <param name="imageSize">Size of the disk image in bytes</param>
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
        /// Flushes any pending changes to the underlying stream.
        /// </summary>
        public virtual void Flush()
        {
            if (_imageData == null || _imageStream == null)
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

            _imageStream.Position = 0;
            _imageStream.Write(_imageData, 0, _imageData.Length);
            _imageStream.Flush();
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

            _imageData = null;
            _imageStream = null;
        }

        // ===================================================================
        // HELPER METHODS - LBA ↔ Track/Sector Conversion
        // ===================================================================

        /// <summary>
        /// Converts LBA (logical block address) to Track/Sector addressing.
        /// Used by devices with variable sectors-per-track (e.g., D64 with ZBR).
        /// </summary>
        /// <param name="lba">Logical block address</param>
        /// <param name="sectorsPerTrack">Array of sectors per track (1-indexed)</param>
        /// <returns>Track and sector coordinates</returns>
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
        /// <param name="track">Track number (1-indexed)</param>
        /// <param name="sector">Sector number (0-indexed)</param>
        /// <param name="sectorsPerTrack">Array of sectors per track (1-indexed)</param>
        /// <returns>Logical block address</returns>
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
        /// <param name="track">Track number (1-indexed)</param>
        /// <param name="sector">Sector number (0-indexed)</param>
        /// <param name="sectorsPerTrack">Array of sectors per track (1-indexed)</param>
        /// <param name="blockSize">Size of each block in bytes</param>
        /// <returns>Byte offset within the image</returns>
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
        /// <param name="lba">Logical block address</param>
        /// <param name="headsPerCylinder">Number of heads (sides) per cylinder</param>
        /// <param name="sectorsPerTrack">Number of sectors per track (uniform)</param>
        /// <returns>Cylinder, head, and sector coordinates</returns>
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
        /// <param name="cylinder">Cylinder number</param>
        /// <param name="head">Head (side) number</param>
        /// <param name="sector">Sector number</param>
        /// <param name="headsPerCylinder">Number of heads per cylinder</param>
        /// <param name="sectorsPerTrack">Number of sectors per track</param>
        /// <returns>Logical block address</returns>
        protected uint CHSToLBA(ushort cylinder, byte head, byte sector, byte headsPerCylinder, byte sectorsPerTrack)
        {
            return (uint)((cylinder * headsPerCylinder + head) * sectorsPerTrack + sector);
        }

        /// <summary>
        /// Calculates byte offset for CHS addressing with uniform geometry.
        /// Used by devices like Amiga ADF, Dragon VDK where geometry is constant.
        /// </summary>
        /// <param name="cylinder">Cylinder number</param>
        /// <param name="head">Head (side) number</param>
        /// <param name="sector">Sector number</param>
        /// <param name="headsPerCylinder">Number of heads per cylinder</param>
        /// <param name="sectorsPerTrack">Number of sectors per track</param>
        /// <param name="blockSize">Size of each block in bytes</param>
        /// <returns>Byte offset within the image</returns>
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
        /// <exception cref="InvalidOperationException">Device not mounted</exception>
        protected void EnsureMounted()
        {
            if (!IsMounted)
            {
                throw new InvalidOperationException("Device not mounted");
            }
        }

        /// <summary>
        /// Gets the raw image data.
        /// </summary>
        protected byte[]? GetImageData() => _imageData;

        /// <summary>
        /// Gets the total size of the image in bytes.
        /// </summary>
        protected long GetImageSize() => _imageData?.Length ?? 0;
    }
}
