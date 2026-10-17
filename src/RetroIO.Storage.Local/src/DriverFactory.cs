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

namespace RetroIO.Storage.Local
{
    /// <summary>
    /// Factory for creating filesystem drivers with automatic format detection.
    /// Uses confidence-based scoring to determine the best driver for a given image.
    /// </summary>
    public sealed class DriverFactory : IFileSystemDriverFactory
    {
        private readonly Dictionary<string, IFileSystemDriverProvider> _providers;

        /// <summary>
        /// Initializes a new driver factory.
        /// </summary>
        public DriverFactory()
        {
            _providers = new Dictionary<string, IFileSystemDriverProvider>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Registers a filesystem driver provider.
        /// </summary>
        /// <param name="provider">
        /// Provider to register
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when provider is null
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when provider type is already registered
        /// </exception>
        public void Register(IFileSystemDriverProvider provider)
        {
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            if (_providers.ContainsKey(provider.FileSystemType))
            {
                throw new ArgumentException($"Provider for filesystem type '{provider.FileSystemType}' is already registered.");
            }

            _providers[provider.FileSystemType] = provider;
        }

        /// <summary>
        /// Detects filesystem format and creates appropriate driver.
        /// First tries detecting raw filesystem at sector 0.
        /// If that fails, tries detecting partition table and filesystem within first mountable partition.
        /// Reads file ONCE and provides same hints to all drivers for efficiency.
        /// </summary>
        /// <param name="imagePath">
        /// Path to disk image file
        /// </param>
        /// <returns>
        /// Filesystem driver instance, or null if no driver can handle the format
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when imagePath is null or empty
        /// </exception>
        /// <exception cref="FileNotFoundException">
        /// Thrown when file does not exist
        /// </exception>
        public IFileSystemDriver? Detect(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath))
            {
                throw new ArgumentException("Image path cannot be null or empty.", nameof(imagePath));
            }

            if (!File.Exists(imagePath))
            {
                throw new FileNotFoundException($"Image file not found: {imagePath}", imagePath);
            }

            // Try detecting raw filesystem at sector 0 (no partition table)
            IFileSystemDriver? driver = DetectRawFilesystem(imagePath);

            // If raw detection failed, try detecting partitioned disk and filesystem within partition
            if (driver == null)
            {
                driver = DetectInPartition(imagePath);
            }

            return driver;
        }

        /// <summary>
        /// Checks if a partition is swap space (unmountable).
        /// </summary>
        /// <param name="partition">
        /// Partition info to check
        /// </param>
        /// <returns>
        /// True if partition is swap, false otherwise
        /// </returns>
        private static bool IsSwapPartitionInfo(RetroIO.Storage.Local.Partitions.PartitionInfo partition)
        {
            // BSD/V7: FS_SWAP = 1
            // Sun: Type 3 = swap
            // MBR: 0x82 = Linux Swap
            return partition.TableType switch
            {
                RetroIO.Storage.Local.Partitions.PartitionTableType.BsdDiskLabel => partition.FileSystemTypeCode == 1,
                RetroIO.Storage.Local.Partitions.PartitionTableType.SunLabel => partition.FileSystemTypeCode == 3,
                RetroIO.Storage.Local.Partitions.PartitionTableType.Mbr => partition.FileSystemTypeCode == 0x82,
                _ => false
            };
        }

        /// <summary>
        /// Detects filesystem at sector 0 (raw filesystem, no partition table).
        /// </summary>
        /// <param name="imagePath">
        /// Path to disk image file
        /// </param>
        /// <param name="offsetBytes">
        /// Offset in bytes to start detection (default 0)
        /// </param>
        /// <returns>
        /// Filesystem driver instance, or null if no driver can handle the format
        /// </returns>
        private IFileSystemDriver? DetectRawFilesystem(string imagePath, long offsetBytes = 0)
        {
            // Read file ONCE - create detection hints
            DetectionHints hints = CreateDetectionHints(imagePath, offsetBytes);

            // Score all providers using same hints
            IFileSystemDriverProvider? bestProvider = null;
            float bestConfidence = 0.0f;

            int providerCount = _providers.Count;
            if (providerCount == 0)
            {
                return null;
            }

            // Manual iteration to avoid LINQ and foreach
            Dictionary<string, IFileSystemDriverProvider>.Enumerator enumerator = _providers.GetEnumerator();
            while (enumerator.MoveNext())
            {
                IFileSystemDriverProvider provider = enumerator.Current.Value;
                float confidence = provider.DetectConfidence(hints);

                if (confidence > bestConfidence)
                {
                    bestConfidence = confidence;
                    bestProvider = provider;
                }
            }
            enumerator.Dispose();

            // Require minimum confidence threshold
            if (bestProvider == null || bestConfidence < 0.5f)
            {
                return null;
            }

            return bestProvider.CreateDriver();
        }

