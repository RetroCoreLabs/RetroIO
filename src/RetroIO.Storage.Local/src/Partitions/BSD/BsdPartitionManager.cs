//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.BSD.Structures;

namespace RetroIO.Storage.Local.BSD.Partitions
{
    /// <summary>
    /// Manages BSD Disk Labels and provides utilities for partition enumeration.
    ///
    /// IMPORTANT NAMING NOTE:
    /// BSD Disk Label is a PARTITION TABLE FORMAT (like MBR or GPT), NOT a filesystem.
    ///
    /// BSD Disk Label:
    /// - Stored at sector 1 (offset 512 bytes) on BSD disks
    /// - Contains partition table entries labeled 'a' through 'h' (up to 8 partitions)
    /// - Contains disk geometry information (cylinders, heads, sectors)
    /// - Contains filesystem type codes for each partition (V7, FFS/UFS, swap, etc.)
    /// - Similar in purpose to MBR (PC partition table) or GPT (GUID partition table)
    ///
    /// Layering:
    ///   Disk → [Optional: MBR/GPT] → BSD Disk Label → Partition → Filesystem (V7/FFS/UFS)
    ///
    /// This class operates at the PARTITION TABLE layer, not the filesystem layer.
    /// For filesystem access, see: V7DriverProvider (V7), FfsDriverProvider (FFS/UFS)
    ///
    /// References:
    /// - NetBSD: sys/sys/disklabel.h
    /// - FreeBSD: geom/geom_bsd_label.c
    /// - OpenBSD: sys/sys/disklabel.h
    /// </summary>
    public class BsdPartitionManager
    {
        private readonly IBlockDevice _device;
        private BsdDiskLabel? _diskLabel;

