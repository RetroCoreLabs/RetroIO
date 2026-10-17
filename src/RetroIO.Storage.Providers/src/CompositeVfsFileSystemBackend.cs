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

namespace RetroIO.Storage.Providers;

/// <summary>
/// Presents several VFS-mounted discs to the Econet file server as one <see cref="IFileSystemBackend"/>.
/// </summary>
/// <remarks>
/// The Econet server uses a single backend for all discs, but each disc can live on a different VFS
/// mount (native / azureblob / ftp / sftp / tnfs). This façade routes each host path by its disc-
/// directory prefix (e.g. <c>/econet-root/0RetroFS/...</c>) to that disc's own single-mount
/// <see cref="VfsFileSystemBackend"/>, and synthesises the virtual disc-root listing (the mount set is
/// not a real directory in any one provider). This is the composition model chosen in the Phase 2
/// review: per-disc settings (transport, read-only, size cap, cache) stay encapsulated in each per-disc
/// backend, and the tested single-mount adapter is reused unchanged.
/// </remarks>
public sealed class CompositeVfsFileSystemBackend : IFileSystemBackend
{
    /// <summary>One disc: its on-root directory name (e.g. "0RetroFS") and the backend serving it.</summary>
    public sealed class DiscMount
    {
        public string DirName { get; }
        public IFileSystemBackend Backend { get; }

        public DiscMount(string dirName, IFileSystemBackend backend)
        {
            DirName = dirName ?? throw new ArgumentNullException(nameof(dirName));
            Backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }
    }

    private readonly string _root;      // the logical Econet root (e.g. "/econet-root")
    private readonly DiscMount[] _discs;

    /// <summary>
    /// Creates the composite over a logical root and its discs.
    /// </summary>
    public CompositeVfsFileSystemBackend(string econetRoot, DiscMount[] discs)
    {
        _root = (econetRoot ?? "/").Replace('\\', '/').TrimEnd('/');
        _discs = discs ?? Array.Empty<DiscMount>();
    }

    /// <inheritdoc/>
    public bool DirectoryExists(string path)
    {
        IFileSystemBackend? backend = Route(path);
        return backend != null ? backend.DirectoryExists(path) : IsRoot(path);
    }

    /// <inheritdoc/>
    public bool IsDirectory(string path)
    {
        IFileSystemBackend? backend = Route(path);
        return backend != null ? backend.IsDirectory(path) : IsRoot(path);
    }

    /// <inheritdoc/>
    public bool FileExists(string path)
    {
        IFileSystemBackend? backend = Route(path);
        return backend != null && backend.FileExists(path);
    }

    /// <inheritdoc/>
    public IEnumerable<string> EnumerateFileSystemEntries(string path)
    {
        IFileSystemBackend? backend = Route(path);
        if (backend != null)
            return backend.EnumerateFileSystemEntries(path);

        if (IsRoot(path))
        {
            // The virtual root lists the configured disc directories.
            string[] result = new string[_discs.Length];
            for (int i = 0; i < _discs.Length; i++)
                result[i] = _root + "/" + _discs[i].DirName;
            return result;
        }

        return Array.Empty<string>();
    }

    /// <inheritdoc/>
    public FileInfo? GetFileInfo(string path)
    {
        IFileSystemBackend? backend = Route(path);
        return backend?.GetFileInfo(path);
    }

    /// <inheritdoc/>
    public FileStream OpenFile(string path, FileMode mode, FileAccess access)
    {
        IFileSystemBackend? backend = Route(path);
        if (backend == null)
            throw new UnauthorizedAccessException("Cannot open the disc-root level as a file.");
        return backend.OpenFile(path, mode, access);
    }

    /// <inheritdoc/>
    public void CreateDirectory(string path)
    {
        IFileSystemBackend? backend = Route(path);
        if (backend == null)
            throw new UnauthorizedAccessException("Cannot create a directory at the disc-root level.");
        backend.CreateDirectory(path);
    }

    /// <inheritdoc/>
    public string GetFileName(string path)
    {
        string p = path.Replace('\\', '/').TrimEnd('/');
        int slash = p.LastIndexOf('/');
        return slash < 0 ? p : p.Substring(slash + 1);
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

    // ---- routing ---------------------------------------------------------------------------------

    // Returns the backend for the disc a path falls under, or null for the virtual root / unknown.
    private IFileSystemBackend? Route(string path)
    {
        string p = (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        for (int i = 0; i < _discs.Length; i++)
        {
            string discRoot = _root + "/" + _discs[i].DirName;
            if (string.Equals(p, discRoot, StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith(discRoot + "/", StringComparison.OrdinalIgnoreCase))
                return _discs[i].Backend;
        }
        return null;
    }

    private bool IsRoot(string path)
    {
        string p = (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        return p.Length == 0 || p == "/" || string.Equals(p, _root, StringComparison.OrdinalIgnoreCase);
    }
}
