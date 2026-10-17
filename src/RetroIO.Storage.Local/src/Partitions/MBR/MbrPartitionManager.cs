//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.MBR.Structures;

namespace RetroIO.Storage.Local.MBR.Partitions
{
    /// <summary>
    /// Manages MBR (Master Boot Record) partitions and provides utilities for partition enumeration.
    /// </summary>
    public class MbrPartitionManager
    {
        private readonly IBlockDevice _device;
        private MbrPartitionTable? _partitionTable;

        public MbrPartitionManager(IBlockDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>
        /// Reads and caches the MBR partition table.
        /// </summary>
        public MbrPartitionTable? GetPartitionTable()
        {
            if (_partitionTable != null)
                return _partitionTable;

            try
            {
                // Read MBR (sector 0)
                byte[] mbrBytes = new byte[512];
                _device.ReadBlock(BlockAddress.FromLBA(0), mbrBytes);

                _partitionTable = MbrPartitionTable.Parse(mbrBytes);

                if (!_partitionTable.IsValid())
                {
                    _partitionTable = null;
                }

                return _partitionTable;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets a specific partition by index (0-3).
        /// </summary>
        public MbrPartitionEntry? GetPartition(int index)
        {
            var table = GetPartitionTable();

            if (table == null || index < 0 || index >= 4)
                return null;

            var partition = table.Partitions[index];

            return partition.IsUnused ? null : partition;
        }

        /// <summary>
        /// Lists all non-empty partitions.
        /// </summary>
        public List<(int Index, MbrPartitionEntry Entry)> ListPartitions()
        {
            var table = GetPartitionTable();
            var result = new List<(int, MbrPartitionEntry)>();

            if (table == null)
                return result;

            for (int i = 0; i < 4; i++)
            {
                var partition = table.Partitions[i];

                if (!partition.IsUnused)
                {
                    result.Add((i, partition));
                }
            }

            return result;
        }

        /// <summary>
        /// Finds the first bootable partition.
        /// </summary>
        public (int Index, MbrPartitionEntry Entry)? FindBootablePartition()
        {
            var partitions = ListPartitions();

            for (int i = 0; i < partitions.Count; i++)
            {
                if (partitions[i].Entry.IsBootable)
                {
                    return partitions[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Static helper: Detects MBR partitions from a disk image file.
        /// Creates necessary block device infrastructure and returns partition metadata.
        /// </summary>
        /// <param name="imagePath">Path to disk image file</param>
        /// <returns>List of detected MBR partitions, or empty list if no valid MBR found</returns>
        public static List<PartitionMetadata> DetectFromFile(string imagePath)
        {
            var result = new List<PartitionMetadata>();

            try
            {
                // Check file exists and is at least 512 bytes
                var fileInfo = new System.IO.FileInfo(imagePath);
                if (!fileInfo.Exists || fileInfo.Length < 512)
                    return result;

                // Read MBR directly from file (no need for block device for detection)
                byte[] mbrBytes = new byte[512];
                using (var stream = System.IO.File.OpenRead(imagePath))
                {
                    int bytesRead = stream.Read(mbrBytes, 0, 512);
                    if (bytesRead < 512)
                        return result;
                }

                // Parse MBR
                var mbr = MbrPartitionTable.Parse(mbrBytes);

                if (!mbr.IsValid())
                    return result;

                // Convert MBR partitions to unified PartitionMetadata
                string normalizedPath = imagePath.Replace('\\', '/');

                for (int i = 0; i < 4; i++)
                {
                    var partition = mbr.Partitions[i];

                    if (!partition.IsUnused)
                    {
                        double sizeMB = (partition.NumSectors * 512.0) / (1024.0 * 1024.0);
                        string bootableFlag = partition.IsBootable ? " [Bootable]" : "";

                        result.Add(new PartitionMetadata
                        {
                            Id = i.ToString(),
                            DisplayText = $"{i} - {partition.TypeName} ({sizeMB:F1} MB){bootableFlag}",
                            OffsetSectors = partition.LbaStart,
                            SizeSectors = partition.NumSectors,
                            FileSystemTypeCode = partition.PartitionType,
                            TableType = PartitionTableType.Mbr,
                            RecommendedUrn = $"img:///{normalizedPath}#{i}",
                            IsBootable = partition.IsBootable
                        });
                    }
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
