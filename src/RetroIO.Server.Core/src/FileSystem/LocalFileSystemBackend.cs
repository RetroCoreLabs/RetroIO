//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Server.Core.FileSystem;

/// <summary>
/// Real file system backend implementation using System.IO.
/// </summary>
public sealed class LocalFileSystemBackend : IFileSystemBackend
{
    /// <inheritdoc/>
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc/>
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc/>
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc/>
    public IEnumerable<string> EnumerateFileSystemEntries(string path)
        => Directory.EnumerateFileSystemEntries(path);

    /// <inheritdoc/>
    public string GetFileName(string path) => Path.GetFileName(path);

    /// <inheritdoc/>
    public bool IsDirectory(string path) => Directory.Exists(path);

    /// <inheritdoc/>
    public FileInfo? GetFileInfo(string path)
    {
        try
        {
            return new FileInfo(path);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public FileStream OpenFile(string path, FileMode mode, FileAccess access)
        => File.Open(path, mode, access);

    /// <inheritdoc/>
    public string GetFullPath(string path) => Path.GetFullPath(path);

    /// <inheritdoc/>
    public string CombinePath(params string[] paths) => Path.Combine(paths);
}
