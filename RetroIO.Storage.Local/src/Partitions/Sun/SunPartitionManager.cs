//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.Sun.Structures;

namespace RetroIO.Storage.Local.Sun.Partitions
{
    /// <summary>
    /// Manages Sun Disk Labels (Sun VTOC) and provides partition enumeration.
    ///
    /// Sun Disk Label:
    /// - Stored at sector 0 (unlike BSD which uses sector 1)
    /// - Contains up to 8 partitions (labeled 0-7 or a-h)
    /// - Contains disk geometry information
    /// - Uses magic number 0xDABE for validation
    /// - Used by SunOS, Solaris, and other Sun Unix variants
    /// </summary>
    public class SunPartitionManager
    {
        private readonly IBlockDevice _device;
        private SunDiskLabel? _diskLabel;

        public SunPartitionManager(IBlockDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>
        /// Reads and caches the Sun disk label.
        /// </summary>
        public SunDiskLabel? GetDiskLabel()
        {
            if (_diskLabel != null)
                return _diskLabel;

            try
            {
                // Sun disk label is at sector 0
                byte[] buffer = new byte[512];
                _device.ReadBlock(BlockAddress.FromLBA(0), buffer);

                System.Diagnostics.Debug.WriteLine($"[SunPartitionManager.GetDiskLabel] Read sector 0");
                System.Diagnostics.Debug.WriteLine($"[SunPartitionManager.GetDiskLabel] Magic at offset 508: 0x{BitConverter.ToUInt16(buffer, 508):X4}");

                _diskLabel = SunDiskLabel.Parse(buffer);

                if (_diskLabel != null && !_diskLabel.IsValid())
                {
                    System.Diagnostics.Debug.WriteLine($"[SunPartitionManager.GetDiskLabel] Parsed label is invalid. Magic=0x{_diskLabel.Magic:X4}, expected=0x{SunDiskLabel.SUN_LABEL_MAGIC:X4}");
                    _diskLabel = null;
                }
                else if (_diskLabel != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[SunPartitionManager.GetDiskLabel] Valid Sun label found! Magic=0x{_diskLabel.Magic:X4}");
                    System.Diagnostics.Debug.WriteLine($"[SunPartitionManager.GetDiskLabel] Geometry: {_diskLabel.GetGeometryString()}");
                }

                return _diskLabel;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SunPartitionManager.GetDiskLabel] Exception: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets a specific partition by index (0-7) or letter (a-h).
        /// </summary>
        public SunPartition? GetPartition(char partitionId)
        {
            var label = GetDiskLabel();

            if (label == null)
                return null;

            int index;
            if (partitionId >= '0' && partitionId <= '7')
            {
                index = partitionId - '0';
            }
            else if (partitionId >= 'a' && partitionId <= 'h')
            {
                index = partitionId - 'a';
            }
            else
            {
                return null;
            }

            if (index < 0 || index >= SunDiskLabel.MAX_PARTITIONS)
                return null;

            return label.Partitions[index];
        }

        /// <summary>
        /// Lists all non-empty partitions.
        /// </summary>
        public List<(char Letter, SunPartition Partition)> ListPartitions()
        {
            var label = GetDiskLabel();
            var result = new List<(char, SunPartition)>();

            if (label == null)
                return result;

            for (int i = 0; i < SunDiskLabel.MAX_PARTITIONS; i++)
            {
                var partition = label.Partitions[i];

                if (!partition.IsUnused)
                {
                    char letter = (char)('a' + i);
                    result.Add((letter, partition));
                }
            }

            return result;
        }

        /// <summary>
        /// Gets disk geometry information.
        /// </summary>
        public string GetGeometryInfo()
        {
            var label = GetDiskLabel();

            if (label == null)
                return "No Sun disk label found";

            return label.GetGeometryString();
        }

        /// <summary>
        /// Calculates the starting sector for a partition.
        /// </summary>
        public uint GetPartitionStartSector(SunPartition partition, SunDiskLabel label)
        {
            // Convert cylinder to sector
            uint sectorsPerCylinder = (uint)(label.NumHeads * label.NumSectors);
            return partition.StartCylinder * sectorsPerCylinder;
        }

        /// <summary>
        /// Static helper: Detects Sun partitions from a disk image file.
        /// </summary>
        public static List<PartitionMetadata> DetectFromFile(string imagePath)
        {
            var result = new List<PartitionMetadata>();

            try
            {
                System.Diagnostics.Debug.WriteLine($"[SunPartitionManager.DetectFromFile] Starting detection for: {imagePath}");

                var fileInfo = new System.IO.FileInfo(imagePath);
                if (!fileInfo.Exists)
                {
                    System.Diagnostics.Debug.WriteLine($"[SunPartitionManager.DetectFromFile] File does not exist");
                    return result;
                }

                System.Diagnostics.Debug.WriteLine($"[SunPartitionManager.DetectFromFile] File size: {fileInfo.Length} bytes ({fileInfo.Length / 512} sectors)");

                var descriptor = new BlockDeviceDescriptor
                {
                    Name = "Sun Partition Detection",
                    BlockSize = 512,
                    AddressMode = BlockAddressMode.LBA,
                    TotalBlocks = (uint)(fileInfo.Length / 512)
                };

                var device = new BlockDevices.GenericBlockDevice(descriptor);
                using var stream = System.IO.File.OpenRead(imagePath);
                device.Mount(stream, readOnly: true);

                System.Diagnostics.Debug.WriteLine($"[SunPartitionManager.DetectFromFile] Block device mounted successfully");

                var partMgr = new SunPartitionManager(device);
                var diskLabel = partMgr.GetDiskLabel();

                if (diskLabel == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[SunPartitionManager.DetectFromFile] GetDiskLabel() returned null");
                    return result;
                }

                if (!diskLabel.IsValid())
                {
                    System.Diagnostics.Debug.WriteLine($"[SunPartitionManager.DetectFromFile] Disk label is invalid. Magic=0x{diskLabel.Magic:X4}, expected=0x{SunDiskLabel.SUN_LABEL_MAGIC:X4}");
                    return result;
                }

                System.Diagnostics.Debug.WriteLine($"[SunPartitionManager.DetectFromFile] Valid Sun label found! Magic=0x{diskLabel.Magic:X4}");

                var sunPartitions = partMgr.ListPartitions();
                string normalizedPath = imagePath.Replace('\\', '/');

                System.Diagnostics.Debug.WriteLine($"[SunPartitionManager.DetectFromFile] Found {sunPartitions.Count} non-empty partitions");

                for (int i = 0; i < sunPartitions.Count; i++)
                {
                    var (letter, part) = sunPartitions[i];
                    uint startSector = partMgr.GetPartitionStartSector(part, diskLabel);

                    result.Add(new PartitionMetadata
                    {
                        Id = letter.ToString(),
                        DisplayText = $"{letter} - {part.GetDisplayName(letter)} ({part.NumSectors * 512.0 / (1024.0 * 1024.0):F1} MB)",
                        OffsetSectors = startSector,
                        SizeSectors = part.NumSectors,
                        FileSystemTypeCode = (byte)part.Tag,
                        TableType = PartitionTableType.SunLabel,
                        RecommendedUrn = $"ffs:///{normalizedPath}#{letter}",
                        IsBootable = (part.Tag == 1) // Tag 1 = boot partition
                    });

                    System.Diagnostics.Debug.WriteLine($"[SunPartitionManager.DetectFromFile]   {letter}: start={startSector}, size={part.NumSectors}, tag={part.GetDisplayName(letter)}");
                }

                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SunPartitionManager.DetectFromFile] Exception: {ex}");
                return result;
            }
        }
    }
}
