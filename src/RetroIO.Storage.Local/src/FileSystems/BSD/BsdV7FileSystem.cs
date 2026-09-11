//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BSD.Structures;
using RetroIO.Storage.Local.V7.Core;
using RetroIO.Storage.Local.V7.Structures;

namespace RetroIO.Storage.Local.V7.FileSystems
{
    /// <summary>
    /// UNIX V7 filesystem implementation (1979).
    /// Supports 1KB block filesystem on both 512-byte and 1024-byte physical sectors.
    /// Does not implement IBlockFileSystem to avoid circular dependencies.
    /// V7Driver in RetroIO.Storage.Local wraps this class.
    /// </summary>
    public class V7FileSystem
    {
        private IBlockDevice? _device;
        private V7Superblock? _superblock;
        private BsdDiskLabel? _diskLabel;
        private BsdPartition? _currentPartition;
        private uint _partitionStartBlock;

        private V7BlockMapper? _mapper;
        private V7InodeReader? _inodeReader;
        private V7DirectoryReader? _dirReader;

        // =================================================================
        // CONFIGURATION
        // =================================================================

        /// <summary>
        /// Partition letter to mount ('a' through 'h'). Default is 'a' (root).
        /// </summary>
        public char PartitionLetter { get; set; } = 'a';

        /// <summary>
        /// If true, attempts to read disk label at sector 1.
        /// If false, assumes filesystem starts at block 0 (no partitions).
        /// </summary>
        public bool UseDiskLabel { get; set; } = true;

        // =================================================================
        // PUBLIC PROPERTIES
        // =================================================================

        public string FileSystemType => "V7";

        public string VolumeLabel => _superblock?.FsName ?? string.Empty;

        public void Initialize(IBlockDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));

            // Initialize mapper
            _mapper = new V7BlockMapper(device);

            // Try to read disk label (if enabled)
            if (UseDiskLabel)
            {
                try
                {
                    _diskLabel = ReadDiskLabel();

                    if (_diskLabel != null && _diskLabel.IsValid())
                    {
                        // Find the specified partition
                        int partitionIndex = BsdPartition.GetPartitionIndex(PartitionLetter);
                        _currentPartition = _diskLabel.Partitions[partitionIndex];

                        if (_currentPartition.Value.IsUnused)
                        {
                            throw new InvalidOperationException($"Partition '{PartitionLetter}' is not in use");
                        }

                        if (!_currentPartition.Value.IsV7Filesystem)
                        {
                            throw new InvalidOperationException(
                                $"Partition '{PartitionLetter}' is not a V7 filesystem (type={_currentPartition.Value.FileSystemTypeName})");
                        }

                        // Calculate partition start in filesystem blocks
                        _partitionStartBlock = ConvertSectorToFsBlock(_currentPartition.Value.Offset);
                    }
                    else
                    {
                        // No valid label, assume filesystem starts at block 0
                        _partitionStartBlock = 0;
                    }
                }
                catch
                {
                    // Failed to read label, assume no partitions
                    _partitionStartBlock = 0;
                }
            }
            else
            {
                _partitionStartBlock = 0;
            }

            // Hand the partition offset to the mapper BEFORE any read happens. From here on every
            // block number in this filesystem - superblock, inode blocks, and the data-block
            // pointers inside inodes - is partition-relative and the mapper translates it.
            _mapper.PartitionStartBlock = _partitionStartBlock;

            // Read superblock
            _superblock = ReadSuperblock();

            if (!_superblock.Validate())
            {
                // Log superblock details for debugging
                var sb = _superblock;
                string validationDetails = $"ISize={sb.ISize}, FSize={sb.FSize}, NFree={sb.NFree}, NInode={sb.NInode}, " +
                    $"TFree={sb.TFree}, TInode={sb.TInode}, DataBlockCount={sb.DataBlockCount}, TotalInodes={sb.TotalInodes}";

                throw new InvalidOperationException($"Invalid superblock. {validationDetails}");
            }

