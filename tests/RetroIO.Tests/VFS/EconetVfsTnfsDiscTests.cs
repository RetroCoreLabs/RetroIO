//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Storage.Core.VFS;
using RetroIO.Protocols.Tnfs.Server;
using RetroIO.Protocols.Tnfs.Server.Handlers;
using RetroIO.Protocols.Tnfs.Server.Transport;
using RetroIO.Storage.Providers;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Server.Hosting;
using RetroIO.Server.Hosting.Configuration;

namespace RetroIO.Tests.VFS;

/// <summary>
/// Integration test for a <b>tnfs-backed Econet disc</b>: <see cref="EconetVfsDiscs.BuildComposite"/>
/// mounts a <c>Provider="tnfs"</c> disc against a real in-process TNFS server (TCP) and serves it over
/// the Econet composite backend. Proves the tnfs provider wiring end-to-end (mount → read) — a BBC
/// would <c>*SDISC</c> to this disc and read files off the TNFS server.
/// </summary>
/// <remarks>
/// Uses a real TCP socket to a self-hosted TNFS server (the same pattern as
/// <c>TnfsClientTcpIntegrationTests</c>), because <see cref="TnfsVolumeProvider"/>'s client connects
/// over a real socket — there is no in-memory shortcut. Categorised Integration so it can be filtered
/// out of a fast unit-only run.
/// </remarks>
[TestFixture]
[Category("Integration")]
[Category("TnfsDisc")]
public sealed class EconetVfsTnfsDiscTests
{
    private const int TcpPort = 16690; // distinct from the other TNFS test ports (16667/16668)
    private const string Root = "/econet-root";

    private IHost _host = null!;
    private string _tnfsRoot = null!;
    private string _cacheDir = null!;
    private VfsCore? _vfs;

    [OneTimeSetUp]
    public async Task StartTnfsServer()
    {
        _tnfsRoot = Path.Combine(Path.GetTempPath(), "econet-tnfsdisc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tnfsRoot);
        Directory.CreateDirectory(Path.Combine(_tnfsRoot, "SUB"));
        File.WriteAllText(Path.Combine(_tnfsRoot, "HELLO.TXT"), "over tnfs");

        // Self-hosted TNFS server over TCP, serving _tnfsRoot (mirrors TnfsClientTcpIntegrationTests).
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<IFileSystemBackend, LocalFileSystemBackend>();

                services.AddSingleton<SessionManager>(sp =>
                    new SessionManager(
                        sp.GetRequiredService<ILogger<SessionManager>>(),
                        TimeSpan.FromMinutes(10),
                        maxSessionsPerIP: 50));

                services.AddSingleton<SessionCommandHandler>(sp =>
                    new SessionCommandHandler(
                        sp.GetRequiredService<SessionManager>(),
                        sp.GetRequiredService<ILogger<SessionCommandHandler>>(),
                        _tnfsRoot,
                        sp.GetRequiredService<IFileSystemBackend>()));

                services.AddSingleton<DirectoryCommandHandler>(sp =>
                    new DirectoryCommandHandler(
                        sp.GetRequiredService<SessionManager>(),
                        sp.GetRequiredService<ILogger<DirectoryCommandHandler>>(),
                        sp.GetRequiredService<IFileSystemBackend>()));

                services.AddSingleton<FileCommandHandler>(sp =>
                    new FileCommandHandler(
                        sp.GetRequiredService<SessionManager>(),
                        sp.GetRequiredService<ILogger<FileCommandHandler>>(),
                        sp.GetRequiredService<IFileSystemBackend>()));

                services.AddSingleton<ITransport>(sp =>
                    new TcpTransport(TcpPort, sp.GetRequiredService<ILogger<TcpTransport>>()));

                services.AddHostedService<TnfsServerService>();
                services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            })
            .Build();

