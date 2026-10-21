//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Text;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FileSystems;
using RetroIO.Storage.Local.Streams;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Driver for Dragon 32/64 VDK disk images with DragonDOS filesystem.
    /// Supports 40/80 tracks, single/double-sided formats.
    /// Thin wrapper that composes GenericBlockDevice + an OS-9 or DragonDOS file system.
    /// </summary>
    /// <remarks>
    /// <para><b>The VDK format</b></para>
    /// VDK is the disk image format of the PC-Dragon emulator. A header is followed by the raw sectors, with no gaps, marks or CRCs. The
    /// description below is the header layout as documented in the XRoar emulator from the PC-Dragon source, and it matches the real images
    /// in the test folder (6809TUT1.VDK and 6809EXPR.VDK with a 12 byte header, HTCHHKER.VDK and PLANETFALL.VDK with a 256 byte header).
    /// <para><b>Header</b></para>
    ///  - Bytes 0 and 1: the signature dk.
    ///  - Bytes 2 and 3: the length of the whole header, low byte first. 12 or more. The sectors start at this offset.
    ///  - Byte 4: the VDK version. Byte 5: the oldest version that can read the file (the real images have 10H in both).
    ///  - Byte 6: who made the file, P for PC-Dragon or X for XRoar (the 256 byte header images say X). Byte 7: that program's version.
    ///  - Byte 8: the number of cylinders. Byte 9: the number of heads (sides).
    ///  - Byte 10: flags. Bit 0 set means write protected.
    ///  - Byte 11: bits 0 to 2 are the compression flags, which must be zero because compressed images are not supported, and bits 3 to 7 are
    ///    the length of the disk name that may follow.
    ///  - After byte 11 comes the disk name (up to 31 bytes) and padding up to the header length. This reader skips them.
    /// <para><b>Sectors</b></para>
    /// The data is 256-byte sectors, always 18 per track, in the order cylinder, then head, then sector 1 to 18. That is the order of the
    /// logical sector numbers of both DragonDOS (which counts a two sided cylinder as one track of 36 sectors) and OS-9. The file is the header
    /// plus cylinders x heads x 18 x 256 bytes.
    /// <para><b>What this driver does with it</b></para>
    /// It reads the geometry from bytes 8 and 9 and the header length from bytes 2 and 3, mounts the sectors after the header, and picks the
    /// file system from the contents of the disk: OS-9 first (it has a strict signature in sector 0), otherwise DragonDOS. The DSK and JVC format
    /// is described in <see cref="DragonDskDriver"/>, and the DMK format in <see cref="DmkDiskImage"/>.
    /// </remarks>
    public sealed class DragonVdkDriver : IFileSystemDriver
    {
        private GenericBlockDevice? _blockDevice;
        private IBlockFileSystem? _filesystem;
        private BlockDeviceDescriptor? _descriptor;

        /// <summary>
        /// Gets the file system type of the mounted DragonDOS file system, or <c>DragonDOS</c> when nothing is mounted.
        /// </summary>
        public string FileSystemType => _filesystem?.FileSystemType ?? "DragonDOS";
        /// <summary>
        /// Gets the volume label of the mounted DragonDOS file system, or an empty string when nothing is mounted.
        /// </summary>
        public string VolumeLabel => _filesystem?.VolumeLabel ?? string.Empty;
        /// <summary>
        /// Gets <c>.</c>, the separator between file name and extension in DragonDOS.
        /// </summary>
        public string ExtensionSeparator => _filesystem?.ExtensionSeparator ?? ".";

        /// <summary>
        /// Gets the capability flags of this driver: read, directory listing, file types, volume label and block read.
        /// </summary>
        public DriverCapabilities Capabilities
        {
            get
            {
                return DriverCapabilities.Read |
                       DriverCapabilities.ListDirectory |
                       DriverCapabilities.FileTypes |
                       DriverCapabilities.VolumeLabel |
                       DriverCapabilities.BlockRead;
                // TODO: Add write capabilities when implemented
            }
        }

        /// <summary>
        /// Gets <c>true</c>, because this driver reads files.
        /// </summary>
        public bool CanRead => true;
        /// <summary>
        /// Gets false, because the driver does not write yet.
        /// </summary>
        public bool CanWrite => false; // TODO: Implement write operations
        /// <summary>
        /// Gets <c>true</c>, because this driver can read raw blocks of the image. Block writes are not implemented.
        /// </summary>
        public bool HasBlockAccess => true;

        /// <summary>
        /// Returns null, because this driver has no metadata descriptor yet.
        /// </summary>
        /// <returns>
        /// Always null.
        /// </returns>
        public IFileSystemMetadataDescriptor? GetMetadataDescriptor()
        {
            // TODO: Implement Dragon/CoCo VDK metadata descriptor
            return null;
        }

        /// <summary>
        /// Checks the VDK header, picks the disk geometry from it, mounts the data part of the image as a block device and initializes the DragonDOS file system.
        /// </summary>
        /// <param name="imageStream">
        /// The stream holding the disk image.
        /// </param>
        /// <param name="readOnly">
        /// True to mount read-only.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="imageStream"/> is null.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// Thrown when the file is too small, the signature is missing, the header size is under 12 bytes, the geometry is unknown or the data size does not fit the geometry.
        /// </exception>
        public void Mount(Stream imageStream, bool readOnly)
        {
            if (imageStream == null)
            {
                throw new ArgumentNullException(nameof(imageStream));
            }

            // 1. Verify VDK header
            byte[] header = new byte[12];
            imageStream.Position = 0;
            int bytesRead = imageStream.Read(header, 0, 12);
            if (bytesRead < 12)
            {
                throw new InvalidDataException("File too small to be a VDK image");
            }

            // Check signature
            if (header[0] != 0x64 || header[1] != 0x6B)
            {
                throw new InvalidDataException("Not a VDK file (missing 'dk' signature)");
            }

            // Read actual header size (little-endian at bytes 2-3)
            ushort headerSize = (ushort)(header[2] | (header[3] << 8));

            // VDK spec says header should be 12 bytes, but some VDK files have extended headers
            // Common sizes: 12 bytes (standard), 256 bytes (extended with disk name)
            if (headerSize < 12)
            {
                throw new InvalidDataException($"Invalid VDK header size: {headerSize} (minimum 12 bytes)");
            }

            // Extract geometry from standard 12-byte header
            byte tracks = header[8];
            byte sides = header[9];

            // 2. Detect descriptor from geometry
            _descriptor = DetectDescriptor(tracks, sides);

            // 3. Create a sub-stream starting after the VDK header
            // This allows GenericBlockDevice to read raw sector data without header offset
            long dataOffset = headerSize;
            long dataLength = imageStream.Length - headerSize;

            // Validate data size matches expected geometry
            long expectedSize = tracks * sides * 18 * 256;
            if (dataLength != expectedSize)
            {
                throw new InvalidDataException(
                    $"VDK data size mismatch: expected {expectedSize} bytes ({tracks}T × {sides}S × 18SPT × 256), " +
                    $"got {dataLength} bytes after {headerSize}-byte header");
            }

            // 4. Create a SubStream that exposes only the data portion (skipping VDK header)
            // This ensures GenericBlockDevice sees the correct length for validation
            SubStream dataStream = new SubStream(imageStream, dataOffset, dataLength);

            // 5. Mount block device with descriptor using data-only stream
            _blockDevice = new GenericBlockDevice(_descriptor);
            _blockDevice.Mount(dataStream, readOnly);

            // 6. Mount the file system: an OS-9 volume has a strict signature in sector 0, so it is tried first, then DragonDOS.
            Os9RbfFileSystem os9 = new Os9RbfFileSystem();
            if (os9.CanHandle(_blockDevice))
            {
                _filesystem = os9;
            }
            else
            {
                _filesystem = new DragonDosFileSystem();
            }

            _filesystem.Initialize(_blockDevice);
        }

        /// <summary>
        /// Detects the correct descriptor based on VDK geometry.
        /// </summary>
        private BlockDeviceDescriptor DetectDescriptor(byte tracks, byte sides)
        {
            if (sides == 1)
            {
                // Single-sided
                if (tracks == 40)
                {
                    return BlockDeviceDescriptor.DragonVDK_SS40;
                }
                else if (tracks == 80)
                {
                    return BlockDeviceDescriptor.DragonVDK_SS80;
                }
                else
                {
                    throw new InvalidDataException($"Unknown single-sided VDK track count: {tracks}");
                }
            }
            else if (sides == 2)
            {
                // Double-sided
                if (tracks == 40)
                {
                    return BlockDeviceDescriptor.DragonVDK_DS40;
                }
                else if (tracks == 80)
                {
                    return BlockDeviceDescriptor.DragonVDK_DS80;
                }
                else
                {
                    throw new InvalidDataException($"Unknown double-sided VDK track count: {tracks}");
                }
            }
            else
            {
                throw new InvalidDataException($"Invalid VDK side count: {sides}");
            }
        }

        // ===================================================================
        // FILE OPERATIONS - Delegate to Filesystem
        // ===================================================================

        /// <summary>
        /// Reads a whole file from the mounted DragonDOS file system.
        /// </summary>
        /// <param name="path">
        /// Name of the file.
        /// </param>
        /// <returns>
        /// The file contents.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public byte[] ReadFile(string path)
        {
            EnsureMounted();
            return _filesystem!.ReadFile(path);
        }

        /// <summary>
        /// Always throws UnauthorizedAccessException: the Dragon VDK driver has no write support, so the volume is read-only.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <param name="data">
        /// Not used.
        /// </param>
        /// <exception cref="UnauthorizedAccessException">
        /// Always thrown.
        /// </exception>
        public void WriteFile(string path, byte[] data)
        {
            throw new UnauthorizedAccessException("The Dragon VDK driver has no write support, so the volume is read-only");
        }

        /// <summary>
        /// Always throws UnauthorizedAccessException: the Dragon VDK driver has no write support, so the volume is read-only.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <exception cref="UnauthorizedAccessException">
        /// Always thrown.
        /// </exception>
        public void DeleteFile(string path)
        {
            throw new UnauthorizedAccessException("The Dragon VDK driver has no write support, so the volume is read-only");
        }

        /// <summary>
        /// Lists the directory of the mounted DragonDOS file system.
        /// </summary>
        /// <param name="path">
        /// Directory path.
        /// </param>
        /// <returns>
        /// The directory entries.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureMounted();
            return _filesystem!.ListDirectory(path);
        }

        /// <summary>
        /// Gets metadata of a file in the mounted DragonDOS file system.
        /// </summary>
        /// <param name="path">
        /// Name of the file.
        /// </param>
        /// <returns>
        /// The metadata, or <c>null</c> when the file system does not find <paramref name="path"/>.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public FileMetadata? GetMetadata(string path)
        {
            EnsureMounted();
            return _filesystem!.GetMetadata(path);
        }

        // ===================================================================
        // BLOCK OPERATIONS - Delegate to Block Device
        // ===================================================================

        /// <summary>
        /// Reads one sector of the image data (after the VDK header) from the block device.
        /// </summary>
        /// <param name="address">
        /// The block to read.
        /// </param>
        /// <param name="buffer">
        /// Receives the block data.
        /// </param>
        /// <returns>
        /// The number of bytes read.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            EnsureMounted();
            return _blockDevice!.ReadBlock(address, buffer);
        }

        /// <summary>
        /// Always throws UnauthorizedAccessException: the Dragon VDK driver has no write support, so the volume is read-only.
        /// </summary>
        /// <param name="address">
        /// Not used.
        /// </param>
        /// <param name="data">
        /// Not used.
        /// </param>
        /// <exception cref="UnauthorizedAccessException">
        /// Always thrown.
        /// </exception>
        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            throw new UnauthorizedAccessException("The Dragon VDK driver has no write support, so the volume is read-only");
        }

        /// <summary>
        /// Gets the information of the block device the image data is mounted on.
        /// </summary>
        /// <returns>
        /// The block device information; never <c>null</c> once mounted.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public BlockDeviceInfo? GetBlockDeviceInfo()
        {
            EnsureMounted();
            return _blockDevice!.GetBlockDeviceInfo();
        }

        // ===================================================================
        // LIFECYCLE
        // ===================================================================

        /// <summary>
        /// Releases the file system and disposes the block device.
        /// </summary>
        public void Dispose()
        {
            _filesystem = null;
            _blockDevice?.Dispose();
            _blockDevice = null;
        }

        private void EnsureMounted()
        {
            if (_blockDevice == null || _filesystem == null)
            {
                throw new InvalidOperationException("VDK image not mounted");
            }
        }
    }
}
