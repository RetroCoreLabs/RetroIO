//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FileSystems;
using RetroIO.Storage.Local.Streams;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Driver for Dragon 32/64 DSK/JVC disk images with DragonDOS filesystem.
    /// Supports optional 0-255 byte header with geometry detection.
    /// Thin wrapper that composes GenericBlockDevice + DragonDosFileSystem.
    /// </summary>
    public sealed class DragonDskDriver : IFileSystemDriver
    {
        private GenericBlockDevice? _blockDevice;
        private IBlockFileSystem? _filesystem;
        private BlockDeviceDescriptor? _descriptor;

        /// <inheritdoc/>
        public string FileSystemType => _filesystem?.FileSystemType ?? "DragonDOS";
        /// <inheritdoc/>
        public string VolumeLabel => _filesystem?.VolumeLabel ?? string.Empty;
        /// <inheritdoc/>
        public string ExtensionSeparator => _filesystem?.ExtensionSeparator ?? ".";

        /// <inheritdoc/>
        public DriverCapabilities Capabilities
        {
            get
            {
                return DriverCapabilities.Read |
                       DriverCapabilities.ListDirectory |
                       DriverCapabilities.FileTypes |
                       DriverCapabilities.VolumeLabel |
                       DriverCapabilities.BlockRead;
            }
        }

        /// <inheritdoc/>
        public bool CanRead => true;
        /// <summary>
        /// Gets false, because the driver does not write yet.
        /// </summary>
        public bool CanWrite => false;
        /// <inheritdoc/>
        public bool HasBlockAccess => true;

        /// <summary>
        /// Returns null, because this driver has no metadata descriptor.
        /// </summary>
        /// <returns>
        /// Always null.
        /// </returns>
        public IFileSystemMetadataDescriptor? GetMetadataDescriptor()
        {
            return null;
        }

        /// <summary>
        /// Reads the optional JVC header, checks the geometry, mounts the data part of the image as a block device and initializes the file system.
        /// </summary>
        /// <remarks>
        /// The header size is the file size modulo 256. DragonDOS is tried first. For a 35-track disk the DECB file system is tried before the fall back to DragonDOS.
        /// </remarks>
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
        /// Thrown when the header cannot be read or the sector size, first sector number, track count or data size do not fit a DragonDOS disk.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// Thrown when the track and side count are not supported.
        /// </exception>
        public void Mount(Stream imageStream, bool readOnly)
        {
            if (imageStream == null)
            {
                throw new ArgumentNullException(nameof(imageStream));
            }

            long fileSize = imageStream.Length;

            // Calculate header size using JVC formula: fileSize % 256
            int headerSize = (int)(fileSize % 256);

            // Read header bytes if present
            byte sectorsPerTrack = 18;  // Default
            byte sides = 1;              // Default
            byte sectorSizeCode = 1;     // Default (256 bytes)
            byte firstSectorId = 1;      // Default
            byte attributeFlag = 0;      // Default

            if (headerSize > 0)
            {
                byte[] header = new byte[headerSize];
                imageStream.Position = 0;
                int bytesRead = imageStream.Read(header, 0, headerSize);
                if (bytesRead < headerSize)
                {
                    throw new InvalidDataException("Unable to read DSK/JVC header");
                }

                // Parse header bytes (missing bytes use defaults)
                if (headerSize >= 1) sectorsPerTrack = header[0];
                if (headerSize >= 2) sides = header[1];
                if (headerSize >= 3) sectorSizeCode = header[2];
                if (headerSize >= 4) firstSectorId = header[3];
                if (headerSize >= 5) attributeFlag = header[4];
            }

            // Calculate sector size from code: 128 << code
            int sectorSize = 128 << sectorSizeCode;

            // Validate sector size (must be 256 for DragonDOS)
            if (sectorSize != 256)
            {
                throw new InvalidDataException(
                    $"Unsupported sector size: {sectorSize} bytes. DragonDOS requires 256-byte sectors.");
            }

            // Validate first sector ID (must be 1 for DragonDOS)
            if (firstSectorId != 1)
            {
                throw new InvalidDataException(
                    $"Unsupported first sector ID: {firstSectorId}. DragonDOS requires sector numbering starting at 1.");
            }

            // Calculate track count
            long dataSize = fileSize - headerSize;
            long bytesPerTrack = sectorsPerTrack * sectorSize;
            int tracks = (int)(dataSize / (bytesPerTrack * sides));

            // Validate track count
            if (tracks != 35 && tracks != 40 && tracks != 80)
            {
                throw new InvalidDataException(
                    $"Invalid track count: {tracks}. Expected 35 (CoCo), 40, or 80 tracks.");
            }

            // Detect descriptor based on geometry
            _descriptor = DetectDescriptor(tracks, sides);

            // Validate data size matches expected geometry
            long expectedSize = tracks * sides * sectorsPerTrack * sectorSize;
            if (dataSize != expectedSize)
            {
                throw new InvalidDataException(
                    $"DSK/JVC data size mismatch: expected {expectedSize} bytes ({tracks}T × {sides}S × {sectorsPerTrack}SPT × {sectorSize}), " +
                    $"got {dataSize} bytes after {headerSize}-byte header");
            }

            // Create SubStream to expose only data portion (skipping header)
            SubStream dataStream = new SubStream(imageStream, headerSize, dataSize);

            // Mount block device with descriptor using data-only stream
            _blockDevice = new GenericBlockDevice(_descriptor);
            _blockDevice.Mount(dataStream, readOnly);

            // Try DragonDOS first, then fall back to DECB for 35-track disks
            _filesystem = new DragonDosFileSystem();

            if (_blockDevice.GetBlockDeviceInfo()?.CylinderCount == 35)
            {
                // 35-track disk - could be DECB, try that first
                DecbFileSystem decbFs = new DecbFileSystem();
                if (decbFs.CanHandle(_blockDevice))
                {
                    _filesystem = decbFs;
                    _filesystem.Initialize(_blockDevice);
                }
                else
                {
                    // Fall back to DragonDOS
                    _filesystem.Initialize(_blockDevice);
                }
            }
            else
            {
                // Not 35-track, use DragonDOS
                _filesystem.Initialize(_blockDevice);
            }
        }

        private BlockDeviceDescriptor DetectDescriptor(int tracks, byte sides)
        {
            // Determine descriptor based on geometry
            if (sides == 1)
            {
                // Single-sided
                if (tracks == 35)
                {
                    // CoCo standard (35 tracks, not common for Dragon)
                    return new BlockDeviceDescriptor
                    {
                        Name = "CoCo DSK/JVC 35T/SS (160KB)",
                        BlockSize = 256,
                        AddressMode = BlockAddressMode.LBA,
                        Cylinders = 35,
                        Heads = 1,
                        UniformSectorsPerTrack = 18,
                        TotalBlocks = 630,
                        ExpectedImageSize = 161280,
                        SupportsVariableGeometry = false
                    };
                }
                else if (tracks == 40)
                {
                    return BlockDeviceDescriptor.DragonVDK_SS40;
                }
                else if (tracks == 80)
                {
                    return BlockDeviceDescriptor.DragonVDK_SS80;
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
            }

            throw new NotSupportedException(
                $"Unsupported disk geometry: {tracks} tracks, {sides} side(s)");
        }

        /// <inheritdoc/>
        public DirectoryEntry[] ListDirectory(string path)
        {
            if (_filesystem == null)
            {
                throw new InvalidOperationException("Filesystem not mounted");
            }

            return _filesystem.ListDirectory(path);
        }

        /// <inheritdoc/>
        public byte[] ReadFile(string path)
        {
            if (_filesystem == null)
            {
                throw new InvalidOperationException("Filesystem not mounted");
            }

            return _filesystem.ReadFile(path);
        }

        /// <summary>
        /// Not implemented. Always throws.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <param name="data">
        /// Not used.
        /// </param>
        /// <exception cref="NotImplementedException">
        /// Always thrown.
        /// </exception>
        public void WriteFile(string path, byte[] data)
        {
            throw new NotImplementedException("Write operations not yet supported for DSK/JVC images");
        }

        /// <summary>
        /// Not implemented. Always throws.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <exception cref="NotImplementedException">
        /// Always thrown.
        /// </exception>
        public void DeleteFile(string path)
        {
            throw new NotImplementedException("Delete operations not yet supported for DSK/JVC images");
        }

        /// <inheritdoc/>
        public FileMetadata? GetMetadata(string path)
        {
            if (_filesystem == null)
            {
                throw new InvalidOperationException("Filesystem not mounted");
            }

            return _filesystem.GetMetadata(path);
        }

        /// <summary>
        /// Returns the number of free blocks reported by the file system.
        /// </summary>
        /// <returns>
        /// The free block count.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the image is not mounted.
        /// </exception>
        public int GetBlocksFree()
        {
            if (_filesystem == null)
            {
                throw new InvalidOperationException("Filesystem not mounted");
            }

            return _filesystem.GetBlocksFree();
        }

        /// <summary>
        /// Returns the block device of the mounted image.
        /// </summary>
        /// <returns>
        /// The block device, or null when not mounted.
        /// </returns>
        public IBlockDevice? GetBlockDevice()
        {
            return _blockDevice;
        }

        /// <inheritdoc/>
        public int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            if (_blockDevice == null)
            {
                throw new InvalidOperationException("Filesystem not mounted");
            }

            return _blockDevice.ReadBlock(address, buffer);
        }

        /// <summary>
        /// Not implemented. Always throws.
        /// </summary>
        /// <param name="address">
        /// Not used.
        /// </param>
        /// <param name="data">
        /// Not used.
        /// </param>
        /// <exception cref="NotImplementedException">
        /// Always thrown.
        /// </exception>
        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            throw new NotImplementedException("Write operations not yet supported for DSK/JVC images");
        }

        /// <inheritdoc/>
        public BlockDeviceInfo? GetBlockDeviceInfo()
        {
            if (_blockDevice == null)
            {
                throw new InvalidOperationException("Filesystem not mounted");
            }

            return _blockDevice.GetBlockDeviceInfo();
        }

        /// <summary>
        /// Drops the block device and the file system. The block device is not disposed.
        /// </summary>
        public void Dispose()
        {
            _blockDevice = null;
            _filesystem = null;
            _descriptor = null;
        }
    }
}
