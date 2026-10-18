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
    /// <summary>
    /// One disc: its on-root directory name (e.g. "0RetroFS") and the backend serving it.
    /// </summary>
    public sealed class DiscMount
    {
        /// <summary>
        /// Gets the directory name of the disc below the Econet root.
        /// </summary>
        public string DirName { get; }
        /// <summary>
        /// Gets the backend that serves the disc.
        /// </summary>
        public IFileSystemBackend Backend { get; }

        /// <summary>
        /// Creates a disc entry.
        /// </summary>
        /// <param name="dirName">
        /// The directory name of the disc below the Econet root.
        /// </param>
        /// <param name="backend">
        /// The backend that serves the disc.
        /// </param>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="dirName"/> or <paramref name="backend"/> is null.
        /// </exception>
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

    /// <summary>
    /// Checks whether a directory exists.
    /// </summary>
    /// <param name="path">
    /// The host-style path to check.
    /// </param>
    /// <returns>
    /// The answer of the disc backend when <paramref name="path"/> lies on a disc; otherwise
    /// <c>true</c> only for the virtual root (the Econet root, <c>/</c> or an empty path).
    /// </returns>
    public bool DirectoryExists(string path)
    {
        IFileSystemBackend? backend = Route(path);
        return backend != null ? backend.DirectoryExists(path) : IsRoot(path);
    }

    /// <summary>
    /// Determines whether a path is a directory.
    /// </summary>
    /// <param name="path">
    /// The host-style path to check.
    /// </param>
    /// <returns>
    /// The answer of the disc backend when <paramref name="path"/> lies on a disc; otherwise
    /// <c>true</c> only for the virtual root (the Econet root, <c>/</c> or an empty path).
    /// </returns>
    public bool IsDirectory(string path)
    {
        IFileSystemBackend? backend = Route(path);
        return backend != null ? backend.IsDirectory(path) : IsRoot(path);
    }

    /// <summary>
    /// Checks whether a file exists.
    /// </summary>
    /// <param name="path">
    /// The host-style path to check.
    /// </param>
    /// <returns>
    /// The answer of the disc backend when <paramref name="path"/> lies on a disc; otherwise
    /// <c>false</c>.
    /// </returns>
    public bool FileExists(string path)
    {
        IFileSystemBackend? backend = Route(path);
        return backend != null && backend.FileExists(path);
    }

    /// <summary>
    /// Enumerates all entries in a directory.
    /// </summary>
    /// <param name="path">
    /// The host-style directory path.
    /// </param>
    /// <returns>
    /// The entries from the disc backend when <paramref name="path"/> lies on a disc; for the
    /// virtual root, one path per configured disc (the Econet root, a slash and the disc directory
    /// name); otherwise an empty sequence.
    /// </returns>
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

    /// <summary>
    /// Gets file information.
    /// </summary>
    /// <param name="path">
    /// The host-style path of the file.
    /// </param>
    /// <returns>
    /// The answer of the disc backend when <paramref name="path"/> lies on a disc; otherwise
    /// <c>null</c>.
    /// </returns>
    public FileInfo? GetFileInfo(string path)
    {
        IFileSystemBackend? backend = Route(path);
        return backend?.GetFileInfo(path);
    }

    /// <summary>
    /// Opens a file stream through the backend of the disc the path lies on.
    /// </summary>
    /// <remarks>
    /// Errors from the disc backend pass through to the caller.
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
    /// The stream returned by the disc backend.
    /// </returns>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when <paramref name="path"/> does not lie on any configured disc.
    /// </exception>
    public FileStream OpenFile(string path, FileMode mode, FileAccess access)
    {
        IFileSystemBackend? backend = Route(path);
        if (backend == null)
            throw new UnauthorizedAccessException("Cannot open the disc-root level as a file.");
        return backend.OpenFile(path, mode, access);
    }

    /// <summary>
    /// Creates a directory through the backend of the disc the path lies on.
    /// </summary>
    /// <remarks>
    /// Errors from the disc backend pass through to the caller.
    /// </remarks>
    /// <param name="path">
    /// The host-style directory path to create.
    /// </param>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when <paramref name="path"/> does not lie on any configured disc.
    /// </exception>
    public void CreateDirectory(string path)
    {
        IFileSystemBackend? backend = Route(path);
        if (backend == null)
            throw new UnauthorizedAccessException("Cannot create a directory at the disc-root level.");
        backend.CreateDirectory(path);
    }

    /// <summary>
    /// Deletes a file on the disc that <paramref name="path"/> belongs to.
    /// </summary>
    /// <param name="path">
    /// The path of the file to delete.
    /// </param>
    /// <exception cref="System.UnauthorizedAccessException">
    /// Thrown when <paramref name="path"/> is at the disc-root level and so belongs to no disc.
    /// </exception>
    public void DeleteFile(string path)
    {
        IFileSystemBackend? backend = Route(path);
        if (backend == null)
            throw new UnauthorizedAccessException("Cannot delete a file at the disc-root level.");
        backend.DeleteFile(path);
    }

    /// <summary>
    /// Deletes a directory on the disc that <paramref name="path"/> belongs to.
    /// </summary>
    /// <param name="path">
    /// The path of the directory to delete.
    /// </param>
    /// <exception cref="System.UnauthorizedAccessException">
    /// Thrown when <paramref name="path"/> is a disc itself, or belongs to no disc. A disc cannot be deleted.
    /// </exception>
    public void DeleteDirectory(string path)
    {
        IFileSystemBackend? backend = Route(path);
        if (backend == null || IsDiscRoot(path))
            throw new UnauthorizedAccessException("Cannot delete a disc.");
        backend.DeleteDirectory(path);
    }

    /// <summary>
    /// Moves or renames a file inside one disc.
    /// </summary>
    /// <param name="sourcePath">
    /// The path of the file or directory to move.
    /// </param>
    /// <param name="destinationPath">
    /// The new path.
    /// </param>
    /// <exception cref="NotSupportedException">
    /// Thrown when the source and the destination are on different discs.
    /// </exception>
    public void MoveFile(string sourcePath, string destinationPath)
    {
        RouteBoth(sourcePath, destinationPath).MoveFile(sourcePath, destinationPath);
    }

    /// <summary>
    /// Moves or renames a directory inside one disc.
    /// </summary>
    /// <param name="sourcePath">
    /// The path of the file or directory to move.
    /// </param>
    /// <param name="destinationPath">
    /// The new path.
    /// </param>
    /// <exception cref="NotSupportedException">
    /// Thrown when the source and the destination are on different discs.
    /// </exception>
    public void MoveDirectory(string sourcePath, string destinationPath)
    {
        RouteBoth(sourcePath, destinationPath).MoveDirectory(sourcePath, destinationPath);
    }

    // A move stays inside one disc: both paths must route to the same backend.
    private IFileSystemBackend RouteBoth(string sourcePath, string destinationPath)
    {
        IFileSystemBackend? source = Route(sourcePath);
        IFileSystemBackend? destination = Route(destinationPath);
        if (source == null || destination == null || IsDiscRoot(sourcePath) || IsDiscRoot(destinationPath))
            throw new UnauthorizedAccessException("Cannot move or rename at the disc-root level.");
        if (!ReferenceEquals(source, destination))
            throw new NotSupportedException("Cannot move between discs.");
        return source;
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
    /// Gets the full path. This implementation only turns backslashes into slashes; it does not
    /// resolve relative parts.
    /// </summary>
    /// <param name="path">
    /// The path.
    /// </param>
    /// <returns>
    /// <paramref name="path"/> with every backslash replaced by a slash.
    /// </returns>
    public string GetFullPath(string path) => path.Replace('\\', '/');

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

    // ---- routing ---------------------------------------------------------------------------------

    // Returns the backend for the disc a path falls under, or null for the virtual root / unknown.
    // True when the path is the top folder of a disc itself, not something inside it.
    private bool IsDiscRoot(string path)
    {
        string p = (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        for (int i = 0; i < _discs.Length; i++)
        {
            if (string.Equals(p, _root + "/" + _discs[i].DirName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

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
