//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Base class for filesystem driver providers with optional detection support.
    /// Provides default "no detection" implementation that can be overridden.
    /// </summary>
    /// <remarks>
    /// Detection is OPTIONAL - drivers that don't implement detection will:
    /// - Return 0.0 confidence (not detected)
    /// - Still work when forced via Mount(path, volumeId, readOnly, "TypeName")
    ///
    /// Detection recommendations:
    /// - Use filename/extension as weak signal (0.1-0.3 confidence)
    /// - Check file size for format-specific sizes (0.4-0.6 confidence)
    /// - Validate magic bytes/signatures in HeaderBytes (0.2-0.4 confidence)
    /// - Keep detection fast - all data is pre-read by factory
    ///
    /// Factory reads file ONCE and provides same hints to all drivers:
    /// - FilePath, FileName, FileExtension
    /// - FileSize
    /// - HeaderBytes (at least RequiredHeaderBytes, determined by max of all registered providers)
    /// </remarks>
    public abstract class FileSystemDriverProviderBase : IFileSystemDriverProvider
    {
        /// <summary>
        /// Gets the filesystem type identifier (e.g., "D64", "NDFS", "HFS").
        /// </summary>
        public abstract string FileSystemType { get; }

        /// <summary>
        /// Gets a human-readable description of the filesystem format.
        /// </summary>
        public abstract string Description { get; }

        /// <summary>
        /// Gets the number of header bytes required for detection.
        /// Factory will read at least this many bytes and provide them in DetectionHints.HeaderBytes.
        /// Default is 512 bytes (one sector). Override for formats requiring more (e.g., FFS needs 10240).
        /// </summary>
        public virtual int RequiredHeaderBytes => 512;

        /// <summary>
        /// Analyzes detection hints to determine confidence that the file matches this filesystem format.
        /// Default implementation returns 0.0 (no detection).
        /// Override to provide detection logic.
        /// </summary>
        /// <param name="hints">Pre-read detection hints (filename, size, header bytes)</param>
        /// <returns>Confidence score from 0.0 (no match) to 1.0 (definite match)</returns>
        public virtual float DetectConfidence(DetectionHints hints)
        {
            return 0.0f; // Default: no detection
        }

        /// <summary>
        /// Creates a new driver instance.
        /// </summary>
        /// <returns>New filesystem driver instance</returns>
        public abstract IFileSystemDriver CreateDriver();
    }
}
