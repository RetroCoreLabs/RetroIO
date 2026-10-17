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
using RetroIO.Storage.Local.FFS.FileSystems;
using RetroIO.Storage.Local.FFS.Structures;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Driver for Berkeley Fast File System (FFS/UFS) images.
    /// Thin wrapper that composes GenericBlockDevice + FfsFileSystem.
    /// Supports SunOS 3.4 / 4.2BSD filesystem format with BSD Disk Label partitions.
    /// </summary>
    public sealed class FfsDriver : IFileSystemDriver, IPartitionProvider, IVolumeInfoProvider
    {
        private GenericBlockDevice? _blockDevice;
        private FfsFileSystem? _filesystem;
        private char _partitionLetter = 'a';  // Default to partition 'a' (root)
        private bool _useDiskLabel = true;     // Default to using disk label
        private string? _imagePath;            // Optional: image path for hardcoded geometry detection
        private long _filesystemOffsetBytes = -1;  // -1 = discover the base (label, else scan)

        public string FileSystemType
        {
            get
            {
                string baseType = _filesystem?.FileSystemType ?? "FFS";
                return _useDiskLabel && _partitionLetter != '\0'
                    ? $"{baseType}-{_partitionLetter}"
                    : baseType;
            }
        }
        public string VolumeLabel => _filesystem?.VolumeLabel ?? string.Empty;
        public string ExtensionSeparator => "";  // Unix FFS doesn't use DOS-style extensions

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
            DriverCapabilities.HardLinks |
            DriverCapabilities.LongFilenames;

        public bool CanRead => true;
        public bool CanWrite => false;  // Phase 2
        public bool HasBlockAccess => true;

        public IFileSystemMetadataDescriptor? GetMetadataDescriptor()
        {
            // TODO: Implement FFS metadata descriptor (permissions, uid/gid, timestamps, fragments)
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
        /// Sets image path for hardcoded geometry detection. Must be called before Mount().
        /// </summary>
        public void SetImagePath(string imagePath)
        {
            _imagePath = imagePath;
        }

        /// <summary>
        /// Pins the byte offset at which the filesystem starts inside the image, bypassing
        /// both partition-table lookup and the superblock scan. Must be called before Mount().
        ///
        /// Use this when the base is already known — for example an NDIX di70 pack, where the
        /// NDIX disk driver skips physical cylinder 0 (reserved for SINTRAN) and maps logical
        /// block 0 to sector 90, i.e. byte 90 * 1024 = 92160.
        /// </summary>
        /// <param name="offsetBytes">Filesystem base in bytes. Must be a multiple of the block size.</param>
        public void SetFilesystemOffset(long offsetBytes)
        {
            if (offsetBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offsetBytes), "Filesystem offset cannot be negative");
            }

            _filesystemOffsetBytes = offsetBytes;
            _useDiskLabel = false;  // An explicit base means there is no partition table to consult
        }

        /// <summary>
        /// Byte offset at which the mounted filesystem actually begins.
        /// 0 for a normal image; non-zero when it sits behind a partition or a reserved gap.
        /// </summary>
        public long FilesystemOffsetBytes => _filesystem?.ResolvedFilesystemOffsetBytes ?? 0;

        public void Mount(Stream imageStream, bool readOnly)
        {
            if (imageStream == null)
            {
                throw new ArgumentNullException(nameof(imageStream));
            }

            // Create block device with 512-byte sectors (standard)
            var descriptor = new BlockDeviceDescriptor
            {
                Name = "FFS Disk",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(imageStream.Length / 512),
                ExpectedImageSize = imageStream.Length
            };

            _blockDevice = new GenericBlockDevice(descriptor);
            _blockDevice.Mount(imageStream, readOnly);

            // Mount FFS filesystem with partition support
            _filesystem = new FfsFileSystem
            {
                PartitionLetter = _partitionLetter,
                UseDiskLabel = _useDiskLabel,
                ImagePath = _imagePath,
                FilesystemOffsetBytes = _filesystemOffsetBytes
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
            throw new NotImplementedException("FFS write operations not yet implemented (Phase 2)");
        }

        public void DeleteFile(string path)
        {
            throw new NotImplementedException("FFS write operations not yet implemented (Phase 2)");
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
            throw new NotImplementedException("FFS write operations not yet implemented (Phase 2)");
        }

        public BlockDeviceInfo? GetBlockDeviceInfo()
        {
            EnsureMounted();
            return _blockDevice!.GetBlockDeviceInfo();
        }

        // ===================================================================
        // VOLUME INFO PROVIDER
        // ===================================================================

        /// <summary>
        /// Describes the mounted FFS volume: where the filesystem actually starts, the
        /// superblock geometry, cylinder-group layout and free-space totals.
        ///
        /// The "Location" section exists because an FFS filesystem does not necessarily begin
        /// at byte 0 of the image, and until you can see the resolved base offset there is no
        /// way to tell a mis-detected volume from a correct one.
        /// </summary>
        public VolumeInfo? GetVolumeInfo()
        {
            if (_blockDevice == null || _filesystem == null)
            {
                return null;
            }

            FfsSuperblock? sb = _filesystem.Superblock;
            if (sb == null)
            {
                return null;
            }

            var info = new VolumeInfo
            {
                FileSystemType = FileSystemType,
                VolumeLabel = VolumeLabel,
                ImagePath = _imagePath ?? string.Empty,
                IsReadOnly = _blockDevice.IsReadOnly
            };

            long baseOffset = _filesystem.ResolvedFilesystemOffsetBytes;
            long fsBytes = (long)sb.fs_size * sb.fs_fsize;

            // -- Location -----------------------------------------------------------------
            var location = info.AddSection("Location", "\U0001F4CD");
            location.Add("Filesystem base", $"{baseOffset:N0} bytes",
                baseOffset == 0 ? "start of image" : $"sector {baseOffset / _blockDevice.BlockSize} of {_blockDevice.BlockSize} bytes");
            location.Add("Primary superblock", $"{baseOffset + FfsConstants.SBLOCK_OFFSET:N0} bytes",
                $"0x{baseOffset + FfsConstants.SBLOCK_OFFSET:X}");
            location.Add("Magic field", $"{baseOffset + FfsConstants.SBLOCK_OFFSET + 1372:N0} bytes",
                $"fs_magic = 0x{sb.fs_magic:X6} (big-endian)");
            location.Add("Filesystem extent", $"{fsBytes:N0} bytes",
                $"base + fs_size x fs_fsize = {baseOffset + fsBytes:N0}");
            if (_useDiskLabel && _partitionLetter != '\0')
            {
                location.Add("Partition", _partitionLetter.ToString(), "from disk label");
            }

            // -- Geometry -----------------------------------------------------------------
            var geometry = info.AddSection("Geometry", "\U0001F4D0");
            geometry.Add("Block size", $"{sb.fs_bsize:N0} bytes", "fs_bsize");
            geometry.Add("Fragment size", $"{sb.fs_fsize:N0} bytes", "fs_fsize");
            geometry.Add("Fragments per block", sb.fs_frag.ToString(), "fs_frag");
            geometry.Add("Total size", $"{sb.fs_size:N0} fragments", "fs_size");
            geometry.Add("Data size", $"{sb.fs_dsize:N0} fragments", "fs_dsize");
            geometry.Add("Sectors per track", sb.fs_nsect.ToString(), "fs_nsect");
            geometry.Add("Tracks per cylinder", sb.fs_ntrak.ToString(), "fs_ntrak");
            geometry.Add("Sectors per cylinder", sb.fs_spc.ToString(), "fs_spc");
            geometry.Add("Cylinders", sb.fs_ncyl.ToString(), "fs_ncyl");

            // -- Cylinder groups ----------------------------------------------------------
            var groups = info.AddSection("Cylinder Groups", "\U0001F5C2");
            groups.Add("Group count", sb.fs_ncg.ToString(), "fs_ncg");
            groups.Add("Cylinders per group", sb.fs_cpg.ToString(), "fs_cpg");
            groups.Add("Inodes per group", sb.fs_ipg.ToString(), "fs_ipg");
            groups.Add("Fragments per group", sb.fs_fpg.ToString(), "fs_fpg");
            groups.Add("Total inodes", $"{(long)sb.fs_ncg * sb.fs_ipg:N0}", "fs_ncg x fs_ipg");
            groups.Add("Superblock offset", $"{sb.fs_sblkno} frags", "fs_sblkno");
            groups.Add("Cyl group offset", $"{sb.fs_cblkno} frags", "fs_cblkno");
            groups.Add("Inode table offset", $"{sb.fs_iblkno} frags", "fs_iblkno");
            groups.Add("First data offset", $"{sb.fs_dblkno} frags", "fs_dblkno");

            // -- Free space ---------------------------------------------------------------
            long freeBytes = (long)sb.fs_cstotal_cs_nbfree * sb.fs_bsize
                           + (long)sb.fs_cstotal_cs_nffree * sb.fs_fsize;

            var free = info.AddSection("Free Space", "\U0001F4CA");
            free.Add("Free blocks", $"{sb.fs_cstotal_cs_nbfree:N0}", "fs_cstotal.cs_nbfree");
            free.Add("Free fragments", $"{sb.fs_cstotal_cs_nffree:N0}", "fs_cstotal.cs_nffree");
            free.Add("Free inodes", $"{sb.fs_cstotal_cs_nifree:N0}", "fs_cstotal.cs_nifree");
            free.Add("Directories", $"{sb.fs_cstotal_cs_ndir:N0}", "fs_cstotal.cs_ndir");
            free.Add("Free space", $"{freeBytes:N0} bytes");
            free.Add("Reserve", $"{sb.fs_minfree}%", "fs_minfree");

            // -- Filesystem state ---------------------------------------------------------
            var state = info.AddSection("Filesystem", "\U0001F5A5");
            state.Add("Magic", $"0x{sb.fs_magic:X6}");
            state.Add("Last written", FormatUnixTime(sb.fs_time), $"fs_time = {sb.fs_time}");
            state.Add("Last mounted on", string.IsNullOrEmpty(sb.fs_fsmnt) ? "(not recorded)" : sb.fs_fsmnt, "fs_fsmnt");
            state.Add("Optimisation", sb.fs_optim == 0 ? "space" : "time", "fs_optim");
            state.Add("Inodes per block", sb.fs_inopb.ToString(), "fs_inopb");
            state.Add("Indirect pointers", sb.fs_nindir.ToString(), "fs_nindir");

            return info;
        }

        /// <summary>
        /// Formats a UNIX epoch timestamp for display, tolerating out-of-range values that
        /// old or hand-built images sometimes carry.
        /// </summary>
        private static string FormatUnixTime(uint value)
        {
            if (value == 0)
            {
                return "(not set)";
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
            }
            catch (ArgumentOutOfRangeException)
            {
                return $"(invalid: {value})";
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
                    RecommendedUrn = $"ffs://{_blockDevice!.GetBlockDeviceInfo()?.GeometryDescription ?? "unknown"}#{letter}",
                    TableType = PartitionTableType.BsdDiskLabel,
                    IsBootable = false
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

            SetPartition(letter);
            return true;
        }

        public string? CurrentPartition => _useDiskLabel ? _partitionLetter.ToString() : null;

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
                throw new InvalidOperationException("FFS image not mounted");
            }
        }
    }
}
