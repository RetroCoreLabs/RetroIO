//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Storage.Core.VFS
{
    /// <summary>
    /// VfsCore - Unified VFS entry point with integrated caching.
    /// Provides file operations with automatic caching for directory listings and metadata.
    /// </summary>
    /// <remarks>
    /// VfsCore acts as a facade over RetroVFS, providing:
    /// - Unified file operations across all provider types
    /// - Automatic caching of directory listings and metadata
    /// - Cache invalidation on write operations
    /// - URN-based file identification
    ///
    /// Usage:
    /// <code>
    /// VfsCore vfs = new VfsCore();
    /// vfs.Mount("diskimage", "path/to/disk.d64", "d64", readOnly: false);
    ///
    /// DirectoryEntry[] entries = vfs.ReadDirectory("urn:retrofs:diskimage:d64:/");
    /// FileMetadata metadata = vfs.GetMetadata("urn:retrofs:diskimage:d64:/FILE.TXT");
    /// </code>
    /// </remarks>
    public class VfsCore : IDisposable
    {
        private readonly RetroVFS _vfs;
        private bool _disposed;

        /// <summary>
        /// Initializes a new VfsCore instance.
        /// </summary>
        /// <param name="enablePersistence">Enable automatic save/load of mount configuration</param>
        /// <param name="configPath">Path to configuration file (null = use default)</param>
        /// <param name="cacheConfig">Cache configuration (null = use default)</param>
        public VfsCore(bool enablePersistence = true, string? configPath = null, VfsCacheConfiguration? cacheConfig = null)
        {
            _vfs = new RetroVFS(enablePersistence, configPath, cacheConfig);
        }

        /// <summary>
        /// Gets the underlying RetroVFS instance.
        /// </summary>
        public RetroVFS Vfs => _vfs;

        /// <summary>
        /// Registers a volume provider with the VFS.
        /// </summary>
        /// <param name="provider">Provider to register</param>
        public void RegisterProvider(IVolumeProvider provider)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _vfs.RegisterProvider(provider);
        }

        /// <summary>
        /// Loads persisted mounts from configuration.
        /// </summary>
        public void LoadPersistedMounts()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _vfs.LoadPersistedMounts();
        }

        /// <summary>
        /// Mounts a volume.
        /// </summary>
        /// <param name="providerType">Provider type (diskimage, native, tnfs, etc.)</param>
        /// <param name="uri">Source URI</param>
        /// <param name="volumeId">Volume identifier</param>
        /// <param name="readOnly">Read-only flag</param>
        /// <param name="forceFileSystemType">Force specific filesystem type</param>
        /// <returns>Mount information</returns>
        public MountInfo Mount(string providerType, string uri, string volumeId, bool readOnly, string? forceFileSystemType = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _vfs.Mount(providerType, uri, volumeId, readOnly, forceFileSystemType);
        }

        /// <summary>
        /// Unmounts a volume and invalidates all cache entries for that volume.
        /// </summary>
        /// <param name="volumeId">Volume identifier</param>
        public void Unmount(string volumeId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _vfs.Unmount(volumeId);
        }

        /// <summary>
        /// Gets all mounted volumes.
        /// </summary>
        /// <returns>Array of mount information</returns>
        public MountInfo[] GetAllMounts()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _vfs.GetAllMounts();
        }

        /// <summary>
        /// Gets mount information for a specific volume.
        /// </summary>
        /// <param name="volumeId">Volume identifier</param>
        /// <returns>Mount information, or null if not found</returns>
        public MountInfo? GetMountInfo(string volumeId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _vfs.GetMountInfo(volumeId);
        }

        /// <summary>
        /// Reads a directory listing with automatic caching.
        /// </summary>
        /// <param name="urnPath">URN path to directory</param>
        /// <param name="context">URN context (optional, can be null for basic operations)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Directory entries</returns>
        public async Task<DirectoryEntry[]> ReadDirectoryAsync(string urnPath, UrnContext? context = null, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrEmpty(urnPath))
                throw new ArgumentNullException(nameof(urnPath));

            // Parse URN to get volume ID, provider sub-path, and remote-ness
            (string volumeId, string subPath, bool isRemote) = ParseUrnForCaching(urnPath);

            // Try cache first
            VfsDentryCache? dentryCache = _vfs.GetDentryCache();
            if (dentryCache != null)
            {
                DirectoryEntry[]? cached = dentryCache.TryGetDirectory(urnPath, isRemote);
                if (cached != null)
                {
                    return cached;
                }
            }

            // Cache miss - fetch from provider
            IVolumeProvider? provider = _vfs.GetProvider(volumeId);
            if (provider == null)
                throw new ArgumentException($"Volume '{volumeId}' not found or not mounted.", nameof(urnPath));

            // Create context if not provided
            UrnContext effectiveContext = context ?? new UrnContext { VolumeId = volumeId, ReadOnly = true };

            // Read from provider (provider expects the sub-path, not the full URN)
            DirectoryEntry[] entries = await provider.ListDirectoryAsync(effectiveContext, subPath, cancellationToken);

            // Cache the result
            if (dentryCache != null)
            {
                dentryCache.CacheDirectory(urnPath, entries, isRemote);
            }

            return entries;
        }

        /// <summary>
        /// Gets file metadata with automatic caching.
        /// </summary>
        /// <param name="urnPath">URN path to file</param>
        /// <param name="context">URN context (optional, can be null for basic operations)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>File metadata</returns>
        public async Task<FileMetadata> GetMetadataAsync(string urnPath, UrnContext? context = null, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrEmpty(urnPath))
                throw new ArgumentNullException(nameof(urnPath));

            // Parse URN to get volume ID, provider sub-path, and remote-ness
            (string volumeId, string subPath, bool isRemote) = ParseUrnForCaching(urnPath);

            // Try cache first
            VfsMetadataCache? metadataCache = _vfs.GetMetadataCache();
            if (metadataCache != null)
            {
                FileMetadata? cached = metadataCache.TryGetMetadata(urnPath, isRemote);
                if (cached != null)
                {
                    return cached;
                }
            }

            // Cache miss - fetch from provider
            IVolumeProvider? provider = _vfs.GetProvider(volumeId);
            if (provider == null)
                throw new ArgumentException($"Volume '{volumeId}' not found or not mounted.", nameof(urnPath));

            // Create context if not provided
            UrnContext effectiveContext = context ?? new UrnContext { VolumeId = volumeId, ReadOnly = true };

            // Read from provider (provider expects the sub-path, not the full URN)
            FileMetadata? metadata = await provider.GetMetadataAsync(effectiveContext, subPath, cancellationToken);

            if (metadata is null)
                throw new FileNotFoundException($"Metadata not found for '{urnPath}'.", urnPath);

            // Cache the result
            if (metadataCache != null)
            {
                metadataCache.CacheMetadata(urnPath, metadata, isRemote);
            }

            return metadata;
        }

        /// <summary>
        /// Writes data to a file and invalidates cache.
        /// </summary>
        /// <param name="urnPath">URN path to file</param>
        /// <param name="data">Data to write</param>
        /// <param name="context">URN context (optional, can be null for basic operations)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task WriteFileAsync(string urnPath, byte[] data, UrnContext? context = null, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrEmpty(urnPath))
                throw new ArgumentNullException(nameof(urnPath));
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            // Parse URN to get volume ID and provider sub-path
            (string volumeId, string subPath, _) = ParseUrnForCaching(urnPath);

            // Get provider
            IVolumeProvider? provider = _vfs.GetProvider(volumeId);
            if (provider == null)
                throw new ArgumentException($"Volume '{volumeId}' not found or not mounted.", nameof(urnPath));

            // Create context if not provided
            UrnContext effectiveContext = context ?? new UrnContext { VolumeId = volumeId, ReadOnly = false };

            // Write to provider using stream
            using var stream = new MemoryStream(data);
            await provider.WriteAsync(effectiveContext, subPath, stream, cancellationToken);

            // Invalidate cache for this file and its parent directory
            InvalidateCacheForFile(urnPath);
        }

        /// <summary>
        /// Deletes a file and invalidates cache.
        /// </summary>
        /// <param name="urnPath">URN path to file</param>
        /// <param name="context">URN context (optional, can be null for basic operations)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task DeleteFileAsync(string urnPath, UrnContext? context = null, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrEmpty(urnPath))
                throw new ArgumentNullException(nameof(urnPath));

            // Parse URN to get volume ID and provider sub-path
            (string volumeId, string subPath, _) = ParseUrnForCaching(urnPath);

            // Get provider
            IVolumeProvider? provider = _vfs.GetProvider(volumeId);
            if (provider == null)
                throw new ArgumentException($"Volume '{volumeId}' not found or not mounted.", nameof(urnPath));

            // Create context if not provided
            UrnContext effectiveContext = context ?? new UrnContext { VolumeId = volumeId, ReadOnly = false };

            // Delete from provider - use synchronous method since there's no async version
            provider.DeleteFile(effectiveContext, subPath);

            // Invalidate cache for this file and its parent directory
            InvalidateCacheForFile(urnPath);

            await Task.CompletedTask;
        }

        /// <summary>
        /// Creates a directory in the mount (including missing parents) and invalidates the affected
        /// cache entries. Providers that cannot create directories throw
        /// <see cref="NotSupportedException"/>.
        /// </summary>
        /// <param name="urnPath">URN path to the directory to create</param>
        /// <param name="context">URN context (optional)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task CreateDirectoryAsync(string urnPath, UrnContext? context = null, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrEmpty(urnPath))
                throw new ArgumentNullException(nameof(urnPath));

            // Parse URN to get volume ID and provider sub-path
            (string volumeId, string subPath, _) = ParseUrnForCaching(urnPath);

            IVolumeProvider? provider = _vfs.GetProvider(volumeId);
            if (provider == null)
                throw new ArgumentException($"Volume '{volumeId}' not found or not mounted.", nameof(urnPath));

            UrnContext effectiveContext = context ?? new UrnContext { VolumeId = volumeId, ReadOnly = false };

            // Create via provider (sub-path, not the full URN), then invalidate the parent listing.
            provider.CreateDirectory(effectiveContext, subPath);
            InvalidateCacheForFile(urnPath);

            await Task.CompletedTask;
        }

        /// <summary>
        /// Opens a file for reading.
        /// </summary>
        /// <param name="urnPath">URN path to file</param>
        /// <param name="context">URN context (optional, can be null for basic operations)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Stream to read file data</returns>
        public async Task<Stream> OpenReadAsync(string urnPath, UrnContext? context = null, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrEmpty(urnPath))
                throw new ArgumentNullException(nameof(urnPath));

            // Parse URN to get volume ID and provider sub-path
            (string volumeId, string subPath, _) = ParseUrnForCaching(urnPath);

            // Get provider
            IVolumeProvider? provider = _vfs.GetProvider(volumeId);
            if (provider == null)
                throw new ArgumentException($"Volume '{volumeId}' not found or not mounted.", nameof(urnPath));

            // Create context if not provided
            UrnContext effectiveContext = context ?? new UrnContext { VolumeId = volumeId, ReadOnly = true };

            // Open from provider (provider expects the sub-path, not the full URN)
            return await provider.OpenReadAsync(effectiveContext, subPath, cancellationToken);
        }

        /// <summary>
        /// Clears all cache entries.
        /// </summary>
        public void ClearCaches()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _vfs.ClearCaches();
        }

        /// <summary>
        /// Gets cache statistics.
        /// </summary>
        /// <returns>Tuple of (dentry cache stats, metadata cache stats)</returns>
        public (CacheStatistics? DentryCacheStats, CacheStatistics? MetadataCacheStats) GetCacheStatistics()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _vfs.GetCacheStatistics();
        }

        /// <summary>
        /// Parses a URN to extract volume ID and determine if it's a remote filesystem.
        /// </summary>
        /// <param name="urnPath">URN path</param>
        /// <returns>Tuple of (volumeId, isRemote)</returns>
        private (string VolumeId, string SubPath, bool IsRemote) ParseUrnForCaching(string urnPath)
        {
            // URN format: urn:retrofs:<transport>:<volume-id>:<path>
            // Example: urn:retrofs:local:0:d64:/FILE.TXT
            // Example: urn:retrofs:tnfs:server:/FILE.TXT
            //
            // Split into at most 5 parts so the <path> keeps any embedded ':' (an Acorn path can carry a
            // disc spec like ":DISC.X"). parts: [0]=urn [1]=retrofs [2]=transport [3]=volume [4]=path.

            string[] parts = urnPath.Split(':', 5);
            if (parts.Length < 4)
                throw new ArgumentException($"Invalid URN format: {urnPath}", nameof(urnPath));

            string transport = parts[2];
            string volumeId = parts[3];

            // The provider path is everything after the volume id — providers expect a sub-path relative
            // to the mount root (this is what VfsResolver passes too), NOT the full URN. Default to root.
            string subPath = parts.Length >= 5 && parts[4].Length > 0 ? parts[4] : "/";

            // Determine if remote (TNFS, FTP, SFTP, etc. are remote)
            bool isRemote = !string.Equals(transport, "local", StringComparison.OrdinalIgnoreCase) &&
                          !string.Equals(transport, "diskimage", StringComparison.OrdinalIgnoreCase);

            return (volumeId, subPath, isRemote);
        }

        /// <summary>
        /// Invalidates cache entries for a file and its parent directory.
        /// </summary>
        /// <param name="urnPath">URN path to file</param>
        private void InvalidateCacheForFile(string urnPath)
        {
            // Invalidate metadata for the file itself
            VfsMetadataCache? metadataCache = _vfs.GetMetadataCache();
            metadataCache?.Invalidate(urnPath);

            // Invalidate dentry cache for parent directory
            VfsDentryCache? dentryCache = _vfs.GetDentryCache();
            if (dentryCache != null)
            {
                // Extract parent directory path
                int lastSlash = urnPath.LastIndexOf('/');
                if (lastSlash > 0)
                {
                    string parentPath = urnPath.Substring(0, lastSlash);
                    dentryCache.Invalidate(parentPath);
                }
            }
        }

        /// <summary>
        /// Disposes the VfsCore and underlying RetroVFS.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _vfs.Dispose();
            _disposed = true;
        }
    }
}
