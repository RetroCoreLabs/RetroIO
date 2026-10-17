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
using RetroIO.Storage.Local.BSD.Partitions;
using RetroIO.Storage.Local.Sun.Partitions;

namespace RetroIO.Storage.Local.Partitions
{
    /// <summary>
    /// Information about a detected partition.
    /// </summary>
    public class PartitionInfo
    {
        /// <summary>
        /// Partition identifier (e.g., "a", "0", "1").
        /// </summary>
        public string Id { get; set; } = "";

        /// <summary>
        /// Display text for UI (e.g., "a - V7-1K (2.11BSD) (7.8 MB)").
        /// </summary>
        public string DisplayText { get; set; } = "";

        /// <summary>
        /// Partition offset in sectors.
        /// </summary>
        public uint OffsetSectors { get; set; }

        /// <summary>
        /// Partition size in sectors.
        /// </summary>
        public uint SizeSectors { get; set; }

        /// <summary>
        /// Filesystem type code (BSD type code or MBR type code).
        /// </summary>
        public byte FileSystemTypeCode { get; set; }

        /// <summary>
        /// Partition table type (BSD, MBR, GPT).
        /// </summary>
        public PartitionTableType TableType { get; set; }

        /// <summary>
        /// Recommended URN for mounting this partition (e.g., "bsd:///E:/disk.img#a").
        /// </summary>
        public string RecommendedUrn { get; set; } = "";

        /// <summary>
        /// Indicates if this partition is marked as bootable (MBR only).
        /// </summary>
        public bool IsBootable { get; set; }
    }

    /// <summary>
    /// Type of partition table detected.
    /// </summary>
    public enum PartitionTableType
    {
        Unknown = 0,
        BsdDiskLabel = 1,
        Mbr = 2,
        Gpt = 3,
        SunLabel = 4
    }

    /// <summary>
    /// Service for detecting partitions on disk images.
    /// Supports BSD disk labels, MBR, and GPT partition tables.
    /// </summary>
    public class PartitionDetectionService
    {
        /// <summary>
        /// Detects all partitions on a disk image file.
        /// </summary>
        /// <param name="imagePath">Path to disk image file.</param>
        /// <returns>List of detected partitions.</returns>
        public static List<PartitionInfo> DetectPartitions(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath))
                throw new ArgumentNullException(nameof(imagePath));

            var partitions = new List<PartitionInfo>();

            // Try Sun disk label first (at sector 0)
            var sunPartitions = DetectSunPartitions(imagePath);
            if (sunPartitions.Count > 0)
                return sunPartitions;

            // Try BSD disk label (at sector 1)
            var bsdPartitions = DetectBsdPartitions(imagePath);
            if (bsdPartitions.Count > 0)
                return bsdPartitions;

            // Try MBR
            var mbrPartitions = DetectMbrPartitions(imagePath);
            if (mbrPartitions.Count > 0)
                return mbrPartitions;

            // Try GPT (future)
            // var gptPartitions = DetectGptPartitions(imagePath);
            // if (gptPartitions.Count > 0)
            //     return gptPartitions;

