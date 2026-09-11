//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Protocols.Econet.FileServer;

public static class FileServerBootstrap
{
    public static void EnsureLayout(IFileSystemBackend fileSystem, string rootDirectory, string discName)
    {
        string discDirectoryName = "0" + discName;
        string discPath = fileSystem.CombinePath(rootDirectory, discDirectoryName);
        string userPath = fileSystem.CombinePath(discPath, "USER");

        if (!fileSystem.DirectoryExists(rootDirectory))
            Directory.CreateDirectory(rootDirectory);

        if (!fileSystem.DirectoryExists(discPath))
            Directory.CreateDirectory(discPath);

        if (!fileSystem.DirectoryExists(userPath))
            Directory.CreateDirectory(userPath);

        EconetSampleCatalog.EnsureUserSamples(fileSystem, userPath);
    }
}