        /// <summary>
        /// Detects filesystem within partitions when raw detection fails.
        /// Automatically finds first mountable (non-swap) partition and detects filesystem within it.
        /// </summary>
        /// <param name="imagePath">
        /// Path to disk image file
        /// </param>
        /// <returns>
        /// Configured filesystem driver for partition, or null if no mountable partition found
        /// </returns>
        private IFileSystemDriver? DetectInPartition(string imagePath)
        {
            System.Collections.Generic.List<RetroIO.Storage.Local.Partitions.PartitionInfo>? partitions = null;

            try
            {
                partitions = RetroIO.Storage.Local.Partitions.PartitionDetectionService.DetectPartitions(imagePath);
            }
            catch
            {
                // Partition detection failed - not a partitioned disk
                return null;
            }

            if (partitions == null || partitions.Count == 0)
            {
                return null;
            }

            // Find first mountable (non-swap) partition
            for (int i = 0; i < partitions.Count; i++)
            {
                RetroIO.Storage.Local.Partitions.PartitionInfo partition = partitions[i];

                // Skip swap partitions
                if (IsSwapPartitionInfo(partition))
                {
                    continue;
                }

                // Try detecting filesystem at partition offset
                long offsetBytes = (long)partition.OffsetSectors * 512;
                IFileSystemDriver? driver = DetectRawFilesystem(imagePath, offsetBytes);

                if (driver != null)
                {
                    // Return driver WITHOUT pre-configuring partition
                    // DiskImageVolumeProvider will call SelectPartition() with user's choice
                    return driver;
                }
            }

            // No mountable partition with detectable filesystem found
            return null;
        }

        /// <summary>
        /// Detects filesystem format from a stream and creates appropriate driver.
        /// Reads stream ONCE and provides same hints to all drivers for efficiency.
        /// </summary>
        /// <param name="imageStream">
        /// Stream containing disk image
        /// </param>
        /// <returns>
        /// Filesystem driver instance, or null if no driver can handle the format
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when imageStream is null
        /// </exception>
        public IFileSystemDriver? Detect(Stream imageStream)
        {
            if (imageStream == null)
            {
                throw new ArgumentNullException(nameof(imageStream));
            }

            // Read stream ONCE - create detection hints
            DetectionHints hints = CreateDetectionHints(imageStream);

            // Score all providers using same hints
            IFileSystemDriverProvider? bestProvider = null;
            float bestConfidence = 0.0f;

            int providerCount = _providers.Count;
            if (providerCount == 0)
            {
                return null;
            }

            // Manual iteration to avoid LINQ and foreach
            Dictionary<string, IFileSystemDriverProvider>.Enumerator enumerator = _providers.GetEnumerator();
            while (enumerator.MoveNext())
            {
                IFileSystemDriverProvider provider = enumerator.Current.Value;
                float confidence = provider.DetectConfidence(hints);

                if (confidence > bestConfidence)
                {
                    bestConfidence = confidence;
                    bestProvider = provider;
                }
            }
            enumerator.Dispose();

            // Require minimum confidence threshold
            if (bestProvider == null || bestConfidence < 0.5f)
            {
                return null;
            }

            return bestProvider.CreateDriver();
        }

        /// <summary>
        /// Gets detection confidence scores for specific filesystem types.
        /// Reads file ONCE and provides same hints to all requested providers for efficiency.
        /// </summary>
        /// <param name="imagePath">
        /// Path to disk image file
        /// </param>
        /// <param name="filesystemTypes">
        /// Array of filesystem type identifiers to check
        /// </param>
        /// <returns>
        /// Dictionary mapping filesystem type to confidence score (0.0 to 1.0)
        /// </returns>
        public Dictionary<string, float> GetDetectionConfidences(string imagePath, params string[] filesystemTypes)
        {
            var confidences = new Dictionary<string, float>();

            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                return confidences;

            try
            {
                // Read file ONCE - create detection hints
                DetectionHints hints = CreateDetectionHints(imagePath);

                // When no explicit list is given, check ALL registered providers automatically so new
                // filesystem types (Commodore, containers, etc.) are detected without code changes.
                if (filesystemTypes == null || filesystemTypes.Length == 0)
                {
                    Dictionary<string, IFileSystemDriverProvider>.Enumerator all = _providers.GetEnumerator();
                    while (all.MoveNext())
                    {
                        confidences[all.Current.Key] = all.Current.Value.DetectConfidence(hints);
                    }
                }
                else
                {
                    // Get confidence scores for the requested filesystem types.
                    for (int i = 0; i < filesystemTypes.Length; i++)
                    {
                        var provider = GetProvider(filesystemTypes[i]);
                        confidences[filesystemTypes[i]] = provider != null ? provider.DetectConfidence(hints) : 0.0f;
                    }
                }
            }
            catch
            {
                // Error reading file or detecting - return empty confidences
            }

            return confidences;
        }