            return partitions;
        }

        /// <summary>
        /// Detects Sun disk label partitions.
        /// </summary>
        public static List<PartitionInfo> DetectSunPartitions(string imagePath)
        {
            var partitions = new List<PartitionInfo>();

            try
            {
                var metadata = SunPartitionManager.DetectFromFile(imagePath);

                for (int i = 0; i < metadata.Count; i++)
                {
                    var meta = metadata[i];
                    partitions.Add(new PartitionInfo
                    {
                        Id = meta.Id,
                        DisplayText = meta.DisplayText,
                        OffsetSectors = meta.OffsetSectors,
                        SizeSectors = meta.SizeSectors,
                        FileSystemTypeCode = meta.FileSystemTypeCode,
                        TableType = (PartitionTableType)meta.TableType, // Cast from Core enum to local enum
                        RecommendedUrn = meta.RecommendedUrn,
                        IsBootable = meta.IsBootable
                    });
                }
            }
            catch
            {
                // Failed to detect Sun partitions
            }

            return partitions;
        }

        /// <summary>
        /// Detects BSD disk label partitions.
        /// </summary>
        public static List<PartitionInfo> DetectBsdPartitions(string imagePath)
        {
            var partitions = new List<PartitionInfo>();

            try
            {
                var descriptor = new BlockDeviceDescriptor
                {
                    Name = "BSD Partition Detection",
                    BlockSize = 512,
                    AddressMode = BlockAddressMode.LBA,
                    TotalBlocks = (uint)(new System.IO.FileInfo(imagePath).Length / 512)
                };

                var device = new GenericBlockDevice(descriptor);
                using var stream = new System.IO.FileStream(imagePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);
                device.Mount(stream, readOnly: true);

                var partMgr = new BsdPartitionManager(device);
                var diskLabel = partMgr.GetDiskLabel();

                if (diskLabel != null && diskLabel.IsValid())
                {
                    var bsdPartitions = partMgr.ListPartitions();
                    for (int i = 0; i < bsdPartitions.Count; i++)
                    {
                        var (letter, part) = bsdPartitions[i];
                        double sizeMB = (part.Size * 512.0) / (1024.0 * 1024.0);
                        string normalizedPath = imagePath.Replace('\\', '/');

                        partitions.Add(new PartitionInfo
                        {
                            Id = letter.ToString(),
                            DisplayText = $"{letter} - {part.FileSystemTypeName} ({sizeMB:F1} MB)",
                            OffsetSectors = part.Offset,
                            SizeSectors = part.Size,
                            FileSystemTypeCode = part.FsType,
                            TableType = PartitionTableType.BsdDiskLabel,
                            RecommendedUrn = $"bsd:///{normalizedPath}#{letter}",
                            IsBootable = false // BSD partitions don't have bootable flag
                        });
                    }
                }
            }
            catch
            {
                // Failed to detect BSD partitions
            }

            return partitions;
        }

        /// <summary>
        /// Detects MBR partition table.
        /// </summary>
        public static List<PartitionInfo> DetectMbrPartitions(string imagePath)
        {
            var partitions = new List<PartitionInfo>();

            try
            {
                using var fs = new System.IO.FileStream(imagePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);

                // MBR is at sector 0
                if (fs.Length < 512)
                    return partitions;

                fs.Seek(0, System.IO.SeekOrigin.Begin);
                byte[] mbrBytes = new byte[512];
                fs.ReadExactly(mbrBytes, 0, 512);

                // Check for MBR signature (0x55AA at offset 510)
                if (mbrBytes[510] != 0x55 || mbrBytes[511] != 0xAA)
                    return partitions;

                // Read 4 partition entries starting at offset 446
                for (int i = 0; i < 4; i++)
                {
                    int offset = 446 + (i * 16);

                    byte status = mbrBytes[offset + 0];          // Boot indicator
                    byte partType = mbrBytes[offset + 4];        // Partition type
                    uint lbaStart = BitConverter.ToUInt32(mbrBytes, offset + 8);
                    uint numSectors = BitConverter.ToUInt32(mbrBytes, offset + 12);

                    if (partType != 0 && numSectors > 0)
                    {
                        string fsType = GetMbrPartitionTypeName(partType);
                        double sizeMB = (numSectors * 512.0) / (1024.0 * 1024.0);
                        bool bootable = (status & 0x80) != 0;
                        string normalizedPath = imagePath.Replace('\\', '/');

                        partitions.Add(new PartitionInfo
                        {
                            Id = i.ToString(),
                            DisplayText = $"{i + 1} - {fsType} ({sizeMB:F1} MB){(bootable ? " [Bootable]" : "")}",
                            OffsetSectors = lbaStart,
                            SizeSectors = numSectors,
                            FileSystemTypeCode = partType,
                            TableType = PartitionTableType.Mbr,
                            RecommendedUrn = $"img:///{normalizedPath}#{i}",
                            IsBootable = bootable
                        });
                    }
                }
            }
            catch
            {
                // Failed to detect MBR partitions
            }

            return partitions;
        }

        /// <summary>
        /// Gets friendly name for MBR partition type code.
        /// </summary>
        public static string GetMbrPartitionTypeName(byte type)
        {
            return type switch
            {
                0x00 => "Empty",
                0x01 => "FAT12",
                0x04 => "FAT16 <32M",
                0x05 => "Extended",
                0x06 => "FAT16",
                0x07 => "NTFS/exFAT",
                0x0B => "FAT32",
                0x0C => "FAT32 LBA",
                0x0E => "FAT16 LBA",
                0x0F => "Extended LBA",
                0x11 => "Hidden FAT12",
                0x14 => "Hidden FAT16 <32M",
                0x16 => "Hidden FAT16",
                0x1B => "Hidden FAT32",
                0x1C => "Hidden FAT32 LBA",
                0x1E => "Hidden FAT16 LBA",
                0x82 => "Linux Swap",
                0x83 => "Linux",
                0x85 => "Linux Extended",
                0x8E => "Linux LVM",
                0xA5 => "FreeBSD",
                0xA6 => "OpenBSD",
                0xA8 => "macOS UFS",
                0xA9 => "NetBSD",
                0xAB => "macOS Boot",
                0xEE => "GPT Protective",
                0xEF => "EFI System",
                _ => $"0x{type:X2}"
            };
        }
    }
}

