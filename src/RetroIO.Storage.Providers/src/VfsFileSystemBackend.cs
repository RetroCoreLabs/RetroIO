//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Storage.Core;
using RetroIO.Storage.Core.VFS;

namespace RetroIO.Storage.Providers;

/// <summary>
/// An <see cref="IFileSystemBackend"/> that serves a single VFS mount (native / azureblob / ftp / sftp /
/// tnfs / disk-image) so the Econet file server can expose it as a disc.
/// </summary>
/// <remarks>
/// <para><b>Step 1 — read-only skeleton</b></para>
/// <para>
/// This first cut implements the read surface only. It maps the Econet backend's host-style paths to
/// VFS URNs of the form <c>urn:retrofs:transport:volumeId:subPath</c> and delegates to
/// <see cref="VfsCore"/>. Following the established Azure precedent, files are materialized to a temp
/// cache (there is no true streaming) and, per the reviewed design, the write path
/// (<see cref="OpenFile"/> for write, <see cref="CreateDirectory"/>) is not implemented yet and
/// throws — those land in steps 2–3 (write-back-on-close stream, and <c>VfsCore.CreateDirectoryAsync</c>).
/// </para>
/// <para>
/// The async VFS calls are bridged to this synchronous interface with <c>.GetAwaiter().GetResult()</c>,
/// as the codebase already does elsewhere (Econet handlers run on plain worker threads).
/// </para>
/// </remarks>
public sealed class VfsFileSystemBackend : IFileSystemBackend
{
    private readonly VfsCore _vfs;
    private readonly string _transport;
    private readonly string _volumeId;
    private readonly string _hostRoot;      // host-style path that corresponds to the mount root
    private readonly string _tempCacheDir;
    private readonly long _maxMaterializeBytes;
    private readonly bool _readOnly;        // reject writes UP FRONT (before materializing a temp)

