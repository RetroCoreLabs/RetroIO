//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.FileServer;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Tests.TNFS;

namespace RetroIO.Tests.Econet;

[TestFixture]
[Category("Unit")]
public sealed class EconetSampleCatalogTests
{
    [Test]
    public void Seed_IncludesThreeFilesWithContent()
    {
        MockFileSystemBackend fileSystem = new MockFileSystemBackend();
        EconetSampleCatalogFixture.Seed(fileSystem);

        AssertSampleFiles(fileSystem, EconetSampleCatalogFixture.UserDirectory);
    }

    [Test]
    public void Bootstrap_OnEmptyDisc_CreatesSampleFilesWithContent()
    {
        string root = Path.Combine(Path.GetTempPath(), "retrofs-econet-" + Guid.NewGuid().ToString("N"));
        LocalFileSystemBackend fileSystem = new LocalFileSystemBackend();
        try
        {
            FileServerBootstrap.EnsureLayout(fileSystem, root, "RetroFS");
            string userDirectory = fileSystem.CombinePath(root, "0RetroFS", "USER");
            AssertSampleFiles(fileSystem, userDirectory);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Cat_AfterLogin_ListsAllSampleFilesWithNonZeroSize()
    {
        EconetFsTestHarness harness = new EconetFsTestHarness();
        DnfsClientSimulator client = new DnfsClientSimulator(harness);
        client.OsCli("I AM SYST \r");

        DnfsCatalogueResult cat = client.Cat();
        Assert.That(cat.Succeeded, Is.True);
        Assert.That(cat.FileNames, Is.EquivalentTo(EconetSampleCatalog.DefaultFileNames));
        Assert.That(cat.Examine.Payload[2], Is.EqualTo(3));

        for (int i = 0; i < 3; i++)
        {
            int offset = 4 + (i * 27);
            long size = cat.Examine.Payload[offset + 24]
                | (cat.Examine.Payload[offset + 25] << 8)
                | (cat.Examine.Payload[offset + 26] << 16);
            Assert.That(size, Is.GreaterThan(0));
        }
    }

    private static void AssertSampleFiles(IFileSystemBackend fileSystem, string userDirectory)
    {
        for (int i = 0; i < EconetSampleCatalog.GetSampleTexts().Count; i++)
        {
            (string name, string expectedContent) = EconetSampleCatalog.GetSampleTexts()[i];
            string path = fileSystem.CombinePath(userDirectory, name);
            Assert.That(fileSystem.FileExists(path), Is.True, name);

            using FileStream stream = fileSystem.OpenFile(path, FileMode.Open, FileAccess.Read);
            using MemoryStream buffer = new MemoryStream();
            stream.CopyTo(buffer);
            byte[] bytes = buffer.ToArray();

            Assert.That(bytes.Length, Is.EqualTo(Encoding.ASCII.GetByteCount(expectedContent)), name);
            Assert.That(Encoding.ASCII.GetString(bytes), Is.EqualTo(expectedContent), name);
        }
    }
}
