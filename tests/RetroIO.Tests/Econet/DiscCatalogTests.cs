//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.FileServer.Discs;
using RetroIO.Tests.TNFS;

namespace RetroIO.Tests.Econet;

[TestFixture]
[Category("Unit")]
public sealed class DiscCatalogTests
{
    [Test]
    public void TryFind_ByHexIndex_ReturnsDisc()
    {
        MockFileSystemBackend fileSystem = new MockFileSystemBackend();
        fileSystem.AddDirectory("/root");
        fileSystem.AddDirectory("/root/0RetroFS");
        fileSystem.AddDirectory("/root/1STORAGE");

        DiscCatalog catalog = new DiscCatalog(fileSystem, "/root", "RetroFS");

        Assert.That(catalog.TryFind("0", out DiscEntry? disc0), Is.True);
        Assert.That(disc0!.AcornName, Is.EqualTo("RetroFS"));

        Assert.That(catalog.TryFind("1", out DiscEntry? disc1), Is.True);
        Assert.That(disc1!.AcornName, Is.EqualTo("STORAGE"));
    }

    [Test]
    public void TryFind_ByName_IsCaseInsensitive()
    {
        MockFileSystemBackend fileSystem = new MockFileSystemBackend();
        fileSystem.AddDirectory("/root");
        fileSystem.AddDirectory("/root/0RetroFS");

        DiscCatalog catalog = new DiscCatalog(fileSystem, "/root", "RetroFS");

        Assert.That(catalog.TryFind("retrofs", out DiscEntry? disc), Is.True);
        Assert.That(disc!.Index, Is.EqualTo(0));
    }

    [Test]
    public void IsDiscVisible_RespectsBitmask()
    {
        Assert.That(DiscCatalog.IsDiscVisible(0x0001, 0), Is.True);
        Assert.That(DiscCatalog.IsDiscVisible(0x0001, 1), Is.False);
        Assert.That(DiscCatalog.IsDiscVisible(0xFFFF, 15), Is.True);
    }
}
