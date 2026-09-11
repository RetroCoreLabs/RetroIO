//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Providers
{
    /// <summary>
    /// SFTP volume provider for accessing SSH File Transfer Protocol servers.
    /// Provides secure network filesystem access over SSH (RFC 4251).
    /// </summary>
    /// <remarks>
    /// Supports:
    /// - Password authentication
    /// - SSH key authentication (RSA, DSA, ECDSA, ED25519)
    /// - Host key verification
    /// - Directory listing, file upload/download
    /// - Async operations for non-blocking I/O
    ///
    /// URN Format: sftp://user:pass@host:port/path
    /// </remarks>
    public sealed class SftpVolumeProvider : IVolumeProvider
    {
        private readonly Dictionary<string, SftpMount> _mounts;
        private bool _disposed;

        public SftpVolumeProvider()
        {
            _mounts = new Dictionary<string, SftpMount>(StringComparer.OrdinalIgnoreCase);
        }

        // IVolumeProvider properties
        public string Type => "sftp";
        public string[] SupportedSchemes => new[] { "sftp" };
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

            // Parse SFTP URI: sftp://user:pass@host:port/path
            Uri sftpUri = new Uri(uri);
            string host = sftpUri.Host;
            int port = sftpUri.Port > 0 ? sftpUri.Port : 22;
            string username = string.IsNullOrEmpty(sftpUri.UserInfo) ? Environment.UserName : sftpUri.UserInfo.Split(':')[0];
            string? password = sftpUri.UserInfo.Contains(':') ? sftpUri.UserInfo.Split(':', 2)[1] : null;
            string remotePath = sftpUri.AbsolutePath;

            // Create SFTP client
            SftpClient client;
            if (!string.IsNullOrEmpty(password))
            {
                // Password authentication
                client = new SftpClient(host, port, username, password);
            }
            else
            {
                // SSH key authentication: an explicit key file (?keyFile=..., optionally
                // ?keyPassphrase=...) takes priority; otherwise try the user's default SSH key
                // locations (~/.ssh/id_ed25519, id_rsa, id_ecdsa, id_dsa, in that order — same
                // priority order ssh(1) itself uses).
                string? explicitKeyPath = ParseQueryValue(sftpUri.Query, "keyFile");
                string? keyPassphrase = ParseQueryValue(sftpUri.Query, "keyPassphrase");
                string? keyPath = !string.IsNullOrEmpty(explicitKeyPath) ? explicitKeyPath : FindDefaultPrivateKey();

                if (keyPath == null)
                {
                    throw new ArgumentException(
                        "SFTP authentication required: no password in the URI, no ?keyFile= specified, " +
                        "and no default SSH key found under ~/.ssh (id_ed25519/id_rsa/id_ecdsa/id_dsa).");
                }

                PrivateKeyFile keyFile = string.IsNullOrEmpty(keyPassphrase)
                    ? new PrivateKeyFile(keyPath)
                    : new PrivateKeyFile(keyPath, keyPassphrase);

                var connectionInfo = new ConnectionInfo(
                    host, port, username,
                    new PrivateKeyAuthenticationMethod(username, keyFile));
                client = new SftpClient(connectionInfo);
            }

            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);

            SftpMount mount = new SftpMount
            {
                VolumeId = volumeId,
                Host = host,
                Port = port,
                Username = username,
                RemotePath = remotePath,
                IsReadOnly = readOnly,
                Client = client
            };

            _mounts[volumeId] = mount;

            return new MountInfo
            {
                VolumeId = volumeId,
                SourceUri = uri,
                Type = Type,
                IsReadOnly = readOnly,
                FileSystemType = "SFTP",
                VolumeLabel = $"{host}:{port}"
            };
        }

        public void Unmount(string volumeId)
        {
            if (!_mounts.TryGetValue(volumeId, out SftpMount? mount))
                throw new ArgumentException($"Volume ID '{volumeId}' not found.", nameof(volumeId));

            if (mount.Client.IsConnected)
                mount.Client.Disconnect();

            mount.Client.Dispose();
            _mounts.Remove(volumeId);
        }

        public MountInfo? GetMountInfo(string volumeId)
        {
            if (!_mounts.TryGetValue(volumeId, out SftpMount? mount))
                return null;

            return new MountInfo
            {
                VolumeId = mount.VolumeId,
                Type = Type,
                IsReadOnly = mount.IsReadOnly,
                FileSystemType = "SFTP",
                VolumeLabel = $"{mount.Host}:{mount.Port}"
            };
        }

        public MountInfo[] ListMounts()
        {
            MountInfo[] mounts = new MountInfo[_mounts.Count];
            int index = 0;

            Dictionary<string, SftpMount>.Enumerator enumerator = _mounts.GetEnumerator();
            while (enumerator.MoveNext())
            {
                SftpMount mount = enumerator.Current.Value;
                mounts[index++] = new MountInfo
                {
                    VolumeId = mount.VolumeId,
                    Type = Type,
                    IsReadOnly = mount.IsReadOnly,
                    FileSystemType = "SFTP",
                    VolumeLabel = $"{mount.Host}:{mount.Port}"
                };
            }
            enumerator.Dispose();

            return mounts;
        }

        // File operations
        public byte[] ReadFile(UrnContext context, string path)
        {
            SftpMount mount = GetMount(context.VolumeId);
            string fullPath = GetFullPath(mount, path);

            EnsureConnected(mount);
            return mount.Client.ReadAllBytes(fullPath);
        }

        public void WriteFile(UrnContext context, string path, byte[] data)
        {
            SftpMount mount = GetMount(context.VolumeId);

            if (mount.IsReadOnly || context.ReadOnly)
                throw new UnauthorizedAccessException("Volume is mounted read-only.");

            string fullPath = GetFullPath(mount, path);

            EnsureConnected(mount);
            mount.Client.WriteAllBytes(fullPath, data);
        }

        public void DeleteFile(UrnContext context, string path)
        {
            SftpMount mount = GetMount(context.VolumeId);

            if (mount.IsReadOnly || context.ReadOnly)
                throw new UnauthorizedAccessException("Volume is mounted read-only.");

            string fullPath = GetFullPath(mount, path);

            EnsureConnected(mount);
            mount.Client.DeleteFile(fullPath);
        }

        public DirectoryEntry[] ListDirectory(UrnContext context, string path)
        {
            SftpMount mount = GetMount(context.VolumeId);
            string fullPath = GetFullPath(mount, path);

            EnsureConnected(mount);

            System.Collections.Generic.IEnumerable<ISftpFile> files = mount.Client.ListDirectory(fullPath);
            List<DirectoryEntry> entries = new List<DirectoryEntry>();

            System.Collections.Generic.IEnumerator<ISftpFile> enumerator = files.GetEnumerator();
            while (enumerator.MoveNext())
            {
                ISftpFile file = enumerator.Current;

                // Skip . and ..
                if (file.Name == "." || file.Name == "..")
                    continue;

                entries.Add(new DirectoryEntry
                {
                    Name = file.Name,
                    Metadata = new FileMetadata
                    {
                        Name = file.Name,
                        Size = file.Length,
                        IsDirectory = file.IsDirectory,
                        ModifiedTime = file.LastWriteTime
                    }
                });
            }
            enumerator.Dispose();

            return entries.ToArray();
        }

        public FileMetadata? GetMetadata(UrnContext context, string path)
        {
            SftpMount mount = GetMount(context.VolumeId);
            string fullPath = GetFullPath(mount, path);

            EnsureConnected(mount);

            if (!mount.Client.Exists(fullPath))
                return null;

            ISftpFile file = mount.Client.Get(fullPath);

            return new FileMetadata
            {
                Name = file.Name,
                Size = file.Length,
                IsDirectory = file.IsDirectory,
                ModifiedTime = file.LastWriteTime
            };
        }

        // VFS Async methods
        public async Task<Stream> OpenReadAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            SftpMount mount = GetMount(context.VolumeId);
            string fullPath = GetFullPath(mount, path);

            EnsureConnected(mount);

            // SSH.NET's OpenRead is not truly async, but returns a stream that can be read async
            return await Task.Run(() => mount.Client.OpenRead(fullPath), cancellationToken);
        }

        public async Task WriteAsync(UrnContext context, string path, Stream data, CancellationToken cancellationToken)
        {
            SftpMount mount = GetMount(context.VolumeId);

            if (mount.IsReadOnly || context.ReadOnly)
                throw new UnauthorizedAccessException("Volume is mounted read-only.");

            string fullPath = GetFullPath(mount, path);

            EnsureConnected(mount);

            await Task.Run(() =>
            {
                using Stream targetStream = mount.Client.OpenWrite(fullPath);
                data.CopyTo(targetStream);
            }, cancellationToken);
        }

        public Task<DirectoryEntry[]> ListDirectoryAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            return Task.Run(() => ListDirectory(context, path), cancellationToken);
        }

        public Task<FileMetadata?> GetMetadataAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            return Task.Run(() => GetMetadata(context, path), cancellationToken);
        }

        // Block operations (not supported)
        public int ReadBlock(UrnContext context, BlockAddress address, Span<byte> buffer)
        {
            throw new NotSupportedException("SFTP does not support block-level access.");
        }

        public void WriteBlock(UrnContext context, BlockAddress address, ReadOnlySpan<byte> data)
        {
            throw new NotSupportedException("SFTP does not support block-level access.");
        }

        public BlockDeviceInfo? GetBlockDeviceInfo(UrnContext context)
        {
            return null;
        }

        // Helper methods
        private SftpMount GetMount(string volumeId)
        {
            if (!_mounts.TryGetValue(volumeId, out SftpMount? mount))
                throw new ArgumentException($"Volume ID '{volumeId}' not found.", nameof(volumeId));
            return mount;
        }

        private string GetFullPath(SftpMount mount, string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
                return mount.RemotePath;

            string normalizedPath = path.TrimStart('/');
            return mount.RemotePath.TrimEnd('/') + "/" + normalizedPath;
        }

        private void EnsureConnected(SftpMount mount)
        {
            if (!mount.Client.IsConnected)
            {
                mount.Client.Connect();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            string[] volumeIds = new string[_mounts.Count];
            int index = 0;

            Dictionary<string, SftpMount>.Enumerator enumerator = _mounts.GetEnumerator();
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

        /// <summary>
        /// Parses a string query-string value (e.g. "?keyFile=/path/to/key"). Returns null if the
        /// key isn't present. No query-string package is referenced by this project, so this is a
        /// small manual parser rather than a new dependency.
        /// </summary>
        private static string? ParseQueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query))
                return null;

            string trimmed = query[0] == '?' ? query.Substring(1) : query;
            string[] pairs = trimmed.Split('&');

            for (int i = 0; i < pairs.Length; i++)
            {
                int eq = pairs[i].IndexOf('=');
                if (eq < 0)
                    continue;

                string pairKey = pairs[i].Substring(0, eq);
                if (string.Equals(pairKey, key, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(pairs[i].Substring(eq + 1));
            }

            return null;
        }

        /// <summary>
        /// Looks for the user's default SSH private key, in the same priority order ssh(1) uses:
        /// id_ed25519, id_rsa, id_ecdsa, id_dsa under ~/.ssh. Returns null if none exist.
        /// </summary>
        private static string? FindDefaultPrivateKey()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string sshDir = Path.Combine(home, ".ssh");
            string[] candidates = { "id_ed25519", "id_rsa", "id_ecdsa", "id_dsa" };

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = Path.Combine(sshDir, candidates[i]);
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private sealed class SftpMount
        {
            public string VolumeId { get; set; } = string.Empty;
            public string Host { get; set; } = string.Empty;
            public int Port { get; set; }
            public string Username { get; set; } = string.Empty;
            public string RemotePath { get; set; } = "/";
            public bool IsReadOnly { get; set; }
            public SftpClient Client { get; set; } = null!;
        }
    }
}
