//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Server.Core.FileSystem;

/// <summary>
/// Abstraction for file system operations.
/// Allows for testing with mock file systems and different storage backends.
/// </summary>
public interface IFileSystemBackend
{
    /// <summary>
    /// Check if a directory exists.
    /// </summary>
    bool DirectoryExists(string path);

    /// <summary>
    /// Create a directory, including any missing parent directories. Does nothing if it already
    /// exists.
    /// </summary>
    /// <param name="path">
    /// The directory path to create.
    /// </param>
    void CreateDirectory(string path);

    /// <summary>
    /// Check if a file exists.
    /// </summary>
    bool FileExists(string path);

    /// <summary>
    /// Enumerate all file system entries in a directory.
    /// </summary>
    IEnumerable<string> EnumerateFileSystemEntries(string path);

    /// <summary>
    /// Get the file name from a path.
    /// </summary>
    string GetFileName(string path);

    /// <summary>
    /// Determine if a path represents a directory.
    /// </summary>
    bool IsDirectory(string path);

    /// <summary>
    /// Get file information.
    /// </summary>
    FileInfo? GetFileInfo(string path);

    /// <summary>
    /// Open a file stream.
    /// </summary>
    FileStream OpenFile(string path, FileMode mode, FileAccess access);

    /// <summary>
    /// Get the full path (normalized and resolved).
    /// </summary>
    string GetFullPath(string path);

    /// <summary>
    /// Combine path segments.
    /// </summary>
    string CombinePath(params string[] paths);
}
