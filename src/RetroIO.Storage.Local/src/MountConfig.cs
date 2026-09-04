//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Collections.Generic;

namespace RetroIO.Storage.Local
{
    /// <summary>
    /// Persistent configuration for a single mount point.
    /// Saved to JSON and restored on application restart.
    /// </summary>
    public sealed class MountConfig
    {
        /// <summary>
        /// Volume identifier used in URNs.
        /// </summary>
        public string VolumeId { get; set; } = string.Empty;

        /// <summary>
        /// Source URI (file path to disk image).
        /// </summary>
        public string SourceUri { get; set; } = string.Empty;

        /// <summary>
        /// True if mounted read-only.
        /// </summary>
        public bool IsReadOnly { get; set; }

        /// <summary>
        /// Forced filesystem type (e.g., "D64"), or null for auto-detection.
        /// </summary>
        public string? FileSystemType { get; set; }
    }

    /// <summary>
    /// Root configuration container for all mount points.
    /// Serialized to ~/.retrofs/mounts.json (Linux) or %APPDATA%\RetroFS\mounts.json (Windows).
    /// </summary>
    public sealed class MountConfigRoot
    {
        /// <summary>
        /// Version of configuration format.
        /// </summary>
        public int Version { get; set; } = 1;

        /// <summary>
        /// List of configured mount points.
        /// </summary>
        public List<MountConfig> Mounts { get; set; } = new List<MountConfig>();
    }
}
