//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.BSD.Partitions;
using RetroIO.Storage.Local.V7.FileSystems;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Driver for UNIX V7 filesystem images (1979).
    /// Thin wrapper that composes GenericBlockDevice + V7FileSystem.
    /// </summary>
    public sealed class V7Driver : IFileSystemDriver, IPartitionProvider
    {
        private GenericBlockDevice? _blockDevice;
        private V7FileSystem? _filesystem;
        private char _partitionLetter = 'a';  // Default to partition 'a' (root)
        private bool _useDiskLabel = true;     // Default to using disk label

        public string FileSystemType
        {
            get
            {
                string baseType = _filesystem?.FileSystemType ?? "BSD-V7";
                return _useDiskLabel && _partitionLetter != '\0'
                    ? $"{baseType}-{_partitionLetter}"
                    : baseType;
            }
        }
        public string VolumeLabel => _filesystem?.VolumeLabel ?? string.Empty;
        public string ExtensionSeparator => "";  // Unix V7 doesn't use DOS-style extensions

        public DriverCapabilities Capabilities =>
            DriverCapabilities.Read |
            DriverCapabilities.ListDirectory |
            DriverCapabilities.Subdirectories |
            DriverCapabilities.Permissions |
            DriverCapabilities.Timestamps |
            DriverCapabilities.VolumeLabel |
            DriverCapabilities.BlockRead |
            DriverCapabilities.Partitions |
            DriverCapabilities.CaseSensitive |
            DriverCapabilities.SymbolicLinks |
            DriverCapabilities.HardLinks;

        public bool CanRead => true;
        public bool CanWrite => false;  // Phase 4
        public bool HasBlockAccess => true;

        public IFileSystemMetadataDescriptor? GetMetadataDescriptor()
        {
            // TODO: Implement BSD metadata descriptor (permissions, uid/gid, timestamps)
            return null;
        }

        /// <summary>
        /// Sets the partition letter to mount (a-h). Must be called before Mount().
        /// </summary>
        public void SetPartition(char partitionLetter)
        {
            if (partitionLetter < 'a' || partitionLetter > 'h')
            {
                throw new ArgumentException($"Invalid partition letter '{partitionLetter}'. Must be 'a' through 'h'.", nameof(partitionLetter));
            }
            _partitionLetter = partitionLetter;
            _useDiskLabel = true;  // Using partitions implies disk label
        }

        /// <summary>
        /// Sets raw filesystem mode (no partition table). Must be called before Mount().
        /// </summary>
        public void SetRawMode()
        {
            _useDiskLabel = false;
        }

        public void Mount(Stream imageStream, bool readOnly)
        {
            if (imageStream == null)
            {
                throw new ArgumentNullException(nameof(imageStream));
            }

            // Create block device with 512-byte sectors (traditional BSD)
            var descriptor = new BlockDeviceDescriptor
            {
                Name = "BSD Disk",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(imageStream.Length / 512),
                ExpectedImageSize = imageStream.Length
            };

            _blockDevice = new GenericBlockDevice(descriptor);
            _blockDevice.Mount(imageStream, readOnly);

            // Mount BSD filesystem with selected partition
            _filesystem = new V7FileSystem
            {
                PartitionLetter = _partitionLetter,
                UseDiskLabel = _useDiskLabel
            };

            _filesystem.Initialize(_blockDevice);
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
            throw new NotImplementedException("V7 write operations not yet implemented (Phase 4)");
        }

        public void DeleteFile(string path)
        {
            throw new NotImplementedException("V7 write operations not yet implemented (Phase 4)");
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
            throw new NotImplementedException("V7 write operations not yet implemented (Phase 4)");
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
                throw new InvalidOperationException("V7 image not mounted");
            }
        }

        // ===================================================================
        // PARTITION PROVIDER INTERFACE
        // ===================================================================

        public bool SupportsPartitions()
        {
            EnsureMounted();

            // Check if BSD disk label is present
            var partMgr = new BsdPartitionManager(_blockDevice!);
            var diskLabel = partMgr.GetDiskLabel();
            return diskLabel != null && diskLabel.IsValid();
        }

        public IReadOnlyList<PartitionMetadata> ListPartitions()
        {
            EnsureMounted();

            var result = new List<PartitionMetadata>();
            var partMgr = new BsdPartitionManager(_blockDevice!);
            var diskLabel = partMgr.GetDiskLabel();

            if (diskLabel == null || !diskLabel.IsValid())
            {
                return result;
            }

            var bsdPartitions = partMgr.ListPartitions();
            for (int i = 0; i < bsdPartitions.Count; i++)
            {
                var (letter, part) = bsdPartitions[i];
                double sizeMB = (part.Size * 512.0) / (1024.0 * 1024.0);

                result.Add(new PartitionMetadata
                {
                    Id = letter.ToString(),
                    DisplayText = $"{letter} - {part.FileSystemTypeName} ({sizeMB:F1} MB)",
                    OffsetSectors = part.Offset,
                    SizeSectors = part.Size,
                    FileSystemTypeCode = part.FsType,
                    RecommendedUrn = $"bsd:///<path>#{letter}", // Path will be filled in by caller
                    IsBootable = false, // BSD partitions don't have bootable flag
                    TableType = PartitionTableType.BsdDiskLabel
                });
            }

            return result;
        }

        public bool SelectPartition(string partitionId)
        {
            if (string.IsNullOrEmpty(partitionId) || partitionId.Length != 1)
            {
                return false;
            }

            char letter = partitionId[0];
            if (letter < 'a' || letter > 'h')
            {
                return false;
            }

            // Verify partition exists if mounted
            if (_blockDevice != null)
            {
                var partMgr = new BsdPartitionManager(_blockDevice);
                var partition = partMgr.GetPartition(letter);
                if (partition == null)
                {
                    return false;
                }
            }

            _partitionLetter = letter;
            _useDiskLabel = true;
            return true;
        }

        public string? CurrentPartition => _useDiskLabel ? _partitionLetter.ToString() : null;
    }
}

