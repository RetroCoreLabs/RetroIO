//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Providers
{
    /// <summary>
    /// FTP volume provider for accessing FTP servers.
    /// Provides network filesystem access using the FTP protocol (RFC 959).
    /// </summary>
    /// <remarks>
    /// Supports:
    /// - Active and passive mode FTP
    /// - FTPS (FTP over TLS/SSL)
    /// - Anonymous and authenticated access
    /// - Directory listing, file upload/download
    /// - Async operations for non-blocking I/O
    ///
    /// URN Format: ftp://user:pass@host:port/path or ftps://user:pass@host:port/path
    /// </remarks>
    public sealed class FtpVolumeProvider : IVolumeProvider
    {
        private readonly Dictionary<string, FtpMount> _mounts;
        private bool _disposed;

        public FtpVolumeProvider()
        {
            _mounts = new Dictionary<string, FtpMount>(StringComparer.OrdinalIgnoreCase);
        }

        // IVolumeProvider properties
        public string Type => "ftp";
        public string[] SupportedSchemes => new[] { "ftp", "ftps" };
        public bool IsRemote => true;

        public bool CanRestore(UnifiedMountEntry entry, out string? errorMessage)
        {
            if (entry.ProviderType != Type)
            {
                errorMessage = $"Type mismatch: expected '{Type}', got '{entry.ProviderType}'";
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
            if (string.IsNullOrWhiteSpace(uri))
                throw new ArgumentException("URI cannot be null or empty.", nameof(uri));

            if (string.IsNullOrWhiteSpace(volumeId))
                throw new ArgumentException("Volume ID cannot be null or empty.", nameof(volumeId));

            if (_mounts.ContainsKey(volumeId))
                throw new ArgumentException($"Volume ID '{volumeId}' is already mounted.", nameof(volumeId));

            // Parse FTP URI: ftp://user:pass@host:port/path
            Uri ftpUri = new Uri(uri);
            string host = ftpUri.Host;
            int port = ftpUri.Port > 0 ? ftpUri.Port : 21;
            string username = string.IsNullOrEmpty(ftpUri.UserInfo) ? "anonymous" : ftpUri.UserInfo.Split(':')[0];
            string password = ftpUri.UserInfo.Contains(':') ? ftpUri.UserInfo.Split(':', 2)[1] : "";
            string remotePath = ftpUri.AbsolutePath;
            bool useFtps = ftpUri.Scheme.Equals("ftps", StringComparison.OrdinalIgnoreCase);

            // Per-mount, explicit opt-out of certificate validation — NOT a global setting.
            // Deliberately requires the caller to type this into the URI they're mounting (and it
            // therefore persists visibly in mounts.json), rather than a silent app-wide "trust
            // everything" flag that's easy to flip once and forget. Legitimate for internal/legacy
            // FTPS servers with self-signed certificates; still validated by default otherwise.
            bool allowInvalidCertificate = ParseQueryFlag(ftpUri.Query, "allowInvalidCertificate");

            // Create FTP client
            AsyncFtpClient client = new AsyncFtpClient(host, username, password, port);

            if (useFtps)
            {
                client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
                client.Config.ValidateAnyCertificate = allowInvalidCertificate;
            }

            client.Config.ConnectTimeout = 30000; // 30 seconds
            client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;

            FtpMount mount = new FtpMount
            {
                VolumeId = volumeId,
                Host = host,
                Port = port,
                Username = username,
                RemotePath = remotePath,
                IsReadOnly = readOnly,
                Client = client,
                UseFtps = useFtps
            };

            _mounts[volumeId] = mount;

            string fileSystemType = useFtps
                ? (allowInvalidCertificate ? "FTPS (UNVERIFIED CERTIFICATE)" : "FTPS")
                : "FTP";

            return new MountInfo
            {
                VolumeId = volumeId,
                SourceUri = uri,
                Type = Type,
                IsReadOnly = readOnly,
                FileSystemType = fileSystemType,
                VolumeLabel = $"{host}:{port}"
            };
        }

        /// <summary>
        /// Parses a boolean query-string flag (e.g. "?allowInvalidCertificate=true") without
        /// pulling in a query-string-parsing package this project doesn't otherwise reference.
        /// </summary>
        private static bool ParseQueryFlag(string query, string key)
        {
            if (string.IsNullOrEmpty(query))
                return false;

            string trimmed = query[0] == '?' ? query.Substring(1) : query;
            string[] pairs = trimmed.Split('&');

            for (int i = 0; i < pairs.Length; i++)
            {
                int eq = pairs[i].IndexOf('=');
                string pairKey = eq >= 0 ? pairs[i].Substring(0, eq) : pairs[i];
                string pairValue = eq >= 0 ? pairs[i].Substring(eq + 1) : string.Empty;

                if (string.Equals(pairKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Equals(pairValue, "true", StringComparison.OrdinalIgnoreCase) || pairValue == "1";
                }
            }

            return false;
        }

        public void Unmount(string volumeId)
        {
            if (!_mounts.TryGetValue(volumeId, out FtpMount? mount))
                throw new ArgumentException($"Volume ID '{volumeId}' not found.", nameof(volumeId));

            mount.Client?.Dispose();
            _mounts.Remove(volumeId);
        }

        public MountInfo? GetMountInfo(string volumeId)
        {
            if (!_mounts.TryGetValue(volumeId, out FtpMount? mount))
                return null;

            return new MountInfo
            {
                VolumeId = mount.VolumeId,
                Type = Type,
                IsReadOnly = mount.IsReadOnly,
                FileSystemType = mount.UseFtps ? "FTPS" : "FTP",
                VolumeLabel = $"{mount.Host}:{mount.Port}"
            };
        }

        public MountInfo[] ListMounts()
        {
            MountInfo[] mounts = new MountInfo[_mounts.Count];
            int index = 0;

            Dictionary<string, FtpMount>.Enumerator enumerator = _mounts.GetEnumerator();
            while (enumerator.MoveNext())
            {
                FtpMount mount = enumerator.Current.Value;
                mounts[index++] = new MountInfo
                {
                    VolumeId = mount.VolumeId,
                    Type = Type,
                    IsReadOnly = mount.IsReadOnly,
                    FileSystemType = mount.UseFtps ? "FTPS" : "FTP",
                    VolumeLabel = $"{mount.Host}:{mount.Port}"
                };
            }
            enumerator.Dispose();

            return mounts;
        }

        // File operations
        public byte[] ReadFile(UrnContext context, string path)
        {
            return ReadFileAsync(context, path).GetAwaiter().GetResult();
        }

        private async Task<byte[]> ReadFileAsync(UrnContext context, string path)
        {
            FtpMount mount = GetMount(context.VolumeId);
            string fullPath = GetFullPath(mount, path);

            await EnsureConnectedAsync(mount);

            using MemoryStream ms = new MemoryStream();
            await mount.Client.DownloadStream(ms, fullPath);
            return ms.ToArray();
        }

        public void WriteFile(UrnContext context, string path, byte[] data)
        {
            WriteFileAsync(context, path, data).GetAwaiter().GetResult();
        }

        private async Task WriteFileAsync(UrnContext context, string path, byte[] data)
        {
            FtpMount mount = GetMount(context.VolumeId);

            if (mount.IsReadOnly || context.ReadOnly)
                throw new UnauthorizedAccessException("Volume is mounted read-only.");

            string fullPath = GetFullPath(mount, path);

            await EnsureConnectedAsync(mount);

            using MemoryStream ms = new MemoryStream(data);
            await mount.Client.UploadStream(ms, fullPath, FtpRemoteExists.Overwrite);
        }

        public void DeleteFile(UrnContext context, string path)
        {
            DeleteFileAsync(context, path).GetAwaiter().GetResult();
        }

        private async Task DeleteFileAsync(UrnContext context, string path)
        {
            FtpMount mount = GetMount(context.VolumeId);

            if (mount.IsReadOnly || context.ReadOnly)
                throw new UnauthorizedAccessException("Volume is mounted read-only.");

            string fullPath = GetFullPath(mount, path);

            await EnsureConnectedAsync(mount);
            await mount.Client.DeleteFile(fullPath);
        }

        public DirectoryEntry[] ListDirectory(UrnContext context, string path)
        {
            return ListDirectoryInternalAsync(context, path).GetAwaiter().GetResult();
        }

        private async Task<DirectoryEntry[]> ListDirectoryInternalAsync(UrnContext context, string path)
        {
            FtpMount mount = GetMount(context.VolumeId);
            string fullPath = GetFullPath(mount, path);

            await EnsureConnectedAsync(mount);

            FtpListItem[] items = await mount.Client.GetListing(fullPath);
            DirectoryEntry[] entries = new DirectoryEntry[items.Length];

            for (int i = 0; i < items.Length; i++)
            {
                FtpListItem item = items[i];
                entries[i] = new DirectoryEntry
                {
                    Name = item.Name,
                    Metadata = new FileMetadata
                    {
                        Name = item.Name,
                        Size = item.Size,
                        IsDirectory = item.Type == FtpObjectType.Directory,
                        ModifiedTime = item.Modified
                    }
                };
            }

            return entries;
        }

        public FileMetadata? GetMetadata(UrnContext context, string path)
        {
            return GetMetadataInternalAsync(context, path).GetAwaiter().GetResult();
        }

        private async Task<FileMetadata?> GetMetadataInternalAsync(UrnContext context, string path)
        {
            FtpMount mount = GetMount(context.VolumeId);
            string fullPath = GetFullPath(mount, path);

            await EnsureConnectedAsync(mount);

            FtpListItem? item = await mount.Client.GetObjectInfo(fullPath);
            if (item == null)
                return null;

            return new FileMetadata
            {
                Name = item.Name,
                Size = item.Size,
                IsDirectory = item.Type == FtpObjectType.Directory,
                ModifiedTime = item.Modified
            };
        }

        // VFS Async methods
        public async Task<Stream> OpenReadAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            FtpMount mount = GetMount(context.VolumeId);
            string fullPath = GetFullPath(mount, path);

            await EnsureConnectedAsync(mount);

            Stream stream = await mount.Client.OpenRead(fullPath, FtpDataType.Binary, token: cancellationToken);
            return stream;
        }

        public async Task WriteAsync(UrnContext context, string path, Stream data, CancellationToken cancellationToken)
        {
            FtpMount mount = GetMount(context.VolumeId);

            if (mount.IsReadOnly || context.ReadOnly)
                throw new UnauthorizedAccessException("Volume is mounted read-only.");

            string fullPath = GetFullPath(mount, path);

            await EnsureConnectedAsync(mount);
            await mount.Client.UploadStream(data, fullPath, FtpRemoteExists.Overwrite, createRemoteDir: true, token: cancellationToken);
        }

        public Task<DirectoryEntry[]> ListDirectoryAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            return ListDirectoryInternalAsync(context, path);
        }

        public Task<FileMetadata?> GetMetadataAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            return GetMetadataInternalAsync(context, path);
        }

        // Block operations (not supported)
        public int ReadBlock(UrnContext context, BlockAddress address, Span<byte> buffer)
        {
            throw new NotSupportedException("FTP does not support block-level access.");
        }

        public void WriteBlock(UrnContext context, BlockAddress address, ReadOnlySpan<byte> data)
        {
            throw new NotSupportedException("FTP does not support block-level access.");
        }

        public BlockDeviceInfo? GetBlockDeviceInfo(UrnContext context)
        {
            return null;
        }

        // Helper methods
        private FtpMount GetMount(string volumeId)
        {
            if (!_mounts.TryGetValue(volumeId, out FtpMount? mount))
                throw new ArgumentException($"Volume ID '{volumeId}' not found.", nameof(volumeId));
            return mount;
        }

        private string GetFullPath(FtpMount mount, string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
                return mount.RemotePath;

            string normalizedPath = path.TrimStart('/');
            return mount.RemotePath.TrimEnd('/') + "/" + normalizedPath;
        }

        private async Task EnsureConnectedAsync(FtpMount mount)
        {
            if (!mount.Client.IsConnected)
            {
                await mount.Client.Connect();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            string[] volumeIds = new string[_mounts.Count];
            int index = 0;

            Dictionary<string, FtpMount>.Enumerator enumerator = _mounts.GetEnumerator();
            while (enumerator.MoveNext())
            {
                volumeIds[index++] = enumerator.Current.Key;
            }
            enumerator.Dispose();

            for (int i = 0; i < volumeIds.Length; i++)
            {
                try
                {
                    Unmount(volumeIds[i]);
                }
                catch
                {
                    // Continue cleanup
                }
            }

            _disposed = true;
        }

        private sealed class FtpMount
        {
            public string VolumeId { get; set; } = string.Empty;
            public string Host { get; set; } = string.Empty;
            public int Port { get; set; }
            public string Username { get; set; } = string.Empty;
            public string RemotePath { get; set; } = "/";
            public bool IsReadOnly { get; set; }
            public bool UseFtps { get; set; }
            public AsyncFtpClient Client { get; set; } = null!;
        }
    }
}
