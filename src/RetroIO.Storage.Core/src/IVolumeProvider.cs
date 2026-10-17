//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Volume provider interface for accessing retro filesystems through various storage types.
    /// This is the external contract used by URN Manager and applications.
    /// Supports both synchronous (legacy) and asynchronous (VFS-aligned) operations.
    /// </summary>
    /// <remarks>
    /// Volume providers handle access to different storage types (disk images, native directories, network shares).
    /// Each provider manages its own mounts and delegates filesystem operations to appropriate drivers or backends.
    ///
    /// VFS Architecture Integration:
    /// - SupportedSchemes: Enables URN-based resolution (e.g., "ftp", "sftp", "tnfs")
    /// - IsRemote: Indicates if caching should be used (network/cloud providers)
    /// - Async methods: Preferred for network/cloud operations (non-blocking I/O)
    /// - Sync methods: Maintained for backward compatibility and local file operations
    /// </remarks>
    public interface IVolumeProvider : IDisposable
    {
        /// <summary>
        /// Gets the volume type handled by this provider (e.g., "diskimage", "native", "grpc", "tnfs").
        /// </summary>
        string Type { get; }

        /// <summary>
        /// Gets the URI schemes this provider supports (e.g., ["ftp", "ftps"] or ["file"]).
        /// Used by VfsResolver for automatic provider selection based on URN scheme.
        /// </summary>
        /// <remarks>
        /// Examples:
        ///  - NativeVolumeProvider: ["file"]
        ///  - FtpVolumeProvider: ["ftp", "ftps"]
        ///  - SftpVolumeProvider: ["sftp"]
        ///  - TnfsVolumeProvider: ["tnfs"]
        ///  - DiskImageVolumeProvider: ["d64", "d71", "d81", "ndfs", "vdk", "dmk"]
        /// </remarks>
        string[] SupportedSchemes { get; }

        /// <summary>
        /// Indicates whether this is a remote/network filesystem.
        /// When true, RetroCommander should enable caching for better performance.
        /// </summary>
        bool IsRemote { get; }

        /// <summary>
        /// Validates whether a mount entry can be restored by this provider.
        /// </summary>
        /// <param name="entry">
        /// Mount configuration entry to validate
        /// </param>
        /// <param name="errorMessage">
        /// Error message if validation fails, or null if successful
        /// </param>
        /// <returns>
        /// True if mount can be restored, false otherwise
        /// </returns>
        bool CanRestore(UnifiedMountEntry entry, out string? errorMessage);

        /// <summary>
        /// Mounts a volume from a unified configuration entry.
        /// This is used by MountManager to restore persisted mounts.
        /// </summary>
        /// <param name="entry">
        /// Mount configuration entry containing all parameters
        /// </param>
        /// <returns>
        /// Information about the mounted volume
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="entry"/> is invalid or volume ID is already in use
        /// </exception>
        /// <exception cref="System.IO.IOException">
        /// Thrown when mount operation fails
        /// </exception>
        MountInfo MountFromConfig(UnifiedMountEntry entry);

        /// <summary>
        /// Mounts a filesystem volume and makes it accessible via the specified volume ID.
        /// </summary>
        /// <param name="uri">
        /// Transport-specific URI for the resource (file path, server address, etc.)
        /// </param>
        /// <param name="volumeId">
        /// Unique identifier for this mount within the URN namespace
        /// </param>
        /// <param name="readOnly">
        /// If true, mount as read-only; if false, allow write operations
        /// </param>
        /// <returns>
        /// Information about the mounted volume
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when URI is invalid or volume ID is already in use
        /// </exception>
        /// <exception cref="System.IO.IOException">
        /// Thrown when mount operation fails
        /// </exception>
        MountInfo Mount(string uri, string volumeId, bool readOnly);

        /// <summary>
        /// Unmounts a previously mounted volume.
        /// </summary>
        /// <param name="volumeId">
        /// Volume identifier to unmount
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when volume ID is not found
        /// </exception>
        void Unmount(string volumeId);

        /// <summary>
        /// Gets information about a mounted volume.
        /// </summary>
        /// <param name="volumeId">
        /// Volume identifier
        /// </param>
        /// <returns>
        /// Mount information, or null if not found
        /// </returns>
        MountInfo? GetMountInfo(string volumeId);

        /// <summary>
        /// Lists all currently mounted volumes handled by this provider.
        /// </summary>
        /// <returns>
        /// Array of mount information for all mounted volumes
        /// </returns>
        MountInfo[] ListMounts();

        /// <summary>
        /// Reads an entire file from the filesystem.
        /// </summary>
        /// <param name="context">
        /// URN context identifying the volume
        /// </param>
        /// <param name="path">
        /// Filesystem path to the file (e.g., "/GAMES/PITFALL.PRG")
        /// </param>
        /// <returns>
        /// File contents as byte array
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when volume ID is not found
        /// </exception>
        /// <exception cref="System.IO.FileNotFoundException">
        /// Thrown when file does not exist
        /// </exception>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when access is denied
        /// </exception>
        byte[] ReadFile(UrnContext context, string path);

        /// <summary>
        /// Writes data to a file in the filesystem.
        /// </summary>
        /// <param name="context">
        /// URN context identifying the volume
        /// </param>
        /// <param name="path">
        /// Filesystem path where file should be written
        /// </param>
        /// <param name="data">
        /// Data to write
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when volume ID is not found
        /// </exception>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when volume is read-only or access is denied
        /// </exception>
        /// <exception cref="System.IO.IOException">
        /// Thrown when write operation fails
        /// </exception>
        void WriteFile(UrnContext context, string path, byte[] data);

        /// <summary>
        /// Deletes a file from the filesystem.
        /// </summary>
        /// <param name="context">
        /// URN context identifying the volume
        /// </param>
        /// <param name="path">
        /// Filesystem path to the file
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when volume ID is not found
        /// </exception>
        /// <exception cref="System.IO.FileNotFoundException">
        /// Thrown when file does not exist
        /// </exception>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when volume is read-only or access is denied
        /// </exception>
        void DeleteFile(UrnContext context, string path);

        /// <summary>
        /// Creates a directory (including any missing parents). Providers that cannot create directories
        /// inherit this default, which throws <see cref="System.NotSupportedException"/> — an honest
        /// failure rather than a silent no-op. Providers that can (native, ftp, sftp, tnfs) override it.
        /// </summary>
        /// <param name="context">
        /// URN context identifying the volume
        /// </param>
        /// <param name="path">
        /// Directory path to create (relative to the mount root)
        /// </param>
        void CreateDirectory(UrnContext context, string path)
            => throw new System.NotSupportedException("This volume provider does not support creating directories.");

        /// <summary>
        /// Lists directory contents.
        /// </summary>
        /// <param name="context">
        /// URN context identifying the volume
        /// </param>
        /// <param name="path">
        /// Directory path (use "/" or "" for root)
        /// </param>
        /// <returns>
        /// Array of directory entries
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when volume ID is not found
        /// </exception>
        /// <exception cref="System.IO.DirectoryNotFoundException">
        /// Thrown when directory does not exist
        /// </exception>
        DirectoryEntry[] ListDirectory(UrnContext context, string path);

        /// <summary>
        /// Gets metadata about a file or directory.
        /// </summary>
        /// <param name="context">
        /// URN context identifying the volume
        /// </param>
        /// <param name="path">
        /// Path to file or directory
        /// </param>
        /// <returns>
        /// File metadata, or null if not found
        /// </returns>
        FileMetadata? GetMetadata(UrnContext context, string path);

        // ==================== VFS Async Methods (Network/Cloud Optimized) ====================

        /// <summary>
        /// Opens a file for reading as a stream (async).
        /// Preferred method for network/cloud providers as it enables streaming without loading entire file into memory.
        /// </summary>
        /// <param name="context">
        /// URN context identifying the volume
        /// </param>
        /// <param name="path">
        /// Filesystem path to the file
        /// </param>
        /// <param name="cancellationToken">
        /// Cancellation token for async operation
        /// </param>
        /// <returns>
        /// Stream for reading file contents
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when volume ID is not found
        /// </exception>
        /// <exception cref="FileNotFoundException">
        /// Thrown when file does not exist
        /// </exception>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when access is denied
        /// </exception>
        Task<Stream> OpenReadAsync(UrnContext context, string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// Writes data from a stream to a file (async).
        /// Preferred method for network/cloud providers as it enables streaming uploads.
        /// </summary>
        /// <param name="context">
        /// URN context identifying the volume
        /// </param>
        /// <param name="path">
        /// Filesystem path where file should be written
        /// </param>
        /// <param name="data">
        /// Stream containing data to write
        /// </param>
        /// <param name="cancellationToken">
        /// Cancellation token for async operation
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when volume ID is not found
        /// </exception>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when volume is read-only or access is denied
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when write operation fails
        /// </exception>
        Task WriteAsync(UrnContext context, string path, Stream data, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists directory contents (async).
        /// Preferred method for network/cloud providers.
        /// </summary>
        /// <param name="context">
        /// URN context identifying the volume
        /// </param>
        /// <param name="path">
        /// Directory path (use "/" or "" for root)
        /// </param>
        /// <param name="cancellationToken">
        /// Cancellation token for async operation
        /// </param>
        /// <returns>
        /// Array of directory entries
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when volume ID is not found
        /// </exception>
        /// <exception cref="DirectoryNotFoundException">
        /// Thrown when directory does not exist
        /// </exception>
        Task<DirectoryEntry[]> ListDirectoryAsync(UrnContext context, string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets file/directory metadata (async).
        /// Preferred method for network/cloud providers.
        /// </summary>
        /// <param name="context">
        /// URN context identifying the volume
        /// </param>
        /// <param name="path">
        /// Path to file or directory
        /// </param>
        /// <param name="cancellationToken">
        /// Cancellation token for async operation
        /// </param>
        /// <returns>
        /// File metadata, or null if not found
        /// </returns>
        Task<FileMetadata?> GetMetadataAsync(UrnContext context, string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads a block of data from the filesystem at the block/sector level.
        /// This is used by emulators for direct disk access.
        /// </summary>
        /// <param name="context">
        /// URN context identifying the volume
        /// </param>
        /// <param name="address">
        /// Block address (LBA, track/sector, cylinder/head/sector)
        /// </param>
        /// <param name="buffer">
        /// Buffer to receive the block data
        /// </param>
        /// <returns>
        /// Number of bytes read
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when volume does not support block access
        /// </exception>
        /// <exception cref="System.IO.IOException">
        /// Thrown when read operation fails
        /// </exception>
        int ReadBlock(UrnContext context, BlockAddress address, Span<byte> buffer);

        /// <summary>
        /// Writes a block of data to the filesystem at the block/sector level.
        /// </summary>
        /// <param name="context">
        /// URN context identifying the volume
        /// </param>
        /// <param name="address">
        /// Block address
        /// </param>
        /// <param name="data">
        /// Data to write
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when volume does not support block access or is read-only
        /// </exception>
        /// <exception cref="System.IO.IOException">
        /// Thrown when write operation fails
        /// </exception>
        void WriteBlock(UrnContext context, BlockAddress address, ReadOnlySpan<byte> data);

        /// <summary>
        /// Gets information about block device characteristics.
        /// </summary>
        /// <param name="context">
        /// URN context identifying the volume
        /// </param>
        /// <returns>
        /// Block device information, or null if volume does not support block access
        /// </returns>
        BlockDeviceInfo? GetBlockDeviceInfo(UrnContext context);
    }
}
