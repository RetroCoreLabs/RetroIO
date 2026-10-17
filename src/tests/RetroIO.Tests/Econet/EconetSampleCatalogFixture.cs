//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.IO;
using System.Text;
using RetroIO.Protocols.Econet.FileServer;
using RetroIO.Tests.TNFS;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Shared <c>*CAT</c> fixture: <c>0RetroFS/USER</c> with sample files (same names as live bootstrap).
/// </summary>
internal static class EconetSampleCatalogFixture
{
    public const string RootDirectory = "/econet-root";
    public const string UserDirectory = "/econet-root/0RetroFS/USER";

    public static void Seed(MockFileSystemBackend fileSystem)
    {
        fileSystem.AddDirectory(RootDirectory);
        fileSystem.AddDirectory($"{RootDirectory}/0RetroFS");
        fileSystem.AddDirectory(UserDirectory);
        fileSystem.AddDirectory($"{RootDirectory}/1STORAGE");
        fileSystem.AddDirectory($"{RootDirectory}/1STORAGE/USER");

        EconetSampleCatalog.EnsureUserSamples(fileSystem, UserDirectory);
    }

    public static byte[] ReadSampleFile(MockFileSystemBackend fileSystem, string acornName)
    {
        string path = fileSystem.CombinePath(UserDirectory, acornName);
        if (!fileSystem.FileExists(path))
            return [];

        using System.IO.MemoryStream ms = new System.IO.MemoryStream();
        using System.IO.FileStream fs = fileSystem.OpenFile(path, FileMode.Open, FileAccess.Read);
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    public static string ReadSampleFileText(MockFileSystemBackend fileSystem, string acornName) =>
        Encoding.ASCII.GetString(ReadSampleFile(fileSystem, acornName));
}
