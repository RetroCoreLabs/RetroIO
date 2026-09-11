//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Collections.Generic;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Result of partition table detection.
    /// </summary>
    public class PartitionDetectionResult
    {
        /// <summary>
        /// Type of partition table detected.
        /// </summary>
        public PartitionTableType TableType { get; set; }

        /// <summary>
        /// List of detected partitions.
        /// </summary>
        public List<PartitionMetadata> Partitions { get; set; } = new List<PartitionMetadata>();

        /// <summary>
        /// True if partitions were detected.
        /// </summary>
        public bool HasPartitions => Partitions.Count > 0;

        /// <summary>
        /// Disk geometry information (if available).
        /// </summary>
        public string? GeometryInfo { get; set; }
    }

    /// <summary>
    /// Unified partition detection service.
    /// Auto-detects PARTITION TABLE formats: BSD Disk Labels, MBR, and GPT.
    ///
    /// IMPORTANT: This detects PARTITION TABLES, not filesystems.
    /// Partition tables divide a disk into partitions. Filesystems store files within partitions.
    ///
    /// Supported partition table formats:
    /// - BSD Disk Label: BSD-specific partition table (partitions a-h), stored at sector 1
    /// - MBR: PC Master Boot Record (partitions 0-3), stored at sector 0
    /// - GPT: GUID Partition Table (future support)
    ///
    /// For filesystem detection, see: IFileSystemDriverProvider implementations
    /// (V7DriverProvider for V7, FfsDriverProvider for FFS/UFS, etc.)
    /// </summary>
    public static class PartitionDetector
    {
        /// <summary>
        /// Detects partition table from a disk image file.
        /// Tries BSD first, then MBR, then GPT (future).
        /// </summary>
        /// <param name="imagePath">Path to disk image file</param>
        /// <returns>Detection result with partition metadata</returns>
        public static PartitionDetectionResult DetectFromFile(string imagePath)
        {
            // Try Sun disk label first (at sector 0, most distinctive magic)
            var sunPartitions = DetectSunPartitions(imagePath);
            if (sunPartitions.Count > 0)
            {
                return new PartitionDetectionResult
                {
                    TableType = PartitionTableType.SunLabel,
                    Partitions = sunPartitions
                };
            }

            // Try BSD disk label (at sector 1)
            var bsdPartitions = DetectBsdPartitions(imagePath);
            if (bsdPartitions.Count > 0)
            {
                return new PartitionDetectionResult
                {
                    TableType = PartitionTableType.BsdDiskLabel,
                    Partitions = bsdPartitions
                };
            }

            // Try MBR (at sector 0, but different format than Sun)
            var mbrPartitions = DetectMbrPartitions(imagePath);
            if (mbrPartitions.Count > 0)
            {
                return new PartitionDetectionResult
                {
                    TableType = PartitionTableType.Mbr,
                    Partitions = mbrPartitions
                };
            }

            // Try GPT (future support)
            // var gptPartitions = DetectGptPartitions(imagePath);
            // if (gptPartitions.Count > 0) { ... }

            // No partitions detected
            return new PartitionDetectionResult
            {
                TableType = PartitionTableType.Unknown,
                Partitions = new List<PartitionMetadata>()
            };
        }

        /// <summary>
        /// Detects BSD Disk Label partitions using reflection to avoid circular dependency.
        ///
        /// Note: BSD Disk Label is a PARTITION TABLE FORMAT (like MBR/GPT), not a filesystem.
        /// It divides a disk into partitions labeled 'a' through 'h', each potentially containing
        /// different filesystems (V7, FFS/UFS, swap, etc.)
        /// </summary>
        private static List<PartitionMetadata> DetectBsdPartitions(string imagePath)
        {
            try
            {
                // Use reflection to call RetroIO.Storage.Local.BSD.Partitions.BsdPartitionManager.DetectFromFile()
                var assembly = System.Reflection.Assembly.Load("RetroIO.Storage.Local");
                var type = assembly.GetType("RetroIO.Storage.Local.BSD.Partitions.BsdPartitionManager");
                if (type == null)
                    return new List<PartitionMetadata>();

                var method = type.GetMethod("DetectFromFile", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method == null)
                    return new List<PartitionMetadata>();

                var result = method.Invoke(null, new object[] { imagePath });
                if (result is List<PartitionMetadata> partitions)
                    return partitions;

                return new List<PartitionMetadata>();
            }
            catch
            {
                return new List<PartitionMetadata>();
            }
        }

        /// <summary>
        /// Detects Sun Disk Label partitions using reflection to avoid circular dependency.
        ///
        /// Note: Sun Disk Label is a PARTITION TABLE FORMAT used by SunOS/Solaris, not a filesystem.
        /// It is stored at sector 0 and divides a disk into partitions labeled 'a' through 'h'.
        /// </summary>
        private static List<PartitionMetadata> DetectSunPartitions(string imagePath)
        {
            try
            {
                // Use reflection to call RetroIO.Storage.Local.Sun.Partitions.SunPartitionManager.DetectFromFile()
                var assembly = System.Reflection.Assembly.Load("RetroIO.Storage.Local");
                var type = assembly.GetType("RetroIO.Storage.Local.Sun.Partitions.SunPartitionManager");
                if (type == null)
                    return new List<PartitionMetadata>();

                var method = type.GetMethod("DetectFromFile", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method == null)
                    return new List<PartitionMetadata>();

                var result = method.Invoke(null, new object[] { imagePath });
                if (result is List<PartitionMetadata> partitions)
                    return partitions;

                return new List<PartitionMetadata>();
            }
            catch
            {
                return new List<PartitionMetadata>();
            }
        }

        /// <summary>
        /// Detects MBR partitions using reflection to avoid circular dependency.
        /// </summary>
        private static List<PartitionMetadata> DetectMbrPartitions(string imagePath)
        {
            try
            {
                // Use reflection to call RetroIO.Storage.Local.MBR.Partitions.MbrPartitionManager.DetectFromFile()
                var assembly = System.Reflection.Assembly.Load("RetroIO.Storage.Local");
                var type = assembly.GetType("RetroIO.Storage.Local.MBR.Partitions.MbrPartitionManager");
                if (type == null)
                    return new List<PartitionMetadata>();

                var method = type.GetMethod("DetectFromFile", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method == null)
                    return new List<PartitionMetadata>();

                var result = method.Invoke(null, new object[] { imagePath });
                if (result is List<PartitionMetadata> partitions)
                    return partitions;

                return new List<PartitionMetadata>();
            }
            catch
            {
                return new List<PartitionMetadata>();
            }
        }
    }
}
