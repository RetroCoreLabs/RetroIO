//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.BSD.Partitions;
using RetroIO.Storage.Local.BSD.Structures;
using RetroIO.Storage.Local.FFS.Core;
using RetroIO.Storage.Local.FFS.Structures;
using RetroIO.Storage.Local.Sun.Partitions;
using RetroIO.Storage.Local.Sun.Structures;

namespace RetroIO.Storage.Local.FFS.FileSystems
{
    /// <summary>
    /// Berkeley Fast File System (FFS/UFS) implementation.
    /// Supports SunOS 3.4 / 4.2BSD with fragment allocation and cylinder groups.
    /// Does not implement IBlockFileSystem to avoid circular dependencies.
    /// FfsDriver in RetroIO.Storage.Local wraps this class.
    /// </summary>
    public class FfsFileSystem
    {
        private IBlockDevice? _device;
        private FfsSuperblock? _superblock;

        private FfsBlockMapper? _mapper;
        private FfsInodeReader? _inodeReader;
        private FfsDirectoryReader? _dirReader;

        private BsdDiskLabel? _diskLabel;
        private BsdPartition? _currentPartition;

        private SunDiskLabel? _sunDiskLabel;
        private SunPartition? _currentSunPartition;

        // =================================================================
        // CONFIGURATION
        // =================================================================

        /// <summary>
        /// Partition letter to mount ('a' through 'h'). Default is 'a' (root).
        /// </summary>
        public char PartitionLetter { get; set; } = 'a';

        /// <summary>
        /// If true, attempts to read BSD disk label at sector 1.
        /// If false, assumes filesystem starts at block 0 (no partitions).
        /// </summary>
        public bool UseDiskLabel { get; set; } = true;

        /// <summary>
        /// Optional: Source image path for detecting known disk images with corrupt labels.
        /// </summary>
        public string? ImagePath { get; set; }

        /// <summary>
        /// Explicit byte offset at which the filesystem begins within the image.
        /// -1 (default) means "discover it": use the partition table if there is one,
        /// otherwise fall back to scanning for the superblock.
        ///
        /// Set this when the base is already known and guessing is undesirable — e.g. an
        /// NDIX di70 pack, whose filesystem starts one cylinder in at 90 * 1024 = 92160.
        /// See <see cref="FfsSuperblockLocator"/> for why that gap exists.
        /// </summary>
        public long FilesystemOffsetBytes { get; set; } = -1;

        /// <summary>
        /// Byte offset at which the mounted filesystem actually begins.
        /// 0 for a normal image, non-zero when the filesystem sits behind a gap or partition.
        /// Valid only after <see cref="Initialize"/> succeeds.
        /// </summary>
        public long ResolvedFilesystemOffsetBytes { get; private set; }

        // =================================================================
        // PUBLIC PROPERTIES
        // =================================================================

        public string FileSystemType => "FFS";

        public string VolumeLabel => _superblock?.fs_fsmnt ?? string.Empty;

        /// <summary>
        /// The mounted superblock, or null when not initialized.
        /// Exposed so callers can report on-disk geometry without re-reading the image.
        /// </summary>
        public FfsSuperblock? Superblock => _superblock;

        public void Initialize(IBlockDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));

            // Device to use for filesystem operations (may be wrapped in PartitionBlockDevice)
            IBlockDevice fsDevice = device;

            ResolvedFilesystemOffsetBytes = 0;

