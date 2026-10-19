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
using RetroIO.Server.Core.FileSystem;
using RetroIO.Storage.Core.VFS;
using RetroIO.Storage.Providers;

namespace RetroIO.Tests.VFS;

/// <summary>
/// Step 1 coverage for <see cref="VfsFileSystemBackend"/>: the read-only surface over a real
/// <c>native</c> VFS mount (a temp directory). Proves the host-path→URN mapping and the materialize
/// path work, and that the not-yet-implemented write/mkdir operations fail honestly.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class VfsFileSystemBackendTests
{
    private string _tempDir = null!;
    private string _cacheDir = null!;
    private VfsCore _vfs = null!;
    private VfsFileSystemBackend _backend = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "retrofs-vfsbackend-" + Guid.NewGuid().ToString("N"));
        _cacheDir = Path.Combine(_tempDir, "_cache");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "SUB"));
        File.WriteAllText(Path.Combine(_tempDir, "HELLO.TXT"), "hi there"); // 8 bytes

        _vfs = new VfsCore(enablePersistence: false);
        _vfs.RegisterProvider(new NativeVolumeProvider());
        _vfs.Mount("native", _tempDir, "vol0", readOnly: false);

        _backend = new VfsFileSystemBackend(_vfs, "native", "vol0", hostRoot: _tempDir, tempCacheDir: _cacheDir);
    }

    [TearDown]
    public void TearDown()
    {
        _vfs?.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private string P(params string[] parts) => Path.Combine(new[] { _tempDir }.Concat(parts).ToArray());

    [Test]
    public void DirectoryExists_ReflectsReality()
    {
        Assert.That(_backend.DirectoryExists(_tempDir), Is.True, "the mount root is a directory");
        Assert.That(_backend.DirectoryExists(P("SUB")), Is.True);
        Assert.That(_backend.DirectoryExists(P("NOPE")), Is.False);
        Assert.That(_backend.DirectoryExists(P("HELLO.TXT")), Is.False, "a file is not a directory");
    }

    [Test]
    public void FileExists_ReflectsReality()
    {
        Assert.That(_backend.FileExists(P("HELLO.TXT")), Is.True);
        Assert.That(_backend.FileExists(P("SUB")), Is.False, "a directory is not a file");
        Assert.That(_backend.FileExists(P("NOPE.TXT")), Is.False);
    }

    [Test]
    public void IsDirectory_ReflectsReality()
    {
        Assert.That(_backend.IsDirectory(P("SUB")), Is.True);
        Assert.That(_backend.IsDirectory(P("HELLO.TXT")), Is.False);
    }

    [Test]
    public void EnumerateFileSystemEntries_ReturnsChildren()
    {
        string[] leaves = _backend.EnumerateFileSystemEntries(_tempDir)
            .Select(e => _backend.GetFileName(e))
            .ToArray();

        Assert.That(leaves, Does.Contain("HELLO.TXT"));
        Assert.That(leaves, Does.Contain("SUB"));
    }

    [Test]
    public void GetFileInfo_CarriesRealLengthAndDate()
    {
        BackendFileInfo? info = _backend.GetFileInfo(P("HELLO.TXT"));

        Assert.That(info, Is.Not.Null);
        Assert.That(info!.Length, Is.EqualTo(8));
        Assert.That(info.LastWriteTimeUtc, Is.Not.Null);
        Assert.That(info.LastWriteTimeUtc!.Value, Is.EqualTo(File.GetLastWriteTimeUtc(P("HELLO.TXT"))).Within(TimeSpan.FromSeconds(2)));
        Assert.That(_backend.GetFileInfo(P("NOPE.TXT")), Is.Null, "missing file -> null");
    }

    [Test]
    public void GetFileInfo_Directory_ReturnsNull()
    {
        Assert.That(_backend.GetFileInfo(P("SUB")), Is.Null, "a directory is not a file");
    }

    [Test]
    public void GetFileInfo_DoesNotLeaveFilesInTheTempCache()
    {
        _backend.GetFileInfo(P("HELLO.TXT"));

        Assert.That(Directory.GetFiles(_cacheDir), Is.Empty);
    }

    [Test]
    public void GetFileInfo_TimesThatTheVfsDoesNotKnow_AreNull()
    {
        BackendFileInfo? info = _backend.GetFileInfo(P("HELLO.TXT"));

        Assert.That(info!.CreationTimeUtc, Is.Null);
        Assert.That(info.LastAccessTimeUtc, Is.Null);
    }

    [Test]
    public void OpenFile_Read_ReturnsRealBytes()
    {
        using FileStream stream = _backend.OpenFile(P("HELLO.TXT"), FileMode.Open, FileAccess.Read);
        using MemoryStream ms = new MemoryStream();
        stream.CopyTo(ms);

        Assert.That(Encoding.ASCII.GetString(ms.ToArray()), Is.EqualTo("hi there"));
    }

    [Test]
    public void OpenFile_Write_Create_WritesBackToMount()
    {
        using (FileStream s = _backend.OpenFile(P("NEW.TXT"), FileMode.Create, FileAccess.Write))
        {
            byte[] b = Encoding.ASCII.GetBytes("written");
            s.Write(b, 0, b.Length);
        } // close triggers write-back

        // Landed on the real mount storage...
        Assert.That(File.Exists(P("NEW.TXT")), Is.True);
        Assert.That(File.ReadAllText(P("NEW.TXT")), Is.EqualTo("written"));

        // ...and reads back through the adapter.
        using FileStream r = _backend.OpenFile(P("NEW.TXT"), FileMode.Open, FileAccess.Read);
        using MemoryStream ms = new MemoryStream();
        r.CopyTo(ms);
        Assert.That(Encoding.ASCII.GetString(ms.ToArray()), Is.EqualTo("written"));
    }

    [Test]
    public void OpenFile_Write_Create_OverwritesExisting()
    {
        using (FileStream s = _backend.OpenFile(P("HELLO.TXT"), FileMode.Create, FileAccess.Write))
        {
            byte[] b = Encoding.ASCII.GetBytes("new");
            s.Write(b, 0, b.Length);
        }

        Assert.That(File.ReadAllText(P("HELLO.TXT")), Is.EqualTo("new"));
    }

    [Test]
    public void OpenFile_Append_ConcatenatesToExisting()
    {
        File.WriteAllText(P("APP.TXT"), "abc");

        using (FileStream s = _backend.OpenFile(P("APP.TXT"), FileMode.Append, FileAccess.Write))
        {
            byte[] b = Encoding.ASCII.GetBytes("def");
            s.Write(b, 0, b.Length);
        }

        Assert.That(File.ReadAllText(P("APP.TXT")), Is.EqualTo("abcdef"));
    }

    [Test]
    public void CreateDirectory_CreatesRealDirectory()
    {
        _backend.CreateDirectory(P("NEWDIR"));

        Assert.That(Directory.Exists(P("NEWDIR")), Is.True, "created on the real mount storage");
        Assert.That(_backend.DirectoryExists(P("NEWDIR")), Is.True, "and visible through the adapter");
    }

    [Test]
    public void CreateDirectory_CreatesMissingParents()
    {
        _backend.CreateDirectory(P("A", "B", "C"));

        Assert.That(Directory.Exists(P("A", "B", "C")), Is.True);
    }

    // ---- read-only mount: fail-fast writes (Step 6 hardening) ----------------------------------

    private VfsFileSystemBackend ReadOnlyBackend() =>
        new VfsFileSystemBackend(_vfs, "native", "vol0", hostRoot: _tempDir, tempCacheDir: _cacheDir, readOnly: true);

    [Test]
    public void ReadOnly_OpenFileForWrite_ThrowsBeforeMaterializing()
    {
        VfsFileSystemBackend ro = ReadOnlyBackend();

        Assert.Throws<UnauthorizedAccessException>(
            () => ro.OpenFile(P("NEW.TXT"), FileMode.Create, FileAccess.Write));

        // Nothing was written to the mount, and no temp leaked into the cache.
        Assert.That(File.Exists(P("NEW.TXT")), Is.False, "read-only mount must not create the file");
        Assert.That(Directory.GetFiles(_cacheDir).Length, Is.EqualTo(0), "no temp materialized on a rejected write");
    }

    [Test]
    public void ReadOnly_CreateDirectory_Throws()
    {
        VfsFileSystemBackend ro = ReadOnlyBackend();

        Assert.Throws<UnauthorizedAccessException>(() => ro.CreateDirectory(P("NODIR")));
        Assert.That(Directory.Exists(P("NODIR")), Is.False);
    }

    [Test]
    public void ReadOnly_ReadsStillWork()
    {
        VfsFileSystemBackend ro = ReadOnlyBackend();

        Assert.That(ro.FileExists(P("HELLO.TXT")), Is.True);
        Assert.That(ro.DirectoryExists(P("SUB")), Is.True);

        using FileStream r = ro.OpenFile(P("HELLO.TXT"), FileMode.Open, FileAccess.Read);
        using MemoryStream ms = new MemoryStream();
        r.CopyTo(ms);
        Assert.That(Encoding.ASCII.GetString(ms.ToArray()), Is.EqualTo("hi there"), "reads are unaffected by read-only");
    }
}
