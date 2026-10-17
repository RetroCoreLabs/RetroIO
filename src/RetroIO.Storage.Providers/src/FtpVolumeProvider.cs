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

        /// <summary>
        /// Creates the provider with no mounted volumes.
        /// </summary>
        public FtpVolumeProvider()
        {
            _mounts = new Dictionary<string, FtpMount>(StringComparer.OrdinalIgnoreCase);
        }

        // IVolumeProvider properties
        /// <summary>
        /// Gets the volume type, which is ftp.
        /// </summary>
        public string Type => "ftp";
        /// <summary>
        /// Gets the URI schemes this provider handles, ftp and ftps.
        /// </summary>
        public string[] SupportedSchemes => new[] { "ftp", "ftps" };
        /// <summary>
        /// Gets whether the volumes are remote, which is true.
        /// </summary>
        public bool IsRemote => true;

        /// <summary>
        /// Checks that a mount entry has the ftp provider type.
        /// </summary>
        /// <param name="entry">
        /// The mount entry to check.
        /// </param>
        /// <param name="errorMessage">
        /// The reason when the check fails, otherwise null.
        /// </param>
        /// <returns>
        /// True when the provider type matches. False otherwise.
        /// </returns>
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
        public MountInfo MountFromConfig(UnifiedMountEntry entry)
        {
            return Mount(entry.SourceUri, entry.VolumeId, entry.IsReadOnly);
        }

        /// <summary>
        /// Mounts an FTP or FTPS server path as a volume.
        /// </summary>
        /// <remarks>
        /// The URI has the form ftp or ftps, then ://, then optional user name and password followed by an at sign, then host, optional port and path. With no user name, the user anonymous is used. The default port is 21. FTPS uses explicit encryption and checks the server certificate unless the query has allowInvalidCertificate=true or 1. The connection is not opened until the first operation.
        /// </remarks>
        /// <param name="uri">
        /// The URI of the FTP server and path.
        /// </param>
        /// <param name="volumeId">
        /// The identifier for the new volume.
        /// </param>
        /// <param name="readOnly">
        /// True to refuse writes.
        /// </param>
        /// <returns>
        /// Information about the mounted volume.
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// Thrown when <paramref name="uri"/> or <paramref name="volumeId"/> is empty, or the volume identifier is already mounted.
        /// </exception>
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

        /// <summary>
        /// Disposes the FTP client of a volume and removes the volume.
        /// </summary>
        /// <param name="volumeId">
        /// The identifier of the volume.
        /// </param>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
        public void Unmount(string volumeId)
        {
            if (!_mounts.TryGetValue(volumeId, out FtpMount? mount))
                throw new ArgumentException($"Volume ID '{volumeId}' not found.", nameof(volumeId));

            mount.Client?.Dispose();
            _mounts.Remove(volumeId);
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

        /// <summary>
        /// Lists the mounted volumes.
        /// </summary>
        /// <returns>
        /// An array with the mount information of each volume.
        /// </returns>
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
        /// <summary>
        /// Downloads a file from the server and waits for it to finish.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path of the file below the mounted path.
        /// </param>
        /// <returns>
        /// The content of the file.
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
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

        /// <summary>
        /// Uploads a byte array to the server, replacing an existing file, and waits for it to finish.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path of the file below the mounted path.
        /// </param>
        /// <param name="data">
        /// The content to write.
        /// </param>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
        /// <exception cref="System.UnauthorizedAccessException">
        /// Thrown when the volume or the context is read-only.
        /// </exception>
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

        /// <summary>
        /// Deletes a file on the server and waits for it to finish.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path of the file below the mounted path.
        /// </param>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
        /// <exception cref="System.UnauthorizedAccessException">
        /// Thrown when the volume or the context is read-only.
        /// </exception>
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

        /// <summary>
        /// Lists a directory on the server and waits for it to finish.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path of the directory below the mounted path.
        /// </param>
        /// <returns>
        /// An array with the entries of the directory, with name, size, directory flag and modified time.
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
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

        /// <summary>
        /// Gets information about a file or directory on the server and waits for it to finish.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path below the mounted path.
        /// </param>
        /// <returns>
        /// The metadata, or null when the server reports no such object.
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
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
        /// <summary>
        /// Opens a file on the server for binary reading as a stream.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path of the file below the mounted path.
        /// </param>
        /// <param name="cancellationToken">
        /// A token that cancels the operation.
        /// </param>
        /// <returns>
        /// A stream that reads the file.
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
        public async Task<Stream> OpenReadAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            FtpMount mount = GetMount(context.VolumeId);
            string fullPath = GetFullPath(mount, path);

            await EnsureConnectedAsync(mount);

            Stream stream = await mount.Client.OpenRead(fullPath, FtpDataType.Binary, token: cancellationToken);
            return stream;
        }

        /// <summary>
        /// Uploads a stream to the server, replacing an existing file and creating missing remote directories.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path of the file below the mounted path.
        /// </param>
        /// <param name="data">
        /// The stream with the content to write.
        /// </param>
        /// <param name="cancellationToken">
        /// A token that cancels the operation.
        /// </param>
        /// <returns>
        /// A task that completes when the upload is done.
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
        /// <exception cref="System.UnauthorizedAccessException">
        /// Thrown when the volume or the context is read-only.
        /// </exception>
        public async Task WriteAsync(UrnContext context, string path, Stream data, CancellationToken cancellationToken)
        {
            FtpMount mount = GetMount(context.VolumeId);

            if (mount.IsReadOnly || context.ReadOnly)
                throw new UnauthorizedAccessException("Volume is mounted read-only.");

            string fullPath = GetFullPath(mount, path);

            await EnsureConnectedAsync(mount);
            await mount.Client.UploadStream(data, fullPath, FtpRemoteExists.Overwrite, createRemoteDir: true, token: cancellationToken);
        }

        /// <summary>
        /// Lists a directory on the server.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path of the directory below the mounted path.
        /// </param>
        /// <param name="cancellationToken">
        /// A token that is not used.
        /// </param>
        /// <returns>
        /// A task with the directory entries.
        /// </returns>
        public Task<DirectoryEntry[]> ListDirectoryAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            return ListDirectoryInternalAsync(context, path);
        }

        /// <summary>
        /// Gets information about a file or directory on the server.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path below the mounted path.
        /// </param>
        /// <param name="cancellationToken">
        /// A token that is not used.
        /// </param>
        /// <returns>
        /// A task with the metadata, or null when the server reports no such object.
        /// </returns>
        public Task<FileMetadata?> GetMetadataAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            return GetMetadataInternalAsync(context, path);
        }

        // Block operations (not supported)
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
        /// Always thrown, because FTP has no block-level access.
        /// </exception>
        public int ReadBlock(UrnContext context, BlockAddress address, Span<byte> buffer)
        {
            throw new NotSupportedException("FTP does not support block-level access.");
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
        /// Always thrown, because FTP has no block-level access.
        /// </exception>
        public void WriteBlock(UrnContext context, BlockAddress address, ReadOnlySpan<byte> data)
        {
            throw new NotSupportedException("FTP does not support block-level access.");
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

        /// <summary>
        /// Unmounts every mounted volume.
        /// </summary>
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
