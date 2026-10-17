//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RetroIO.Storage.Core;
using RetroIO.Storage.Core.Utilities;
using RetroIO.Storage.Local.Partitions;

namespace RetroIO.Storage.Local
{
    /// <summary>
    /// Volume provider for retro disk image files (D64, D71, D81, T64, etc.).
    /// Manages mounted disk images and delegates operations to filesystem drivers.
    /// NOTE: This provider does NOT handle persistence - use MountManager for centralized persistence.
    /// </summary>
    public sealed class DiskImageVolumeProvider : IVolumeProvider
    {
        private readonly IFileSystemDriverFactory _factory;
        private readonly Dictionary<string, Mount> _mounts;
        private bool _disposed;

        /// <summary>
        /// Gets the volume type ("diskimage").
        /// </summary>
        public string Type => "diskimage";

        // VFS Properties
        public string[] SupportedSchemes => new[] { "d64", "d71", "d81", "ndfs", "ffs", "ufs", "bsd", "img", "vdk", "dmk", "t64", "diskimage" };
        public bool IsRemote => false;

        /// <summary>
        /// Initializes a new disk image volume provider.
        /// NOTE: Persistence is handled by MountManager, not this provider.
        /// </summary>
        /// <param name="factory">
        /// Factory for creating filesystem drivers
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when factory is null
        /// </exception>
        public DiskImageVolumeProvider(IFileSystemDriverFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _mounts = new Dictionary<string, Mount>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Validates whether a mount entry can be restored by this provider.
        /// </summary>
        /// <param name="entry">
        /// Mount configuration entry to validate
        /// </param>
        /// <param name="errorMessage">
        /// Error message if validation fails, or null if successful
        /// </param>
        /// <returns>
        /// True if mount can be restored, false otherwise
        /// </returns>
        public bool CanRestore(UnifiedMountEntry entry, out string? errorMessage)
        {
            // Extract and clean file path from URN
            string imagePath = PathCleaner.ExtractFilePathFromUri(entry.SourceUri);

            if (!PathCleaner.ValidateFilePath(imagePath, out errorMessage))
            {
                return false;
            }

            errorMessage = null;
            return true;
        }


        /// <summary>
        /// Mounts a volume from a unified configuration entry.
        /// </summary>
        /// <param name="entry">
        /// Mount configuration entry containing all parameters
        /// </param>
        /// <returns>
        /// Information about the mounted volume
        /// </returns>
        public MountInfo MountFromConfig(UnifiedMountEntry entry)
        {
            return Mount(entry.SourceUri, entry.VolumeId, entry.IsReadOnly, entry.FileSystemType);
        }

        /// <summary>
        /// Mounts a disk image and makes it accessible via the specified volume ID.
        /// Uses automatic format detection unless a specific filesystem type is provided.
        /// </summary>
        /// <param name="uri">
        /// Path to disk image file
        /// </param>
        /// <param name="volumeId">
        /// Unique identifier for this mount
        /// </param>
        /// <param name="readOnly">
        /// If true, mount as read-only
        /// </param>
        /// <returns>
        /// Information about the mounted volume
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when arguments are invalid or volume ID is already in use
        /// </exception>
        /// <exception cref="FileNotFoundException">
        /// Thrown when image file does not exist
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when mount operation fails
        /// </exception>
        public MountInfo Mount(string uri, string volumeId, bool readOnly)
        {
            return Mount(uri, volumeId, readOnly, forceFileSystemType: null);
        }

        /// <summary>
        /// Mounts a disk image and makes it accessible via the specified volume ID.
        /// </summary>
        /// <param name="uri">
        /// Path to disk image file
        /// </param>
        /// <param name="volumeId">
        /// Unique identifier for this mount
        /// </param>
        /// <param name="readOnly">
        /// If true, mount as read-only
        /// </param>
        /// <param name="forceFileSystemType">
        /// Force specific filesystem type (e.g., "D64", "D71"), or null for automatic detection
        /// </param>
        /// <returns>
        /// Information about the mounted volume
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when arguments are invalid, volume ID is already in use, or forced type is not registered
        /// </exception>
        /// <exception cref="FileNotFoundException">
        /// Thrown when image file does not exist
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when mount operation fails
        /// </exception>
        public MountInfo Mount(string uri, string volumeId, bool readOnly, string? forceFileSystemType)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrEmpty(uri))
            {
                throw new ArgumentException("URI cannot be null or empty.", nameof(uri));
            }