    /// <summary>
    /// Creates a backend over one mounted volume.
    /// </summary>
    /// <param name="vfs">
    /// The shared VfsCore the volume is mounted in.
    /// </param>
    /// <param name="transport">
    /// The provider transport (e.g. "native", "tnfs") — the URN's transport field.
    /// </param>
    /// <param name="volumeId">
    /// The mount's volume id.
    /// </param>
    /// <param name="hostRoot">
    /// The host-style path the Econet layer uses for this disc's root.
    /// </param>
    /// <param name="tempCacheDir">
    /// Directory for materialized temp files.
    /// </param>
    /// <param name="maxMaterializeBytes">
    /// Reject reads of files larger than this (guards against OOM on a huge remote blob).
    /// </param>
    /// <param name="readOnly">
    /// When true, reject every write (<see cref="OpenFile"/> for write, <see cref="CreateDirectory"/>)
    /// UP FRONT with <see cref="UnauthorizedAccessException"/>. The underlying provider also enforces
    /// read-only, but only at write time — which, on the write-back-on-close path, is at stream
    /// <c>Dispose</c> after a temp was already materialized. Failing fast here avoids that wasted work
    /// and the awkward throw-from-Dispose. (Step 6 hardening.)
    /// </param>
    public VfsFileSystemBackend(
        VfsCore vfs,
        string transport,
        string volumeId,
        string hostRoot,
        string tempCacheDir,
        long maxMaterializeBytes = 16L * 1024 * 1024,
        bool readOnly = false)
    {
        _vfs = vfs ?? throw new ArgumentNullException(nameof(vfs));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _volumeId = volumeId ?? throw new ArgumentNullException(nameof(volumeId));
        _hostRoot = (hostRoot ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        _tempCacheDir = tempCacheDir ?? throw new ArgumentNullException(nameof(tempCacheDir));
        _maxMaterializeBytes = maxMaterializeBytes;
        _readOnly = readOnly;
        Directory.CreateDirectory(_tempCacheDir);
    }

    /// <summary>
    /// Checks whether a directory exists on the mount.
    /// </summary>
    /// <param name="path">
    /// The host-style path to check.
    /// </param>
    /// <returns>
    /// <c>true</c> when the VFS reports metadata for the path and it is a directory; <c>false</c>
    /// when it is a file or is not found.
    /// </returns>
    public bool DirectoryExists(string path)
    {
        FileMetadata? meta = TryGetMetadata(path);
        return meta != null && meta.IsDirectory;
    }

    /// <summary>
    /// Checks whether a file exists on the mount.
    /// </summary>
    /// <param name="path">
    /// The host-style path to check.
    /// </param>
    /// <returns>
    /// <c>true</c> when the VFS reports metadata for the path and it is not a directory; <c>false</c>
    /// when it is a directory or is not found.
    /// </returns>
    public bool FileExists(string path)
    {
        FileMetadata? meta = TryGetMetadata(path);
        return meta != null && !meta.IsDirectory;
    }

    /// <summary>
    /// Determines whether a path is a directory on the mount.
    /// </summary>
    /// <param name="path">
    /// The host-style path to check.
    /// </param>
    /// <returns>
    /// The same answer as <see cref="DirectoryExists"/>.
    /// </returns>
    public bool IsDirectory(string path)
    {
        FileMetadata? meta = TryGetMetadata(path);
        return meta != null && meta.IsDirectory;
    }

    /// <summary>
    /// Enumerates all entries in a directory on the mount.
    /// </summary>
    /// <remarks>
    /// Errors from the VFS pass through to the caller.
    /// </remarks>
    /// <param name="path">
    /// The host-style directory path.
    /// </param>
    /// <returns>
    /// One path per entry, made by combining <paramref name="path"/> with the entry name.
    /// </returns>
    public IEnumerable<string> EnumerateFileSystemEntries(string path)
    {
        DirectoryEntry[] entries = _vfs.ReadDirectoryAsync(ToUrn(path)).GetAwaiter().GetResult();
        string[] result = new string[entries.Length];
        for (int i = 0; i < entries.Length; i++)
            result[i] = CombinePath(path, entries[i].Name);
        return result;
    }

    /// <summary>
    /// Gets the file name from a path.
    /// </summary>
    /// <remarks>
    /// Backslashes count as slashes and trailing slashes are ignored.
    /// </remarks>
    /// <param name="path">
    /// The path.
    /// </param>
    /// <returns>
    /// The part after the last slash, or the whole path when it has no slash.
    /// </returns>
    public string GetFileName(string path)
    {
        string p = path.Replace('\\', '/').TrimEnd('/');
        int slash = p.LastIndexOf('/');
        return slash < 0 ? p : p.Substring(slash + 1);
    }

    /// <summary>
    /// Gets the length and times of a file on the mount.
    /// </summary>
    /// <remarks>
    /// The VFS reports a size and a modified time. Creation time and access time are not known and
    /// are <c>null</c> in the result, and so is the write time when the VFS has none.
    /// </remarks>
    /// <param name="path">
    /// The host-style path of the file.
    /// </param>
    /// <returns>
    /// The length and times, or <c>null</c> when the path is not found or is a directory.
    /// </returns>
    public BackendFileInfo? GetFileInfo(string path)
    {
        FileMetadata? meta = TryGetMetadata(path);
        if (meta == null || meta.IsDirectory)
            return null;

        DateTime? lastWriteUtc = meta.ModifiedTime.HasValue ? meta.ModifiedTime.Value.ToUniversalTime() : null;
        return new BackendFileInfo(meta.Size, null, lastWriteUtc, null);
    }

    /// <summary>
    /// Opens a file stream for a file on the mount.
    /// </summary>
    /// <remarks>
    /// Only <see cref="FileMode.Open"/> with <see cref="FileAccess.Read"/> counts as a read; every
    /// other combination is a write.
    /// <para>
    /// A read copies the file into a temporary file and returns a read-only stream that deletes the
    /// temporary file when closed.
    /// </para>
    /// <para>
    /// A write works on a temporary file and writes its whole content back to the mount when the
    /// stream is closed. For Open, OpenOrCreate and Append the existing file is copied in first;
    /// Append starts at the end. Other modes start empty.
    /// </para>
    /// </remarks>
    /// <param name="path">
    /// The host-style path of the file.
    /// </param>
    /// <param name="mode">
    /// How to open or create the file.
    /// </param>
    /// <param name="access">
    /// The access wanted.
    /// </param>
    /// <returns>
    /// A stream over a local temporary copy of the file.
    /// </returns>
    /// <exception cref="FileNotFoundException">
    /// Thrown for a read when the file is not found.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when the file that must be copied is larger than the size cap set in the constructor.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown for a write when the backend was created read-only.
    /// </exception>
    public FileStream OpenFile(string path, FileMode mode, FileAccess access)
    {
        // A plain read (Open + Read) takes the read-materialize path; anything else is a write.
        bool wantsWrite = access != FileAccess.Read || mode != FileMode.Open;
        return wantsWrite ? OpenForWrite(path, mode) : OpenForRead(path);
    }

    private FileStream OpenForRead(string path)
    {
        FileMetadata? meta = TryGetMetadata(path);
        if (meta == null)
            throw new FileNotFoundException("File not found in VFS mount.", path);
        if (meta.Size > _maxMaterializeBytes)
            throw new NotSupportedException(
                $"File is {meta.Size} bytes, larger than the materialize cap of {_maxMaterializeBytes}.");

        // Materialize to a temp file and hand back a read stream that deletes the temp on close.
        // NOTE: use the explicit-buffer CopyTo overload — the parameterless Stream.CopyTo() probes
        // src.Length, which a provider stream may not support (e.g. TnfsReadStream throws
        // "does not support getting file length from handle"). The materialize path must not depend on
        // the source stream being seekable/length-aware.
        string temp = Path.Combine(_tempCacheDir, "vfsread_" + Guid.NewGuid().ToString("N") + ".tmp");
        using (Stream src = _vfs.OpenReadAsync(ToUrn(path)).GetAwaiter().GetResult())
        using (FileStream dst = new FileStream(temp, FileMode.Create, FileAccess.Write))
            src.CopyTo(dst, 81920);

        return new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose);
    }

