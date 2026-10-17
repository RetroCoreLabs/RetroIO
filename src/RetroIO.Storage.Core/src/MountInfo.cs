//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Information about a mounted volume.
    /// Returned after successful mount operation.
    /// </summary>
    public sealed class MountInfo
    {
        /// <summary>
        /// Volume identifier used in URNs.
        /// </summary>
        public string VolumeId { get; set; } = string.Empty;

        /// <summary>
        /// Filesystem type (e.g., "D64", "NDFS", "HFS").
        /// </summary>
        public string FileSystemType { get; set; } = string.Empty;

        /// <summary>
        /// Volume label/name from the filesystem.
        /// </summary>
        public string VolumeLabel { get; set; } = string.Empty;

        /// <summary>
        /// True if mounted read-only.
        /// </summary>
        public bool IsReadOnly { get; set; }

        /// <summary>
        /// True if this volume supports block-level access.
        /// </summary>
        public bool HasBlockAccess { get; set; }

        /// <summary>
        /// Original URI used during mount (file path, server address, etc.).
        /// </summary>
        public string? SourceUri { get; set; }

        /// <summary>
        /// Volume type identifier (e.g., "diskimage", "native", "grpc", "tnfs").
        /// </summary>
        public string? Type { get; set; }

        /// <summary>
        /// Total size of the volume in bytes (if available).
        /// </summary>
        public long? TotalSize { get; set; }

        /// <summary>
        /// Free space in bytes (if available).
        /// </summary>
        public long? FreeSpace { get; set; }
    }
}
