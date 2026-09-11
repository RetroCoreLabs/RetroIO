//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local
{
    /// <summary>
    /// Internal representation of a mounted volume.
    /// Holds the driver and associated resources.
    /// </summary>
    internal sealed class Mount : IDisposable
    {
        /// <summary>
        /// Volume identifier.
        /// </summary>
        public string VolumeId { get; set; } = string.Empty;

        /// <summary>
        /// Filesystem driver instance.
        /// </summary>
        public IFileSystemDriver Driver { get; set; } = null!;

        /// <summary>
        /// Stream containing the disk image (may be a PartitionStream wrapper).
        /// </summary>
        public Stream ImageStream { get; set; } = null!;

        /// <summary>
        /// Base stream when ImageStream is a PartitionStream wrapper (null otherwise).
        /// Needed for proper cleanup when mounting MBR partitions.
        /// </summary>
        public Stream? BaseImageStream { get; set; }

        /// <summary>
        /// Original URI used during mount.
        /// </summary>
        public string? SourceUri { get; set; }

        /// <summary>
        /// True if mounted read-only.
        /// </summary>
        public bool IsReadOnly { get; set; }

        /// <summary>
        /// Forced filesystem type (null = auto-detected).
        /// </summary>
        public string? ForcedFileSystemType { get; set; }

        /// <summary>
        /// Disposes the driver and image stream.
        /// </summary>
        public void Dispose()
        {
            Driver?.Dispose();
            ImageStream?.Dispose();
            BaseImageStream?.Dispose();
        }
    }
}
