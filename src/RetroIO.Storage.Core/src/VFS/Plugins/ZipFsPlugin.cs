//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
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
    /// <remarks>
    /// One instance holds at most one archive at a time. Every write and block operation throws
    /// <see cref="NotSupportedException"/>.
    /// </remarks>
    public class ZipFsPlugin : IExtFsPlugin
    {
        private ZipArchive? _archive;
        private Stream? _containerStream;
        private string? _volumeId;
        private bool _disposed;

        // IExtFsPlugin properties

        /// <summary>
        /// Always <c>zip</c>.
        /// </summary>
        public string HandlerName => "zip";

        /// <summary>
        /// The file extensions this plugin claims: <c>.zip</c>, <c>.jar</c>, <c>.apk</c>, <c>.xpi</c>, <c>.war</c> and <c>.ear</c>.
        /// </summary>
        public string[] SupportedExtensions => new[] { ".zip", ".jar", ".apk", ".xpi", ".war", ".ear" };

        // IVolumeProvider properties

        /// <summary>
        /// Always <c>archive/zip</c>.
        /// </summary>
        public string Type => "archive/zip";

        /// <summary>
        /// Always the single scheme <c>zip</c>.
        /// </summary>
        public string[] SupportedSchemes => new[] { "zip" };

        /// <summary>
        /// Always false: the archive is a local file or stream.
        /// </summary>
        public bool IsRemote => false;

        /// <summary>
        /// Checks whether a path has one of the <see cref="SupportedExtensions"/>.
        /// </summary>
        /// <param name="path">
        /// The file path to test. Only its extension is looked at, without regard to case.
        /// </param>
        /// <returns>
        /// True when the extension matches; false for any other extension, or when <paramref name="path"/> is null or blank.
        /// </returns>
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

        /// <summary>
        /// Checks whether a stream starts with the ZIP local-file signature <c>PK 03 04</c>.
        /// </summary>
        /// <remarks>
        /// Reads the first four bytes and then sets the stream position back to 0, whatever it was before.
        /// </remarks>
        /// <param name="stream">
        /// The stream to test. Must be seekable.
        /// </param>
        /// <param name="cancellationToken">
        /// Cancels the read.
        /// </param>
        /// <returns>
        /// True when the signature is present; false when it is not, or when <paramref name="stream"/> is null.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="stream"/> cannot seek, as <see cref="IExtFsPlugin.CanHandleAsync"/> promises.
        /// </exception>
        public async Task<bool> CanHandleAsync(Stream stream, CancellationToken cancellationToken)
        {
            if (stream == null)
                return false;

            // The interface contract (and VfsPluginRegistry.DetectPluginAsync, which checks first)
            // treats a stream that cannot seek as a caller error, not as "not a ZIP".
            if (!stream.CanSeek)
                throw new ArgumentException("Stream must be seekable for ZIP detection.", nameof(stream));

            // Check ZIP magic number: PK\x03\x04
            byte[] header = new byte[4];
            int bytesRead = await stream.ReadAsync(header, 0, 4, cancellationToken);
            stream.Position = 0; // Reset

            return bytesRead == 4 && header[0] == 0x50 && header[1] == 0x4B &&
                   header[2] == 0x03 && header[3] == 0x04;
        }

        /// <summary>
        /// Opens a ZIP archive from a stream, read-only.
        /// </summary>
        /// <remarks>
        /// The plugin takes ownership of <paramref name="containerStream"/> and disposes it on
        /// <see cref="Dispose"/>. The volume id is set to <c>zip-temp</c>.
        /// </remarks>
        /// <param name="containerStream">
        /// The stream holding the archive.
        /// </param>
        /// <param name="cancellationToken">
        /// Not used: the archive is opened synchronously.
        /// </param>
        /// <returns>
        /// A task that is already complete.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="containerStream"/> is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when an archive is already mounted.
        /// </exception>
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

        /// <summary>
        /// Always refuses: ZIP archives are not restored from saved mount configuration.
        /// </summary>
        /// <param name="entry">
        /// The saved mount entry. Ignored.
        /// </param>
        /// <param name="errorMessage">
        /// Always set to the reason it cannot be restored.
        /// </param>
        /// <returns>
        /// Always false.
        /// </returns>
        public bool CanRestore(UnifiedMountEntry entry, out string? errorMessage)
        {
            errorMessage = "ZIP archives cannot be restored from configuration.";
            return false;
        }

        /// <summary>
        /// Not supported for ZIP archives.
        /// </summary>
        /// <param name="entry">
        /// The saved mount entry. Ignored.
        /// </param>
        /// <returns>
        /// Never returns.
        /// </returns>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public MountInfo MountFromConfig(UnifiedMountEntry entry)
        {
            throw new NotSupportedException("ZIP archives cannot be mounted from configuration.");
        }

        /// <summary>
        /// Opens a ZIP file from disk and mounts it under <paramref name="volumeId"/>.
        /// </summary>
        /// <param name="uri">
        /// The file system path of the ZIP file.
        /// </param>
        /// <param name="volumeId">
        /// The volume id to mount it as.
        /// </param>
        /// <param name="readOnly">
        /// Ignored: the mount is always read-only.
        /// </param>
        /// <returns>
        /// The mount description, with <see cref="MountInfo.IsReadOnly"/> set and the file name as the volume label.
        /// </returns>
        /// <exception cref="FileNotFoundException">
        /// Thrown when no file exists at <paramref name="uri"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when an archive is already mounted.
        /// </exception>
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

        /// <summary>
        /// Unmounts the archive and closes it and its stream. The plugin can then mount another archive.
        /// </summary>
        /// <param name="volumeId">
        /// The volume id the archive is mounted as.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="volumeId"/> is not the mounted volume.
        /// </exception>
        public void Unmount(string volumeId)
        {
            if (_volumeId != volumeId)
                throw new ArgumentException($"Volume ID '{volumeId}' not mounted.");

            // Release the archive but do NOT latch _disposed: an unmounted plugin is empty and
            // reusable, and a later Dispose must still close whatever is mounted by then.
            CloseArchive();
        }

        /// <summary>
        /// Describes the mounted archive.
        /// </summary>
        /// <param name="volumeId">
        /// The volume id to look up.
        /// </param>
        /// <returns>
        /// The mount description, or null when <paramref name="volumeId"/> is not the mounted volume or nothing is mounted.
        /// </returns>
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

        /// <summary>
        /// Lists the mounted archive.
        /// </summary>
        /// <returns>
        /// One entry when an archive is mounted; otherwise an empty array.
        /// </returns>
        public MountInfo[] ListMounts()
        {
            if (_archive == null || _volumeId == null)
                return Array.Empty<MountInfo>();

            return new[] { GetMountInfo(_volumeId)! };
        }

        // File operations

        /// <summary>
        /// Reads one file out of the archive, uncompressed.
        /// </summary>
        /// <param name="context">
        /// The URN context. Not used.
        /// </param>
        /// <param name="path">
        /// The path of the entry inside the archive. A leading <c>/</c> is ignored.
        /// </param>
        /// <returns>
        /// The whole content of the entry.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no archive is mounted.
        /// </exception>
        /// <exception cref="FileNotFoundException">
        /// Thrown when the archive has no entry at <paramref name="path"/>.
        /// </exception>
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

        /// <summary>
        /// Not supported: the plugin is read-only.
        /// </summary>
        /// <param name="context">
        /// Ignored.
        /// </param>
        /// <param name="path">
        /// Ignored.
        /// </param>
        /// <param name="data">
        /// Ignored.
        /// </param>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public void WriteFile(UrnContext context, string path, byte[] data)
        {
            throw new NotSupportedException("ZIP plugin is read-only.");
        }

        /// <summary>
        /// Not supported: the plugin is read-only.
        /// </summary>
        /// <param name="context">
        /// Ignored.
        /// </param>
        /// <param name="path">
        /// Ignored.
        /// </param>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public void DeleteFile(UrnContext context, string path)
        {
            throw new NotSupportedException("ZIP plugin is read-only.");
        }

        /// <summary>
        /// Lists the files and subdirectories directly under a directory of the archive.
        /// </summary>
        /// <remarks>
        /// A subdirectory is listed once, even when only the files inside it have entries, and gets size 0.
        /// </remarks>
        /// <param name="context">
        /// The URN context. Not used.
        /// </param>
        /// <param name="path">
        /// The directory inside the archive. Empty or <c>/</c> lists the top level.
        /// </param>
        /// <returns>
        /// The entries in archive order. Empty when the directory does not exist.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no archive is mounted.
        /// </exception>
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

        /// <summary>
        /// Describes one entry of the archive.
        /// </summary>
        /// <param name="context">
        /// The URN context. Not used.
        /// </param>
        /// <param name="path">
        /// The path of the entry inside the archive. A leading <c>/</c> is ignored.
        /// </param>
        /// <returns>
        /// The entry's name, uncompressed size, directory flag and last-write time; or null when nothing is mounted or the entry does not exist.
        /// </returns>
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

        /// <summary>
        /// Opens one entry of the archive for reading.
        /// </summary>
        /// <param name="context">
        /// The URN context. Not used.
        /// </param>
        /// <param name="path">
        /// The path of the entry inside the archive. A leading <c>/</c> is ignored.
        /// </param>
        /// <param name="cancellationToken">
        /// Not used: the entry is opened synchronously.
        /// </param>
        /// <returns>
        /// A stream that decompresses the entry. The caller disposes it.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no archive is mounted.
        /// </exception>
        /// <exception cref="FileNotFoundException">
        /// Thrown when the archive has no entry at <paramref name="path"/>.
        /// </exception>
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

        /// <summary>
        /// Not supported: the plugin is read-only.
        /// </summary>
        /// <param name="context">
        /// Ignored.
        /// </param>
        /// <param name="path">
        /// Ignored.
        /// </param>
        /// <param name="data">
        /// Ignored.
        /// </param>
        /// <param name="cancellationToken">
        /// Ignored.
        /// </param>
        /// <returns>
        /// Never returns.
        /// </returns>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public Task WriteAsync(UrnContext context, string path, Stream data, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("ZIP plugin is read-only.");
        }

        /// <summary>
        /// Same as <see cref="ListDirectory"/>, run synchronously and wrapped in a completed task.
        /// </summary>
        /// <param name="context">
        /// The URN context. Not used.
        /// </param>
        /// <param name="path">
        /// The directory inside the archive.
        /// </param>
        /// <param name="cancellationToken">
        /// Not used.
        /// </param>
        /// <returns>
        /// The entries directly under <paramref name="path"/>.
        /// </returns>
        public Task<DirectoryEntry[]> ListDirectoryAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            return Task.FromResult(ListDirectory(context, path));
        }

        /// <summary>
        /// Same as <see cref="GetMetadata"/>, run synchronously and wrapped in a completed task.
        /// </summary>
        /// <param name="context">
        /// The URN context. Not used.
        /// </param>
        /// <param name="path">
        /// The path of the entry inside the archive.
        /// </param>
        /// <param name="cancellationToken">
        /// Not used.
        /// </param>
        /// <returns>
        /// The entry's metadata, or null.
        /// </returns>
        public Task<FileMetadata?> GetMetadataAsync(UrnContext context, string path, CancellationToken cancellationToken)
        {
            return Task.FromResult(GetMetadata(context, path));
        }

        // Block access (not supported)

        /// <summary>
        /// Not supported: a ZIP archive has no blocks.
        /// </summary>
        /// <param name="context">
        /// Ignored.
        /// </param>
        /// <param name="address">
        /// Ignored.
        /// </param>
        /// <param name="buffer">
        /// Ignored.
        /// </param>
        /// <returns>
        /// Never returns.
        /// </returns>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public int ReadBlock(UrnContext context, BlockAddress address, Span<byte> buffer)
        {
            throw new NotSupportedException("ZIP archives do not support block-level access.");
        }

        /// <summary>
        /// Not supported: a ZIP archive has no blocks.
        /// </summary>
        /// <param name="context">
        /// Ignored.
        /// </param>
        /// <param name="address">
        /// Ignored.
        /// </param>
        /// <param name="data">
        /// Ignored.
        /// </param>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public void WriteBlock(UrnContext context, BlockAddress address, ReadOnlySpan<byte> data)
        {
            throw new NotSupportedException("ZIP archives do not support block-level access.");
        }

        /// <summary>
        /// Always returns null: a ZIP archive has no blocks.
        /// </summary>
        /// <param name="context">
        /// Ignored.
        /// </param>
        /// <returns>
        /// Always null.
        /// </returns>
        public BlockDeviceInfo? GetBlockDeviceInfo(UrnContext context)
        {
            return null;
        }

        /// <summary>
        /// Closes the archive and the stream it was read from, and forgets the volume id.
        /// </summary>
        /// <remarks>
        /// Only the first call does anything.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed)
                return;

            CloseArchive();
            _disposed = true;
        }

        // Closes the mounted archive and the stream it reads from, and empties the plugin.
        private void CloseArchive()
        {
            _archive?.Dispose();
            _containerStream?.Dispose();
            _archive = null;
            _containerStream = null;
            _volumeId = null;
        }
    }
}