        /// <summary>
        /// Creates a partition manager for the given block device.
        /// </summary>
        /// <param name="device">
        /// The device that holds the disk label.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="device"/> is null.
        /// </exception>
        public BsdPartitionManager(IBlockDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>
        /// Reads and caches the disk label.
        /// </summary>
        public BsdDiskLabel? GetDiskLabel()
        {
            if (_diskLabel != null)
                return _diskLabel;

            try
            {
                int physicalSectorSize = _device.BlockSize;
                System.Diagnostics.Debug.WriteLine($"[BsdPartitionManager.GetDiskLabel] Physical sector size: {physicalSectorSize}");

                if (physicalSectorSize == 512)
                {
                    // Read sector 1 directly
                    byte[] buffer = new byte[512];
                    _device.ReadBlock(BlockAddress.FromLBA(1), buffer);
                    System.Diagnostics.Debug.WriteLine($"[BsdPartitionManager.GetDiskLabel] Read sector 1 (512-byte mode)");
                    System.Diagnostics.Debug.WriteLine($"[BsdPartitionManager.GetDiskLabel] First 16 bytes: {BitConverter.ToString(buffer, 0, 16)}");
                    _diskLabel = BsdDiskLabel.Parse(buffer);
                }
                else if (physicalSectorSize == 1024)
                {
                    // Label is in second half of block 0
                    byte[] block = new byte[1024];
                    _device.ReadBlock(BlockAddress.FromLBA(0), block);

                    byte[] labelBytes = new byte[512];
                    Array.Copy(block, 512, labelBytes, 0, 512);
                    System.Diagnostics.Debug.WriteLine($"[BsdPartitionManager.GetDiskLabel] Read block 0 second half (1024-byte mode)");
                    System.Diagnostics.Debug.WriteLine($"[BsdPartitionManager.GetDiskLabel] First 16 bytes: {BitConverter.ToString(labelBytes, 0, 16)}");
                    _diskLabel = BsdDiskLabel.Parse(labelBytes);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[BsdPartitionManager.GetDiskLabel] Unsupported sector size: {physicalSectorSize}");
                    return null;
                }

                if (_diskLabel != null && !_diskLabel.IsValid())
                {
                    System.Diagnostics.Debug.WriteLine($"[BsdPartitionManager.GetDiskLabel] Parsed label is invalid. Magic=0x{_diskLabel.Magic:X8}");
                    _diskLabel = null;
                }
                else if (_diskLabel != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[BsdPartitionManager.GetDiskLabel] Valid label found! Magic=0x{_diskLabel.Magic:X8}, NumPartitions={_diskLabel.NumPartitions}");
                }

                return _diskLabel;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BsdPartitionManager.GetDiskLabel] Exception: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets a specific partition by letter ('a' through 'h').
        /// </summary>
        public BsdPartition? GetPartition(char letter)
        {
            var label = GetDiskLabel();

            if (label == null)
                return null;

            int index = BsdPartition.GetPartitionIndex(letter);

            if (index < 0 || index >= label.NumPartitions)
                return null;

            return label.Partitions[index];
        }

        /// <summary>
        /// Lists all non-empty partitions.
        /// </summary>
        public List<(char Letter, BsdPartition Partition)> ListPartitions()
        {
            var label = GetDiskLabel();
            var result = new List<(char, BsdPartition)>();

            if (label == null)
                return result;

            for (int i = 0; i < label.NumPartitions; i++)
            {
                var partition = label.Partitions[i];

                if (!partition.IsUnused)
                {
                    char letter = BsdPartition.GetPartitionLetter(i);
                    result.Add((letter, partition));
                }
            }

            return result;
        }

        /// <summary>
        /// Finds the root filesystem partition (typically 'a').
        /// </summary>
        public (char Letter, BsdPartition Partition)? FindRootPartition()
        {
            var partitions = ListPartitions();

            // First, try partition 'a'
            for (int i = 0; i < partitions.Count; i++)
            {
                if (partitions[i].Letter == 'a' && partitions[i].Partition.IsV7Filesystem)
                {
                    return partitions[i];
                }
            }

            // If 'a' doesn't exist, find first V7 filesystem
            for (int i = 0; i < partitions.Count; i++)
            {
                if (partitions[i].Partition.IsV7Filesystem)
                {
                    return partitions[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Validates all partitions in the disk label.
        /// </summary>
        public bool ValidatePartitions()
        {
            var label = GetDiskLabel();

            if (label == null)
                return false;

            for (int i = 0; i < label.NumPartitions; i++)
            {
                if (!label.Partitions[i].Validate(label.SectorsPerUnit))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Gets disk geometry information.
        /// </summary>
        public string GetGeometryInfo()
        {
            var label = GetDiskLabel();

            if (label == null)
                return "No disk label found";

            return $"{label.CylindersPerUnit} cylinders × " +
                   $"{label.TracksPerCylinder} heads × " +
                   $"{label.SectorsPerTrack} sectors = " +
                   $"{label.SectorsPerUnit} sectors " +
                   $"({label.SectorsPerUnit * label.BytesPerSector / (1024 * 1024)} MB)";
        }

        /// <summary>
        /// Static helper: Detects BSD partitions from a disk image file.
        /// Creates necessary block device infrastructure and returns partition metadata.
        /// </summary>
        /// <param name="imagePath">
        /// Path to disk image file
        /// </param>
        /// <returns>
        /// List of detected BSD partitions, or empty list if no BSD disk label found
        /// </returns>
        public static List<PartitionMetadata> DetectFromFile(string imagePath)
        {
            var result = new List<PartitionMetadata>();

            try
            {
                System.Diagnostics.Debug.WriteLine($"[BsdPartitionManager.DetectFromFile] Starting detection for: {imagePath}");

                // Create block device descriptor
                var fileInfo = new System.IO.FileInfo(imagePath);
                if (!fileInfo.Exists)
                {
                    System.Diagnostics.Debug.WriteLine($"[BsdPartitionManager.DetectFromFile] File does not exist");
                    return result;
                }

                System.Diagnostics.Debug.WriteLine($"[BsdPartitionManager.DetectFromFile] File size: {fileInfo.Length} bytes ({fileInfo.Length / 512} sectors)");

                var descriptor = new BlockDeviceDescriptor
                {
                    Name = "BSD Partition Detection",
                    BlockSize = 512,
                    AddressMode = BlockAddressMode.LBA,
                    TotalBlocks = (uint)(fileInfo.Length / 512)
                };

                // Create and mount block device
                var device = new BlockDevices.GenericBlockDevice(descriptor);
                using var stream = System.IO.File.OpenRead(imagePath);
                device.Mount(stream, readOnly: true);

                System.Diagnostics.Debug.WriteLine($"[BsdPartitionManager.DetectFromFile] Block device mounted successfully");

                // Create partition manager and detect partitions
                var partMgr = new BsdPartitionManager(device);
                var diskLabel = partMgr.GetDiskLabel();

                if (diskLabel == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[BsdPartitionManager.DetectFromFile] GetDiskLabel() returned null");
                    return result;
                }

                if (!diskLabel.IsValid())
                {
                    System.Diagnostics.Debug.WriteLine($"[BsdPartitionManager.DetectFromFile] Disk label is invalid. Magic=0x{diskLabel.Magic:X8}, expected=0x82564557");
                    return result;
                }

                System.Diagnostics.Debug.WriteLine($"[BsdPartitionManager.DetectFromFile] Valid disk label found. Magic=0x{diskLabel.Magic:X8}, NumPartitions={diskLabel.NumPartitions}");


                // Convert BSD partitions to unified PartitionMetadata
                var bsdPartitions = partMgr.ListPartitions();
                string normalizedPath = imagePath.Replace('\\', '/');

                for (int i = 0; i < bsdPartitions.Count; i++)
                {
                    var (letter, part) = bsdPartitions[i];

                    result.Add(new PartitionMetadata
                    {
                        Id = letter.ToString(),
                        DisplayText = $"{letter} - {part.FileSystemTypeName} ({part.Size * 512.0 / (1024.0 * 1024.0):F1} MB)",
                        OffsetSectors = part.Offset,
                        SizeSectors = part.Size,
                        FileSystemTypeCode = part.FsType,
                        TableType = PartitionTableType.BsdDiskLabel,
                        RecommendedUrn = $"bsd:///{normalizedPath}#{letter}",
                        IsBootable = false // BSD partitions don't have bootable flag
                    });
                }

                return result;
            }
            catch
            {
                // Detection failed - return empty list
                return result;
            }
        }
    }
}

