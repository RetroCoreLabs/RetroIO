//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Centralized registry for all mounted volumes across all providers.
    /// Tracks mounts from disk images, native directories, TNFS servers, etc.
    /// </summary>
    public sealed class MountRegistry
    {
        private readonly Dictionary<string, MountRegistryEntry> _mounts;
        private readonly object _lock = new object();

        /// <summary>
        /// Creates an empty registry. Volume ids are compared without regard to case.
        /// </summary>
        public MountRegistry()
        {
            _mounts = new Dictionary<string, MountRegistryEntry>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Registers a mount with the central registry.
        /// </summary>
        /// <param name="mountInfo">
        /// Mount information
        /// </param>
        /// <param name="provider">
        /// Provider that owns this mount
        /// </param>
        public void RegisterMount(MountInfo mountInfo, IVolumeProvider provider)
        {
            if (mountInfo == null)
            {
                throw new ArgumentNullException(nameof(mountInfo));
            }

            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            lock (_lock)
            {
                if (_mounts.ContainsKey(mountInfo.VolumeId))
                {
                    throw new ArgumentException($"Volume ID '{mountInfo.VolumeId}' is already registered.", nameof(mountInfo));
                }

                _mounts[mountInfo.VolumeId] = new MountRegistryEntry
                {
                    MountInfo = mountInfo,
                    Provider = provider
                };
            }
        }

        /// <summary>
        /// Unregisters a mount from the central registry.
        /// </summary>
        /// <param name="volumeId">
        /// Volume ID to unregister
        /// </param>
        public void UnregisterMount(string volumeId)
        {
            lock (_lock)
            {
                _mounts.Remove(volumeId);
            }
        }

        /// <summary>
        /// Gets all registered mounts.
        /// </summary>
        /// <returns>
        /// Array of all mount information
        /// </returns>
        public MountInfo[] GetAllMounts()
        {
            lock (_lock)
            {
                MountInfo[] result = new MountInfo[_mounts.Count];
                int index = 0;

                Dictionary<string, MountRegistryEntry>.Enumerator enumerator = _mounts.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    result[index++] = enumerator.Current.Value.MountInfo;
                }
                enumerator.Dispose();

                return result;
            }
        }

        /// <summary>
        /// Gets mount information by volume ID.
        /// </summary>
        /// <param name="volumeId">
        /// Volume ID
        /// </param>
        /// <returns>
        /// Mount information, or null if not found
        /// </returns>
        public MountInfo? GetMountInfo(string volumeId)
        {
            lock (_lock)
            {
                if (_mounts.TryGetValue(volumeId, out MountRegistryEntry? entry))
                {
                    return entry.MountInfo;
                }
                return null;
            }
        }

        /// <summary>
        /// Gets the provider that owns a specific mount.
        /// </summary>
        /// <param name="volumeId">
        /// Volume ID
        /// </param>
        /// <returns>
        /// Volume provider, or null if not found
        /// </returns>
        public IVolumeProvider? GetProvider(string volumeId)
        {
            lock (_lock)
            {
                if (_mounts.TryGetValue(volumeId, out MountRegistryEntry? entry))
                {
                    return entry.Provider;
                }
                return null;
            }
        }

        /// <summary>
        /// Checks if a volume ID is already registered.
        /// </summary>
        /// <param name="volumeId">
        /// Volume ID to check
        /// </param>
        /// <returns>
        /// True if registered, false otherwise
        /// </returns>
        public bool IsRegistered(string volumeId)
        {
            lock (_lock)
            {
                return _mounts.ContainsKey(volumeId);
            }
        }

        private sealed class MountRegistryEntry
        {
            public MountInfo MountInfo { get; set; } = null!;
            public IVolumeProvider Provider { get; set; } = null!;
        }
    }
}
