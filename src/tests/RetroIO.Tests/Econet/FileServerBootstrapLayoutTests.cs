//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Protocols.Econet.FileServer;
using RetroIO.Tests.TNFS;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Tests that <see cref="FileServerBootstrap.EnsureLayout"/> builds the disc folders through the file
/// system backend it is given, not on the host disk.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class FileServerBootstrapLayoutTests
{
    [Test]
    public void EnsureLayout_CreatesTheFoldersInTheBackend()
    {
        // Regression: the folders were made with System.IO.Directory.CreateDirectory. With any backend
        // other than the local disk (the VFS, Azure, a test double) nothing was created where the file
        // server looks, and stray folders appeared on the host disk instead.
        MockFileSystemBackend backend = new MockFileSystemBackend();
        string root = "/econet-layout-test-" + Guid.NewGuid().ToString("N");

        FileServerBootstrap.EnsureLayout(backend, root, "RetroFS");

        Assert.That(backend.DirectoryExists(root), Is.True);
        Assert.That(backend.DirectoryExists(backend.CombinePath(root, "0RetroFS")), Is.True);
        Assert.That(backend.DirectoryExists(backend.CombinePath(root, "0RetroFS", "USER")), Is.True);
    }

    [Test]
    public void EnsureLayout_DoesNotTouchTheHostDisk()
    {
        MockFileSystemBackend backend = new MockFileSystemBackend();
        string root = "/econet-layout-test-" + Guid.NewGuid().ToString("N");

        FileServerBootstrap.EnsureLayout(backend, root, "RetroFS");

        Assert.That(Directory.Exists(root), Is.False, "the host disk must not get the folders");
    }
}
