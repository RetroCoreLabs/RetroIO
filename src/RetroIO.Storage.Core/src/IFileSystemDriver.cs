//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Unified interface for retro filesystem drivers.
    /// Combines high-level file operations with low-level block access.
    /// </summary>
    /// <remarks>
    /// Each driver implements support for a specific filesystem format (D64, NDFS, HFS, etc.).
    /// Drivers are stateless and operate on a mounted Stream.
    /// </remarks>
    public interface IFileSystemDriver : IDisposable
    {
        /// <summary>
        /// Gets the filesystem type identifier (e.g., "D64", "NDFS", "HFS").
        /// </summary>
        string FileSystemType { get; }

        /// <summary>
        /// Gets the separator character(s) used between filename and extension.
        /// Examples: "." for FAT/Windows, ":" for NDFS, "" (empty) for CBM DOS.
        /// </summary>
        string ExtensionSeparator { get; }

        /// <summary>
        /// Gets the metadata descriptor for this filesystem.
        /// Describes what metadata properties this filesystem supports.
        /// </summary>
        /// <returns>Metadata descriptor, or null if filesystem has no extended metadata</returns>
        IFileSystemMetadataDescriptor? GetMetadataDescriptor();

        /// <summary>
        /// Gets the volume label/name from the filesystem.
        /// </summary>
        string VolumeLabel { get; }

        /// <summary>
        /// Gets the driver's capabilities flags.
        /// Used for runtime capability detection and feature negotiation.
        /// </summary>
        DriverCapabilities Capabilities { get; }

        /// <summary>
        /// Indicates whether this driver supports read operations.
        /// </summary>
        bool CanRead { get; }

        /// <summary>
        /// Indicates whether this driver supports write operations.
        /// </summary>
        bool CanWrite { get; }

        /// <summary>
        /// Indicates whether this driver supports block-level access.
        /// </summary>
        bool HasBlockAccess { get; }

        /// <summary>
        /// Mounts a filesystem from the provided stream.
        /// </summary>
        /// <param name="imageStream">Stream containing the filesystem image</param>
        /// <param name="readOnly">If true, mount as read-only</param>
        /// <exception cref="InvalidDataException">Thrown when stream does not contain a valid filesystem</exception>
        void Mount(Stream imageStream, bool readOnly);

        /// <summary>
        /// Reads an entire file from the filesystem.
        /// </summary>
        /// <param name="path">Filesystem path (e.g., "/GAMES/PITFALL.PRG")</param>
        /// <returns>File contents as byte array</returns>
        /// <exception cref="FileNotFoundException">Thrown when file does not exist</exception>
        byte[] ReadFile(string path);

        /// <summary>
        /// Writes data to a file in the filesystem.
        /// </summary>
        /// <param name="path">Filesystem path</param>
        /// <param name="data">Data to write</param>
        /// <exception cref="UnauthorizedAccessException">Thrown when filesystem is read-only</exception>
        /// <exception cref="IOException">Thrown when write fails</exception>
        void WriteFile(string path, byte[] data);

        /// <summary>
        /// Deletes a file from the filesystem.
        /// </summary>
        /// <param name="path">Filesystem path</param>
        /// <exception cref="FileNotFoundException">Thrown when file does not exist</exception>
        /// <exception cref="UnauthorizedAccessException">Thrown when filesystem is read-only</exception>
        void DeleteFile(string path);

        /// <summary>
        /// Renames a file. The default implementation throws <see cref="NotSupportedException"/>;
        /// drivers that support renaming (e.g. CBM DOS) override this.
        /// </summary>
        /// <param name="oldPath">Existing file path.</param>
        /// <param name="newPath">New name/path.</param>
        void RenameFile(string oldPath, string newPath)
            => throw new NotSupportedException("Rename is not supported by this driver");

        /// <summary>
        /// Creates a directory at the given path. The default implementation throws
        /// <see cref="NotSupportedException"/>; drivers that support directories override this.
        /// For NDFS a root-level path (e.g. "/ALICE") creates a User.
        /// </summary>
        /// <param name="path">Directory path to create.</param>
        void CreateDirectory(string path)
            => throw new NotSupportedException("Directory creation is not supported by this driver");

        /// <summary>
        /// Lists contents of a directory.
        /// </summary>
        /// <param name="path">Directory path (use "/" or "" for root)</param>
        /// <returns>Array of directory entries</returns>
        /// <exception cref="DirectoryNotFoundException">Thrown when directory does not exist</exception>
        DirectoryEntry[] ListDirectory(string path);

        /// <summary>
        /// Gets metadata about a file or directory.
        /// </summary>
        /// <param name="path">Path to file or directory</param>
        /// <returns>File metadata, or null if not found</returns>
        FileMetadata? GetMetadata(string path);

        /// <summary>
        /// Reads a block of data at the specified address.
        /// </summary>
        /// <param name="address">Block address (LBA, track/sector, etc.)</param>
        /// <param name="buffer">Buffer to receive block data</param>
        /// <returns>Number of bytes read</returns>
        /// <exception cref="NotSupportedException">Thrown when HasBlockAccess is false</exception>
        /// <exception cref="ArgumentException">Thrown when address is invalid</exception>
        int ReadBlock(BlockAddress address, Span<byte> buffer);

        /// <summary>
        /// Writes a block of data at the specified address.
        /// </summary>
        /// <param name="address">Block address</param>
        /// <param name="data">Data to write</param>
        /// <exception cref="NotSupportedException">Thrown when HasBlockAccess is false</exception>
        /// <exception cref="UnauthorizedAccessException">Thrown when filesystem is read-only</exception>
        /// <exception cref="ArgumentException">Thrown when address is invalid</exception>
        void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data);

        /// <summary>
        /// Gets information about block device characteristics.
        /// </summary>
        /// <returns>Block device information, or null if block access is not supported</returns>
        BlockDeviceInfo? GetBlockDeviceInfo();
    }

    /// <remarks>
    /// For filesystems with extended attribute support (like NDFS), implement the optional
    /// IExtendedAttributeSupport interface. Standard filesystems (D64, T64, FAT) do not need
    /// to implement this interface.
    ///
    /// Usage: if (driver is IExtendedAttributeSupport xatDriver) { ... }
    /// </remarks>
}
