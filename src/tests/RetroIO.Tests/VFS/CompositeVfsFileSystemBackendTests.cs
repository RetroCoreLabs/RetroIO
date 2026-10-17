//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using RetroIO.Storage.Core.VFS;
using RetroIO.Storage.Providers;

namespace RetroIO.Tests.VFS;

/// <summary>
/// Step 4 coverage for <see cref="CompositeVfsFileSystemBackend"/>: two discs backed by two independent
/// native VFS mounts, routed by disc-directory prefix. Proves the virtual root lists the disc dirs,
/// paths route to the right per-disc backend (cross-disc isolation), and read/write/mkdir reach the
/// correct mount.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class CompositeVfsFileSystemBackendTests
{
    private const string Root = "/econet-root";

    private string _disc0Dir = null!;
    private string _disc1Dir = null!;
    private string _cacheDir = null!;
    private VfsCore _vfs = null!;
    private CompositeVfsFileSystemBackend _composite = null!;

    [SetUp]
    public void SetUp()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "retrofs-composite-" + Guid.NewGuid().ToString("N"));
        _disc0Dir = Path.Combine(baseDir, "storage0");
        _disc1Dir = Path.Combine(baseDir, "storage1");
        _cacheDir = Path.Combine(baseDir, "cache");
        Directory.CreateDirectory(_disc0Dir);
        Directory.CreateDirectory(_disc1Dir);
        File.WriteAllText(Path.Combine(_disc0Dir, "HELLO"), "disc0");
        File.WriteAllText(Path.Combine(_disc1Dir, "WORLD"), "disc1");

        _vfs = new VfsCore(enablePersistence: false);
        _vfs.RegisterProvider(new NativeVolumeProvider());
        _vfs.Mount("native", _disc0Dir, "vol0", readOnly: false);
        _vfs.Mount("native", _disc1Dir, "vol1", readOnly: false);

        // Each disc's sub-backend is rooted at that disc's on-root path; its mount root is the temp dir.
        VfsFileSystemBackend b0 = new VfsFileSystemBackend(_vfs, "native", "vol0", Root + "/0AAA", _cacheDir);
        VfsFileSystemBackend b1 = new VfsFileSystemBackend(_vfs, "native", "vol1", Root + "/1BBB", _cacheDir);

        _composite = new CompositeVfsFileSystemBackend(Root, new[]
        {
            new CompositeVfsFileSystemBackend.DiscMount("0AAA", b0),
            new CompositeVfsFileSystemBackend.DiscMount("1BBB", b1),
        });
    }

    [TearDown]
    public void TearDown()
    {
        _vfs?.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(_disc0Dir)!, recursive: true); } catch { }
    }

    [Test]
    public void Root_EnumeratesDiscDirectories()
    {
        Assert.That(_composite.DirectoryExists(Root), Is.True);
        Assert.That(_composite.IsDirectory(Root), Is.True);

        string[] leaves = _composite.EnumerateFileSystemEntries(Root)
            .Select(e => _composite.GetFileName(e))
            .ToArray();

        Assert.That(leaves, Is.EquivalentTo(new[] { "0AAA", "1BBB" }));
    }

    [Test]
    public void Routes_ToCorrectDisc_WithIsolation()
    {
        Assert.That(_composite.FileExists(Root + "/0AAA/HELLO"), Is.True);
        Assert.That(_composite.FileExists(Root + "/1BBB/WORLD"), Is.True);

        // A file on disc 0 is NOT visible on disc 1 and vice versa.
        Assert.That(_composite.FileExists(Root + "/0AAA/WORLD"), Is.False);
        Assert.That(_composite.FileExists(Root + "/1BBB/HELLO"), Is.False);
    }

    [Test]
    public void DiscRoot_IsADirectory()
    {
        Assert.That(_composite.DirectoryExists(Root + "/0AAA"), Is.True);
        Assert.That(_composite.IsDirectory(Root + "/1BBB"), Is.True);
    }

    [Test]
    public void OpenFile_Read_RoutesToDisc()
    {
        using FileStream s = _composite.OpenFile(Root + "/1BBB/WORLD", FileMode.Open, FileAccess.Read);
        using MemoryStream ms = new MemoryStream();
        s.CopyTo(ms);

        Assert.That(Encoding.ASCII.GetString(ms.ToArray()), Is.EqualTo("disc1"));
    }

    [Test]
    public void OpenFile_Write_RoutesToCorrectMount()
    {
        using (FileStream s = _composite.OpenFile(Root + "/0AAA/NEW", FileMode.Create, FileAccess.Write))
        {
            byte[] b = Encoding.ASCII.GetBytes("added");
            s.Write(b, 0, b.Length);
        }

        Assert.That(File.Exists(Path.Combine(_disc0Dir, "NEW")), Is.True, "landed on disc 0's mount storage");
        Assert.That(File.Exists(Path.Combine(_disc1Dir, "NEW")), Is.False, "not on disc 1");
    }

    [Test]
    public void CreateDirectory_RoutesToDisc()
    {
        _composite.CreateDirectory(Root + "/1BBB/SUB");

        Assert.That(Directory.Exists(Path.Combine(_disc1Dir, "SUB")), Is.True);
    }

    [Test]
    public void OpenFile_AtRoot_Throws()
    {
        Assert.Throws<UnauthorizedAccessException>(() => _composite.OpenFile(Root, FileMode.Open, FileAccess.Read));
    }
}