            if (string.IsNullOrEmpty(volumeId))
            {
                throw new ArgumentException("Volume ID cannot be null or empty.", nameof(volumeId));
            }

            if (_mounts.ContainsKey(volumeId))
            {
                throw new ArgumentException($"Volume ID '{volumeId}' is already in use.", nameof(volumeId));
            }

            // Parse URI to extract scheme, path, and partition fragment
            // Format: scheme:///path#partition
            // Examples: bsd:///E:/disk.img#a, d64:///C:/game.d64, ndfs:///disk.ndfs

            FileSystemScheme scheme = ExtractScheme(uri);

            // Extract partition fragment first
            string? partition = null;
            int fragmentIndex = uri.IndexOf('#');
            if (fragmentIndex >= 0)
            {
                partition = uri.Substring(fragmentIndex + 1);
            }

            // Extract and clean file path from URN
            string imagePath = PathCleaner.ExtractFilePathFromUri(uri);

            // Determine if user explicitly selected a filesystem type via URN scheme
            string? explicitType = scheme.ToDriverTypeName();

            // Create driver
            IFileSystemDriver? driver;

            // Priority 1: Explicit forceFileSystemType parameter (API/programmatic usage)
            if (!string.IsNullOrEmpty(forceFileSystemType))
            {
                driver = _factory.Create(forceFileSystemType);
                if (driver == null)
                {
                    throw new ArgumentException($"Filesystem type '{forceFileSystemType}' is not registered.", nameof(forceFileSystemType));
                }
            }
            // Priority 2: User-selected scheme from mount dialog (bsd://, d64://, etc.)
            else if (!string.IsNullOrEmpty(explicitType))
            {
                driver = _factory.Create(explicitType);
                if (driver == null)
                {
                    throw new InvalidOperationException(
                        $"Filesystem driver '{explicitType}' for scheme '{scheme.ToSchemeString()}://' is not registered. " +
                        $"Please ensure the driver provider is registered in the DriverFactory.");
                }
            }
            // Priority 3: Automatic detection (file:/// or no scheme)
            else
            {
                driver = _factory.Detect(imagePath);
                if (driver == null)
                {
                    throw new IOException(
                        $"Unable to detect filesystem format for: {imagePath}\n\n" +
                        "Possible solutions:\n" +
                        "1. Explicitly select the filesystem type from the mount dialog\n" +
                        "2. Use a URN scheme (e.g., bsd:///path, d64:///path, ndfs:///path)\n" +
                        "3. Ensure the disk image is not corrupted\n" +
                        "4. Check that all filesystem drivers are registered");
                }
            }

            // Apply partition selection if specified (for partitioned filesystems)
            PartitionInfo? mbrPartitionInfo = null;
            if (!string.IsNullOrEmpty(partition))
            {
                // Check for "raw" filesystem mode (no partition table)
                if (partition.Equals("raw", StringComparison.OrdinalIgnoreCase))
                {
                    if (driver is IPartitionProvider partitionProviderRaw)
                    {
                        partitionProviderRaw.SetRawMode();
                    }
                }
                // BSD/Sun: partition letter (a-h)
                else if (partition.Length == 1 && partition[0] >= 'a' && partition[0] <= 'h')
                {
                    if (driver is IPartitionProvider partitionProvider)
                    {
                        if (!partitionProvider.SelectPartition(partition))
                        {
                            driver.Dispose();
                            throw new IOException($"Partition '{partition}' not found or invalid.");
                        }
                    }
                }
                // MBR: partition number (0-3)
                else if (int.TryParse(partition, out int partitionNumber) && partitionNumber >= 0 && partitionNumber <= 3)
                {
                    // Detect MBR partitions and get the requested partition info
                    var mbrPartitions = PartitionDetectionService.DetectMbrPartitions(imagePath);
                    for (int i = 0; i < mbrPartitions.Count; i++)
                    {
                        if (mbrPartitions[i].Id == partitionNumber.ToString())
                        {
                            mbrPartitionInfo = mbrPartitions[i];
                            break;
                        }
                    }

                    if (mbrPartitionInfo == null)
                    {
                        driver.Dispose();
                        throw new IOException(
                            $"Partition {partitionNumber} not found in MBR partition table. " +
                            "The disk image may not have an MBR, or the partition number is invalid.");
                    }
                }
            }
            else
            {
                // No partition specified - use raw mode for partition-supporting drivers by default
                if (driver is IPartitionProvider partitionProviderNoPartition)
                {
                    partitionProviderNoPartition.SetRawMode();
                }
            }

