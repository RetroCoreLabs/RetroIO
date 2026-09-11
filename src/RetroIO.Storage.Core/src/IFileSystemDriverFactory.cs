//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.IO;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Factory interface for creating filesystem drivers with automatic format detection.
    /// </summary>
    public interface IFileSystemDriverFactory
    {
        /// <summary>
        /// Registers a filesystem driver provider.
        /// </summary>
        /// <param name="provider">Provider to register</param>
        void Register(IFileSystemDriverProvider provider);

        /// <summary>
        /// Detects filesystem format and creates appropriate driver.
        /// </summary>
        /// <param name="imagePath">Path to disk image file</param>
        /// <returns>Filesystem driver instance, or null if no driver can handle the format</returns>
        IFileSystemDriver? Detect(string imagePath);

        /// <summary>
        /// Detects filesystem format from a stream and creates appropriate driver.
        /// </summary>
        /// <param name="imageStream">Stream containing disk image</param>
        /// <returns>Filesystem driver instance, or null if no driver can handle the format</returns>
        IFileSystemDriver? Detect(Stream imageStream);

        /// <summary>
        /// Creates a driver for a specific filesystem type without detection.
        /// </summary>
        /// <param name="fileSystemType">Filesystem type identifier (e.g., "D64", "NDFS")</param>
        /// <returns>Filesystem driver instance, or null if type is not registered</returns>
        IFileSystemDriver? Create(string fileSystemType);
    }
}
