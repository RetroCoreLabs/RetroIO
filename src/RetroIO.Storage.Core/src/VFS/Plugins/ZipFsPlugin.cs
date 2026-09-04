//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Storage.Core.VFS.Plugins
{
    /// <summary>
    /// ZIP archive plugin for VFS.
    /// Provides read-only access to ZIP, JAR, APK, and other ZIP-based formats.
    /// </summary>
    public class ZipFsPlugin : IExtFsPlugin
    {
        private ZipArchive? _archive;
        private Stream? _containerStream;
        private string? _volumeId;
        private bool _disposed;

        // IExtFsPlugin properties
        public string HandlerName => "zip";
        public string[] SupportedExtensions => new[] { ".zip", ".jar", ".apk", ".xpi", ".war", ".ear" };

        // IVolumeProvider properties
        public string Type => "archive/zip";
        public string[] SupportedSchemes => new[] { "zip" };
        public bool IsRemote => false;

        public bool CanHandle(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string extension = Path.GetExtension(path).ToLowerInvariant();
            for (int i = 0; i < SupportedExtensions.Length; i++)
            {
                if (SupportedExtensions[i] == extension)
                    return true;
            }
            return false;
        }

        public async Task<bool> CanHandleAsync(Stream stream, CancellationToken cancellationToken)
        {
            if (stream == null || !stream.CanSeek)
                return false;

            // Check ZIP magic number: PK\x03\x04
            byte[] header = new byte[4];
            int bytesRead = await stream.ReadAsync(header, 0, 4, cancellationToken);
            stream.Position = 0; // Reset

            return bytesRead == 4 && header[0] == 0x50 && header[1] == 0x4B &&
                   header[2] == 0x03 && header[3] == 0x04;
        }

        public Task MountAsync(Stream containerStream, CancellationToken cancellationToken)
        {
            if (containerStream == null)
                throw new ArgumentNullException(nameof(containerStream));

            if (_archive != null)
                throw new InvalidOperationException("Plugin already has a mounted archive. Unmount first.");

            _containerStream = containerStream;
            _archive = new ZipArchive(containerStream, ZipArchiveMode.Read, leaveOpen: false);
            _volumeId = "zip-temp";

            return Task.CompletedTask;
        }

        // IVolumeProvider mount methods
        public bool CanRestore(UnifiedMountEntry entry, out string? errorMessage)
        {
            errorMessage = "ZIP archives cannot be restored from configuration.";
            return false;
        }

        public MountInfo MountFromConfig(UnifiedMountEntry entry)
        {
            throw new NotSupportedException("ZIP archives cannot be mounted from configuration.");
        }

        public MountInfo Mount(string uri, string volumeId, bool readOnly)
        {
            if (!File.Exists(uri))
                throw new FileNotFoundException($"ZIP file not found: {uri}");

            Stream stream = File.OpenRead(uri);
            MountAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
            _volumeId = volumeId;

            return new MountInfo
            {
                VolumeId = volumeId,
                SourceUri = uri,
                Type = Type,
                IsReadOnly = true,
                FileSystemType = "ZIP Archive",
                VolumeLabel = Path.GetFileName(uri)
            };
        }

        public void Unmount(string volumeId)
        {
            if (_volumeId != volumeId)
                throw new ArgumentException($"Volume ID '{volumeId}' not mounted.");

            Dispose();
        }

        public MountInfo? GetMountInfo(string volumeId)
        {
            if (_volumeId != volumeId || _archive == null)
                return null;

            return new MountInfo
            {
                VolumeId = volumeId,
                Type = Type,
                IsReadOnly = true,
                FileSystemType = "ZIP Archive"
            };
        }

        public MountInfo[] ListMounts()
        {
            if (_archive == null || _volumeId == null)
                return Array.Empty<MountInfo>();

            return new[] { GetMountInfo(_volumeId)! };
        }

        // File operations
        public byte[] ReadFile(UrnContext context, string path)
        {
            if (_archive == null)
                throw new InvalidOperationException("No archive mounted.");

            string normalizedPath = path.TrimStart('/');
            ZipArchiveEntry? entry = _archive.GetEntry(normalizedPath);

            if (entry == null)
                throw new FileNotFoundException($"File not found in archive: {path}", path);

            using Stream entryStream = entry.Open();
            byte[] buffer = new byte[entry.Length];
            entryStream.ReadExactly(buffer);
            return buffer;
        }

        public void WriteFile(UrnContext context, string path, byte[] data)
        {
            throw new NotSupportedException("ZIP plugin is read-only.");
        }

        public void DeleteFile(UrnContext context, string path)
        {
            throw new NotSupportedException("ZIP plugin is read-only.");
        }

        public DirectoryEntry[] ListDirectory(UrnContext context, string path)
        {
            if (_archive == null)
                throw new InvalidOperationException("No archive mounted.");

            string normalizedPath = path.TrimStart('/');
            if (!string.IsNullOrEmpty(normalizedPath) && !normalizedPath.EndsWith("/"))
                normalizedPath += "/";

            List<DirectoryEntry> entries = new List<DirectoryEntry>();
            HashSet<string> addedNames = new HashSet<string>();

            for (int i = 0; i < _archive.Entries.Count; i++)
            {
                ZipArchiveEntry entry = _archive.Entries[i];
                string fullName = entry.FullName;

                // Filter by path prefix
                if (!string.IsNullOrEmpty(normalizedPath) && !fullName.StartsWith(normalizedPath))
                    continue;

                // Get relative path
                string relativePath = string.IsNullOrEmpty(normalizedPath)
                    ? fullName
                    : fullName.Substring(normalizedPath.Length);

                // Skip if this is the directory itself
                if (string.IsNullOrEmpty(relativePath))
                    continue;

                // Get first component (file or subdirectory name)
                int slashIndex = relativePath.IndexOf('/');
                string name = slashIndex >= 0 ? relativePath.Substring(0, slashIndex) : relativePath;

                // Skip if already added
                if (!addedNames.Add(name))
                    continue;

                bool isDirectory = slashIndex >= 0;

                entries.Add(new DirectoryEntry
                {
                    Name = name,
                    Metadata = new FileMetadata
                    {
                        Name = name,
                        Size = isDirectory ? 0 : entry.Length,
                        IsDirectory = isDirectory,
                        ModifiedTime = entry.LastWriteTime.DateTime
                    }
                });
            }

            return entries.ToArray();
        }

        public FileMetadata? GetMetadata(UrnContext context, string path)
        {
            if (_archive == null)
                return null;

            string normalizedPath = path.TrimStart('/');
            ZipArchiveEntry? entry = _archive.GetEntry(normalizedPath);

            if (entry == null)
                return null;

            return new FileMetadata
            {
                Name = entry.Name,
                Size = entry.Length,
                IsDirectory = entry.FullName.EndsWith("/"),
                ModifiedTime = entry.LastWriteTime.DateTime
            };
        }

        // Async methods
        public Task<Stream> OpenReadAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            if (_archive == null)
                throw new InvalidOperationException("No archive mounted.");

            string normalizedPath = path.TrimStart('/');
            ZipArchiveEntry? entry = _archive.GetEntry(normalizedPath);

            if (entry == null)
                throw new FileNotFoundException($"File not found in archive: {path}", path);

            return Task.FromResult(entry.Open());
        }

        public Task WriteAsync(UrnContext context, string path, Stream data, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("ZIP plugin is read-only.");
        }

        public Task<DirectoryEntry[]> ListDirectoryAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            return Task.FromResult(ListDirectory(context, path));
        }

        public Task<FileMetadata?> GetMetadataAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            return Task.FromResult(GetMetadata(context, path));
        }

        // Block access (not supported)
        public int ReadBlock(UrnContext context, BlockAddress address, Span<byte> buffer)
        {
            throw new NotSupportedException("ZIP archives do not support block-level access.");
        }

        public void WriteBlock(UrnContext context, BlockAddress address, ReadOnlySpan<byte> data)
        {
            throw new NotSupportedException("ZIP archives do not support block-level access.");
        }

        public BlockDeviceInfo? GetBlockDeviceInfo(UrnContext context)
        {
            return null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _archive?.Dispose();
            _containerStream?.Dispose();
            _archive = null;
            _containerStream = null;
            _volumeId = null;
            _disposed = true;
        }
    }
}