            // Initialize readers
            _inodeReader = new V7InodeReader(_mapper, _superblock, _partitionStartBlock);
            _dirReader = new V7DirectoryReader(_mapper);
        }

        public bool CanHandle(IBlockDevice device)
        {
            try
            {
                var mapper = new V7BlockMapper(device);

                // Try to read superblock at block 1
                byte[] buffer = new byte[V7Constants.SUPERBLOCK_SIZE];
                mapper.ReadFilesystemBlock(1, buffer);

                var sb = V7Superblock.Parse(buffer);

                // Basic sanity checks
                return sb.ISize > 0 &&
                       sb.FSize > 0 &&
                       sb.ISize < sb.FSize &&
                       sb.Validate();
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
            ushort inodeNum = ResolvePath(path);

            if (inodeNum == 0)
            {
                throw new System.IO.DirectoryNotFoundException($"Directory not found: {path}");
            }

            V7Inode dirInode = _inodeReader!.ReadInode(inodeNum);

            if (!dirInode.IsDirectory)
            {
                throw new InvalidOperationException($"Path is not a directory: {path}");
            }

            // Read directory entries
            var bsdEntries = _dirReader!.ListEntries(dirInode, includeDots: false);

            // Convert to RetroFS DirectoryEntry format
            var entries = new List<DirectoryEntry>();

            for (int i = 0; i < bsdEntries.Length; i++)
            {
                var bsdEntry = bsdEntries[i];
                var entryInode = _inodeReader!.ReadInode(bsdEntry.Inode);

                // Construct full path for navigation
                string fullPath = string.IsNullOrEmpty(path) || path == "/"
                    ? $"/{bsdEntry.Name}"
                    : $"{path.TrimEnd('/')}/{bsdEntry.Name}";

                var metadata = new FileMetadata
                {
                    Name = bsdEntry.Name,
                    Path = fullPath,
                    IsDirectory = entryInode.IsDirectory,
                    Size = entryInode.Size,
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
                    Name = bsdEntry.Name,
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
            ushort inodeNum = ResolvePath(path);

            if (inodeNum == 0)
            {
                throw new System.IO.FileNotFoundException($"File not found: {path}");
            }

            V7Inode fileInode = _inodeReader!.ReadInode(inodeNum);

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
            ushort inodeNum = ResolvePath(path);

            if (inodeNum == 0)
            {
                return null;
            }

            V7Inode inode = _inodeReader!.ReadInode(inodeNum);

            return new FileMetadata
            {
                Name = System.IO.Path.GetFileName(path),
                IsDirectory = inode.IsDirectory,
                Size = inode.Size,
                CreatedTime = inode.ChangeTime,
                ModifiedTime = inode.ModificationTime,
                AccessedTime = inode.AccessTime
            };
        }

        public int GetBlocksFree()
        {
            EnsureInitialized();
            return (int)_superblock!.TFree;
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
        /// Reads disk label from sector 1.
        /// </summary>
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

                    // Extract bytes 512-1023
                    byte[] labelBytes = new byte[512];
                    Array.Copy(block, 512, labelBytes, 0, 512);
                    return BsdDiskLabel.Parse(labelBytes);
                }
            }
            catch
            {
                // Failed to read label
                return null;
            }

            return null;
        }

        /// <summary>
        /// Reads superblock from filesystem block 1 (within partition).
        /// </summary>
        private V7Superblock ReadSuperblock()
        {
            if (_mapper == null)
                throw new InvalidOperationException("Mapper not initialized");

            // Superblock is at filesystem block 1 of the partition. The block number stays
            // partition-relative: V7BlockMapper.PartitionStartBlock applies the offset.
            byte[] buffer = new byte[V7Constants.SUPERBLOCK_SIZE];
            _mapper.ReadFilesystemBlock(V7Constants.SUPERBLOCK_BLOCK, buffer);

            return V7Superblock.Parse(buffer);
        }

        /// <summary>
        /// Converts sector offset to filesystem block number based on physical sector size.
        /// </summary>
        private uint ConvertSectorToFsBlock(uint sectorOffset)
        {
            int physicalSectorSize = _device?.BlockSize ?? 512;

            if (physicalSectorSize == 512)
            {
                // 2 sectors per filesystem block
                return sectorOffset / 2;
            }
            else
            {
                // 1:1 mapping
                return sectorOffset;
            }
        }

        /// <summary>
        /// Resolves a path to an inode number.
        /// </summary>
        private ushort ResolvePath(string path)
        {
            if (_inodeReader == null || _dirReader == null)
                throw new InvalidOperationException("Readers not initialized");

            // Handle root
            if (string.IsNullOrEmpty(path) || path == "/")
            {
                return V7Constants.ROOT_INODE;
            }

            // Clean path
            path = path.TrimStart('/').TrimEnd('/');

            if (string.IsNullOrEmpty(path))
            {
                return V7Constants.ROOT_INODE;
            }

            // Start from root directory
            ushort currentInodeNum = V7Constants.ROOT_INODE;
            V7Inode currentInode = _inodeReader.ReadRootInode();

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

                currentInodeNum = entry.Inode;
                currentInode = _inodeReader.ReadInode(currentInodeNum);
            }

            return currentInodeNum;
        }
    }
}