            // An explicit offset wins over every form of discovery: the caller already knows
            // where the filesystem starts, so neither the disk label nor the scan may override it.
            if (FilesystemOffsetBytes >= 0)
            {
                fsDevice = ApplyExplicitOffset(device, FilesystemOffsetBytes);
            }
            // Try to read BSD or Sun disk label (if enabled)
            else if (UseDiskLabel)
            {
                try
                {
                    // Try BSD disk label first (sector 1)
                    _diskLabel = ReadDiskLabel();

                    if (_diskLabel != null && _diskLabel.IsValid())
                    {
                        // Found BSD disk label
                        int partitionIndex = BsdPartition.GetPartitionIndex(PartitionLetter);
                        _currentPartition = _diskLabel.Partitions[partitionIndex];

                        if (_currentPartition.Value.IsUnused)
                        {
                            throw new InvalidOperationException($"Partition '{PartitionLetter}' is not in use");
                        }

                        if (!_currentPartition.Value.IsFfsFilesystem)
                        {
                            throw new InvalidOperationException(
                                $"Partition '{PartitionLetter}' is not an FFS filesystem (type={_currentPartition.Value.FileSystemTypeName})");
                        }

                        // Wrap device in partition accessor
                        fsDevice = new PartitionBlockDevice(
                            device,
                            _currentPartition.Value.Offset,
                            _currentPartition.Value.Size,
                            PartitionLetter.ToString());

                        ResolvedFilesystemOffsetBytes = (long)_currentPartition.Value.Offset * device.BlockSize;
                    }
                    else
                    {
                        // Try Sun disk label (sector 0)
                        _sunDiskLabel = ReadSunDiskLabel();

                        if (_sunDiskLabel != null && _sunDiskLabel.IsValid())
                        {
                            // Found Sun disk label
                            int partitionIndex = PartitionLetter - 'a';
                            if (partitionIndex < 0 || partitionIndex >= SunDiskLabel.MAX_PARTITIONS)
                            {
                                throw new InvalidOperationException($"Invalid Sun partition letter '{PartitionLetter}'");
                            }

                            _currentSunPartition = _sunDiskLabel.Partitions[partitionIndex];

                            if (_currentSunPartition.Value.IsUnused)
                            {
                                throw new InvalidOperationException($"Sun partition '{PartitionLetter}' is not in use");
                            }

                            // Calculate partition start sector (Sun uses cylinders)
                            var partMgr = new SunPartitionManager(device);
                            uint startSector = partMgr.GetPartitionStartSector(_currentSunPartition.Value, _sunDiskLabel);

                            System.Diagnostics.Debug.WriteLine($"[FfsFileSystem] Sun partition '{PartitionLetter}': start={startSector}, size={_currentSunPartition.Value.NumSectors}");

                            // Wrap device in partition accessor
                            fsDevice = new PartitionBlockDevice(
                                device,
                                startSector,
                                _currentSunPartition.Value.NumSectors,
                                PartitionLetter.ToString());

                            ResolvedFilesystemOffsetBytes = (long)startSector * device.BlockSize;
                        }
                        // If no valid label, fall through to raw filesystem mode
                    }
                }
                catch
                {
                    // Failed to read label, assume no partitions
                }
            }

            // Read superblock at offset 8192 (16 sectors) from partition/disk start
            _superblock = ReadSuperblock(fsDevice);

            // No superblock where we expected one. If we are in raw mode — no partition table
            // told us where to look — the filesystem may simply start further into the image.
            // Scan for it. This is what makes a flat NDIX-style image mountable: its filesystem
            // begins one disk cylinder in (90 sectors on a di70) with nothing on disk to say so.
            // See FfsSuperblockLocator for the full explanation.
            if (!_superblock.Validate() && ResolvedFilesystemOffsetBytes == 0 && FilesystemOffsetBytes < 0)
            {
                if (FfsSuperblockLocator.TryLocate(device, out long scannedBase, out FfsSuperblock? scannedSb)
                    && scannedBase > 0
                    && scannedSb != null)
                {
                    fsDevice = ApplyExplicitOffset(device, scannedBase);
                    _superblock = scannedSb;

                    System.Diagnostics.Debug.WriteLine(
                        $"[FfsFileSystem] Located filesystem at byte offset {scannedBase} by superblock scan");
                }
            }

            if (!_superblock.Validate())
            {
                var sb = _superblock;
                string validationDetails = $"Magic=0x{sb.fs_magic:X6}, BlockSize={sb.fs_bsize}, " +
                    $"FragSize={sb.fs_fsize}, Frag={sb.fs_frag}, NCG={sb.fs_ncg}, IPG={sb.fs_ipg}";

                throw new InvalidOperationException($"Invalid FFS superblock. {validationDetails}");
            }

