//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;

namespace RetroIO.Storage.Core.VFS
{
    /// <summary>
    /// Configuration for VFS caching behavior.
    /// Controls dentry (directory) cache and metadata cache settings.
    /// </summary>
    /// <remarks>
    /// The VFS uses two separate caches:
    /// - Dentry cache: Caches directory listings (ReadDirectory results)
    /// - Metadata cache: Caches file metadata (GetMetadata results)
    ///
    /// Both caches support:
    /// - Configurable max size (LRU eviction when full)
    /// - Configurable TTL (Time To Live)
    /// - Different TTL for remote filesystems
    /// - Thread-safe concurrent access
    ///
    /// Default configuration:
    /// - Dentry cache: 1000 entries, 60 min TTL (5 min remote)
    /// - Metadata cache: 2000 entries, 60 min TTL (5 min remote)
    /// </remarks>
    public class VfsCacheConfiguration
    {
        /// <summary>
        /// Enable directory entry caching.
        /// When enabled, directory listings are cached to avoid repeated filesystem access.
        /// </summary>
        public bool EnableDentryCache { get; set; } = true;

        /// <summary>
        /// Maximum number of cached directory listings.
        /// When exceeded, least recently used entries are evicted.
        /// </summary>
        public int DentryCacheMaxEntries { get; set; } = 1000;

        /// <summary>
        /// Time To Live for local filesystem directory cache entries.
        /// Default: 60 minutes (local disk images rarely change)
        /// </summary>
        public TimeSpan DentryCacheTtl { get; set; } = TimeSpan.FromMinutes(60);

        /// <summary>
        /// Time To Live for remote filesystem directory cache entries.
        /// Default: 5 minutes (remote files may change on server)
        /// </summary>
        public TimeSpan DentryCacheRemoteTtl { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Enable file metadata caching.
        /// When enabled, file metadata (size, dates, permissions) is cached to avoid repeated GetMetadata calls.
        /// </summary>
        public bool EnableMetadataCache { get; set; } = true;

        /// <summary>
        /// Maximum number of cached file metadata entries.
        /// When exceeded, least recently used entries are evicted.
        /// </summary>
        public int MetadataCacheMaxEntries { get; set; } = 2000;

        /// <summary>
        /// Time To Live for local filesystem metadata cache entries.
        /// Default: 60 minutes (local disk images rarely change)
        /// </summary>
        public TimeSpan MetadataCacheTtl { get; set; } = TimeSpan.FromMinutes(60);

        /// <summary>
        /// Time To Live for remote filesystem metadata cache entries.
        /// Default: 5 minutes (remote files may change on server)
        /// </summary>
        public TimeSpan MetadataCacheRemoteTtl { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Default configuration with caching enabled.
        /// </summary>
        public static VfsCacheConfiguration Default => new VfsCacheConfiguration();

        /// <summary>
        /// Configuration with all caching disabled.
        /// Useful for testing or debugging.
        /// </summary>
        public static VfsCacheConfiguration Disabled => new VfsCacheConfiguration
        {
            EnableDentryCache = false,
            EnableMetadataCache = false
        };

        /// <summary>
        /// Configuration optimized for remote filesystems.
        /// Uses shorter TTL values and smaller cache sizes.
        /// </summary>
        public static VfsCacheConfiguration RemoteOptimized => new VfsCacheConfiguration
        {
            DentryCacheMaxEntries = 500,
            DentryCacheTtl = TimeSpan.FromMinutes(5),
            DentryCacheRemoteTtl = TimeSpan.FromMinutes(2),
            MetadataCacheMaxEntries = 1000,
            MetadataCacheTtl = TimeSpan.FromMinutes(5),
            MetadataCacheRemoteTtl = TimeSpan.FromMinutes(2)
        };

        /// <summary>
        /// Configuration optimized for local filesystems.
        /// Uses longer TTL values and larger cache sizes.
        /// </summary>
        public static VfsCacheConfiguration LocalOptimized => new VfsCacheConfiguration
        {
            DentryCacheMaxEntries = 2000,
            DentryCacheTtl = TimeSpan.FromHours(2),
            DentryCacheRemoteTtl = TimeSpan.FromMinutes(5),
            MetadataCacheMaxEntries = 5000,
            MetadataCacheTtl = TimeSpan.FromHours(2),
            MetadataCacheRemoteTtl = TimeSpan.FromMinutes(5)
        };

        /// <summary>
        /// Validate configuration values.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">If any values are invalid</exception>
        public void Validate()
        {
            if (DentryCacheMaxEntries <= 0)
                throw new ArgumentOutOfRangeException(nameof(DentryCacheMaxEntries), "Must be positive");

            if (MetadataCacheMaxEntries <= 0)
                throw new ArgumentOutOfRangeException(nameof(MetadataCacheMaxEntries), "Must be positive");

            if (DentryCacheTtl <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(DentryCacheTtl), "Must be positive");

            if (DentryCacheRemoteTtl <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(DentryCacheRemoteTtl), "Must be positive");

            if (MetadataCacheTtl <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(MetadataCacheTtl), "Must be positive");

            if (MetadataCacheRemoteTtl <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(MetadataCacheRemoteTtl), "Must be positive");
        }

        /// <summary>
        /// Create a deep copy of this configuration.
        /// </summary>
        public VfsCacheConfiguration Clone()
        {
            return new VfsCacheConfiguration
            {
                EnableDentryCache = EnableDentryCache,
                DentryCacheMaxEntries = DentryCacheMaxEntries,
                DentryCacheTtl = DentryCacheTtl,
                DentryCacheRemoteTtl = DentryCacheRemoteTtl,
                EnableMetadataCache = EnableMetadataCache,
                MetadataCacheMaxEntries = MetadataCacheMaxEntries,
                MetadataCacheTtl = MetadataCacheTtl,
                MetadataCacheRemoteTtl = MetadataCacheRemoteTtl
            };
        }
    }
}
