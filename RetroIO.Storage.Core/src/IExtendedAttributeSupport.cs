//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Collections.Generic;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Optional interface for filesystem drivers that support extended attributes/metadata.
    /// Only filesystems with advanced metadata (like NDFS) need to implement this.
    /// Standard filesystems (D64, T64, FAT, etc.) do not need to implement this interface.
    /// </summary>
    /// <remarks>
    /// This interface enables .xat (Extended Attribute) sidecar file support for preserving
    /// metadata when copying files between different filesystem types.
    ///
    /// Usage pattern:
    ///   if (driver is IExtendedAttributeSupport xatDriver)
    ///   {
    ///       var props = xatDriver.GetFileProperties(path);
    ///   }
    /// </remarks>
    public interface IExtendedAttributeSupport
    {
        /// <summary>
        /// Gets the filesystem capabilities (extended attribute support, property keys).
        /// </summary>
        /// <returns>Filesystem capabilities</returns>
        FileSystemCapabilities GetCapabilities();

        /// <summary>
        /// Gets file properties/metadata without reading file contents.
        /// Returns null if file doesn't exist.
        /// </summary>
        /// <param name="path">Filesystem path</param>
        /// <returns>Property bag containing file metadata, or null</returns>
        IDictionary<string, object>? GetFileProperties(string path);

        /// <summary>
        /// Writes a file with extended properties/metadata.
        /// </summary>
        /// <param name="path">Filesystem path</param>
        /// <param name="data">File data</param>
        /// <param name="propertyBag">Extended properties to apply</param>
        /// <exception cref="System.UnauthorizedAccessException">Thrown when filesystem is read-only</exception>
        void WriteFileWithProperties(string path, byte[] data, IDictionary<string, object> propertyBag);

        /// <summary>
        /// Reads a file with its extended properties/metadata.
        /// </summary>
        /// <param name="path">Filesystem path</param>
        /// <param name="propertyBag">Output parameter to receive file properties</param>
        /// <returns>File contents as byte array</returns>
        /// <exception cref="System.IO.FileNotFoundException">Thrown when file does not exist</exception>
        byte[] ReadFileWithProperties(string path, IDictionary<string, object> propertyBag);
    }
}
