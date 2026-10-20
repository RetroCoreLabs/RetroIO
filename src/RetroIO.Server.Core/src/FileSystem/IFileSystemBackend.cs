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

    /// <summary>
    /// Tells where the property bags of objects are kept.
    /// </summary>
    /// <returns>
    /// A capabilities value whose <see cref="RetroIO.Storage.Core.FileSystemCapabilities.SupportsExtendedAttributes"/> is true when the backend stores the
    /// property bag on the object itself, as an extended attribute of the host. It is false when the backend cannot, and the bag then lives in the .xat file
    /// (see <see cref="XatSidecar"/>), exactly as NDFS declares it. Every backend in this repository says false, because .NET has no portable extended attribute call.
    /// </returns>
    RetroIO.Storage.Core.FileSystemCapabilities GetAttributeCapabilities();

    /// <summary>
    /// Gets the property bag of a file or directory.
    /// </summary>
    /// <param name="path">
    /// The path of the object.
    /// </param>
    /// <returns>
    /// The property bag, or null when the object has none.
    /// </returns>
    /// <remarks>
    /// A backend that cannot store attributes on the object reads the .xat file with <see cref="XatSidecar.Read"/>. A backend with native attributes implements all four attribute members together.
    /// </remarks>
    IDictionary<string, object>? GetObjectProperties(string path);

    /// <summary>
    /// Sets the property bag of a file or directory. The bag replaces the bag that was there, and an empty bag removes it.
    /// </summary>
    /// <param name="path">
    /// The path of the object.
    /// </param>
    /// <param name="propertyBag">
    /// The properties to store.
    /// </param>
    void SetObjectProperties(string path, IDictionary<string, object> propertyBag);

    /// <summary>
    /// Moves the property bag of an object to the new path of the object, when it has one. Call it after the object itself was moved.
    /// </summary>
    /// <param name="fromPath">
    /// The path the object had.
    /// </param>
    /// <param name="toPath">
    /// The path the object has now.
    /// </param>
    void MoveObjectProperties(string fromPath, string toPath);

    /// <summary>
    /// Removes the property bag of an object, when it has one. Call it after the object itself was deleted.
    /// </summary>
    /// <param name="path">
    /// The path the object had.
    /// </param>
    void DeleteObjectProperties(string path);
}