        await _host.StartAsync();
        await Task.Delay(500); // give the listener time to bind
    }

    [OneTimeTearDown]
    public async Task StopTnfsServer()
    {
        _vfs?.Dispose();
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        try { Directory.Delete(_tnfsRoot, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_cacheDir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public void TnfsDisc_ServedOverComposite_ReadsThroughTnfs()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "econet-tnfsdisc-cache-" + Guid.NewGuid().ToString("N"));

        EconetServerConfiguration config = new EconetServerConfiguration
        {
            RootDirectory = Root,
            Discs = new List<EconetDiscConfiguration>
            {
                new EconetDiscConfiguration
                {
                    Index = 0,
                    AcornName = "NET",
                    Provider = "tnfs",
                    Uri = $"tnfs://127.0.0.1:{TcpPort}/",
                    ReadOnly = true, // most TNFS servers are read-only
                },
            },
        };

        // BuildComposite mounts the tnfs disc — this CONNECTS to the TNFS server.
        (CompositeVfsFileSystemBackend composite, VfsCore vfs) = EconetVfsDiscs.BuildComposite(config, _cacheDir);
        _vfs = vfs;

        // The disc dir is "0NET" (Index + AcornName). Its contents come from the TNFS server root.
        Assert.That(composite.DirectoryExists(Root + "/0NET/SUB"), Is.True, "TNFS subdirectory is visible");
        Assert.That(composite.FileExists(Root + "/0NET/HELLO.TXT"), Is.True, "TNFS file is visible over the composite");

        using FileStream s = composite.OpenFile(Root + "/0NET/HELLO.TXT", FileMode.Open, FileAccess.Read);
        using MemoryStream ms = new MemoryStream();
        s.CopyTo(ms);
        Assert.That(Encoding.ASCII.GetString(ms.ToArray()), Is.EqualTo("over tnfs"), "file content read back through TNFS");
    }

    [Test]
    public void TnfsDisc_Writable_SaveLandsOnTnfsServer()
    {
        // A writable tnfs disc: a write through the Econet composite must land on the TNFS server's
        // storage (proves TNFS streaming write end-to-end — what a BBC *SAVE would drive).
        _cacheDir = Path.Combine(Path.GetTempPath(), "econet-tnfsdisc-cache-" + Guid.NewGuid().ToString("N"));

        EconetServerConfiguration config = new EconetServerConfiguration
        {
            RootDirectory = Root,
            Discs = new List<EconetDiscConfiguration>
            {
                new EconetDiscConfiguration
                {
                    Index = 0, AcornName = "NET", Provider = "tnfs",
                    Uri = $"tnfs://127.0.0.1:{TcpPort}/", ReadOnly = false,
                },
            },
        };

        (CompositeVfsFileSystemBackend composite, VfsCore vfs) = EconetVfsDiscs.BuildComposite(config, _cacheDir);
        _vfs = vfs;

        // Content larger than one 512-byte TNFS block, to exercise the chunked write loop.
        string payload = new string('A', 1300);
        using (FileStream s = composite.OpenFile(Root + "/0NET/SAVED.TXT", FileMode.Create, FileAccess.Write))
        {
            byte[] b = Encoding.ASCII.GetBytes(payload);
            s.Write(b, 0, b.Length);
        } // close triggers write-back through TNFS

        // Landed on the TNFS server's real storage...
        string serverPath = Path.Combine(_tnfsRoot, "SAVED.TXT");
        Assert.That(File.Exists(serverPath), Is.True, "file must appear on the TNFS server");
        Assert.That(File.ReadAllText(serverPath), Is.EqualTo(payload), "full content (multi-block) written back");

        // ...and reads back through the composite (round-trip over TNFS).
        using FileStream r = composite.OpenFile(Root + "/0NET/SAVED.TXT", FileMode.Open, FileAccess.Read);
        using MemoryStream ms = new MemoryStream();
        r.CopyTo(ms);
        Assert.That(Encoding.ASCII.GetString(ms.ToArray()), Is.EqualTo(payload));
    }

    [Test]
    public void TnfsDisc_ReadOnly_RejectsWrite()
    {
        // The same read-only disc must reject a write up front (Step 6 fail-fast), even over TNFS.
        _cacheDir = Path.Combine(Path.GetTempPath(), "econet-tnfsdisc-cache-" + Guid.NewGuid().ToString("N"));

        EconetServerConfiguration config = new EconetServerConfiguration
        {
            RootDirectory = Root,
            Discs = new List<EconetDiscConfiguration>
            {
                new EconetDiscConfiguration
                {
                    Index = 0, AcornName = "NET", Provider = "tnfs",
                    Uri = $"tnfs://127.0.0.1:{TcpPort}/", ReadOnly = true,
                },
            },
        };

        (CompositeVfsFileSystemBackend composite, VfsCore vfs) = EconetVfsDiscs.BuildComposite(config, _cacheDir);
        _vfs = vfs;

        Assert.Throws<UnauthorizedAccessException>(
            () => composite.OpenFile(Root + "/0NET/NEW.TXT", FileMode.Create, FileAccess.Write));
    }
}
