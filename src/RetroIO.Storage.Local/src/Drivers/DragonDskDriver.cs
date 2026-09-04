//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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

        public string FileSystemType => _filesystem?.FileSystemType ?? "DragonDOS";
        public string VolumeLabel => _filesystem?.VolumeLabel ?? string.Empty;
        public string ExtensionSeparator => _filesystem?.ExtensionSeparator ?? ".";

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

        public bool CanRead => true;
        public bool CanWrite => false;
        public bool HasBlockAccess => true;

        public IFileSystemMetadataDescriptor? GetMetadataDescriptor()
        {
            return null;
        }

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

        public DirectoryEntry[] ListDirectory(string path)
        {
            if (_filesystem == null)
            {
                throw new InvalidOperationException("Filesystem not mounted");
            }

            return _filesystem.ListDirectory(path);
        }

        public byte[] ReadFile(string path)
        {
            if (_filesystem == null)
            {
                throw new InvalidOperationException("Filesystem not mounted");
            }

            return _filesystem.ReadFile(path);
        }

        public void WriteFile(string path, byte[] data)
        {
            throw new NotImplementedException("Write operations not yet supported for DSK/JVC images");
        }

        public void DeleteFile(string path)
        {
            throw new NotImplementedException("Delete operations not yet supported for DSK/JVC images");
        }

        public FileMetadata? GetMetadata(string path)
        {
            if (_filesystem == null)
            {
                throw new InvalidOperationException("Filesystem not mounted");
            }

            return _filesystem.GetMetadata(path);
        }

        public int GetBlocksFree()
        {
            if (_filesystem == null)
            {
                throw new InvalidOperationException("Filesystem not mounted");
            }

            return _filesystem.GetBlocksFree();
        }

        public IBlockDevice? GetBlockDevice()
        {
            return _blockDevice;
        }

        public int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            if (_blockDevice == null)
            {
                throw new InvalidOperationException("Filesystem not mounted");
            }

            return _blockDevice.ReadBlock(address, buffer);
        }

        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            throw new NotImplementedException("Write operations not yet supported for DSK/JVC images");
        }

        public BlockDeviceInfo? GetBlockDeviceInfo()
        {
            if (_blockDevice == null)
            {
                throw new InvalidOperationException("Filesystem not mounted");
            }

            return _blockDevice.GetBlockDeviceInfo();
        }

        public void Dispose()
        {
            _blockDevice = null;
            _filesystem = null;
            _descriptor = null;
        }
    }
}
