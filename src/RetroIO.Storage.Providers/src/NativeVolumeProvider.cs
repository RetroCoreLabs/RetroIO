//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Providers
{
    /// <summary>
    /// Volume provider for mounting native OS filesystem directories.
    /// Provides direct passthrough access to Windows/Linux/macOS directories.
    /// </summary>
    public sealed class NativeVolumeProvider : IVolumeProvider
    {
        private readonly Dictionary<string, NativeDirectoryMount> _mounts;
        private bool _disposed;

        /// <summary>
        /// Creates the provider with no mounted volumes.
        /// </summary>
        public NativeVolumeProvider()
        {
            _mounts = new Dictionary<string, NativeDirectoryMount>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets the volume type, which is native.
        /// </summary>
        public string Type => "native";

        // VFS Properties
        /// <summary>
        /// Gets the URI schemes this provider handles, file.
        /// </summary>
        public string[] SupportedSchemes => new[] { "file" };
        /// <summary>
        /// Gets whether the volumes are remote, which is false.
        /// </summary>
        public bool IsRemote => false;

        /// <summary>
        /// Checks that the directory named by the source URI of a mount entry exists.
        /// </summary>
        /// <param name="entry">
        /// The mount entry to check.
        /// </param>
        /// <param name="errorMessage">
        /// The reason when the check fails, otherwise null.
        /// </param>
        /// <returns>
        /// True when the directory exists. False otherwise.
        /// </returns>
        public bool CanRestore(UnifiedMountEntry entry, out string? errorMessage)
        {
            if (!Directory.Exists(entry.SourceUri))
            {
                errorMessage = $"Directory not found: {entry.SourceUri}";
                return false;
            }

            errorMessage = null;
            return true;
        }

        /// <summary>
        /// Mounts a volume from a mount entry, using its source URI, volume identifier and read-only flag.
        /// </summary>
        /// <param name="entry">
        /// The mount entry.
        /// </param>
        /// <returns>
        /// Information about the mounted volume.
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the URI or the volume identifier is empty, or the volume identifier is already mounted.
        /// </exception>
        /// <exception cref="System.IO.DirectoryNotFoundException">
        /// Thrown when the directory does not exist.
        /// </exception>
        public MountInfo MountFromConfig(UnifiedMountEntry entry)
        {
            return Mount(entry.SourceUri, entry.VolumeId, entry.IsReadOnly);
        }

        /// <summary>
        /// Mounts a directory of the local file system as a volume.
        /// </summary>
        /// <param name="uri">
        /// The path of the directory.
        /// </param>
        /// <param name="volumeId">
        /// The identifier for the new volume.
        /// </param>
        /// <param name="readOnly">
        /// True to refuse writes.
        /// </param>
        /// <returns>
        /// Information about the mounted volume. The label is the name of the directory.
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// Thrown when <paramref name="uri"/> or <paramref name="volumeId"/> is empty, or the volume identifier is already mounted.
        /// </exception>
        /// <exception cref="System.IO.DirectoryNotFoundException">
        /// Thrown when the directory does not exist.
        /// </exception>
        public MountInfo Mount(string uri, string volumeId, bool readOnly)
        {
            if (string.IsNullOrEmpty(uri))
                throw new ArgumentException("URI cannot be null or empty.", nameof(uri));
            if (string.IsNullOrEmpty(volumeId))
                throw new ArgumentException("Volume ID cannot be null or empty.", nameof(volumeId));
            if (_mounts.ContainsKey(volumeId))
                throw new ArgumentException($"Volume ID '{volumeId}' is already mounted.");
            if (!Directory.Exists(uri))
                throw new DirectoryNotFoundException($"Directory not found: {uri}");

            NativeDirectoryMount mount = new NativeDirectoryMount
            {
                VolumeId = volumeId,
                RootPath = Path.GetFullPath(uri),
                IsReadOnly = readOnly
            };

            _mounts.Add(volumeId, mount);

            return new MountInfo
            {
                VolumeId = volumeId,
                Type = Type,
                FileSystemType = "Native Filesystem",
                VolumeLabel = Path.GetFileName(uri) ?? uri,
                SourceUri = uri,
                IsReadOnly = readOnly,
                HasBlockAccess = false
            };
        }

        /// <summary>
        /// Removes a volume from the provider. The directory is not changed.
        /// </summary>
        /// <param name="volumeId">
        /// The identifier of the volume.
        /// </param>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
        public void Unmount(string volumeId)
        {
            if (!_mounts.Remove(volumeId))
                throw new ArgumentException($"Volume ID '{volumeId}' not found.");
        }

        /// <summary>
        /// Gets information about a mounted volume.
        /// </summary>
        /// <param name="volumeId">
        /// The identifier of the volume.
        /// </param>
        /// <returns>
        /// The mount information, or null when the volume is not found.
        /// </returns>
        public MountInfo? GetMountInfo(string volumeId)
        {
            if (!_mounts.TryGetValue(volumeId, out NativeDirectoryMount? mount))
                return null;

            return new MountInfo
            {
                VolumeId = mount.VolumeId,
                Type = Type,
                FileSystemType = "Native Filesystem",
                VolumeLabel = Path.GetFileName(mount.RootPath) ?? mount.RootPath,
                SourceUri = mount.RootPath,
                IsReadOnly = mount.IsReadOnly,
                HasBlockAccess = false
            };
        }

        /// <summary>
        /// Lists the mounted volumes.
        /// </summary>
        /// <returns>
        /// An array with the mount information of each volume.
        /// </returns>
        public MountInfo[] ListMounts()
        {
            int count = _mounts.Count;
            MountInfo[] result = new MountInfo[count];
            int index = 0;

            Dictionary<string, NativeDirectoryMount>.Enumerator enumerator = _mounts.GetEnumerator();
            while (enumerator.MoveNext())
            {
                NativeDirectoryMount mount = enumerator.Current.Value;
                result[index++] = new MountInfo
                {
                    VolumeId = mount.VolumeId,
                    Type = Type,
                    FileSystemType = "Native Filesystem",
                    VolumeLabel = Path.GetFileName(mount.RootPath) ?? mount.RootPath,
                    SourceUri = mount.RootPath,
                    IsReadOnly = mount.IsReadOnly,
                    HasBlockAccess = false
                };
            }
            enumerator.Dispose();

            return result;
        }

        /// <summary>
        /// Reads a file from the mounted directory.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path of the file below the mounted directory.
        /// </param>
        /// <returns>
        /// The content of the file.
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
        /// <exception cref="System.UnauthorizedAccessException">
        /// Thrown when the path leaves the mounted directory.
        /// </exception>
        public byte[] ReadFile(UrnContext context, string path)
        {
            NativeDirectoryMount mount = GetMount(context.VolumeId);
            string fullPath = GetFullPath(mount, path);
            return File.ReadAllBytes(fullPath);
        }

        /// <summary>
        /// Writes a byte array to a file in the mounted directory, replacing an existing file.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path of the file below the mounted directory.
        /// </param>
        /// <param name="data">
        /// The content to write.
        /// </param>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
        /// <exception cref="System.UnauthorizedAccessException">
        /// Thrown when the volume is mounted read-only, or the path leaves the mounted directory.
        /// </exception>
        public void WriteFile(UrnContext context, string path, byte[] data)
        {
            NativeDirectoryMount mount = GetMount(context.VolumeId);
            if (mount.IsReadOnly)
                throw new UnauthorizedAccessException("Volume is mounted read-only.");

            string fullPath = GetFullPath(mount, path);
            File.WriteAllBytes(fullPath, data);
        }

        /// <summary>
        /// Deletes a file in the mounted directory.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path of the file below the mounted directory.
        /// </param>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
        /// <exception cref="System.UnauthorizedAccessException">
        /// Thrown when the volume is mounted read-only, or the path leaves the mounted directory.
        /// </exception>
        public void DeleteFile(UrnContext context, string path)
        {
            NativeDirectoryMount mount = GetMount(context.VolumeId);
            if (mount.IsReadOnly)
                throw new UnauthorizedAccessException("Volume is mounted read-only.");

            string fullPath = GetFullPath(mount, path);
            File.Delete(fullPath);
        }

        /// <summary>
        /// Lists the subdirectories and files of a directory in the mounted directory.
        /// </summary>
        /// <remarks>
        /// Subdirectories come first, then files. A file entry has the extension as its file type.
        /// </remarks>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path of the directory below the mounted directory.
        /// </param>
        /// <returns>
        /// An array with the entries, with size, creation time and last write time.
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
        /// <exception cref="System.IO.DirectoryNotFoundException">
        /// Thrown when the directory does not exist.
        /// </exception>
        /// <exception cref="System.UnauthorizedAccessException">
        /// Thrown when the path leaves the mounted directory.
        /// </exception>
        public DirectoryEntry[] ListDirectory(UrnContext context, string path)
        {
            NativeDirectoryMount mount = GetMount(context.VolumeId);
            string fullPath = GetFullPath(mount, path);

            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException($"Directory not found: {path}");

            List<DirectoryEntry> entries = new List<DirectoryEntry>();

            // Add directories
            string[] directories = Directory.GetDirectories(fullPath);
            for (int i = 0; i < directories.Length; i++)
            {
                DirectoryInfo dirInfo = new DirectoryInfo(directories[i]);
                entries.Add(new DirectoryEntry
                {
                    Name = dirInfo.Name,
                    Metadata = new FileMetadata
                    {
                        IsDirectory = true,
                        Size = 0,
                        CreatedTime = dirInfo.CreationTime,
                        ModifiedTime = dirInfo.LastWriteTime
                    }
                });
            }

            // Add files
            string[] files = Directory.GetFiles(fullPath);
            for (int i = 0; i < files.Length; i++)
            {
                FileInfo fileInfo = new FileInfo(files[i]);
                entries.Add(new DirectoryEntry
                {
                    Name = fileInfo.Name,
                    Metadata = new FileMetadata
                    {
                        IsDirectory = false,
                        Size = fileInfo.Length,
                        CreatedTime = fileInfo.CreationTime,
                        ModifiedTime = fileInfo.LastWriteTime,
                        FileType = fileInfo.Extension
                    }
                });
            }

            return entries.ToArray();
        }

        /// <summary>
        /// Gets information about a directory or file in the mounted directory.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path below the mounted directory.
        /// </param>
        /// <returns>
        /// The metadata, or null when neither a directory nor a file exists at the path.
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
        /// <exception cref="System.UnauthorizedAccessException">
        /// Thrown when the path leaves the mounted directory.
        /// </exception>
        public FileMetadata? GetMetadata(UrnContext context, string path)
        {
            NativeDirectoryMount mount = GetMount(context.VolumeId);
            string fullPath = GetFullPath(mount, path);

            if (Directory.Exists(fullPath))
            {
                DirectoryInfo dirInfo = new DirectoryInfo(fullPath);
                return new FileMetadata
                {
                    IsDirectory = true,
                    Size = 0,
                    CreatedTime = dirInfo.CreationTime,
                    ModifiedTime = dirInfo.LastWriteTime
                };
            }

            if (File.Exists(fullPath))
            {
                FileInfo fileInfo = new FileInfo(fullPath);
                return new FileMetadata
                {
                    IsDirectory = false,
                    Size = fileInfo.Length,
                    CreatedTime = fileInfo.CreationTime,
                    ModifiedTime = fileInfo.LastWriteTime,
                    FileType = fileInfo.Extension
                };
            }

            return null;
        }

        /// <summary>
        /// Not supported. Always throws.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="address">
        /// The block address.
        /// </param>
        /// <param name="buffer">
        /// The buffer that would receive the block.
        /// </param>
        /// <returns>
        /// Never returns.
        /// </returns>
        /// <exception cref="System.NotSupportedException">
        /// Always thrown, because the provider has no block-level access.
        /// </exception>
        public int ReadBlock(UrnContext context, BlockAddress address, Span<byte> buffer)
        {
            throw new NotSupportedException("Native volume provider does not support block-level access.");
        }

        /// <summary>
        /// Not supported. Always throws.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="address">
        /// The block address.
        /// </param>
        /// <param name="data">
        /// The data that would be written.
        /// </param>
        /// <exception cref="System.NotSupportedException">
        /// Always thrown, because the provider has no block-level access.
        /// </exception>
        public void WriteBlock(UrnContext context, BlockAddress address, ReadOnlySpan<byte> data)
        {
            throw new NotSupportedException("Native volume provider does not support block-level access.");
        }

        /// <summary>
        /// Returns null, because there is no block access.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <returns>
        /// Always null.
        /// </returns>
        public BlockDeviceInfo? GetBlockDeviceInfo(UrnContext context)
        {
            return null;
        }

        // ==================== VFS Async Methods ====================

        /// <summary>
        /// Opens a file for reading with shared read access.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path of the file below the mounted directory.
        /// </param>
        /// <param name="cancellationToken">
        /// A token that cancels waiting for the task to start.
        /// </param>
        /// <returns>
        /// A task with the file stream.
        /// </returns>
        public async Task<Stream> OpenReadAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            NativeDirectoryMount mount = GetMount(context.VolumeId);
            string fullPath = GetFullPath(mount, path);

            // Use FileStream with async flag for true async I/O
            return await Task.Run(() => new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true), cancellationToken);
        }

        /// <summary>
        /// Copies a stream into a file, replacing an existing file.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path of the file below the mounted directory.
        /// </param>
        /// <param name="data">
        /// The stream with the content to write.
        /// </param>
        /// <param name="cancellationToken">
        /// A token that cancels the copy.
        /// </param>
        /// <returns>
        /// A task that completes when the copy is done.
        /// </returns>
        /// <exception cref="System.UnauthorizedAccessException">
        /// Thrown when the volume is mounted read-only, or the path leaves the mounted directory.
        /// </exception>
        public async Task WriteAsync(UrnContext context, string path, Stream data, CancellationToken cancellationToken)
        {
            NativeDirectoryMount mount = GetMount(context.VolumeId);
            if (mount.IsReadOnly)
                throw new UnauthorizedAccessException("Volume is mounted read-only.");

            string fullPath = GetFullPath(mount, path);

            using FileStream fs = new FileStream(
                fullPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true);

            await data.CopyToAsync(fs, bufferSize: 81920, cancellationToken);
        }

        /// <summary>
        /// Creates a directory, with any missing parent directories, in the mounted directory.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path of the directory below the mounted directory.
        /// </param>
        /// <exception cref="System.UnauthorizedAccessException">
        /// Thrown when the volume is mounted read-only, or the path leaves the mounted directory.
        /// </exception>
        public void CreateDirectory(UrnContext context, string path)
        {
            NativeDirectoryMount mount = GetMount(context.VolumeId);
            if (mount.IsReadOnly)
                throw new UnauthorizedAccessException("Volume is mounted read-only.");

            string fullPath = GetFullPath(mount, path);
            Directory.CreateDirectory(fullPath);
        }

        /// <summary>
        /// Lists a directory by running ListDirectory on a thread pool thread.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path of the directory below the mounted directory.
        /// </param>
        /// <param name="cancellationToken">
        /// A token that cancels waiting for the task to start.
        /// </param>
        /// <returns>
        /// A task with the directory entries.
        /// </returns>
        public Task<DirectoryEntry[]> ListDirectoryAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            // Wrap synchronous method (Directory class doesn't support true async)
            return Task.Run(() => ListDirectory(context, path), cancellationToken);
        }

        /// <summary>
        /// Gets metadata by running GetMetadata on a thread pool thread.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path below the mounted directory.
        /// </param>
        /// <param name="cancellationToken">
        /// A token that cancels waiting for the task to start.
        /// </param>
        /// <returns>
        /// A task with the metadata, or null.
        /// </returns>
        public Task<FileMetadata?> GetMetadataAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            // Wrap synchronous method
            return Task.Run(() => GetMetadata(context, path), cancellationToken);
        }

        /// <summary>
        /// Removes all mounted volumes. The directories are not changed.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _mounts.Clear();
            _disposed = true;
        }

        private NativeDirectoryMount GetMount(string volumeId)
        {
            if (!_mounts.TryGetValue(volumeId, out NativeDirectoryMount? mount))
                throw new ArgumentException($"Volume ID '{volumeId}' not found.");
            return mount;
        }

        private string GetFullPath(NativeDirectoryMount mount, string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
                return mount.RootPath;

            // Remove leading slash
            if (path.StartsWith("/"))
                path = path.Substring(1);

            // Convert forward slashes to platform-specific separator
            path = path.Replace('/', Path.DirectorySeparatorChar);

            string fullPath = Path.Combine(mount.RootPath, path);

            // Security check: ensure the path is within the mount root
            string normalizedFull = Path.GetFullPath(fullPath);
            string normalizedRoot = Path.GetFullPath(mount.RootPath);

            // Compare against the root WITH a trailing separator. A plain StartsWith(root) also accepts a
            // sibling folder whose name begins with the root's name ("root" and "rootX").
            string rootWithSeparator = Path.EndsInDirectorySeparator(normalizedRoot)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;

            bool isRoot = string.Equals(normalizedFull, normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            if (!isRoot && !normalizedFull.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Path traversal attempt detected.");

            return normalizedFull;
        }

        private sealed class NativeDirectoryMount
        {
            public string VolumeId { get; set; } = string.Empty;
            public string RootPath { get; set; } = string.Empty;
            public bool IsReadOnly { get; set; }
        }
    }
}
