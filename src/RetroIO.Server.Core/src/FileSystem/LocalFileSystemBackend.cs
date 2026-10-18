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
    /// <summary>
    /// Checks whether a directory exists, using <see cref="Directory.Exists"/>.
    /// </summary>
    /// <param name="path">
    /// The directory path to check.
    /// </param>
    /// <returns>
    /// <c>true</c> when the directory exists; otherwise <c>false</c>.
    /// </returns>
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <summary>
    /// Creates a directory, including any missing parent directories, using
    /// <see cref="Directory.CreateDirectory(string)"/>. Does nothing if it already exists.
    /// </summary>
    /// <remarks>
    /// Errors from <see cref="Directory.CreateDirectory(string)"/> pass through to the caller.
    /// </remarks>
    /// <param name="path">
    /// The directory path to create.
    /// </param>
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <summary>
    /// Deletes a file from the local disk.
    /// </summary>
    /// <param name="path">
    /// The path of the file to delete.
    /// </param>
    /// <exception cref="System.IO.FileNotFoundException">
    /// Thrown when there is no file at <paramref name="path"/>. <c>File.Delete</c> does nothing for a missing file, so the check is made first.
    /// </exception>
    public void DeleteFile(string path)
    {
        // File.Delete does nothing for a missing file; the interface promises FileNotFoundException.
        if (!File.Exists(path))
            throw new FileNotFoundException("File not found.", path);

        File.Delete(path);
    }

    /// <summary>
    /// Deletes an empty directory from the local disk. The directory is not deleted recursively.
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
    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: false);

    /// <summary>
    /// Moves or renames a file on the local disk, with <c>File.Move</c>.
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
    public void MoveFile(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath);

    /// <summary>
    /// Moves or renames a directory together with everything in it, on the local disk, with <c>Directory.Move</c>.
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
    public void MoveDirectory(string sourcePath, string destinationPath) => Directory.Move(sourcePath, destinationPath);

    /// <summary>
    /// Checks whether a file exists, using <see cref="File.Exists"/>.
    /// </summary>
    /// <param name="path">
    /// The file path to check.
    /// </param>
    /// <returns>
    /// <c>true</c> when the file exists; otherwise <c>false</c>.
    /// </returns>
    public bool FileExists(string path) => File.Exists(path);

    /// <summary>
    /// Enumerates all files and directories in a directory, using
    /// <see cref="Directory.EnumerateFileSystemEntries(string)"/>.
    /// </summary>
    /// <remarks>
    /// Errors from <see cref="Directory.EnumerateFileSystemEntries(string)"/> pass through to the caller.
    /// </remarks>
    /// <param name="path">
    /// The directory path.
    /// </param>
    /// <returns>
    /// The full paths of the entries.
    /// </returns>
    public IEnumerable<string> EnumerateFileSystemEntries(string path)
        => Directory.EnumerateFileSystemEntries(path);

    /// <summary>
    /// Gets the file name from a path, using <see cref="Path.GetFileName(string)"/>.
    /// </summary>
    /// <param name="path">
    /// The path.
    /// </param>
    /// <returns>
    /// The part after the last directory separator.
    /// </returns>
    public string GetFileName(string path) => Path.GetFileName(path);

    /// <summary>
    /// Determines whether a path is an existing directory, using <see cref="Directory.Exists"/>.
    /// </summary>
    /// <param name="path">
    /// The path to check.
    /// </param>
    /// <returns>
    /// <c>true</c> when the path is an existing directory; otherwise <c>false</c>.
    /// </returns>
    public bool IsDirectory(string path) => Directory.Exists(path);

    /// <summary>
    /// Gets file information for a path.
    /// </summary>
    /// <remarks>
    /// The <see cref="FileInfo"/> is created whether or not the file exists; check its
    /// <c>Exists</c> property.
    /// </remarks>
    /// <param name="path">
    /// The file path.
    /// </param>
    /// <returns>
    /// A new <see cref="FileInfo"/> for <paramref name="path"/>, or <c>null</c> when creating it
    /// throws, for example for a null or empty path.
    /// </returns>
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

    /// <summary>
    /// Opens a file stream, using <see cref="File.Open(string, FileMode, FileAccess)"/>.
    /// </summary>
    /// <remarks>
    /// Errors from <see cref="File.Open(string, FileMode, FileAccess)"/> pass through to the caller.
    /// </remarks>
    /// <param name="path">
    /// The file path.
    /// </param>
    /// <param name="mode">
    /// How to open or create the file.
    /// </param>
    /// <param name="access">
    /// The access wanted.
    /// </param>
    /// <returns>
    /// The opened stream.
    /// </returns>
    public FileStream OpenFile(string path, FileMode mode, FileAccess access)
        => File.Open(path, mode, access);

    /// <summary>
    /// Gets the full, absolute path, using <see cref="Path.GetFullPath(string)"/>.
    /// </summary>
    /// <param name="path">
    /// The path to resolve.
    /// </param>
    /// <returns>
    /// The absolute path.
    /// </returns>
    public string GetFullPath(string path) => Path.GetFullPath(path);

    /// <summary>
    /// Combines path segments, using <see cref="Path.Combine(string[])"/>.
    /// </summary>
    /// <param name="paths">
    /// The path segments.
    /// </param>
    /// <returns>
    /// The combined path.
    /// </returns>
    public string CombinePath(params string[] paths) => Path.Combine(paths);
}
