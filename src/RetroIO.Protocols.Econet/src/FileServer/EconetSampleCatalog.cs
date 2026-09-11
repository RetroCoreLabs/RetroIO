//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Text;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Protocols.Econet.FileServer;

/// <summary>
/// Default files under <c>{disc}/USER</c> so <c>*CAT</c> has something to list on a fresh install.
/// Only creates files that are missing — never overwrites user data.
/// </summary>
public static class EconetSampleCatalog
{
    public static readonly IReadOnlyList<string> DefaultFileNames = ["HELLO", "README", "BASIC"];

    private static readonly (string Name, string Content)[] Files =
    [
        ("HELLO", "Hello from RetroFS Econet!\r\nThis file lives in :RetroFS.$.USER.\r\n"),
        ("README", "RetroFS sample catalogue.\r\n*CAT should list HELLO, README and BASIC.\r\n"),
        ("BASIC", "10 PRINT \"Econet catalogue OK\"\r\n20 PRINT \"HELLO from RetroFS\"\r\n30 END\r\n"),
    ];

    public static IReadOnlyList<(string Name, string Content)> GetSampleTexts() => Files;

    public static void EnsureUserSamples(IFileSystemBackend fileSystem, string userDirectory)
    {
        for (int i = 0; i < Files.Length; i++)
        {
            (string name, string content) = Files[i];
            string path = fileSystem.CombinePath(userDirectory, name);
            if (fileSystem.FileExists(path))
                continue;

            byte[] bytes = Encoding.ASCII.GetBytes(content);
            using FileStream stream = fileSystem.OpenFile(path, FileMode.CreateNew, FileAccess.Write);
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
