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
    /// Delete a file.
    /// </summary>
    /// <param name="path">
    /// The path of the file to delete.
    /// </param>
    /// <exception cref="System.IO.FileNotFoundException">
    /// Thrown when there is no file at <paramref name="path"/>.
    /// </exception>
    void DeleteFile(string path);

    /// <summary>
    /// Delete an empty directory.
    /// </summary>
    /// <param name="path">
    /// The path of the directory to delete.
    /// </param>
    /// <exception cref="System.IO.DirectoryNotFoundException">
    /// Thrown when there is no directory at <paramref name="path"/>.
    /// </exception>
    /// <exception cref="System.IO.IOException">
    /// Thrown when the directory is not empty.
    /// </exception>
    void DeleteDirectory(string path);

    /// <summary>
    /// Move or rename a file.
    /// </summary>
    /// <param name="sourcePath">
    /// The path of the file to move.
    /// </param>
    /// <param name="destinationPath">
    /// The new path. The directory it is in must exist.
    /// </param>
    /// <exception cref="System.IO.FileNotFoundException">
    /// Thrown when there is no file at <paramref name="sourcePath"/>.
    /// </exception>
    /// <exception cref="System.IO.IOException">
    /// Thrown when something already exists at <paramref name="destinationPath"/>.
    /// </exception>
    void MoveFile(string sourcePath, string destinationPath);

    /// <summary>
    /// Move or rename a directory together with everything in it.
    /// </summary>
    /// <param name="sourcePath">
    /// The path of the directory to move.
    /// </param>
    /// <param name="destinationPath">
    /// The new path. The directory it is in must exist.
    /// </param>
    /// <exception cref="System.IO.DirectoryNotFoundException">
    /// Thrown when there is no directory at <paramref name="sourcePath"/>.
    /// </exception>
    /// <exception cref="System.IO.IOException">
    /// Thrown when something already exists at <paramref name="destinationPath"/>.
    /// </exception>
    void MoveDirectory(string sourcePath, string destinationPath);

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
    /// Get information about a file: its length and its times.
    /// </summary>
    /// <param name="path">
    /// The path of the file.
    /// </param>
    /// <returns>
    /// The length and times of the file, or <c>null</c> when there is no file at <paramref name="path"/>.
    /// A directory is not a file, so it also gives <c>null</c>.
    /// </returns>
    BackendFileInfo? GetFileInfo(string path);

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
