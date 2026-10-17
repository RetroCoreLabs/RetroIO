//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Unified mount configuration that stores all mounts across all provider types.
    /// This replaces provider-specific mount configs with a single source of truth.
    /// </summary>
    public sealed class UnifiedMountConfig
    {
        /// <summary>
        /// Configuration format version.
        /// </summary>
        public int Version { get; set; } = 1;

        /// <summary>
        /// List of all mount entries across all providers.
        /// </summary>
        public List<UnifiedMountEntry> Mounts { get; set; } = new List<UnifiedMountEntry>();
    }

    /// <summary>
    /// Single mount entry that can represent any provider type.
    /// </summary>
    public sealed class UnifiedMountEntry
    {
        /// <summary>
        /// Volume identifier (unique across all providers).
        /// </summary>
        public string VolumeId { get; set; } = string.Empty;

        /// <summary>
        /// Provider type: "diskimage", "native", "tnfs", "grpc", etc.
        /// </summary>
        public string ProviderType { get; set; } = string.Empty;

        /// <summary>
        /// Source URI (file path for disk images, directory path for native, server address for TNFS, etc.).
        /// </summary>
        public string SourceUri { get; set; } = string.Empty;

        /// <summary>
        /// True if mounted read-only.
        /// </summary>
        public bool IsReadOnly { get; set; }

        /// <summary>
        /// Optional: Forced filesystem type (e.g., "D64", "D71"), or null for auto-detection.
        /// Only used by disk image provider.
        /// </summary>
        public string? FileSystemType { get; set; }

        /// <summary>
        /// Optional username for network protocol mounts (TNFS, Econet/AUN).
        /// </summary>
        public string? Username { get; set; }

        /// <summary>
        /// Optional password for network protocol mounts (TNFS, Econet/AUN).
        /// </summary>
        public string? Password { get; set; }
    }
}
