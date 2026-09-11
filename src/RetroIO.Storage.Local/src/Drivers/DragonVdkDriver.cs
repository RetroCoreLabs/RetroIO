//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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
    /// Thin wrapper that composes GenericBlockDevice + DragonDosFileSystem.
    /// </summary>
    public sealed class DragonVdkDriver : IFileSystemDriver
    {
        private GenericBlockDevice? _blockDevice;
        private DragonDosFileSystem? _filesystem;
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
                // TODO: Add write capabilities when implemented
            }
        }

        public bool CanRead => true;
        public bool CanWrite => false; // TODO: Implement write operations
        public bool HasBlockAccess => true;

        public IFileSystemMetadataDescriptor? GetMetadataDescriptor()
        {
            // TODO: Implement Dragon/CoCo VDK metadata descriptor
            return null;
        }

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

            // 6. Mount DragonDOS filesystem
            _filesystem = new DragonDosFileSystem();
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

        public byte[] ReadFile(string path)
        {
            EnsureMounted();
            return _filesystem!.ReadFile(path);
        }

        public void WriteFile(string path, byte[] data)
        {
            throw new NotImplementedException("Dragon VDK write operations not yet implemented");
        }

        public void DeleteFile(string path)
        {
            throw new NotImplementedException("Dragon VDK delete operations not yet implemented");
        }

        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureMounted();
            return _filesystem!.ListDirectory(path);
        }

        public FileMetadata? GetMetadata(string path)
        {
            EnsureMounted();
            return _filesystem!.GetMetadata(path);
        }

        // ===================================================================
        // BLOCK OPERATIONS - Delegate to Block Device
        // ===================================================================

        public int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            EnsureMounted();
            return _blockDevice!.ReadBlock(address, buffer);
        }

        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            throw new NotImplementedException("Dragon VDK write operations not yet implemented");
        }

        public BlockDeviceInfo? GetBlockDeviceInfo()
        {
            EnsureMounted();
            return _blockDevice!.GetBlockDeviceInfo();
        }

        // ===================================================================
        // LIFECYCLE
        // ===================================================================

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
