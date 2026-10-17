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
using Renci.SshNet;
using Renci.SshNet.Common;
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
    /// - Host key verification: the server's key must be pinned, already known, or (with hostKeyCheck=accept-new) new; see SftpHostKeyPolicy
    /// - Directory listing, file upload/download
    /// - Async operations for non-blocking I/O
    ///
    /// URN Format: sftp://user:pass@host:port/path
    /// </remarks>
    public sealed class SftpVolumeProvider : IVolumeProvider
    {
        private readonly Dictionary<string, SftpMount> _mounts;
        private bool _disposed;

        /// <summary>
        /// Creates the provider with no mounted volumes.
        /// </summary>
        public SftpVolumeProvider()
        {
            _mounts = new Dictionary<string, SftpMount>(StringComparer.OrdinalIgnoreCase);
        }

        // IVolumeProvider properties
        /// <summary>
        /// Gets the volume type, which is sftp.
        /// </summary>
        public string Type => "sftp";
        /// <summary>
        /// Gets the URI schemes this provider handles, sftp.
        /// </summary>
        public string[] SupportedSchemes => new[] { "sftp" };
        /// <summary>
        /// Gets whether the volumes are remote, which is true.
        /// </summary>
        public bool IsRemote => true;

        /// <summary>
        /// Checks that a mount entry has the sftp provider type.
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
        /// Thrown when the URI or the volume identifier is empty, the volume identifier is already mounted, or no password or key is available.
        /// </exception>
        public MountInfo MountFromConfig(UnifiedMountEntry entry)
        {
            return Mount(entry.SourceUri, entry.VolumeId, entry.IsReadOnly);
        }

        /// <summary>
        /// Mounts an SFTP server path as a volume.
        /// </summary>
        /// <remarks>
        /// The URI has the form sftp, then ://, then optional user name and password followed by an at sign, then host, optional port and path. The default port is 22 and the default user name is the current operating system user. With a password in the URI, password login is used. Without one, the key file from the query value keyFile is used, with the optional query value keyPassphrase, or else the first default key found under the .ssh folder of the user. The connection is not opened until the first operation, with a connection timeout of 30 seconds.
        /// <para>
        /// The server's host key is checked on every connection by <see cref="SftpHostKeyPolicy"/>. By default a server whose key is not pinned and not in the known-hosts file is refused. The query values hostKey, hostKeyCheck and knownHosts choose the keys to accept and how strict to be; see that class.
        /// </para>
        /// </remarks>
        /// <param name="uri">
        /// The URI of the SFTP server and path.
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
        /// Thrown when <paramref name="uri"/> or <paramref name="volumeId"/> is empty, the volume identifier is already mounted, the URI has no password and no key file is found, or a host key option is not valid.
        /// </exception>
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

            // Read the host key options first, so a bad option fails the mount before anything is created.
            SftpHostKeyPolicy hostKeyPolicy = SftpHostKeyPolicy.FromQuery(sftpUri.Query);

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

            // SSH.NET trusts every host key unless something is subscribed to HostKeyReceived.
            client.HostKeyReceived += (sender, e) =>
            {
                SftpHostKeyDecision decision = hostKeyPolicy.Evaluate(host, port, e.FingerPrintSHA256);
                e.CanTrust = decision.Trust;
                // SSH.NET reports a refused key only as "key exchange failed", so keep the reason.
                mount.HostKeyRefusal = decision.Trust ? null : decision.Reason;
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

        /// <summary>
        /// Disconnects and disposes the client of a volume and removes the volume.
        /// </summary>
        /// <param name="volumeId">
        /// The identifier of the volume.
        /// </param>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
        public void Unmount(string volumeId)
        {
            if (!_mounts.TryGetValue(volumeId, out SftpMount? mount))
                throw new ArgumentException($"Volume ID '{volumeId}' not found.", nameof(volumeId));

            if (mount.Client.IsConnected)
                mount.Client.Disconnect();

            mount.Client.Dispose();
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
        /// <summary>
        /// Reads a file from the server.
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
            SftpMount mount = GetMount(context.VolumeId);
            string fullPath = GetFullPath(mount, path);

            EnsureConnected(mount);
            return mount.Client.ReadAllBytes(fullPath);
        }

        /// <summary>
        /// Writes a byte array to a file on the server.
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
            SftpMount mount = GetMount(context.VolumeId);

            if (mount.IsReadOnly || context.ReadOnly)
                throw new UnauthorizedAccessException("Volume is mounted read-only.");

            string fullPath = GetFullPath(mount, path);

            EnsureConnected(mount);
            mount.Client.WriteAllBytes(fullPath, data);
        }

        /// <summary>
        /// Deletes a file on the server.
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
            SftpMount mount = GetMount(context.VolumeId);

            if (mount.IsReadOnly || context.ReadOnly)
                throw new UnauthorizedAccessException("Volume is mounted read-only.");

            string fullPath = GetFullPath(mount, path);

            EnsureConnected(mount);
            mount.Client.DeleteFile(fullPath);
        }

        /// <summary>
        /// Lists a directory on the server, without the dot and dot-dot entries.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path of the directory below the mounted path.
        /// </param>
        /// <returns>
        /// An array with the entries of the directory, with name, size, directory flag and last write time.
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
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

        /// <summary>
        /// Gets information about a file or directory on the server.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path below the mounted path.
        /// </param>
        /// <returns>
        /// The metadata, or null when the path does not exist.
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
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
        /// <summary>
        /// Opens a file on the server for reading as a stream.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path of the file below the mounted path.
        /// </param>
        /// <param name="cancellationToken">
        /// A token that cancels waiting for the task to start.
        /// </param>
        /// <returns>
        /// A task with the stream.
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
        public async Task<Stream> OpenReadAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            SftpMount mount = GetMount(context.VolumeId);
            string fullPath = GetFullPath(mount, path);

            EnsureConnected(mount);

            // SSH.NET's OpenRead is not truly async, but returns a stream that can be read async
            return await Task.Run(() => mount.Client.OpenRead(fullPath), cancellationToken);
        }

        /// <summary>
        /// Copies a stream into a file on the server.
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
        /// A token that cancels waiting for the task to start.
        /// </param>
        /// <returns>
        /// A task that completes when the copy is done.
        /// </returns>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the volume is not found.
        /// </exception>
        /// <exception cref="System.UnauthorizedAccessException">
        /// Thrown when the volume or the context is read-only.
        /// </exception>
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

        /// <summary>
        /// Lists a directory by running ListDirectory on a thread pool thread.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path of the directory below the mounted path.
        /// </param>
        /// <param name="cancellationToken">
        /// A token that cancels waiting for the task to start.
        /// </param>
        /// <returns>
        /// A task with the directory entries.
        /// </returns>
        public Task<DirectoryEntry[]> ListDirectoryAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            return Task.Run(() => ListDirectory(context, path), cancellationToken);
        }

        /// <summary>
        /// Gets metadata by running GetMetadata on a thread pool thread.
        /// </summary>
        /// <param name="context">
        /// The context that names the volume.
        /// </param>
        /// <param name="path">
        /// The path below the mounted path.
        /// </param>
        /// <param name="cancellationToken">
        /// A token that cancels waiting for the task to start.
        /// </param>
        /// <returns>
        /// A task with the metadata, or null.
        /// </returns>
        public Task<FileMetadata?> GetMetadataAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            return Task.Run(() => GetMetadata(context, path), cancellationToken);
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
        /// Always thrown, because SFTP has no block-level access.
        /// </exception>
        public int ReadBlock(UrnContext context, BlockAddress address, Span<byte> buffer)
        {
            throw new NotSupportedException("SFTP does not support block-level access.");
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
        /// Always thrown, because SFTP has no block-level access.
        /// </exception>
        public void WriteBlock(UrnContext context, BlockAddress address, ReadOnlySpan<byte> data)
        {
            throw new NotSupportedException("SFTP does not support block-level access.");
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
                try
                {
                    mount.Client.Connect();
                }
                catch (SshConnectionException ex) when (mount.HostKeyRefusal != null)
                {
                    // The host key policy said no. Say why, instead of SSH.NET's generic key exchange error.
                    throw new IOException("SFTP connection to " + mount.Host + ":" + mount.Port + " refused: " + mount.HostKeyRefusal, ex);
                }
            }
        }

        /// <summary>
        /// Unmounts every mounted volume. Errors while unmounting are ignored.
        /// </summary>
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
            public string? HostKeyRefusal { get; set; }
        }
    }
}
