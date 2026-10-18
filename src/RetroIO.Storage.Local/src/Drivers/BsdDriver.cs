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

        /// <summary>
        /// Gets the file system type of the mounted file system. When a disk label is used, the partition letter is appended after a dash.
        /// </summary>
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
        /// <summary>
        /// Gets the volume label of the mounted V7 file system, or an empty string when nothing is mounted.
        /// </summary>
        public string VolumeLabel => _filesystem?.VolumeLabel ?? string.Empty;
        /// <summary>
        /// Gets an empty string, because V7 names have no separate extension.
        /// </summary>
        public string ExtensionSeparator => "";  // Unix V7 doesn't use DOS-style extensions

        /// <summary>
        /// Gets the capability flags of this driver.
        /// </summary>
        /// <remarks>
        /// The flags are read, directory listing, subdirectories, permissions, timestamps, volume label,
        /// block read, partitions, case-sensitive names, symbolic links and hard links. No write flag is set.
        /// </remarks>
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

        /// <summary>
        /// Gets <c>true</c>, because this driver reads files.
        /// </summary>
        public bool CanRead => true;
        /// <summary>
        /// Gets false, because the V7 driver does not write yet.
        /// </summary>
        public bool CanWrite => false;  // Phase 4
        /// <summary>
        /// Gets <c>true</c>, because this driver can read raw blocks of the image.
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

        /// <summary>
        /// Mounts the image as a device of 512-byte blocks and initializes the V7 file system on the selected partition.
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

        /// <summary>
        /// Reads a whole file from the mounted V7 file system.
        /// </summary>
        /// <param name="path">
        /// Path of the file.
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
            throw new NotImplementedException("V7 write operations not yet implemented (Phase 4)");
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
            throw new NotImplementedException("V7 write operations not yet implemented (Phase 4)");
        }

        /// <summary>
        /// Lists the contents of a directory in the mounted V7 file system.
        /// </summary>
        /// <param name="path">
        /// Path of the directory.
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
        /// Gets metadata of a file or directory in the mounted V7 file system.
        /// </summary>
        /// <param name="path">
        /// Path of the file or directory.
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
        /// Reads one 512-byte block of the image from the block device.
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
            throw new NotImplementedException("V7 write operations not yet implemented (Phase 4)");
        }

        /// <summary>
        /// Gets the information of the block device the image is mounted on.
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
                throw new InvalidOperationException("V7 image not mounted");
            }
        }

        // ===================================================================
        // PARTITION PROVIDER INTERFACE
        // ===================================================================

        /// <summary>
        /// Checks whether the mounted image holds a valid BSD disk label.
        /// </summary>
        /// <returns>
        /// <c>true</c> when a valid BSD disk label is found; otherwise <c>false</c>.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public bool SupportsPartitions()
        {
            EnsureMounted();

            // Check if BSD disk label is present
            var partMgr = new BsdPartitionManager(_blockDevice!);
            var diskLabel = partMgr.GetDiskLabel();
            return diskLabel != null && diskLabel.IsValid();
        }

        /// <summary>
        /// Lists the partitions in the BSD disk label of the mounted image.
        /// </summary>
        /// <remarks>
        /// The recommended URN of each entry holds the text <c>path</c> in angle brackets in place of the
        /// image path; the caller fills in the real path.
        /// </remarks>
        /// <returns>
        /// One entry per partition, with the partition letter as its ID; an empty list when the image has
        /// no valid BSD disk label.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
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

        /// <summary>
        /// Selects the BSD partition to use and turns on disk label mode.
        /// </summary>
        /// <remarks>
        /// When an image is already mounted, the partition must exist in its disk label. The new letter is
        /// only stored: a file system that is already mounted keeps using its partition until the next mount.
        /// </remarks>
        /// <param name="partitionId">
        /// A single partition letter, <c>a</c> to <c>h</c>.
        /// </param>
        /// <returns>
        /// <c>true</c> when the partition was selected; <c>false</c> when <paramref name="partitionId"/> is not a
        /// single letter from <c>a</c> to <c>h</c>, or the mounted image has no such partition.
        /// </returns>
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

        /// <summary>
        /// Gets the selected partition letter, or <c>null</c> in raw mode.
        /// </summary>
        public string? CurrentPartition => _useDiskLabel ? _partitionLetter.ToString() : null;
    }
}