            // Initialize readers with partition-aware device
            _mapper = new FfsBlockMapper(fsDevice, _superblock);
            _inodeReader = new FfsInodeReader(_mapper, _superblock);
            _dirReader = new FfsDirectoryReader(_mapper);
        }

        public bool CanHandle(IBlockDevice device)
        {
            try
            {
                // Try to read superblock at offset 8192
                byte[] buffer = new byte[FfsConstants.SBSIZE];

                // Read 16 sectors starting at sector 16 (offset 8192)
                int sectorsToRead = FfsConstants.SBSIZE / device.BlockSize;
                for (int i = 0; i < sectorsToRead; i++)
                {
                    int bufferOffset = i * device.BlockSize;
                    Span<byte> sectorBuffer = buffer.AsSpan(bufferOffset, device.BlockSize);
                    device.ReadBlock(BlockAddress.FromLBA((uint)(FfsConstants.SBLOCK_SECTOR + i)), sectorBuffer);
                }

                var sb = FfsSuperblock.Parse(buffer);

                // Basic sanity checks
                return sb.Validate();
            }
            catch
            {
                return false;
            }
        }

        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureInitialized();

            // Resolve path to inode
            uint inodeNum = ResolvePath(path);

            if (inodeNum == 0)
            {
                throw new System.IO.DirectoryNotFoundException($"Directory not found: {path}");
            }

            FfsInode dirInode = _inodeReader!.ReadInode(inodeNum);

            if (!dirInode.IsDirectory)
            {
                throw new InvalidOperationException($"Path is not a directory: {path}");
            }

            // Read directory entries
            var ffsEntries = _dirReader!.ListEntries(dirInode, includeDots: false);

            // Convert to RetroFS DirectoryEntry format
            var entries = new List<DirectoryEntry>();

            for (int i = 0; i < ffsEntries.Length; i++)
            {
                var ffsEntry = ffsEntries[i];
                Console.WriteLine($"[DEBUG FfsFileSystem.ListDirectory] Reading inode #{ffsEntry.d_ino} for entry '{ffsEntry.d_name}'");
                var entryInode = _inodeReader!.ReadInode(ffsEntry.d_ino);

                // Construct full path for navigation
                string fullPath = string.IsNullOrEmpty(path) || path == "/"
                    ? $"/{ffsEntry.d_name}"
                    : $"{path.TrimEnd('/')}/{ffsEntry.d_name}";

                var metadata = new FileMetadata
                {
                    Name = ffsEntry.d_name,
                    Path = fullPath,
                    IsDirectory = entryInode.IsDirectory,
                    Size = (long)entryInode.di_size,
                    CreatedTime = entryInode.ChangeTime,
                    ModifiedTime = entryInode.ModificationTime,
                    AccessedTime = entryInode.AccessTime
                };

                // Add device file properties
                if (entryInode.IsCharDevice)
                {
                    metadata.SetProperty("IsCharDevice", true);
                    metadata.SetProperty("IsDevice", true);
                }
                if (entryInode.IsBlockDevice)
                {
                    metadata.SetProperty("IsBlockDevice", true);
                    metadata.SetProperty("IsDevice", true);
                }
                if (entryInode.IsSymLink)
                {
                    metadata.SetProperty("IsSymLink", true);
                }
                if (entryInode.IsSocket)
                {
                    metadata.SetProperty("IsSocket", true);
                }

                var retroEntry = new DirectoryEntry
                {
                    Name = ffsEntry.d_name,
                    Metadata = metadata
                };

                entries.Add(retroEntry);
            }

            return entries.ToArray();
        }

        public byte[] ReadFile(string path)
        {
            EnsureInitialized();

            // Resolve path to inode
            uint inodeNum = ResolvePath(path);

            if (inodeNum == 0)
            {
                throw new System.IO.FileNotFoundException($"File not found: {path}");
            }

            FfsInode fileInode = _inodeReader!.ReadInode(inodeNum);

            if (fileInode.IsDirectory)
            {
                throw new InvalidOperationException($"Path is a directory, not a file: {path}");
            }

            // Read file contents
            return _mapper!.ReadFile(fileInode);
        }

        public FileMetadata? GetMetadata(string path)
        {
            EnsureInitialized();

            // Resolve path to inode
            uint inodeNum = ResolvePath(path);

            if (inodeNum == 0)
            {
                return null;
            }

            FfsInode inode = _inodeReader!.ReadInode(inodeNum);

            return new FileMetadata
            {
                Name = System.IO.Path.GetFileName(path),
                IsDirectory = inode.IsDirectory,
                Size = (long)inode.di_size,
                CreatedTime = inode.ChangeTime,
                ModifiedTime = inode.ModificationTime,
                AccessedTime = inode.AccessTime
            };
        }

        public int GetBlocksFree()
        {
            EnsureInitialized();
            return (int)_superblock!.fs_cstotal_cs_nbfree;
        }

        // =================================================================
        // WRITE OPERATIONS (NOT IMPLEMENTED IN READ-ONLY PHASE)
        // =================================================================

        public void WriteFile(string path, byte[] data)
        {
            throw new NotImplementedException("Write operations not yet implemented");
        }

        public void DeleteFile(string path)
        {
            throw new NotImplementedException("Write operations not yet implemented");
        }

        public void CreateDirectory(string path)
        {
            throw new NotImplementedException("Write operations not yet implemented");
        }

        public void DeleteDirectory(string path)
        {
            throw new NotImplementedException("Write operations not yet implemented");
        }

        public void Rename(string oldPath, string newPath)
        {
            throw new NotImplementedException("Write operations not yet implemented");
        }

        // =================================================================
        // HELPER METHODS
        // =================================================================

        private void EnsureInitialized()
        {
            if (_device == null || _mapper == null || _inodeReader == null || _dirReader == null)
            {
                throw new InvalidOperationException("Filesystem not initialized. Call Initialize() first.");
            }
        }

        /// <summary>
        /// Windows the device so that block 0 of the result is the first block of the
        /// filesystem, given a base expressed in bytes.
        /// </summary>
        /// <param name="device">Underlying whole-image device.</param>
        /// <param name="offsetBytes">Filesystem base in bytes. Must be a whole number of device blocks.</param>
        /// <returns>The windowed device, or the original device when the offset is zero.</returns>
        private IBlockDevice ApplyExplicitOffset(IBlockDevice device, long offsetBytes)
        {
            if (offsetBytes == 0)
            {
                ResolvedFilesystemOffsetBytes = 0;
                return device;
            }

            int blockSize = device.BlockSize;

            if (offsetBytes < 0 || (offsetBytes % blockSize) != 0)
            {
                throw new InvalidOperationException(
                    $"Filesystem offset {offsetBytes} is not a multiple of the device block size {blockSize}");
            }

            uint startBlock = (uint)(offsetBytes / blockSize);

            if (startBlock >= device.TotalBlocks)
            {
                throw new InvalidOperationException(
                    $"Filesystem offset {offsetBytes} lies beyond the end of the image");
            }

            uint remainingBlocks = device.TotalBlocks - startBlock;

            ResolvedFilesystemOffsetBytes = offsetBytes;

            return new PartitionBlockDevice(device, startBlock, remainingBlocks, "raw");
        }

        /// <summary>
        /// Reads superblock from offset 8192 (16 sectors from start).
        /// </summary>
        private FfsSuperblock ReadSuperblock(IBlockDevice device)
        {
            if (device == null)
                throw new ArgumentNullException(nameof(device));

            // Read superblock at offset 8192 (SBLOCK_OFFSET)
            byte[] buffer = new byte[FfsConstants.SBSIZE];

            // Read 16 sectors starting at sector 16 (from partition/disk start)
            int sectorsToRead = FfsConstants.SBSIZE / device.BlockSize;
            for (int i = 0; i < sectorsToRead; i++)
            {
                int bufferOffset = i * device.BlockSize;
                Span<byte> sectorBuffer = buffer.AsSpan(bufferOffset, device.BlockSize);
                device.ReadBlock(BlockAddress.FromLBA((uint)(FfsConstants.SBLOCK_SECTOR + i)), sectorBuffer);
            }

            return FfsSuperblock.Parse(buffer);
        }

        private BsdDiskLabel? ReadDiskLabel()
        {
            if (_device == null)
                return null;

            try
            {
                int physicalSectorSize = _device.BlockSize;

                if (physicalSectorSize == 512)
                {
                    // Read sector 1 directly
                    byte[] buffer = new byte[512];
                    _device.ReadBlock(BlockAddress.FromLBA(1), buffer);
                    return BsdDiskLabel.Parse(buffer);
                }
                else if (physicalSectorSize == 1024)
                {
                    // Sector 1 doesn't exist - label is in second half of block 0
                    byte[] block = new byte[1024];
                    _device.ReadBlock(BlockAddress.FromLBA(0), block);

                    // Copy second half to new buffer
                    byte[] labelBuffer = new byte[512];
                    Array.Copy(block, 512, labelBuffer, 0, 512);
                    return BsdDiskLabel.Parse(labelBuffer);
                }
                else
                {
                    // Unsupported sector size for BSD disk labels
                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        private SunDiskLabel? ReadSunDiskLabel()
        {
            if (_device == null)
                return null;

            try
            {
                // Sun disk label is at sector 0 (unlike BSD which is at sector 1)
                byte[] buffer = new byte[512];
                _device.ReadBlock(BlockAddress.FromLBA(0), buffer);

                var sunLabel = SunDiskLabel.Parse(buffer);

                if (sunLabel != null && sunLabel.IsValid())
                {
                    System.Diagnostics.Debug.WriteLine($"[FfsFileSystem] Found valid Sun disk label: magic=0x{sunLabel.Magic:X4}");
                    return sunLabel;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Resolves a path to an inode number.
        /// </summary>
        private uint ResolvePath(string path)
        {
            if (_inodeReader == null || _dirReader == null)
                throw new InvalidOperationException("Readers not initialized");

            // Handle root
            if (string.IsNullOrEmpty(path) || path == "/")
            {
                return FfsConstants.ROOTINO;
            }

            // Clean path
            path = path.TrimStart('/').TrimEnd('/');

            if (string.IsNullOrEmpty(path))
            {
                return FfsConstants.ROOTINO;
            }

            // Start from root directory
            uint currentInodeNum = FfsConstants.ROOTINO;
            FfsInode currentInode = _inodeReader.ReadRootInode();

            // Split path into components
            string[] components = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < components.Length; i++)
            {
                if (!currentInode.IsDirectory)
                {
                    return 0; // Not a directory
                }

                var entry = _dirReader.FindEntry(currentInode, components[i]);

                if (entry == null)
                {
                    return 0; // Component not found
                }

                currentInodeNum = entry.d_ino;
                currentInode = _inodeReader.ReadInode(currentInodeNum);
            }

            return currentInodeNum;
        }
    }
}
