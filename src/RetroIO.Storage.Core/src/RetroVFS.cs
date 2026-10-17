//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using RetroIO.Storage.Core.VFS;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// RetroVFS (Virtual File System) - Top-level unified file system abstraction.
    /// Coordinates all volume providers and handles mount persistence.
    /// This is the single entry point for mounting volumes and accessing files across all provider types.
    /// </summary>
    /// <remarks>
    /// RetroVFS acts as a VFS (Virtual File System) layer that provides uniform access to:
    /// - Disk images (D64, D81, VDK, etc.)
    /// - Native OS directories
    /// - Network file systems (TNFS, future: gRPC, etc.)
    /// - Any future storage provider
    ///
    /// Caching features:
    /// - Directory entry (dentry) cache for fast navigation
    /// - File metadata cache for quick attribute lookups
    /// - Configurable TTL for local vs remote filesystems
    /// - LRU eviction when cache is full
    ///
    /// Usage in servers (TNFS, gRPC, etc.):
    ///   RetroVFS vfs = new RetroVFS(enablePersistence: true);
    ///   vfs.RegisterProvider(new DiskImageVolumeProvider(factory));
    ///   vfs.RegisterProvider(new TnfsVolumeProvider());
    ///   vfs.LoadPersistedMounts();
    ///
    ///   TnfsServer server = new TnfsServer(vfs);
    /// </remarks>
    public sealed class RetroVFS : IDisposable
    {
        private readonly MountRegistry _registry;
        private readonly Dictionary<string, IVolumeProvider> _providers;
        private readonly bool _enablePersistence;
        private readonly string? _configPath;
        private readonly VfsDentryCache? _dentryCache;
        private readonly VfsMetadataCache? _metadataCache;
        private readonly VfsCacheConfiguration _cacheConfig;
        private bool _disposed;

        /// <summary>
        /// Initializes a new RetroVFS instance.
        /// </summary>
        /// <param name="enablePersistence">
        /// Enable automatic save/load of mount configuration
        /// </param>
        /// <param name="configPath">
        /// Path to configuration file (null = use default)
        /// </param>
        /// <param name="cacheConfig">
        /// Cache configuration (null = use default)
        /// </param>
        public RetroVFS(bool enablePersistence = true, string? configPath = null, VfsCacheConfiguration? cacheConfig = null)
        {
            _registry = new MountRegistry();
            _providers = new Dictionary<string, IVolumeProvider>(StringComparer.OrdinalIgnoreCase);
            _enablePersistence = enablePersistence;
            _configPath = configPath;
            _cacheConfig = cacheConfig ?? VfsCacheConfiguration.Default;

            // Initialize caches if enabled
            if (_cacheConfig.EnableDentryCache)
            {
                _dentryCache = new VfsDentryCache(
                    maxEntries: _cacheConfig.DentryCacheMaxEntries,
                    defaultTtl: _cacheConfig.DentryCacheTtl,
                    remoteTtl: _cacheConfig.DentryCacheRemoteTtl
                );
            }

            if (_cacheConfig.EnableMetadataCache)
            {
                _metadataCache = new VfsMetadataCache(
                    maxEntries: _cacheConfig.MetadataCacheMaxEntries,
                    defaultTtl: _cacheConfig.MetadataCacheTtl,
                    remoteTtl: _cacheConfig.MetadataCacheRemoteTtl
                );
            }
        }

        /// <summary>
        /// Registers a volume provider with the mount manager.
        /// </summary>
        /// <param name="provider">
        /// Provider to register
        /// </param>
        public void RegisterProvider(IVolumeProvider provider)
        {
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            if (_providers.ContainsKey(provider.Type))
            {
                throw new ArgumentException($"Provider type '{provider.Type}' is already registered.", nameof(provider));
            }

            _providers[provider.Type] = provider;
        }

        /// <summary>
        /// Loads saved mounts from configuration file and restores them across all providers.
        /// Call this after registering all providers.
        /// </summary>
        public void LoadPersistedMounts()
        {
            if (!_enablePersistence)
            {
                return;
            }

            try
            {
                UnifiedMountConfig config = UnifiedMountConfigStore.Load(_configPath);

                int successCount = 0;
                int failCount = 0;

                for (int i = 0; i < config.Mounts.Count; i++)
                {
                    UnifiedMountEntry entry = config.Mounts[i];

                    try
                    {
                        // Get provider for this mount type
                        if (!_providers.TryGetValue(entry.ProviderType, out IVolumeProvider? provider))
                        {
                            Console.Error.WriteLine($"Warning: Provider '{entry.ProviderType}' not registered, skipping mount '{entry.VolumeId}'");
                            failCount++;
                            continue;
                        }

                        // Validate mount can be restored (provider-specific validation)
                        if (!provider.CanRestore(entry, out string? errorMessage))
                        {
                            Console.Error.WriteLine($"Warning: {errorMessage}, skipping mount '{entry.VolumeId}'");
                            failCount++;
                            continue;
                        }

                        // Mount through provider using generic method
                        MountInfo mountInfo = provider.MountFromConfig(entry);

                        // Register with central registry
                        _registry.RegisterMount(mountInfo, provider);
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Warning: Failed to restore mount '{entry.VolumeId}': {ex.Message}");
                        failCount++;
                    }
                }

                if (successCount > 0 || failCount > 0)
                {
                    Console.WriteLine($"Mount restore complete: {successCount} succeeded, {failCount} failed");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to load mount configuration: {ex.Message}");
            }
        }

        /// <summary>
        /// Mounts a volume using the appropriate provider.
        /// </summary>
        /// <param name="providerType">
        /// Provider type (diskimage, native, tnfs, etc.)
        /// </param>
        /// <param name="uri">
        /// Source URI
        /// </param>
        /// <param name="volumeId">
        /// Volume identifier
        /// </param>
        /// <param name="readOnly">
        /// Read-only flag
        /// </param>
        /// <param name="forceFileSystemType">
        /// For disk images: force specific filesystem type
        /// </param>
        /// <returns>
        /// Mount information
        /// </returns>
        public MountInfo Mount(string providerType, string uri, string volumeId, bool readOnly, string? forceFileSystemType = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_providers.TryGetValue(providerType, out IVolumeProvider? provider))
            {
                throw new ArgumentException($"Provider type '{providerType}' is not registered.", nameof(providerType));
            }

            // Check for duplicate volume ID
            if (_registry.IsRegistered(volumeId))
            {
                throw new ArgumentException($"Volume ID '{volumeId}' is already in use.", nameof(volumeId));
            }

            // Mount through provider
            MountInfo mountInfo;
            if (!string.IsNullOrEmpty(forceFileSystemType))
            {
                UnifiedMountEntry entry = new UnifiedMountEntry
                {
                    VolumeId = volumeId,
                    ProviderType = providerType,
                    SourceUri = uri,
                    IsReadOnly = readOnly,
                    FileSystemType = forceFileSystemType
                };
                mountInfo = provider.MountFromConfig(entry);
            }
            else
            {
                mountInfo = provider.Mount(uri, volumeId, readOnly);
            }

            // Register with central registry
            _registry.RegisterMount(mountInfo, provider);

            // Save configuration
            if (_enablePersistence)
            {
                SaveConfiguration();
            }

            return mountInfo;
        }

        /// <summary>
        /// Unmounts a volume.
        /// </summary>
        /// <param name="volumeId">
        /// Volume identifier
        /// </param>
        public void Unmount(string volumeId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            IVolumeProvider? provider = _registry.GetProvider(volumeId);
            if (provider == null)
            {
                throw new ArgumentException($"Volume ID '{volumeId}' not found.", nameof(volumeId));
            }

            // Invalidate all cache entries for this volume
            string urnPrefix = $"urn:retrofs:{provider.Type}:{volumeId}:";
            _dentryCache?.InvalidatePrefix(urnPrefix);
            _metadataCache?.InvalidatePrefix(urnPrefix);

            // Unmount from provider
            provider.Unmount(volumeId);

            // Unregister from central registry
            _registry.UnregisterMount(volumeId);

            // Save configuration
            if (_enablePersistence)
            {
                SaveConfiguration();
            }
        }

        /// <summary>
        /// Gets all mounted volumes across all providers.
        /// </summary>
        /// <returns>
        /// Array of all mount information
        /// </returns>
        public MountInfo[] GetAllMounts()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _registry.GetAllMounts();
        }

        /// <summary>
        /// Gets mount information for a specific volume.
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
            return _registry.GetMountInfo(volumeId);
        }

        /// <summary>
        /// Gets the provider for a specific volume.
        /// </summary>
        /// <param name="volumeId">
        /// Volume identifier
        /// </param>
        /// <returns>
        /// Volume provider, or null if not found
        /// </returns>
        public IVolumeProvider? GetProvider(string volumeId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _registry.GetProvider(volumeId);
        }

        /// <summary>
        /// Gets the directory entry cache.
        /// </summary>
        /// <returns>
        /// Dentry cache, or null if caching is disabled
        /// </returns>
        public VfsDentryCache? GetDentryCache()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _dentryCache;
        }

        /// <summary>
        /// Gets the file metadata cache.
        /// </summary>
        /// <returns>
        /// Metadata cache, or null if caching is disabled
        /// </returns>
        public VfsMetadataCache? GetMetadataCache()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _metadataCache;
        }

        /// <summary>
        /// Gets the cache configuration.
        /// </summary>
        public VfsCacheConfiguration CacheConfiguration => _cacheConfig;

        /// <summary>
        /// Clears all cache entries.
        /// </summary>
        public void ClearCaches()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _dentryCache?.Clear();
            _metadataCache?.Clear();
        }

        /// <summary>
        /// Gets combined cache statistics.
        /// </summary>
        public (CacheStatistics? DentryCacheStats, CacheStatistics? MetadataCacheStats) GetCacheStatistics()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (_dentryCache?.GetStatistics(), _metadataCache?.GetStatistics());
        }

        /// <summary>
        /// Saves current mount configuration to disk.
        /// </summary>
        private void SaveConfiguration()
        {
            try
            {
                UnifiedMountConfig config = new UnifiedMountConfig();
                MountInfo[] allMounts = _registry.GetAllMounts();

                for (int i = 0; i < allMounts.Length; i++)
                {
                    MountInfo mount = allMounts[i];
                    config.Mounts.Add(new UnifiedMountEntry
                    {
                        VolumeId = mount.VolumeId,
                        ProviderType = mount.Type ?? string.Empty,
                        SourceUri = mount.SourceUri ?? string.Empty,
                        IsReadOnly = mount.IsReadOnly,
                        FileSystemType = mount.FileSystemType == mount.Type ? null : mount.FileSystemType
                    });
                }

                UnifiedMountConfigStore.Save(config, _configPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to save unified mount configuration: {ex.Message}");
            }
        }

        /// <summary>
        /// Disposes all providers and releases resources.
        /// Note: Does NOT save configuration on dispose - mounts persist for next startup.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            // Dispose all providers (they handle their own cleanup without saving)
            Dictionary<string, IVolumeProvider>.Enumerator enumerator = _providers.GetEnumerator();
            while (enumerator.MoveNext())
            {
                try
                {
                    enumerator.Current.Value.Dispose();
                }
                catch
                {
                    // Continue cleanup even if provider disposal fails
                }
            }
            enumerator.Dispose();

            _providers.Clear();
            _disposed = true;
        }
    }

}
