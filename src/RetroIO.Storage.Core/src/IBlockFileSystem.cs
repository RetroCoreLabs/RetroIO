//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

// ============================================================================
// ⚠️ CRITICAL ARCHITECTURE NOTICE ⚠️
// ============================================================================
//
// This interface MUST remain in RetroIO.Storage.Core - DO NOT move to RetroIO.Storage.Local!
//
// REASON: All filesystem implementations depend on this interface.
//         If moved to RetroIO.Storage.Local, it would create circular dependencies:
//         RetroIO.Storage.Local → a former BSD library → RetroIO.Storage.Local (CIRCULAR!)
//
// HISTORY: Originally placed in RetroIO.Storage.Local, causing BSD to be split
//          into a separate project to avoid circular dependency.
//          Moved to Core to fix the root cause and allow proper architecture.
//
// RELATED INTERFACES (all in RetroIO.Storage.Core):
//   - IBlockDevice (physical block I/O layer)
//   - IFileSystemDriver (unified driver interface)
//   - IBlockFileSystem (THIS - logical filesystem layer)
//
// ============================================================================

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Interface for filesystems that operate on block devices.
    /// Provides logical file/directory operations on top of raw block I/O.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This interface represents the logical filesystem layer that sits on top
    /// of a block device (IBlockDevice). It handles directory structures, file allocation,
    /// and filesystem-specific metadata while delegating raw block I/O to the device.
    /// </para>
    /// <para>
    /// <strong>Examples of implementations:</strong>
    /// <list type="bullet">
    /// <item>CbmDosFileSystem - Commodore DOS (D64/D71/D81)</item>
    /// <item>DragonDosFileSystem - Dragon 32/64 DragonDOS</item>
    /// <item>DecbFileSystem - TRS-80 Color Computer Disk BASIC</item>
    /// <item>Fat12/16/32FileSystem - PC floppy/hard disk formats</item>
    /// <item>V7FileSystem - UNIX V7 filesystem (1979)</item>
    /// <item>NdfsFileSystem - Nord Disk File System</item>
    /// <item>Future: CP/M, GEOS, AmigaDOS OFS/FFS, ProDOS, FFS/UFS</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Architectural Pattern:</strong>
    /// <code>
    /// IFileSystemDriver (RetroIO.Storage.Core)
    ///     ↓ composes
    /// IBlockDevice (RetroIO.Storage.Core)
    ///     ↓ used by
    /// IBlockFileSystem (RetroIO.Storage.Core) ← THIS INTERFACE
    /// </code>
    /// </para>
    /// <para>
    /// Analogous to Linux kernel's filesystem driver layer (ext4, FAT, NTFS)
    /// that operates on block devices (struct gendisk).
    /// </para>
    /// </remarks>
    public interface IBlockFileSystem
    {
        /// <summary>
        /// Gets the filesystem type identifier.
        /// </summary>
        /// <value>
        /// Format-specific identifier such as:
        /// "CBM-DOS", "CP/M", "GEOS", "AmigaDOS-OFS", "DragonDOS", "DECB",
        /// "FAT12", "FAT16", "FAT32", "BSD-V7", "NDFS", "FFS", "UFS"
        /// </value>
        string FileSystemType { get; }

        /// <summary>
        /// Gets the separator character(s) used between filename and extension.
        /// </summary>
        /// <value>
        /// Examples: "." for FAT/Windows, ":" for NDFS, "" (empty) for CBM DOS.
        /// </value>
        string ExtensionSeparator { get; }

        /// <summary>
        /// Gets the volume label from the filesystem metadata.
        /// </summary>
        /// <value>
        /// User-defined volume label (e.g., "MY DISK", "BACKUP 01").
        /// Returns empty string if volume label is not supported or not set.
        /// </value>
        string VolumeLabel { get; }

        /// <summary>
        /// Initializes the filesystem with a block device.
        /// Must be called before any other filesystem operations.
        /// </summary>
        /// <param name="device">Block device instance providing raw block I/O</param>
        /// <exception cref="System.ArgumentNullException">If device is null</exception>
        /// <exception cref="System.InvalidOperationException">If filesystem metadata is invalid or corrupted</exception>
        void Initialize(IBlockDevice device);

        /// <summary>
        /// Determines if this filesystem format can handle the given block device.
        /// Used for auto-detection of filesystem format.
        /// </summary>
        /// <param name="device">Block device to inspect</param>
        /// <returns>True if this filesystem can read the device, false otherwise</returns>
        /// <remarks>
        /// Implementations should check for filesystem-specific signatures
        /// (magic numbers, directory markers, etc.) without modifying the device.
        /// </remarks>
        bool CanHandle(IBlockDevice device);

        /// <summary>
        /// Lists all files and subdirectories in the specified directory.
        /// </summary>
        /// <param name="path">Directory path (format-specific, e.g., "/" or "0:")</param>
        /// <returns>Array of directory entries</returns>
        /// <exception cref="System.IO.DirectoryNotFoundException">If path does not exist</exception>
        /// <exception cref="System.UnauthorizedAccessException">If access is denied (NDFS, etc.)</exception>
        DirectoryEntry[] ListDirectory(string path);

        /// <summary>
        /// Reads the complete contents of a file.
        /// </summary>
        /// <param name="path">File path</param>
        /// <returns>File contents as byte array</returns>
        /// <exception cref="System.IO.FileNotFoundException">If file does not exist</exception>
        /// <exception cref="System.UnauthorizedAccessException">If access is denied</exception>
        byte[] ReadFile(string path);

        /// <summary>
        /// Writes data to a file, creating or overwriting it.
        /// </summary>
        /// <param name="path">File path</param>
        /// <param name="data">Data to write</param>
        /// <exception cref="System.IO.IOException">If write fails (disk full, read-only, etc.)</exception>
        void WriteFile(string path, byte[] data);

        /// <summary>
        /// Deletes a file.
        /// </summary>
        /// <param name="path">File path</param>
        /// <exception cref="System.IO.FileNotFoundException">If file does not exist</exception>
        /// <exception cref="System.IO.IOException">If deletion fails</exception>
        void DeleteFile(string path);

        /// <summary>
        /// Gets metadata for a file or directory.
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <returns>Metadata object, or null if path does not exist</returns>
        FileMetadata? GetMetadata(string path);

        /// <summary>
        /// Calculates the number of free blocks on the disk.
        /// </summary>
        /// <returns>Number of free blocks available for allocation</returns>
        /// <remarks>
        /// Block size depends on the filesystem format:
        /// - CBM DOS: 256 bytes
        /// - NDFS: 2048 bytes (pages)
        /// - BSD V7: 1024 bytes
        /// - FAT: Cluster size (varies)
        /// </remarks>
        int GetBlocksFree();

        /// <summary>
        /// Creates a directory at the specified path.
        /// </summary>
        /// <param name="path">Path where directory should be created</param>
        /// <exception cref="System.IO.IOException">If creation fails</exception>
        /// <exception cref="System.NotSupportedException">If filesystem doesn't support directories</exception>
        void CreateDirectory(string path);

        /// <summary>
        /// Deletes a directory at the specified path.
        /// </summary>
        /// <param name="path">Path to directory to delete</param>
        /// <exception cref="System.IO.DirectoryNotFoundException">If directory does not exist</exception>
        /// <exception cref="System.IO.IOException">If directory is not empty or deletion fails</exception>
        void DeleteDirectory(string path);

        /// <summary>
        /// Renames or moves a directory or file.
        /// </summary>
        /// <param name="oldPath">Current path</param>
        /// <param name="newPath">New path</param>
        /// <exception cref="System.IO.FileNotFoundException">If oldPath does not exist</exception>
        /// <exception cref="System.IO.IOException">If rename fails</exception>
        void Rename(string oldPath, string newPath);
    }
}
