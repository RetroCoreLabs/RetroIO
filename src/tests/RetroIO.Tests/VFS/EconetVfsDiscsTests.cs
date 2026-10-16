//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Core.VFS;
using RetroIO.Storage.Providers;
using RetroIO.Server.Hosting;
using RetroIO.Server.Hosting.Configuration;

namespace RetroIO.Tests.VFS;

/// <summary>
/// Step 4b: <see cref="EconetVfsDiscs.BuildComposite"/> turns an econet-config <c>Discs[]</c> into a
/// working <see cref="CompositeVfsFileSystemBackend"/> — one native VFS mount per disc, routed by the
/// synthesised disc-directory name (<c>Index + AcornName</c>).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class EconetVfsDiscsTests
{
    private const string Root = "/econet-root";

    private string _baseDir = null!;
    private string _disc0Dir = null!;
    private string _disc1Dir = null!;
    private VfsCore? _vfs;

    [SetUp]
    public void SetUp()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "retrofs-vfsdiscs-" + Guid.NewGuid().ToString("N"));
        _disc0Dir = Path.Combine(_baseDir, "s0");
        _disc1Dir = Path.Combine(_baseDir, "s1");
        Directory.CreateDirectory(_disc0Dir);
        Directory.CreateDirectory(_disc1Dir);
        File.WriteAllText(Path.Combine(_disc0Dir, "F0"), "c0");
        File.WriteAllText(Path.Combine(_disc1Dir, "F1"), "c1");
    }

    [TearDown]
    public void TearDown()
    {
        _vfs?.Dispose();
        try { Directory.Delete(_baseDir, recursive: true); } catch { }
    }

    private EconetServerConfiguration ConfigWithTwoDiscs() => new EconetServerConfiguration
    {
        RootDirectory = Root,
        Discs = new List<EconetDiscConfiguration>
        {
            new EconetDiscConfiguration { Index = 0, AcornName = "AAA", Provider = "native", Uri = _disc0Dir },
            new EconetDiscConfiguration { Index = 1, AcornName = "BBB", Provider = "native", Uri = _disc1Dir },
        },
    };

    [Test]
    public void BuildComposite_TwoNativeDiscs_RoutesByDiscDir()
    {
        (CompositeVfsFileSystemBackend composite, VfsCore vfs) =
            EconetVfsDiscs.BuildComposite(ConfigWithTwoDiscs(), Path.Combine(_baseDir, "cache"));
        _vfs = vfs;

        // Virtual root lists the synthesised disc dirs (Index+AcornName).
        string[] leaves = composite.EnumerateFileSystemEntries(Root)
            .Select(e => composite.GetFileName(e))
            .ToArray();
        Assert.That(leaves, Is.EquivalentTo(new[] { "0AAA", "1BBB" }));

        // Each disc's file is reachable, and NOT visible on the other disc.
        Assert.That(composite.FileExists(Root + "/0AAA/F0"), Is.True);
        Assert.That(composite.FileExists(Root + "/1BBB/F1"), Is.True);
        Assert.That(composite.FileExists(Root + "/0AAA/F1"), Is.False);
        Assert.That(composite.FileExists(Root + "/1BBB/F0"), Is.False);
    }

    [Test]
    public void BuildComposite_ReadOnlyDisc_RejectsWrites_WritableDiscAllows()
    {
        // Disc 0 (0AAA) read-only, disc 1 (1BBB) writable.
        EconetServerConfiguration config = new EconetServerConfiguration
        {
            RootDirectory = Root,
            Discs = new List<EconetDiscConfiguration>
            {
                new EconetDiscConfiguration { Index = 0, AcornName = "AAA", Provider = "native", Uri = _disc0Dir, ReadOnly = true },
                new EconetDiscConfiguration { Index = 1, AcornName = "BBB", Provider = "native", Uri = _disc1Dir, ReadOnly = false },
            },
        };

        (CompositeVfsFileSystemBackend composite, VfsCore vfs) =
            EconetVfsDiscs.BuildComposite(config, Path.Combine(_baseDir, "cache"));
        _vfs = vfs;

        // Read-only disc rejects a write up front...
        Assert.Throws<UnauthorizedAccessException>(
            () => composite.OpenFile(Root + "/0AAA/NEW.TXT", FileMode.Create, FileAccess.Write));
        Assert.That(File.Exists(Path.Combine(_disc0Dir, "NEW.TXT")), Is.False);
        Assert.Throws<UnauthorizedAccessException>(() => composite.CreateDirectory(Root + "/0AAA/NODIR"));

        // ...while reads on that same disc still work.
        Assert.That(composite.FileExists(Root + "/0AAA/F0"), Is.True);

        // The writable disc accepts a write.
        using (FileStream s = composite.OpenFile(Root + "/1BBB/NEW.TXT", FileMode.Create, FileAccess.Write))
        {
            byte[] b = System.Text.Encoding.ASCII.GetBytes("ok");
            s.Write(b, 0, b.Length);
        }
        Assert.That(File.ReadAllText(Path.Combine(_disc1Dir, "NEW.TXT")), Is.EqualTo("ok"));
    }

    [Test]
    public void BuildComposite_MountRef_ResolvesFromRegistry()
    {
        // The registry (mounts.json) holds a native mount 'my-native' pointing at disc0; the disc
        // references it by MountRef instead of repeating the provider/uri inline.
        List<UnifiedMountEntry> registry = new List<UnifiedMountEntry>
        {
            new UnifiedMountEntry { VolumeId = "my-native", ProviderType = "native", SourceUri = _disc0Dir },
        };
        EconetServerConfiguration config = new EconetServerConfiguration
        {
            RootDirectory = Root,
            Discs = new List<EconetDiscConfiguration>
            {
                new EconetDiscConfiguration { Index = 0, AcornName = "AAA", MountRef = "my-native" },
            },
        };

        (CompositeVfsFileSystemBackend composite, VfsCore vfs) =
            EconetVfsDiscs.BuildComposite(config, Path.Combine(_baseDir, "cache"), registry);
        _vfs = vfs;

        // The referenced native mount is served as disc 0AAA — its file is reachable.
        Assert.That(composite.FileExists(Root + "/0AAA/F0"), Is.True);
    }

    [Test]
    public void BuildComposite_MountRef_ReadOnlyRegistryMount_StaysReadOnly()
    {
        // Registry mount is read-only; the disc does NOT ask for read-only, but it must stay read-only
        // (the disc can only ADD restriction, never remove it).
        List<UnifiedMountEntry> registry = new List<UnifiedMountEntry>
        {
            new UnifiedMountEntry { VolumeId = "ro-native", ProviderType = "native", SourceUri = _disc0Dir, IsReadOnly = true },
        };
        EconetServerConfiguration config = new EconetServerConfiguration
        {
            RootDirectory = Root,
            Discs = new List<EconetDiscConfiguration>
            {
                new EconetDiscConfiguration { Index = 0, AcornName = "AAA", MountRef = "ro-native", ReadOnly = false },
            },
        };

        (CompositeVfsFileSystemBackend composite, VfsCore vfs) =
            EconetVfsDiscs.BuildComposite(config, Path.Combine(_baseDir, "cache"), registry);
        _vfs = vfs;

        Assert.Throws<UnauthorizedAccessException>(
            () => composite.OpenFile(Root + "/0AAA/NEW.TXT", FileMode.Create, FileAccess.Write));
    }

    [Test]
    public void BuildComposite_MountRef_NotInRegistry_Throws()
    {
        List<UnifiedMountEntry> registry = new List<UnifiedMountEntry>
        {
            new UnifiedMountEntry { VolumeId = "other", ProviderType = "native", SourceUri = _disc0Dir },
        };
        EconetServerConfiguration config = new EconetServerConfiguration
        {
            RootDirectory = Root,
            Discs = new List<EconetDiscConfiguration>
            {
                new EconetDiscConfiguration { Index = 0, AcornName = "AAA", MountRef = "missing" },
            },
        };

        Assert.Throws<InvalidOperationException>(
            () => EconetVfsDiscs.BuildComposite(config, Path.Combine(_baseDir, "cache"), registry));
    }

    [Test]
    public void BuildComposite_MountRef_WithInlineProvider_Throws()
    {
        // MountRef and inline Provider/Uri are mutually exclusive.
        List<UnifiedMountEntry> registry = new List<UnifiedMountEntry>
        {
            new UnifiedMountEntry { VolumeId = "my-native", ProviderType = "native", SourceUri = _disc0Dir },
        };
        EconetServerConfiguration config = new EconetServerConfiguration
        {
            RootDirectory = Root,
            Discs = new List<EconetDiscConfiguration>
            {
                new EconetDiscConfiguration { Index = 0, AcornName = "AAA", MountRef = "my-native", Provider = "native", Uri = _disc1Dir },
            },
        };

        Assert.Throws<InvalidOperationException>(
            () => EconetVfsDiscs.BuildComposite(config, Path.Combine(_baseDir, "cache"), registry));
    }

    [Test]
    public void BuildComposite_MountRef_NoRegistry_Throws()
    {
        // A MountRef disc with no registry available (null) fails loudly rather than silently vanishing.
        EconetServerConfiguration config = new EconetServerConfiguration
        {
            RootDirectory = Root,
            Discs = new List<EconetDiscConfiguration>
            {
                new EconetDiscConfiguration { Index = 0, AcornName = "AAA", MountRef = "some-mount" },
            },
        };

        Assert.Throws<InvalidOperationException>(
            () => EconetVfsDiscs.BuildComposite(config, Path.Combine(_baseDir, "cache")));
    }

    [Test]
    public void BuildComposite_MountRef_CredentialsCarried_ThrowsNotSupported()
    {
        // Credentials on a referenced mount are not propagated yet — fail loud, don't auth as anonymous.
        List<UnifiedMountEntry> registry = new List<UnifiedMountEntry>
        {
            new UnifiedMountEntry { VolumeId = "cred-native", ProviderType = "native", SourceUri = _disc0Dir, Username = "u", Password = "p" },
        };
        EconetServerConfiguration config = new EconetServerConfiguration
        {
            RootDirectory = Root,
            Discs = new List<EconetDiscConfiguration>
            {
                new EconetDiscConfiguration { Index = 0, AcornName = "AAA", MountRef = "cred-native" },
            },
        };

        Assert.Throws<NotSupportedException>(
            () => EconetVfsDiscs.BuildComposite(config, Path.Combine(_baseDir, "cache"), registry));
    }

    [Test]
    public void BuildComposite_NonNativeProvider_NotSupportedYet()
    {
        EconetServerConfiguration config = new EconetServerConfiguration
        {
            RootDirectory = Root,
            Discs = new List<EconetDiscConfiguration>
            {
                new EconetDiscConfiguration { Index = 0, AcornName = "CLOUD", Provider = "azureblob", Uri = "azureblob://x" },
            },
        };

        Assert.Throws<NotSupportedException>(() => EconetVfsDiscs.BuildComposite(config, Path.Combine(_baseDir, "cache")));
    }
}
