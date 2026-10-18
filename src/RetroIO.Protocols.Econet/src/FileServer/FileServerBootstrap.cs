//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Protocols.Econet.FileServer;

/// <summary>
/// Creates the directory layout a new file server needs.
/// </summary>
public static class FileServerBootstrap
{
    /// <summary>
    /// Creates the root directory, the disc directory and its USER directory when they are missing, then writes the sample files.
    /// </summary>
    /// <param name="fileSystem">
    /// The file system used to check which directories exist.
    /// </param>
    /// <param name="rootDirectory">
    /// The root directory of the file server.
    /// </param>
    /// <param name="discName">
    /// The disc name. The disc directory is named 0 followed by this name.
    /// </param>
    public static void EnsureLayout(IFileSystemBackend fileSystem, string rootDirectory, string discName)
    {
        string discDirectoryName = "0" + discName;
        string discPath = fileSystem.CombinePath(rootDirectory, discDirectoryName);
        string userPath = fileSystem.CombinePath(discPath, "USER");

        // Through the backend, never System.IO: the backend may be the VFS, Azure or a test double, and
        // its paths are not paths on the host disk.
        if (!fileSystem.DirectoryExists(rootDirectory))
            fileSystem.CreateDirectory(rootDirectory);

        if (!fileSystem.DirectoryExists(discPath))
            fileSystem.CreateDirectory(discPath);

        if (!fileSystem.DirectoryExists(userPath))
            fileSystem.CreateDirectory(userPath);

        EconetSampleCatalog.EnsureUserSamples(fileSystem, userPath);
    }
}
