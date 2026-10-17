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

        public NativeVolumeProvider()
        {
            _mounts = new Dictionary<string, NativeDirectoryMount>(StringComparer.OrdinalIgnoreCase);
        }

        public string Type => "native";

        // VFS Properties
        public string[] SupportedSchemes => new[] { "file" };
        public bool IsRemote => false;

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

        public MountInfo MountFromConfig(UnifiedMountEntry entry)
        {
            return Mount(entry.SourceUri, entry.VolumeId, entry.IsReadOnly);
        }

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

        public void Unmount(string volumeId)
        {
            if (!_mounts.Remove(volumeId))
                throw new ArgumentException($"Volume ID '{volumeId}' not found.");
        }

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

        public byte[] ReadFile(UrnContext context, string path)
        {
            NativeDirectoryMount mount = GetMount(context.VolumeId);
            string fullPath = GetFullPath(mount, path);
            return File.ReadAllBytes(fullPath);
        }

        public void WriteFile(UrnContext context, string path, byte[] data)
        {
            NativeDirectoryMount mount = GetMount(context.VolumeId);
            if (mount.IsReadOnly)
                throw new UnauthorizedAccessException("Volume is mounted read-only.");

            string fullPath = GetFullPath(mount, path);
            File.WriteAllBytes(fullPath, data);
        }

        public void DeleteFile(UrnContext context, string path)
        {
            NativeDirectoryMount mount = GetMount(context.VolumeId);
            if (mount.IsReadOnly)
                throw new UnauthorizedAccessException("Volume is mounted read-only.");

            string fullPath = GetFullPath(mount, path);
            File.Delete(fullPath);
        }

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

        public int ReadBlock(UrnContext context, BlockAddress address, Span<byte> buffer)
        {
            throw new NotSupportedException("Native volume provider does not support block-level access.");
        }

        public void WriteBlock(UrnContext context, BlockAddress address, ReadOnlySpan<byte> data)
        {
            throw new NotSupportedException("Native volume provider does not support block-level access.");
        }

        public BlockDeviceInfo? GetBlockDeviceInfo(UrnContext context)
        {
            return null;
        }

        // ==================== VFS Async Methods ====================

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

        public void CreateDirectory(UrnContext context, string path)
        {
            NativeDirectoryMount mount = GetMount(context.VolumeId);
            if (mount.IsReadOnly)
                throw new UnauthorizedAccessException("Volume is mounted read-only.");

            string fullPath = GetFullPath(mount, path);
            Directory.CreateDirectory(fullPath);
        }

        public Task<DirectoryEntry[]> ListDirectoryAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            // Wrap synchronous method (Directory class doesn't support true async)
            return Task.Run(() => ListDirectory(context, path), cancellationToken);
        }

        public Task<FileMetadata?> GetMetadataAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            // Wrap synchronous method
            return Task.Run(() => GetMetadata(context, path), cancellationToken);
        }

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

            if (!normalizedFull.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
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