        /// <summary>
        /// Gets a registered provider by filesystem type.
        /// </summary>
        /// <param name="fileSystemType">
        /// Filesystem type identifier
        /// </param>
        /// <returns>
        /// Provider instance, or null if not registered
        /// </returns>
        public IFileSystemDriverProvider? GetProvider(string fileSystemType)
        {
            if (string.IsNullOrEmpty(fileSystemType))
                return null;

            return _providers.TryGetValue(fileSystemType, out var provider) ? provider : null;
        }

        /// <summary>
        /// Creates a driver for a specific filesystem type without detection.
        /// </summary>
        /// <param name="fileSystemType">
        /// Filesystem type identifier (e.g., "D64", "NDFS")
        /// </param>
        /// <returns>
        /// Filesystem driver instance, or null if type is not registered
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when fileSystemType is null or empty
        /// </exception>
        public IFileSystemDriver? Create(string fileSystemType)
        {
            if (string.IsNullOrEmpty(fileSystemType))
            {
                throw new ArgumentException("Filesystem type cannot be null or empty.", nameof(fileSystemType));
            }

            if (_providers.TryGetValue(fileSystemType, out IFileSystemDriverProvider? provider))
            {
                return provider.CreateDriver();
            }

            return null;
        }

        /// <summary>
        /// Gets all registered filesystem types.
        /// </summary>
        /// <returns>
        /// Array of registered filesystem type identifiers
        /// </returns>
        public string[] GetRegisteredTypes()
        {
            int count = _providers.Count;
            string[] types = new string[count];
            int index = 0;

            Dictionary<string, IFileSystemDriverProvider>.Enumerator enumerator = _providers.GetEnumerator();
            while (enumerator.MoveNext())
            {
                types[index++] = enumerator.Current.Key;
            }
            enumerator.Dispose();

            return types;
        }

        /// <summary>
        /// Gets the maximum header bytes required by all registered providers.
        /// </summary>
        private int GetMaxRequiredHeaderBytes()
        {
            int maxBytes = 512; // Default minimum

            Dictionary<string, IFileSystemDriverProvider>.Enumerator enumerator = _providers.GetEnumerator();
            while (enumerator.MoveNext())
            {
                IFileSystemDriverProvider provider = enumerator.Current.Value;
                int required = provider.RequiredHeaderBytes;
                if (required > maxBytes)
                {
                    maxBytes = required;
                }
            }
            enumerator.Dispose();

            return maxBytes;
        }

        /// <summary>
        /// Creates detection hints from a file path.
        /// Reads file once - reads enough bytes to satisfy all registered providers' requirements.
        /// </summary>
        /// <param name="imagePath">
        /// Path to disk image file
        /// </param>
        /// <param name="offsetBytes">
        /// Offset in bytes to start reading (default 0 for raw filesystem, use partition offset for partitioned disks)
        /// </param>
        private DetectionHints CreateDetectionHints(string imagePath, long offsetBytes = 0)
        {
            DetectionHints hints = new DetectionHints
            {
                FilePath = imagePath,
                FileName = Path.GetFileName(imagePath),
                FileExtension = Path.GetExtension(imagePath)
            };

            try
            {
                FileInfo fileInfo = new FileInfo(imagePath);
                hints.FileSize = fileInfo.Length;

                // Read enough bytes to satisfy all registered providers
                int maxHeaderBytes = GetMaxRequiredHeaderBytes();
                using FileStream stream = File.OpenRead(imagePath);

                // Seek to offset if specified (for partition detection)
                if (offsetBytes > 0)
                {
                    stream.Seek(offsetBytes, SeekOrigin.Begin);
                }

                int toRead = (int)Math.Min(maxHeaderBytes, stream.Length - offsetBytes);
                hints.HeaderBytes = new byte[toRead];
                stream.ReadExactly(hints.HeaderBytes, 0, toRead);
            }
            catch
            {
                // Error reading file - hints will have empty HeaderBytes
                hints.FileSize = 0;
                hints.HeaderBytes = Array.Empty<byte>();
            }

            return hints;
        }

        /// <summary>
        /// Creates detection hints from a stream.
        /// Reads stream once - reads enough bytes to satisfy all registered providers' requirements.
        /// Preserves stream position.
        /// </summary>
        private DetectionHints CreateDetectionHints(Stream imageStream)
        {
            DetectionHints hints = new DetectionHints
            {
                FilePath = string.Empty,
                FileName = string.Empty,
                FileExtension = string.Empty
            };

            long originalPosition = imageStream.Position;
            try
            {
                hints.FileSize = imageStream.Length;

                // Read enough bytes to satisfy all registered providers
                int maxHeaderBytes = GetMaxRequiredHeaderBytes();
                imageStream.Position = 0;
                int toRead = (int)Math.Min(maxHeaderBytes, imageStream.Length);
                hints.HeaderBytes = new byte[toRead];
                imageStream.ReadExactly(hints.HeaderBytes, 0, toRead);
            }
            catch
            {
                // Error reading stream - hints will have empty HeaderBytes
                hints.FileSize = 0;
                hints.HeaderBytes = Array.Empty<byte>();
            }
            finally
            {
                imageStream.Position = originalPosition;
            }

            return hints;
        }
    }
}