    private FileStream OpenForWrite(string path, FileMode mode)
    {
        // Fail fast on a read-only mount — before materializing any temp (Step 6 hardening).
        if (_readOnly)
            throw new UnauthorizedAccessException($"Econet disc is mounted read-only: cannot write '{path}'.");

        string temp = Path.Combine(_tempCacheDir, "vfswrite_" + Guid.NewGuid().ToString("N") + ".tmp");

        // For modes that preserve existing content (Open/OpenOrCreate/Append), materialize the current
        // file into the temp first so seeks and appends see it; Create/CreateNew/Truncate start empty.
        bool startFromExisting = mode == FileMode.Open || mode == FileMode.OpenOrCreate || mode == FileMode.Append;
        bool materialized = false;
        if (startFromExisting)
        {
            FileMetadata? meta = TryGetMetadata(path);
            if (meta != null && !meta.IsDirectory)
            {
                if (meta.Size > _maxMaterializeBytes)
                    throw new NotSupportedException(
                        $"File is {meta.Size} bytes, larger than the materialize cap of {_maxMaterializeBytes}.");

                using (Stream src = _vfs.OpenReadAsync(ToUrn(path)).GetAwaiter().GetResult())
                using (FileStream dst = new FileStream(temp, FileMode.Create, FileAccess.Write))
                    src.CopyTo(dst, 81920); // explicit buffer: don't probe src.Length (see OpenForRead)
                materialized = true;
            }
        }

        if (!materialized)
        {
            using (new FileStream(temp, FileMode.Create, FileAccess.Write)) { /* empty */ }
        }

        // The returned stream writes the temp's final content back to the mount when it closes.
        return new VfsWriteBackFileStream(temp, _vfs, ToUrn(path), append: mode == FileMode.Append);
    }

