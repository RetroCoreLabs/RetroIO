//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Collections.Generic;
using RetroIO.Storage.Core.Partitions;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Complete disk image analysis result.
    /// Combines filesystem detection and partition detection in one call.
    /// </summary>
    public class DiskImageAnalysisResult
    {
        /// <summary>
        /// Path to the analyzed disk image.
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// File size in bytes.
        /// </summary>
        public long FileSize { get; set; }

        /// <summary>
        /// Detected filesystem type (e.g., "BSD-V7", "NDFS", "D64").
        /// </summary>
        public string FileSystemType { get; set; } = "Unknown";

        /// <summary>
        /// Detected partition table type.
        /// </summary>
        public PartitionTableType PartitionTableType { get; set; }

        /// <summary>
        /// List of detected partitions.
        /// </summary>
        public List<PartitionMetadata> Partitions { get; set; } = new List<PartitionMetadata>();

        /// <summary>
        /// True if partitions were detected.
        /// </summary>
        public bool HasPartitions => Partitions.Count > 0;

        /// <summary>
        /// Recommended action for mounting.
        /// </summary>
        public string RecommendedAction { get; set; } = string.Empty;

        /// <summary>
        /// Filesystem detection confidence score (0.0 to 1.0).
        /// </summary>
        public float DetectionConfidence { get; set; }
    }

    /// <summary>
    /// Integrated disk image detection and analysis.
    /// One-call API that combines filesystem type detection and partition detection.
    /// </summary>
    public static class DiskImageDetector
    {
        /// <summary>
        /// Analyzes a disk image file and returns complete information.
        /// Detects both filesystem type and partition table in one call.
        /// </summary>
        /// <param name="imagePath">Path to disk image file</param>
        /// <returns>Complete analysis result</returns>
        public static DiskImageAnalysisResult Analyze(string imagePath)
        {
            var result = new DiskImageAnalysisResult
            {
                FilePath = imagePath
            };

            try
            {
                // Get file size
                var fileInfo = new System.IO.FileInfo(imagePath);
                if (!fileInfo.Exists)
                {
                    result.RecommendedAction = $"Error: File not found: {imagePath}";
                    return result;
                }

                result.FileSize = fileInfo.Length;

                // 1. Detect filesystem type using DriverFactory (via reflection to avoid circular dependency)
                var (fsType, confidence) = DetectFileSystemType(imagePath);
                result.FileSystemType = fsType;
                result.DetectionConfidence = confidence;

                // 2. Detect partitions
                var partitionResult = PartitionDetector.DetectFromFile(imagePath);
                result.PartitionTableType = partitionResult.TableType;
                result.Partitions = partitionResult.Partitions;

                // 3. Build recommended action
                if (result.HasPartitions)
                {
                    var firstPartition = result.Partitions[0];
                    result.RecommendedAction = $"Mount partition '{firstPartition.Id}' using URN: {firstPartition.RecommendedUrn}";
                }
                else
                {
                    string normalizedPath = imagePath.Replace('\\', '/');
                    result.RecommendedAction = $"Mount entire disk using URN: file:///{normalizedPath}";
                }

                return result;
            }
            catch (System.Exception ex)
            {
                result.RecommendedAction = $"Error analyzing disk image: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Detects filesystem type using DriverFactory via reflection.
        /// </summary>
        private static (string FileSystemType, float Confidence) DetectFileSystemType(string imagePath)
        {
            try
            {
                // Use reflection to call DriverFactory.Detect()
                var assembly = System.Reflection.Assembly.Load("RetroIO.Storage.Local");
                var factoryType = assembly.GetType("RetroIO.Storage.Local.DriverFactory");
                if (factoryType == null)
                    return ("Unknown", 0.0f);

                // Create DriverFactory instance
                var factory = System.Activator.CreateInstance(factoryType);
                if (factory == null)
                    return ("Unknown", 0.0f);

                // Call Detect(imagePath)
                var detectMethod = factoryType.GetMethod("Detect", new System.Type[] { typeof(string) });
                if (detectMethod == null)
                    return ("Unknown", 0.0f);

                var driver = detectMethod.Invoke(factory, new object[] { imagePath });
                if (driver == null)
                    return ("Unknown", 0.0f);

                // Get FileSystemType property
                var driverType = driver.GetType();
                var fsTypeProp = driverType.GetProperty("FileSystemType");
                if (fsTypeProp == null)
                    return ("Unknown", 0.0f);

                string fsType = (string?)fsTypeProp.GetValue(driver) ?? "Unknown";

                // Dispose driver
                if (driver is System.IDisposable disposable)
                    disposable.Dispose();

                // Return type with high confidence (since it was successfully detected)
                return (fsType, 0.9f);
            }
            catch
            {
                return ("Unknown", 0.0f);
            }
        }
    }
}