            // Open image stream
            FileStream baseImageStream;
            try
            {
                FileAccess access = readOnly ? FileAccess.Read : FileAccess.ReadWrite;
                FileShare share = FileShare.Read;  // Always allow multiple read mounts for different partitions
                baseImageStream = new FileStream(imagePath, FileMode.Open, access, share);
            }
            catch (Exception ex)
            {
                driver.Dispose();
                throw new IOException($"Failed to open image file: {imagePath}", ex);
            }

            // Wrap stream in PartitionStream if MBR partition was selected
            Stream imageStream;
            if (mbrPartitionInfo != null)
            {
                imageStream = new PartitionStream(
                    baseImageStream,
                    mbrPartitionInfo.OffsetSectors,
                    mbrPartitionInfo.SizeSectors);
            }
            else
            {
                imageStream = baseImageStream;
            }

            // Mount filesystem
            try
            {
                // For FFS driver, set image path to enable hardcoded geometry detection
                if (driver is Drivers.FfsDriver ffsDriver)
                {
                    ffsDriver.SetImagePath(imagePath);
                }

                driver.Mount(imageStream, readOnly);
            }
            catch (Exception ex)
            {
                imageStream.Dispose(); // This disposes PartitionStream wrapper
                if (mbrPartitionInfo != null)
                {
                    baseImageStream.Dispose(); // Also dispose base stream for partitions
                }
                driver.Dispose();
                throw new IOException($"Failed to mount filesystem: {imagePath}", ex);
            }

            // Create mount entry
            Mount mount = new Mount
            {
                VolumeId = volumeId,
                Driver = driver,
                ImageStream = imageStream,
                BaseImageStream = (mbrPartitionInfo != null) ? baseImageStream : null,
                SourceUri = uri,
                IsReadOnly = readOnly,
                ForcedFileSystemType = forceFileSystemType
            };

            _mounts[volumeId] = mount;

            // Return mount info
            return new MountInfo
            {
                VolumeId = volumeId,
                FileSystemType = driver.FileSystemType,
                VolumeLabel = driver.VolumeLabel,
                IsReadOnly = readOnly,
                HasBlockAccess = driver.HasBlockAccess,
                SourceUri = uri,
                Type = Type
            };
        }

        /// <summary>
        /// Unmounts a previously mounted volume.
        /// </summary>
        /// <param name="volumeId">
        /// Volume identifier to unmount
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when volume ID is not found
        /// </exception>
        public void Unmount(string volumeId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_mounts.TryGetValue(volumeId, out Mount? mount))
            {
                throw new ArgumentException($"Volume ID '{volumeId}' not found.", nameof(volumeId));
            }

