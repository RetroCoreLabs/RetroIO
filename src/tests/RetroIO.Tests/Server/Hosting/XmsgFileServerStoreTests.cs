//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;

using NUnit.Framework;

using RetroIO.Protocols.Xmsg.Ndfs;
using RetroIO.Server.Hosting;
using RetroIO.Server.Hosting.Configuration;
using RetroIO.Storage.Core;
using RetroIO.Storage.Core.VFS;

namespace RetroIO.Tests.Server.Hosting;

/// <summary>
/// The XMSG file server's store is built from the node settings: off, a local folder, a mounts.json
/// volume, and the settings that cannot be used.
/// </summary>
[TestFixture]
public class XmsgFileServerStoreTests
{
    private string _folder = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "xmsg-fa-store-test-" + Guid.NewGuid().ToString("N"));
        // One folder per SINTRAN user under the served root.
        Directory.CreateDirectory(Path.Combine(_folder, "files", "SYSTEM"));
        File.WriteAllText(Path.Combine(_folder, "files", "SYSTEM", "HELLO.SYMB"), "hello");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, true);
    }

    [Test]
    public void Off_GivesNoStore()
    {
        FaUserFolders? store = XmsgFileServerStore.Build(new XmsgNodeServerConfiguration(), _folder, null, out VfsCore? vfs);
        Assert.That(store, Is.Null);
        Assert.That(vfs, Is.Null);
    }

    [Test]
    public void LocalFolder_IsServedStraightFromDisk()
    {
        XmsgNodeServerConfiguration config = new XmsgNodeServerConfiguration
        {
            FileServerEnabled = true,
            FileServerProvider = "native",
            FileServerUri = Path.Combine(_folder, "files"),
        };

        FaUserFolders? store = XmsgFileServerStore.Build(config, Path.Combine(_folder, "cache"), null, out VfsCore? vfs);

        Assert.That(vfs, Is.Null, "a plain folder needs no storage layer");
        Assert.That(store, Is.Not.Null);
        Assert.That(store!.ForUser("SYSTEM")!.ListFiles().Count, Is.EqualTo(1));
    }

    [Test]
    public void ReadOnlyMount_GoesThroughTheStorageLayer()
    {
        List<UnifiedMountEntry> registry = new List<UnifiedMountEntry>
        {
            new UnifiedMountEntry
            {
                VolumeId = "cosmos-volume",
                ProviderType = "native",
                SourceUri = Path.Combine(_folder, "files"),
                IsReadOnly = true,
            },
        };
        XmsgNodeServerConfiguration config = new XmsgNodeServerConfiguration
        {
            FileServerEnabled = true,
            FileServerMountRef = "cosmos-volume",
        };

        FaUserFolders? store = XmsgFileServerStore.Build(config, Path.Combine(_folder, "cache"), registry, out VfsCore? vfs);
        try
        {
            Assert.That(vfs, Is.Not.Null, "a read-only mount is served through the storage layer");
            Assert.That(store, Is.Not.Null);
            Assert.That(store!.ForUser("SYSTEM")!.ListFiles().Count, Is.EqualTo(1));
            Assert.That(store.Location, Does.StartWith("native:"));
        }
        finally
        {
            vfs?.Dispose();
        }
    }

    [Test]
    public void UnusableSettings_AreRefused_WithTheirReason()
    {
        XmsgNodeServerConfiguration neither = new XmsgNodeServerConfiguration { FileServerEnabled = true };
        Assert.Throws<InvalidOperationException>(() => XmsgFileServerStore.Build(neither, _folder, null, out _));

        XmsgNodeServerConfiguration unknownMount = new XmsgNodeServerConfiguration
        {
            FileServerEnabled = true,
            FileServerMountRef = "nowhere",
        };
        Assert.Throws<InvalidOperationException>(
            () => XmsgFileServerStore.Build(unknownMount, _folder, new List<UnifiedMountEntry>(), out _));

        XmsgNodeServerConfiguration ftp = new XmsgNodeServerConfiguration
        {
            FileServerEnabled = true,
            FileServerProvider = "ftp",
            FileServerUri = "ftp://host/",
        };
        Assert.Throws<NotSupportedException>(() => XmsgFileServerStore.Build(ftp, _folder, null, out _));
    }
}
