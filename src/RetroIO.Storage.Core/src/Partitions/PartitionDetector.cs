//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Collections.Generic;

namespace RetroIO.Storage.Core.Partitions
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
    /// Auto-detects BSD disk labels, MBR, and GPT partition tables.
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
            // Try BSD disk label first (uses reflection to avoid direct dependency)
            var bsdPartitions = DetectBsdPartitions(imagePath);
            if (bsdPartitions.Count > 0)
            {
                return new PartitionDetectionResult
                {
                    TableType = PartitionTableType.BsdDiskLabel,
                    Partitions = bsdPartitions
                };
            }

            // Try MBR
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
                TableType = PartitionTableType.None,
                Partitions = new List<PartitionMetadata>()
            };
        }

        /// <summary>
        /// Detects BSD disk label partitions using reflection to avoid circular dependency.
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