            _mounts.Remove(volumeId);
            mount.Dispose();
        }

        /// <summary>
        /// Gets information about a mounted volume.
        /// </summary>
        /// <param name="volumeId">
        /// Volume identifier
        /// </param>
        /// <returns>
        /// Mount information, or null if not found
        /// </returns>
        public MountInfo? GetMountInfo(string volumeId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_mounts.TryGetValue(volumeId, out Mount? mount))
            {
                return null;
            }

            return new MountInfo
            {
                VolumeId = volumeId,
                FileSystemType = mount.Driver.FileSystemType,
                VolumeLabel = mount.Driver.VolumeLabel,
                IsReadOnly = mount.IsReadOnly,
                HasBlockAccess = mount.Driver.HasBlockAccess,
                SourceUri = mount.SourceUri,
                Type = Type
            };
        }

        /// <summary>
        /// Lists all currently mounted volumes.
        /// </summary>
        /// <returns>
        /// Array of mount information for all mounted volumes
        /// </returns>
        public MountInfo[] ListMounts()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            int count = _mounts.Count;
            MountInfo[] result = new MountInfo[count];
            int index = 0;

            Dictionary<string, Mount>.Enumerator enumerator = _mounts.GetEnumerator();
            while (enumerator.MoveNext())
            {
                Mount mount = enumerator.Current.Value;
                result[index++] = new MountInfo
                {
                    VolumeId = mount.VolumeId,
                    FileSystemType = mount.Driver.FileSystemType,
                    VolumeLabel = mount.Driver.VolumeLabel,
                    IsReadOnly = mount.IsReadOnly,
                    HasBlockAccess = mount.Driver.HasBlockAccess,
                    SourceUri = mount.SourceUri,
                    Type = Type
                };
            }
            enumerator.Dispose();

            return result;
        }

        /// <summary>
        /// Reads an entire file from the filesystem.
        /// Supports three identification methods (global across all filesystem types):
        /// 1. Original filename: "CARMEN SANDIEGO?"
        /// 2. URN-encoded identifier: "CARMEN%20SANDIEGO%3F"
        /// 3. Numeric index: "#3" (for third file in directory)
        /// </summary>
        /// <param name="context">
        /// URN context with volume ID
        /// </param>
        /// <param name="path">
        /// File path or identifier
        /// </param>
        /// <returns>
        /// File contents as byte array
        /// </returns>
        public byte[] ReadFile(UrnContext context, string path)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Mount mount = GetMountOrThrow(context.VolumeId);

            // Resolve path using global identification system
            string resolvedPath = ResolveFilePath(mount, path);

            return mount.Driver.ReadFile(resolvedPath);
        }

        /// <summary>
        /// Writes data to a file in the filesystem.
        /// Supports URN identifier resolution (same as ReadFile).
        /// </summary>
        public void WriteFile(UrnContext context, string path, byte[] data)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Mount mount = GetMountOrThrow(context.VolumeId);

            if (mount.IsReadOnly || context.ReadOnly)
            {
                throw new UnauthorizedAccessException($"Volume '{context.VolumeId}' is mounted read-only.");
            }

            // Resolve path using global identification system
            string resolvedPath = ResolveFilePath(mount, path);

            mount.Driver.WriteFile(resolvedPath, data);
        }

        /// <summary>
        /// Deletes a file from the filesystem.
        /// Supports URN identifier resolution (same as ReadFile).
        /// </summary>
        public void DeleteFile(UrnContext context, string path)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Mount mount = GetMountOrThrow(context.VolumeId);

            if (mount.IsReadOnly || context.ReadOnly)
            {
                throw new UnauthorizedAccessException($"Volume '{context.VolumeId}' is mounted read-only.");
            }

            // Resolve path using global identification system
            string resolvedPath = ResolveFilePath(mount, path);

            mount.Driver.DeleteFile(resolvedPath);
        }

        /// <summary>
        /// Lists directory contents.
        /// </summary>
        public DirectoryEntry[] ListDirectory(UrnContext context, string path)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Mount mount = GetMountOrThrow(context.VolumeId);
            return mount.Driver.ListDirectory(path);
        }

        /// <summary>
        /// Gets metadata about a file or directory.
        /// Supports URN identifier resolution (same as ReadFile).
        /// </summary>
        public FileMetadata? GetMetadata(UrnContext context, string path)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Mount mount = GetMountOrThrow(context.VolumeId);

            // Resolve path using global identification system
            string resolvedPath = ResolveFilePath(mount, path);

            return mount.Driver.GetMetadata(resolvedPath);
        }

        /// <summary>
        /// Reads a block of data from the filesystem at the block/sector level.
        /// </summary>
        public int ReadBlock(UrnContext context, BlockAddress address, Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Mount mount = GetMountOrThrow(context.VolumeId);
            return mount.Driver.ReadBlock(address, buffer);
        }

        /// <summary>
        /// Writes a block of data to the filesystem at the block/sector level.
        /// </summary>
        public void WriteBlock(UrnContext context, BlockAddress address, ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Mount mount = GetMountOrThrow(context.VolumeId);

            if (mount.IsReadOnly || context.ReadOnly)
            {
                throw new UnauthorizedAccessException($"Volume '{context.VolumeId}' is mounted read-only.");
            }

            mount.Driver.WriteBlock(address, data);
        }

        /// <summary>
        /// Gets information about block device characteristics.
        /// </summary>
        public BlockDeviceInfo? GetBlockDeviceInfo(UrnContext context)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Mount mount = GetMountOrThrow(context.VolumeId);
            return mount.Driver.GetBlockDeviceInfo();
        }

        // ==================== VFS Async Methods ====================

        public async Task<Stream> OpenReadAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Mount mount = GetMountOrThrow(context.VolumeId);

            // Resolve path using global identification system
            string resolvedPath = ResolveFilePath(mount, path);

            // Read entire file into memory stream (disk images are typically small)
            byte[] data = await Task.Run(() => mount.Driver.ReadFile(resolvedPath), cancellationToken);
            return new MemoryStream(data);
        }

        public async Task WriteAsync(UrnContext context, string path, Stream data, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Mount mount = GetMountOrThrow(context.VolumeId);

            if (mount.IsReadOnly || context.ReadOnly)
                throw new UnauthorizedAccessException($"Volume '{context.VolumeId}' is mounted read-only.");

            // Resolve path using global identification system
            string resolvedPath = ResolveFilePath(mount, path);

            // Read stream into byte array, then write
            using MemoryStream ms = new MemoryStream();
            await data.CopyToAsync(ms, bufferSize: 81920, cancellationToken);
            byte[] fileData = ms.ToArray();

            await Task.Run(() => mount.Driver.WriteFile(resolvedPath, fileData), cancellationToken);
        }

        public Task<DirectoryEntry[]> ListDirectoryAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Wrap synchronous method
            return Task.Run(() => ListDirectory(context, path), cancellationToken);
        }

        public Task<FileMetadata?> GetMetadataAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Wrap synchronous method
            return Task.Run(() => GetMetadata(context, path), cancellationToken);
        }

        /// <summary>
        /// Disposes all mounted volumes and releases resources.
        /// Automatically unmounts all volumes and closes file handles.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            // Unmount all volumes (closes file handles and cleans up resources)
            List<string> volumeIds = new List<string>(_mounts.Keys);
            for (int i = 0; i < volumeIds.Count; i++)
            {
                try
                {
                    Unmount(volumeIds[i]);
                }
                catch
                {
                    // Continue cleanup even if individual unmount fails
                }
            }

            _disposed = true;
        }

        private Mount GetMountOrThrow(string volumeId)
        {
            if (!_mounts.TryGetValue(volumeId, out Mount? mount))
            {
                throw new ArgumentException($"Volume ID '{volumeId}' not found.", nameof(volumeId));
            }

            return mount;
        }

        /// <summary>
        /// Resolves a file path identifier to an actual filesystem path.
        /// Handles three identification methods universally across all filesystem types:
        /// 1. Numeric index (#1, #2, etc.) - converts to actual filename
        /// 2. URN-encoded identifier (contains %) - decodes to original filename
        /// 3. Original filename - passes through unchanged
        ///
        /// Security: Applies RFC 3986 path normalization and validates against
        /// directory traversal attacks. Dot-segments (. and ..) are processed safely.
        /// </summary>
        /// <param name="mount">
        /// Mount information containing the driver
        /// </param>
        /// <param name="path">
        /// Path or identifier to resolve
        /// </param>
        /// <returns>
        /// Resolved and normalized filesystem path
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Path is invalid or contains security violations
        /// </exception>
        private string ResolveFilePath(Mount mount, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Path cannot be null or empty", nameof(path));
            }

            // Method 1: Try numeric index first (#1, #2, etc.)
            // Numeric indices are inherently safe (no path traversal possible)
            if (UrnIdentifier.TryParseIndex(path, out int index))
            {
                DirectoryEntry[] allEntries = mount.Driver.ListDirectory("/");
                if (index >= 0 && index < allEntries.Length)
                {
                    // Numeric reference resolves directly to filename (safe)
                    return allEntries[index].Name;
                }
                // If index is out of range, fall through and try as literal filename
            }

            // Method 2: Decode URN-encoded identifier if present
            // Example: "CARMEN%20SANDIEGO%3F" → "CARMEN SANDIEGO?"
            if (path.Contains("%"))
            {
                path = UrnIdentifier.Decode(path);
            }

            // Method 3: Apply RFC 3986 path normalization
            // This handles dot-segments (. and ..) and prevents directory traversal
            // Examples:
            //   "/a/./b"         → "/a/b"
            //   "/a/b/../c"      → "/a/c"
            //   "/../etc/passwd" → "/etc/passwd" (cannot escape root)
            path = PathNormalizer.Normalize(path);

            // Security: Validate normalized path is safe
            if (!PathNormalizer.IsSafePath(path))
            {
                throw new ArgumentException($"Path failed security validation: {path}", nameof(path));
            }

            // Remove leading / for driver (drivers expect relative paths)
            if (path.StartsWith("/"))
            {
                path = path.Substring(1);
            }

            // Empty path after normalization means root directory
            if (string.IsNullOrEmpty(path))
            {
                path = ".";
            }

            return path;
        }

        /// <summary>
        /// Extracts the scheme from a URN (e.g., Bsd from "bsd:///path").
        /// Returns Unknown if no scheme found or unrecognized.
        /// </summary>
        private FileSystemScheme ExtractScheme(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return FileSystemScheme.Unknown;

            int schemeEnd = uri.IndexOf("://");
            if (schemeEnd > 0)
            {
                string schemeString = uri.Substring(0, schemeEnd);
                return FileSystemSchemeExtensions.ParseScheme(schemeString);
            }

            return FileSystemScheme.Unknown;
        }
    }
}
