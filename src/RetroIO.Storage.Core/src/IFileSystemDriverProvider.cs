//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Provider interface for filesystem driver creation and detection.
    /// Each filesystem format implements a provider for registration with the factory.
    /// </summary>
    public interface IFileSystemDriverProvider
    {
        /// <summary>
        /// Gets the filesystem type identifier (e.g., "D64", "NDFS", "HFS").
        /// </summary>
        string FileSystemType { get; }

        /// <summary>
        /// Gets a human-readable description of the filesystem format.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Gets the number of header bytes required for detection.
        /// Factory will read at least this many bytes and provide them in DetectionHints.HeaderBytes.
        /// Default is 512 bytes (one sector).
        /// </summary>
        int RequiredHeaderBytes => 512;

        /// <summary>
        /// Analyzes detection hints to determine confidence that the file matches this filesystem format.
        /// Factory reads the file ONCE and provides the same hints to all drivers for efficiency.
        /// HeaderBytes will contain at least RequiredHeaderBytes bytes (or entire file if smaller).
        /// </summary>
        /// <param name="hints">Detection hints containing filename, size, and header bytes</param>
        /// <returns>Confidence score from 0.0 (definitely not) to 1.0 (definitely is)</returns>
        float DetectConfidence(DetectionHints hints);

        /// <summary>
        /// Creates a new driver instance.
        /// </summary>
        /// <returns>New filesystem driver instance</returns>
        IFileSystemDriver CreateDriver();
    }

    /// <summary>
    /// Detection hints provided to drivers for format identification.
    /// Factory reads file once and provides same data to all drivers.
    /// </summary>
    public sealed class DetectionHints
    {
        /// <summary>
        /// Full path to the image file.
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// Filename (without directory path).
        /// </summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// File extension (including dot, e.g., ".d64").
        /// Empty string if no extension.
        /// </summary>
        public string FileExtension { get; set; } = string.Empty;

        /// <summary>
        /// Total file size in bytes.
        /// </summary>
        public long FileSize { get; set; }

        /// <summary>
        /// Header bytes from the file (size determined by maximum RequiredHeaderBytes of all registered providers).
        /// Contains at least RequiredHeaderBytes bytes (or entire file if smaller).
        /// Used for header/signature detection.
        /// </summary>
        public byte[] HeaderBytes { get; set; } = System.Array.Empty<byte>();
    }
}