    /// <summary>
    /// Deletes a file through the VFS and waits for it to finish.
    /// </summary>
    /// <param name="path">
    /// The path of the file to delete.
    /// </param>
    /// <exception cref="System.UnauthorizedAccessException">
    /// Thrown when the disc is mounted read-only.
    /// </exception>
    public void DeleteFile(string path)
    {
        if (_readOnly)
            throw new UnauthorizedAccessException($"Econet disc is mounted read-only: cannot delete '{path}'.");

        _vfs.DeleteFileAsync(ToUrn(path)).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Not supported: the VFS core cannot delete a directory yet.
    /// </summary>
    /// <param name="path">
    /// The path of the directory. Ignored.
    /// </param>
    /// <exception cref="NotImplementedException">
    /// Always thrown: the VFS core has no directory delete yet.
    /// </exception>
    public void DeleteDirectory(string path)
    {
        throw new NotImplementedException("The VFS core has no directory delete yet, so a VFS-backed Econet disc cannot delete a directory.");
    }

    /// <summary>
    /// Not supported: the VFS core cannot move or rename yet.
    /// </summary>
    /// <param name="sourcePath">
    /// The path of the file or directory to move.
    /// </param>
    /// <param name="destinationPath">
    /// The new path.
    /// </param>
    /// <exception cref="NotImplementedException">
    /// Always thrown: the VFS core has no move or rename yet.
    /// </exception>
    public void MoveFile(string sourcePath, string destinationPath)
    {
        throw new NotImplementedException("The VFS core has no move or rename yet, so a VFS-backed Econet disc cannot rename a file.");
    }

    /// <summary>
    /// Not supported: the VFS core cannot move or rename yet.
    /// </summary>
    /// <param name="sourcePath">
    /// The path of the file or directory to move.
    /// </param>
    /// <param name="destinationPath">
    /// The new path.
    /// </param>
    /// <exception cref="NotImplementedException">
    /// Always thrown: the VFS core has no move or rename yet.
    /// </exception>
    public void MoveDirectory(string sourcePath, string destinationPath)
    {
        throw new NotImplementedException("The VFS core has no move or rename yet, so a VFS-backed Econet disc cannot rename a directory.");
    }

    /// <summary>
    /// Creates a directory on the mount.
    /// </summary>
    /// <remarks>
    /// The request is passed to the VFS provider; a provider that cannot create directories throws
    /// <see cref="NotSupportedException"/>, and that and other provider errors pass through.
    /// </remarks>
    /// <param name="path">
    /// The host-style directory path to create.
    /// </param>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when the backend was created read-only.
    /// </exception>
    public void CreateDirectory(string path)
    {
        // Fail fast on a read-only mount (Step 6 hardening) — consistent with OpenForWrite.
        if (_readOnly)
            throw new UnauthorizedAccessException($"Econet disc is mounted read-only: cannot create '{path}'.");

        // Delegates to the provider; providers that can't mkdir throw NotSupportedException (honest).
        _vfs.CreateDirectoryAsync(ToUrn(path)).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gets the full path: backslashes become slashes, repeated slashes are merged, <c>.</c> parts are dropped,
    /// <c>..</c> parts are resolved and a trailing slash is removed.
    /// </summary>
    /// <param name="path">
    /// The path.
    /// </param>
    /// <returns>
    /// The resolved path. A rooted path stays rooted and a relative path stays relative.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="path"/> is null.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when a <c>..</c> part climbs above the start of the path, or out of the mount root.
    /// </exception>
    public string GetFullPath(string path) => HostPathResolver.Resolve(path, _hostRoot);

    /// <summary>
    /// Combines path segments with slashes.
    /// </summary>
    /// <remarks>
    /// Backslashes become slashes. Trailing slashes are removed from the first segment, slashes at
    /// both ends are removed from the others, and empty segments are skipped.
    /// </remarks>
    /// <param name="paths">
    /// The path segments.
    /// </param>
    /// <returns>
    /// The combined path, or an empty string when <paramref name="paths"/> is null or empty.
    /// </returns>
    public string CombinePath(params string[] paths)
    {
        if (paths == null || paths.Length == 0)
            return string.Empty;

        string combined = paths[0].Replace('\\', '/').TrimEnd('/');
        for (int i = 1; i < paths.Length; i++)
        {
            string segment = paths[i].Replace('\\', '/').Trim('/');
            if (segment.Length == 0)
                continue;
            combined = combined.Length == 0 ? segment : combined + "/" + segment;
        }
        return combined;
    }

    // ---- helpers --------------------------------------------------------------------------------

    private FileMetadata? TryGetMetadata(string path)
    {
        try
        {
            return _vfs.GetMetadataAsync(ToUrn(path)).GetAwaiter().GetResult();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    // host path -> urn:retrofs:<transport>:<volumeId>:<subPath>
    private string ToUrn(string hostPath) => $"urn:retrofs:{_transport}:{_volumeId}:{ToSubPath(hostPath)}";

    private string ToSubPath(string hostPath)
    {
        string p = (hostPath ?? string.Empty).Replace('\\', '/');
        if (string.Equals(p.TrimEnd('/'), _hostRoot, StringComparison.OrdinalIgnoreCase))
            return "/";

        if (p.StartsWith(_hostRoot + "/", StringComparison.OrdinalIgnoreCase))
            return p.Substring(_hostRoot.Length); // keeps the leading '/'

        // Unrelated to the mount root — treat as already a sub-path, ensuring a leading '/'.
        return p.StartsWith("/") ? p : "/" + p;
    }
}
