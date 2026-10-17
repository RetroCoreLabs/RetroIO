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
using RetroIO.Protocols.Tnfs.Client;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Providers
{
    /// <summary>
    /// TNFS volume provider implementing IVolumeProvider.
    /// Provides access to TNFS servers (Trivial Network File System for retro computers).
    /// </summary>
    /// <remarks>
    /// TNFS is a lightweight network filesystem protocol designed for 8-bit computers.
    /// It uses UDP for transport and provides basic file operations.
    ///
    /// Key Features:
    /// - UDP-based protocol optimized for low-overhead operation
    /// - Simple request/response pattern
    /// - Direct file access (no filesystem driver layer needed)
    /// - Read-only access typical for public servers (write support varies)
    ///
    /// Usage:
    ///   var provider = new TnfsVolumeProvider();
    ///   var mount = provider.Mount("fujinet.online", "fujinet-public", readOnly: true);
    ///   var files = provider.ListDirectory(context, "/ATARI");
    ///   var data = provider.ReadFile(context, "/ATARI/GAMES/PITFALL.ATR");
    /// </remarks>
    public sealed class TnfsVolumeProvider : IVolumeProvider
    {
        private readonly Dictionary<string, TnfsMount> _mounts;
        private bool _disposed;

        /// <summary>
        /// Initializes a new TNFS volume provider.
        /// </summary>
        public TnfsVolumeProvider()
        {
            _mounts = new Dictionary<string, TnfsMount>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Volume type identifier for TNFS.
        /// </summary>
        public string Type => "tnfs";

        // VFS Properties
        public string[] SupportedSchemes => new[] { "tnfs" };
        public bool IsRemote => true;

        /// <summary>
        /// Validates whether a mount entry can be restored by this provider.
        /// TNFS is a network resource, so no local file validation is needed.
        /// </summary>
        /// <param name="entry">Mount configuration entry to validate</param>
        /// <param name="errorMessage">Error message if validation fails, or null if successful</param>
        /// <returns>True if mount can be restored, false otherwise</returns>
        public bool CanRestore(UnifiedMountEntry entry, out string? errorMessage)
        {
            errorMessage = null;
            return true;
        }

        /// <summary>
        /// Mounts a volume from a unified configuration entry.
        /// </summary>
        /// <param name="entry">Mount configuration entry containing all parameters</param>
        /// <returns>Information about the mounted volume</returns>
        public MountInfo MountFromConfig(UnifiedMountEntry entry)
        {
            string uri = entry.SourceUri;
            if (uri.StartsWith("tnfs://", StringComparison.OrdinalIgnoreCase))
            {
                uri = uri.Substring(7);
            }
            if (uri.EndsWith("/"))
            {
                uri = uri.Substring(0, uri.Length - 1);
            }

            ParseTnfsUri(uri, entry.Username, entry.Password, out string server, out int port, out string? username, out string? password);
            return Mount(server, port, entry.VolumeId, entry.IsReadOnly, username, password);
        }

        /// <summary>
        /// Mounts a TNFS server filesystem.
        /// </summary>
        /// <param name="uri">TNFS server address (hostname or hostname:port, optional user:pass@)</param>
        /// <param name="volumeId">Unique volume identifier</param>
        /// <param name="readOnly">Read-only flag (most public TNFS servers are read-only)</param>
        /// <returns>Mount information</returns>
        public MountInfo Mount(string uri, string volumeId, bool readOnly)
        {
            ParseTnfsUri(uri, null, null, out string server, out int port, out string? username, out string? password);
            return Mount(server, port, volumeId, readOnly, username, password);
        }

        /// <summary>
        /// Mounts a TNFS server with optional authentication credentials.
        /// </summary>
        public MountInfo Mount(
            string server,
            int port,
            string volumeId,
            bool readOnly,
            string? username,
            string? password)
        {
            if (string.IsNullOrEmpty(server))
            {
                throw new ArgumentException("Server cannot be null or empty.", nameof(server));
            }

            if (string.IsNullOrEmpty(volumeId))
            {
                throw new ArgumentException("Volume ID cannot be null or empty.", nameof(volumeId));
            }

            if (_mounts.ContainsKey(volumeId))
            {
                throw new ArgumentException($"Volume ID '{volumeId}' is already mounted.");
            }

            // Create TNFS client and mount
            TnfsClient client = new TnfsClient(server, port);
            TnfsResult<bool> mountResult = client.Mount("/", username, password);

            if (!mountResult.Success)
            {
                client.Dispose();
                throw new IOException($"Failed to mount TNFS server '{server}': {mountResult.ErrorMessage}");
            }

            // Create mount entry
            TnfsMount mount = new TnfsMount
            {
                VolumeId = volumeId,
                Server = server,
                Port = port,
                Client = client,
                ReadOnly = readOnly,
                MountedAt = DateTime.UtcNow
            };

            _mounts[volumeId] = mount;

            // Return mount info
            return new MountInfo
            {
                VolumeId = volumeId,
                FileSystemType = "TNFS",
                VolumeLabel = server,
                IsReadOnly = readOnly,
                HasBlockAccess = false, // TNFS is file-based
                SourceUri = $"tnfs://{server}:{port}/",
                Type = Type,
                TotalSize = null, // TNFS doesn't always provide this
                FreeSpace = null
            };
        }

        /// <summary>
        /// Unmounts a TNFS volume.
        /// </summary>
        /// <param name="volumeId">Volume identifier</param>
        public void Unmount(string volumeId)
        {
            if (!_mounts.TryGetValue(volumeId, out TnfsMount? mount))
            {
                throw new ArgumentException($"Volume ID '{volumeId}' is not mounted.");
            }

            mount.Client.Unmount();
            mount.Client.Dispose();
            _mounts.Remove(volumeId);
        }

        /// <summary>
        /// Gets mount information for a volume.
        /// </summary>
        /// <param name="volumeId">Volume identifier</param>
        /// <returns>Mount information or null if not found</returns>
        public MountInfo? GetMountInfo(string volumeId)
        {
            if (!_mounts.TryGetValue(volumeId, out TnfsMount? mount))
            {
                return null;
            }

            return new MountInfo
            {
                VolumeId = mount.VolumeId,
                FileSystemType = "TNFS",
                VolumeLabel = mount.Server,
                IsReadOnly = mount.ReadOnly,
                HasBlockAccess = false,
                SourceUri = $"tnfs://{mount.Server}:{mount.Port}/",
                Type = Type,
                TotalSize = null,
                FreeSpace = null
            };
        }

        /// <summary>
        /// Lists all mounted TNFS volumes.
        /// </summary>
        /// <returns>Array of mount information</returns>
        public MountInfo[] ListMounts()
        {
            MountInfo[] mounts = new MountInfo[_mounts.Count];
            int index = 0;

            Dictionary<string, TnfsMount>.Enumerator enumerator = _mounts.GetEnumerator();
            while (enumerator.MoveNext())
            {
                TnfsMount mount = enumerator.Current.Value;
                mounts[index++] = new MountInfo
                {
                    VolumeId = mount.VolumeId,
                    FileSystemType = "TNFS",
                    VolumeLabel = mount.Server,
                    IsReadOnly = mount.ReadOnly,
                    HasBlockAccess = false,
                    SourceUri = $"tnfs://{mount.Server}:{mount.Port}/",
                    Type = Type,
                    TotalSize = null,
                    FreeSpace = null
                };
            }
            enumerator.Dispose();

            return mounts;
        }

        /// <summary>
        /// Reads entire file from TNFS server.
        /// </summary>
        /// <param name="context">URN context</param>
        /// <param name="path">File path on TNFS server</param>
        /// <returns>File contents</returns>
        public byte[] ReadFile(UrnContext context, string path)
        {
            TnfsMount mount = GetMount(context.VolumeId);

            // Open file
            TnfsResult<ushort> openResult = mount.Client.Open(path, TnfsOpenFlags.O_RDONLY);
            if (!openResult.Success)
            {
                if (openResult.ReturnCode == TnfsReturnCode.ENOENT)
                {
                    throw new FileNotFoundException($"File not found: {path}", path);
                }
                throw new IOException($"Failed to open file '{path}': {openResult.ErrorMessage}");
            }

            try
            {
                // Read file in chunks
                List<byte> fileData = new List<byte>();
                while (true)
                {
                    TnfsResult<byte[]> readResult = mount.Client.Read(openResult.Data, (ushort)TnfsConstants.DefaultReadSize);
                    if (!readResult.Success)
                    {
                        if (readResult.ReturnCode == TnfsReturnCode.EOF)
                        {
                            break;
                        }
                        throw new IOException($"Failed to read file '{path}': {readResult.ErrorMessage}");
                    }

                    if (readResult.Data == null || readResult.Data.Length == 0)
                    {
                        break;
                    }

                    fileData.AddRange(readResult.Data);

                    if (readResult.Data.Length < TnfsConstants.DefaultReadSize)
                    {
                        break; // End of file
                    }
                }

                return fileData.ToArray();
            }
            finally
            {
                mount.Client.Close(openResult.Data);
            }
        }

        /// <summary>
        /// Writes file to TNFS server.
        /// Note: Many public TNFS servers are read-only.
        /// </summary>
        /// <param name="context">URN context</param>
        /// <param name="path">File path</param>
        /// <param name="data">Data to write</param>
        public void WriteFile(UrnContext context, string path, byte[] data)
        {
            using MemoryStream ms = new MemoryStream(data ?? Array.Empty<byte>());
            WriteStream(context, path, ms);
        }

        /// <summary>
        /// Deletes file from TNFS server.
        /// </summary>
        /// <param name="context">URN context</param>
        /// <param name="path">File path</param>
        public void DeleteFile(UrnContext context, string path)
        {
            TnfsMount mount = GetMount(context.VolumeId);

            if (mount.ReadOnly || context.ReadOnly)
            {
                throw new UnauthorizedAccessException("Volume is mounted read-only.");
            }

            throw new NotSupportedException("TNFS write operations not supported on most public servers.");
        }

        /// <summary>
        /// Lists directory contents on TNFS server.
        /// </summary>
        /// <param name="context">URN context</param>
        /// <param name="path">Directory path</param>
        /// <returns>Array of directory entries</returns>
        public DirectoryEntry[] ListDirectory(UrnContext context, string path)
        {
            TnfsMount mount = GetMount(context.VolumeId);

            // Open directory
            TnfsResult<byte> openResult = mount.Client.OpenDir(path);
            if (!openResult.Success)
            {
                if (openResult.ReturnCode == TnfsReturnCode.ENOENT)
                {
                    throw new DirectoryNotFoundException($"Directory not found: {path}");
                }
                throw new IOException($"Failed to open directory '{path}': {openResult.ErrorMessage}");
            }

            byte dirHandle = openResult.Data;
            try
            {
                List<DirectoryEntry> entries = new List<DirectoryEntry>();

                Console.WriteLine($"[TNFS DEBUG] Opening directory: {path}, handle={dirHandle}");
                int readCount = 0;

                while (true)
                {
                    TnfsResult<TnfsDirectoryEntry?> readResult = mount.Client.ReadDir(dirHandle);
                    readCount++;

                    Console.WriteLine($"[TNFS DEBUG] ReadDir #{readCount}: Success={readResult.Success}, ReturnCode={readResult.ReturnCode}, Data={readResult.Data?.Name ?? "null"}");

                    if (!readResult.Success)
                    {
                        Console.WriteLine($"[TNFS DEBUG] ReadDir failed: {readResult.ErrorMessage}");
                        break;
                    }

                    if (readResult.Data == null)
                    {
                        Console.WriteLine($"[TNFS DEBUG] ReadDir returned null (EOF)");
                        break;
                    }

                    TnfsDirectoryEntry tnfsEntry = readResult.Data;
                    Console.WriteLine($"[TNFS DEBUG] Entry: name='{tnfsEntry.Name}', size={tnfsEntry.Size}, isDir={tnfsEntry.IsDirectory}");

                    // BUGFIX: Ensure "." and ".." are always marked as directories
                    // Some TNFS servers return these entries without proper IsDirectory flag
                    bool isDirectory = tnfsEntry.IsDirectory;
                    if (tnfsEntry.Name == "." || tnfsEntry.Name == "..")
                    {
                        isDirectory = true;
                    }

                    DirectoryEntry entry = new DirectoryEntry
                    {
                        Name = tnfsEntry.Name,
                        Metadata = new FileMetadata
                        {
                            Name = tnfsEntry.Name,
                            Size = tnfsEntry.Size,
                            IsDirectory = isDirectory,
                            CreatedTime = tnfsEntry.GetModifiedDateTime(),
                            ModifiedTime = tnfsEntry.GetModifiedDateTime(),
                            AccessedTime = tnfsEntry.GetModifiedDateTime(),
                            Attributes = tnfsEntry.IsHidden ? "H" : null
                        }
                    };

                    entries.Add(entry);
                }

                Console.WriteLine($"[TNFS DEBUG] Total entries read: {entries.Count}");
                return entries.ToArray();
            }
            finally
            {
                mount.Client.CloseDir(dirHandle);
            }
        }

        /// <summary>
        /// Gets file/directory metadata.
        /// </summary>
        /// <param name="context">URN context</param>
        /// <param name="path">Path to file or directory</param>
        /// <returns>File metadata or null if not found</returns>
        public FileMetadata? GetMetadata(UrnContext context, string path)
        {
            TnfsMount mount = GetMount(context.VolumeId);

            TnfsResult<TnfsFileStat> statResult = mount.Client.Stat(path);
            if (!statResult.Success)
            {
                if (statResult.ReturnCode == TnfsReturnCode.ENOENT)
                {
                    return null;
                }
                throw new IOException($"Failed to get status for '{path}': {statResult.ErrorMessage}");
            }

            TnfsFileStat stat = statResult.Data!;

            FileMetadata metadata = new FileMetadata
            {
                Name = Path.GetFileName(path),
                Path = path,
                Size = stat.Size,
                IsDirectory = stat.IsDirectory,
                CreatedTime = DateTimeOffset.FromUnixTimeSeconds(stat.CreationTime).DateTime,
                ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(stat.ModifiedTime).DateTime,
                AccessedTime = DateTimeOffset.FromUnixTimeSeconds(stat.AccessTime).DateTime
            };

            // Store read-only status in properties
            if (mount.ReadOnly)
            {
                metadata.SetProperty("ReadOnly", true);
            }

            return metadata;
        }

        /// <summary>
        /// TNFS does not support block-level access.
        /// </summary>
        public int ReadBlock(UrnContext context, BlockAddress address, Span<byte> buffer)
        {
            throw new NotSupportedException("TNFS does not support block-level access. Use file-level operations.");
        }

        /// <summary>
        /// TNFS does not support block-level access.
        /// </summary>
        public void WriteBlock(UrnContext context, BlockAddress address, ReadOnlySpan<byte> data)
        {
            throw new NotSupportedException("TNFS does not support block-level access. Use file-level operations.");
        }

        /// <summary>
        /// TNFS does not provide block device information.
        /// </summary>
        public BlockDeviceInfo? GetBlockDeviceInfo(UrnContext context)
        {
            return null; // TNFS is file-based, not block-based
        }

        // ==================== VFS Async Methods ====================

        public async Task<Stream> OpenReadAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            TnfsMount mount = GetMount(context.VolumeId);

            // Open file via TNFS
            TnfsResult<ushort> openResult = mount.Client.Open(path, TnfsOpenFlags.O_RDONLY);
            if (!openResult.Success)
            {
                if (openResult.ReturnCode == TnfsReturnCode.ENOENT)
                    throw new FileNotFoundException($"File not found: {path}", path);
                throw new IOException($"Failed to open file '{path}': {openResult.ErrorMessage}");
            }

            // Create stream wrapper for TNFS file handle
            return await Task.FromResult<Stream>(new TnfsReadStream(mount.Client, openResult.Data));
        }

        public Task WriteAsync(UrnContext context, string path, Stream data, CancellationToken cancellationToken)
        {
            // The TNFS client is synchronous; wrap the write on a worker (same pattern as
            // ListDirectoryAsync / GetMetadataAsync). Read-only enforcement lives in WriteStream.
            return Task.Run(() => WriteStream(context, path, data), cancellationToken);
        }

        /// <summary>
        /// Streams <paramref name="data"/> to <paramref name="path"/> on the TNFS server: opens the file
        /// for writing (create + truncate) and pushes it in <see cref="TnfsConstants.MaxIOSize"/>-byte
        /// chunks — a single TNFS WRITE cannot exceed the server's per-op limit. Shared by
        /// <see cref="WriteFile"/> and <see cref="WriteAsync"/>.
        /// </summary>
        private void WriteStream(UrnContext context, string path, Stream data)
        {
            TnfsMount mount = GetMount(context.VolumeId);

            if (mount.ReadOnly || context.ReadOnly)
                throw new UnauthorizedAccessException("Volume is mounted read-only.");

            // O_WRONLY|O_CREAT|O_TRUNC -> the server opens FileAccess.Write + FileMode.Create
            // (create-or-truncate). Flag values from TnfsEnums; verified against the server's
            // ConvertToFileAccess/ConvertToFileMode (flags & 0x03 == 1 -> Write; 0x0100|0x0800 -> Create).
            TnfsOpenFlags flags = TnfsOpenFlags.O_WRONLY | TnfsOpenFlags.O_CREAT | TnfsOpenFlags.O_TRUNC;
            TnfsResult<ushort> openResult = mount.Client.Open(path, flags);
            if (!openResult.Success)
                throw new IOException($"Failed to open '{path}' for writing: {openResult.ErrorMessage}");

            ushort handle = openResult.Data;
            try
            {
                // Reuse one buffer for the full-size chunks; only the final short chunk allocates.
                byte[] buffer = new byte[TnfsConstants.MaxIOSize];
                while (true)
                {
                    int read = data.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;

                    byte[] chunk = read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();
                    TnfsResult<ushort> writeResult = mount.Client.Write(handle, chunk);
                    if (!writeResult.Success)
                        throw new IOException($"TNFS write failed for '{path}': {writeResult.ErrorMessage}");
                }
            }
            finally
            {
                mount.Client.Close(handle);
            }
        }

        public Task<DirectoryEntry[]> ListDirectoryAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            // Wrap synchronous method (TNFS client is sync-based)
            return Task.Run(() => ListDirectory(context, path), cancellationToken);
        }

        public Task<FileMetadata?> GetMetadataAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            // Wrap synchronous method
            return Task.Run(() => GetMetadata(context, path), cancellationToken);
        }

        // Helper methods

        private TnfsMount GetMount(string volumeId)
        {
            if (!_mounts.TryGetValue(volumeId, out TnfsMount? mount))
            {
                throw new ArgumentException($"Volume ID '{volumeId}' is not mounted.");
            }
            return mount;
        }

        private static void ParseTnfsUri(
            string uri,
            string? configUsername,
            string? configPassword,
            out string server,
            out int port,
            out string? username,
            out string? password)
        {
            if (string.IsNullOrEmpty(uri))
                throw new ArgumentException("URI cannot be null or empty.", nameof(uri));

            string working = uri;
            username = configUsername;
            password = configPassword;

            // Accept an optional "tnfs://" scheme so the natural URI form (tnfs://host:port/) works, not
            // just the bare "host[:port]". Stripped before the '@' auth split so credentials are still
            // parsed. The trailing "/path" is removed later (after the host is isolated) — the TNFS mount
            // is always the server root, so any path component is advisory.
            if (working.StartsWith("tnfs://", StringComparison.OrdinalIgnoreCase))
                working = working.Substring("tnfs://".Length);

            if (working.Contains('@'))
            {
                string[] authParts = working.Split('@', 2);
                string userInfo = authParts[0];
                working = authParts[1];

                if (userInfo.Contains(':'))
                {
                    string[] credentials = userInfo.Split(':', 2);
                    username = credentials[0];
                    password = credentials[1];
                }
                else
                {
                    username = userInfo;
                }
            }

            // Drop any trailing "/path" now that credentials are handled — leaves "host[:port]".
            int pathSlash = working.IndexOf('/');
            if (pathSlash >= 0)
                working = working.Substring(0, pathSlash);

            port = TnfsConstants.DEFAULT_PORT;
            if (working.Contains(':'))
            {
                string[] parts = working.Split(':');
                server = parts[0];
                if (parts.Length > 1 && int.TryParse(parts[1], out int parsedPort))
                    port = parsedPort;
            }
            else
            {
                server = working;
            }
        }

        /// <summary>
        /// Dispose resources and unmount all volumes.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            // Unmount all volumes
            string[] volumeIds = new string[_mounts.Count];
            int index = 0;
            Dictionary<string, TnfsMount>.Enumerator enumerator = _mounts.GetEnumerator();
            while (enumerator.MoveNext())
            {
                volumeIds[index++] = enumerator.Current.Key;
            }
            enumerator.Dispose();

            for (int i = 0; i < volumeIds.Length; i++)
            {
                Unmount(volumeIds[i]);
            }

            _disposed = true;
        }

        /// <summary>
        /// Internal mount tracking structure.
        /// </summary>
        private sealed class TnfsMount
        {
            public string VolumeId { get; set; } = string.Empty;
            public string Server { get; set; } = string.Empty;
            public int Port { get; set; }
            public TnfsClient Client { get; set; } = null!;
            public bool ReadOnly { get; set; }
            public DateTime MountedAt { get; set; }
        }

        /// <summary>
        /// Stream wrapper for TNFS file handle.
        /// Provides Stream interface for reading TNFS files.
        /// </summary>
        private sealed class TnfsReadStream : Stream
        {
            private readonly TnfsClient _client;
            private readonly ushort _fileHandle;
            private long _position;
            private bool _disposed;

            public TnfsReadStream(TnfsClient client, ushort fileHandle)
            {
                _client = client;
                _fileHandle = fileHandle;
                _position = 0;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException("TNFS does not support getting file length from handle.");
            public override long Position
            {
                get => _position;
                set => Seek(value, SeekOrigin.Begin);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(TnfsReadStream));

                if (count <= 0)
                    return 0;

                // TNFS caps a single READ at MaxIOSize (512 bytes). A caller may legitimately pass a much
                // larger count (e.g. Stream.CopyTo uses an 81920-byte buffer), which both overflows the
                // 16-bit request field and exceeds the server's per-op limit — the server then returns
                // nothing and the copy reads 0 bytes. Clamp the per-request size; the Stream contract lets
                // the caller loop, so returning a short count here is correct, not truncation.
                int requestCount = Math.Min(count, TnfsConstants.MaxIOSize);
                TnfsResult<byte[]> result = _client.Read(_fileHandle, (ushort)requestCount);
                if (!result.Success)
                {
                    if (result.ReturnCode == TnfsReturnCode.EOF)
                        return 0;
                    throw new IOException($"TNFS read failed: {result.ErrorMessage}");
                }

                if (result.Data == null || result.Data.Length == 0)
                    return 0;

                int bytesToCopy = Math.Min(result.Data.Length, count);
                Array.Copy(result.Data, 0, buffer, offset, bytesToCopy);
                _position += bytesToCopy;
                return bytesToCopy;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(TnfsReadStream));

                byte whence = origin switch
                {
                    SeekOrigin.Begin => 0,
                    SeekOrigin.Current => 1,
                    SeekOrigin.End => 2,
                    _ => throw new ArgumentException("Invalid seek origin", nameof(origin))
                };

                TnfsResult<bool> result = _client.Lseek(_fileHandle, (int)offset, whence);
                if (!result.Success)
                    throw new IOException($"TNFS lseek failed: {result.ErrorMessage}");

                // Update local position based on seek operation
                _position = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => _position + offset,
                    SeekOrigin.End => throw new NotSupportedException("TNFS does not support seeking from end without file size"),
                    _ => throw new ArgumentException("Invalid seek origin", nameof(origin))
                };

                return _position;
            }

            public override void Flush()
            {
                // No-op for read-only stream
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException("TNFS read stream does not support setting length.");
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException("TNFS read stream is read-only.");
            }

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    try
                    {
                        _client.Close(_fileHandle);
                    }
                    catch
                    {
                        // Ignore close errors during dispose
                    }
                    _disposed = true;
                }
                base.Dispose(disposing);
            }
        }
    }
}
