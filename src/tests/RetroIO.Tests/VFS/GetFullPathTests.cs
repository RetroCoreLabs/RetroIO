//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Storage.Core.VFS;
using RetroIO.Storage.Providers;

namespace RetroIO.Tests.VFS;

/// <summary>
/// <see cref="IFileSystemBackend.GetFullPath(string)"/> says the result is normalized and resolved.
/// These tests run the same cases against both backends in the Providers project.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class GetFullPathTests
{
    private const string Root = "/econet-root";

    private string _cacheDir = null!;
    private VfsCore _vfs = null!;

    [SetUp]
    public void SetUp()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "retrofs-fullpath-" + Guid.NewGuid().ToString("N"));
        _vfs = new VfsCore(enablePersistence: false);
    }

    [TearDown]
    public void TearDown()
    {
        _vfs.Dispose();
        try { Directory.Delete(_cacheDir, recursive: true); } catch { /* best effort */ }
    }

    // 0 = single mount backend, 1 = composite backend.
    private IFileSystemBackend Make(int kind)
    {
        if (kind == 0)
            return new VfsFileSystemBackend(_vfs, "native", "vol0", Root, _cacheDir);

        return new CompositeVfsFileSystemBackend(Root, Array.Empty<CompositeVfsFileSystemBackend.DiscMount>());
    }

    [TestCase(0, "/econet-root/a\\b", "/econet-root/a/b")]
    [TestCase(0, "/econet-root//a///b/", "/econet-root/a/b")]
    [TestCase(0, "/econet-root/./a/../b", "/econet-root/b")]
    [TestCase(0, "/econet-root", "/econet-root")]
    [TestCase(0, "/econet-root/a/..", "/econet-root")]
    [TestCase(0, "/", "/")]
    [TestCase(0, "/a/..", "/")]
    [TestCase(0, "", "")]
    [TestCase(0, "a/b/../c", "a/c")]
    [TestCase(0, "C:\\x\\..\\y", "C:/y")]
    [TestCase(0, "C:\\", "C:/")]
    [TestCase(1, "/econet-root/a\\b", "/econet-root/a/b")]
    [TestCase(1, "/econet-root//a///b/", "/econet-root/a/b")]
    [TestCase(1, "/econet-root/./a/../b", "/econet-root/b")]
    [TestCase(1, "/econet-root", "/econet-root")]
    [TestCase(1, "/econet-root/a/..", "/econet-root")]
    [TestCase(1, "/", "/")]
    [TestCase(1, "/a/..", "/")]
    [TestCase(1, "", "")]
    [TestCase(1, "a/b/../c", "a/c")]
    [TestCase(1, "C:\\x\\..\\y", "C:/y")]
    [TestCase(1, "C:\\", "C:/")]
    public void GetFullPath_NormalizesAndResolves(int kind, string path, string expected)
    {
        Assert.That(Make(kind).GetFullPath(path), Is.EqualTo(expected));
    }

    [TestCase(0, "/..")]
    [TestCase(0, "/a/../../b")]
    [TestCase(0, "../a")]
    [TestCase(0, "C:/..")]
    [TestCase(0, "/econet-root/../x")]
    [TestCase(0, "/econet-root/a/../../x")]
    [TestCase(1, "/..")]
    [TestCase(1, "/a/../../b")]
    [TestCase(1, "../a")]
    [TestCase(1, "C:/..")]
    [TestCase(1, "/econet-root/../x")]
    [TestCase(1, "/econet-root/a/../../x")]
    public void GetFullPath_PathThatEscapesTheRoot_Throws(int kind, string path)
    {
        Assert.Throws<UnauthorizedAccessException>(() => Make(kind).GetFullPath(path));
    }

    [TestCase(0)]
    [TestCase(1)]
    public void GetFullPath_NullPath_Throws(int kind)
    {
        Assert.Throws<ArgumentNullException>(() => Make(kind).GetFullPath(null!));
    }
}
