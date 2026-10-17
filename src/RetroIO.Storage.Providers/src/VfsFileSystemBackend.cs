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

    /// <inheritdoc/>
    public bool DirectoryExists(string path)
    {
        FileMetadata? meta = TryGetMetadata(path);
        return meta != null && meta.IsDirectory;
    }

    /// <inheritdoc/>
    public bool FileExists(string path)
    {
        FileMetadata? meta = TryGetMetadata(path);
        return meta != null && !meta.IsDirectory;
    }

    /// <inheritdoc/>
    public bool IsDirectory(string path)
    {
        FileMetadata? meta = TryGetMetadata(path);
        return meta != null && meta.IsDirectory;
    }

    /// <inheritdoc/>
    public IEnumerable<string> EnumerateFileSystemEntries(string path)
    {
        DirectoryEntry[] entries = _vfs.ReadDirectoryAsync(ToUrn(path)).GetAwaiter().GetResult();
        string[] result = new string[entries.Length];
        for (int i = 0; i < entries.Length; i++)
            result[i] = CombinePath(path, entries[i].Name);
        return result;
    }

    /// <inheritdoc/>
    public string GetFileName(string path)
    {
        string p = path.Replace('\\', '/').TrimEnd('/');
        int slash = p.LastIndexOf('/');
        return slash < 0 ? p : p.Substring(slash + 1);
    }

    /// <inheritdoc/>
    public FileInfo? GetFileInfo(string path)
    {
        FileMetadata? meta = TryGetMetadata(path);
        if (meta == null || meta.IsDirectory)
            return null;

        // Synthesize a FileInfo carrying the real Length + LastWriteTimeUtc (all the Econet layer reads).
        // SetLength makes a sparse temp of the right size cheaply; force the FileInfo to cache its values,
        // then delete the temp so nothing leaks.
        string temp = Path.Combine(_tempCacheDir, "vfsinfo_" + Guid.NewGuid().ToString("N") + ".tmp");
        using (FileStream fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
            fs.SetLength(meta.Size);
        if (meta.ModifiedTime.HasValue)
            File.SetLastWriteTimeUtc(temp, meta.ModifiedTime.Value.ToUniversalTime());

        FileInfo info = new FileInfo(temp);
        info.Refresh();
        _ = info.Length;             // force the cache
        _ = info.LastWriteTimeUtc;   // force the cache
        try { File.Delete(temp); } catch { /* best effort */ }
        return info;
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public void CreateDirectory(string path)
    {
        // Fail fast on a read-only mount (Step 6 hardening) — consistent with OpenForWrite.
        if (_readOnly)
            throw new UnauthorizedAccessException($"Econet disc is mounted read-only: cannot create '{path}'.");

        // Delegates to the provider; providers that can't mkdir throw NotSupportedException (honest).
        _vfs.CreateDirectoryAsync(ToUrn(path)).GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public string GetFullPath(string path) => path.Replace('\\', '/');

    /// <inheritdoc/>
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
