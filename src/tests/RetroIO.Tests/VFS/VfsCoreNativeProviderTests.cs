//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Core.VFS;
using RetroIO.Storage.Providers;

namespace RetroIO.Tests.VFS;

/// <summary>
/// Regression: <see cref="VfsCore"/>'s direct methods must pass the provider the SUB-PATH, not the full
/// URN. Before the fix, <c>VfsCore</c> forwarded the whole <c>urn:retrofs:…</c> string as the provider's
/// path argument; a real provider (<see cref="NativeVolumeProvider"/>, which does
/// <c>Path.Combine(RootPath, path)</c>) then built a nonsense path and threw — the bug was masked only
/// because the in-memory mock provider ignores the path. These tests exercise a real native mount over
/// a temp directory, so they would have failed pre-fix.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class VfsCoreNativeProviderTests
{
    private string _tempDir = null!;
    private VfsCore _vfs = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "retrofs-vfscore-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "SUB"));
        File.WriteAllText(Path.Combine(_tempDir, "HELLO.TXT"), "hi there");

        _vfs = new VfsCore(enablePersistence: false);
        _vfs.RegisterProvider(new NativeVolumeProvider());
        _vfs.Mount("native", _tempDir, "vol0", readOnly: false);
    }

    [TearDown]
    public void TearDown()
    {
        _vfs?.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public void ReadDirectory_OverNativeMount_ListsRealEntries()
    {
        DirectoryEntry[] entries = _vfs.ReadDirectoryAsync("urn:retrofs:native:vol0:/").GetAwaiter().GetResult();

        string[] names = entries.Select(e => e.Name).ToArray();
        Assert.That(names, Does.Contain("HELLO.TXT"));
        Assert.That(names, Does.Contain("SUB"));
    }

    [Test]
    public void GetMetadata_OverNativeMount_ReturnsRealFileInfo()
    {
        FileMetadata meta = _vfs.GetMetadataAsync("urn:retrofs:native:vol0:/HELLO.TXT").GetAwaiter().GetResult();

        Assert.That(meta.IsDirectory, Is.False);
        Assert.That(meta.Size, Is.EqualTo(8)); // "hi there"
    }

    [Test]
    public void OpenRead_OverNativeMount_ReturnsRealBytes()
    {
        using Stream stream = _vfs.OpenReadAsync("urn:retrofs:native:vol0:/HELLO.TXT").GetAwaiter().GetResult();
        using MemoryStream ms = new MemoryStream();
        stream.CopyTo(ms);

        Assert.That(Encoding.ASCII.GetString(ms.ToArray()), Is.EqualTo("hi there"));
    }

    [Test]
    public void WriteThenRead_OverNativeMount_RoundTrips()
    {
        _vfs.WriteFileAsync("urn:retrofs:native:vol0:/NEW.TXT", Encoding.ASCII.GetBytes("written")).GetAwaiter().GetResult();

        Assert.That(File.Exists(Path.Combine(_tempDir, "NEW.TXT")), Is.True, "the file lands under the mount root");
        Assert.That(File.ReadAllText(Path.Combine(_tempDir, "NEW.TXT")), Is.EqualTo("written"));
    }
}
