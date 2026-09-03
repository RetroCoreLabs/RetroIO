//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Protocols.Econet.Client;
using RetroIO.Protocols.Econet.FileServer;
using RetroIO.Protocols.Econet.FileServer.Bulk;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Protocols.Econet.FileServer.Discs;
using RetroIO.Protocols.Econet.FileServer.Handlers;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.FileServer.Paths;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Protocols.Econet.Server.Transport;
using RetroIO.Storage.Providers;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Tests.TNFS;

namespace RetroIO.Tests.Econet;

[TestFixture]
[Category("Unit")]
public sealed class AunFileServerClientTests
{
    private InMemoryAunTransport _transport = null!;
    private IHost _host = null!;
    private AunFileServerClient _client = null!;

    [SetUp]
    public async Task SetUp()
    {
        _transport = new InMemoryAunTransport();

        MockFileSystemBackend fileSystem = new MockFileSystemBackend();
        EconetSampleCatalogFixture.Seed(fileSystem);

        EconetUserDirectory users = EconetTestUsers.CreateDirectory(out _);
        DiscCatalog discs = new DiscCatalog(fileSystem, "/econet-root", "RetroFS");
        FileServerSessionManager sessions = new FileServerSessionManager(fileSystem, users, discs, "/econet-root");
        FileServerHandleManager handles = new FileServerHandleManager();
        PathResolver paths = new PathResolver(fileSystem, discs, "/econet-root");
        BulkTransferManager bulk = new BulkTransferManager();
        FileServerContext context = new FileServerContext(
          fileSystem, users, discs, sessions, handles, paths, bulk,
          new FileServerConfiguration(), new FileServerClock(),
          "/econet-root", "RetroFS", FileServerCommandHandler.DefaultVersionString);

        _host = Host.CreateDefaultBuilder()
          .ConfigureServices(services =>
          {
              services.AddSingleton<IFileSystemBackend>(fileSystem);
              services.AddSingleton(context);
              services.AddSingleton(sessions);
              services.AddSingleton<FileServerCommandHandler>();
              services.AddSingleton<BulkTransferHandler>();
              services.AddSingleton<ITransport>(_transport);
              services.AddHostedService(sp =>
            new EconetFileServerService(
              new ITransport[] { _transport },
              sp.GetRequiredService<FileServerCommandHandler>(),
              sp.GetRequiredService<BulkTransferHandler>(),
              sp.GetRequiredService<FileServerSessionManager>(),
              NullLogger<EconetFileServerService>.Instance));
          })
          .Build();

        await _host.StartAsync();
        _client = new AunFileServerClient(_transport);
    }

    [TearDown]
    public async Task TearDown()
    {
        _client?.Dispose();

        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _transport.Dispose();
    }

    [Test]
    public void Login_WithDefaultSyst_Succeeds()
    {
        _client.Login("SYST", string.Empty);

        Assert.That(_client.IsLoggedIn, Is.True);
        Assert.That(_client.UrdHandle, Is.GreaterThan(0));
    }

    [Test]
    public void ListDirectory_AfterLogin_ReturnsHelloFile()
    {
        _client.Login("SYST", string.Empty);

        AunDirectoryEntry[] entries = _client.ListDirectory(".");

        bool foundHello = false;
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].Name == "HELLO")
            {
                foundHello = true;
                Assert.That(entries[i].IsDirectory, Is.False);
                break;
            }
        }

        Assert.That(foundHello, Is.True);
    }

    [Test]
    public void ReadFile_AfterLogin_ReturnsFileContents()
    {
        _client.Login("SYST", string.Empty);

        byte[] data = _client.ReadFile("HELLO");

        Assert.That(Encoding.ASCII.GetString(data), Does.Contain("Hello from RetroFS Econet"));
    }

    [Test]
    public void ReadFile_WithoutLogin_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => _client.ReadFile("HELLO"));
    }
}

[TestFixture]
[Category("Unit")]
public sealed class EconetVolumeProviderTests
{
    private InMemoryAunTransport _transport = null!;
    private IHost _host = null!;
    private EconetVolumeProvider _provider = null!;

    [SetUp]
    public async Task SetUp()
    {
        _transport = new InMemoryAunTransport();
        _provider = new EconetVolumeProvider();

        MockFileSystemBackend fileSystem = new MockFileSystemBackend();
        EconetSampleCatalogFixture.Seed(fileSystem);

        EconetUserDirectory users = EconetTestUsers.CreateDirectory(out _);
        DiscCatalog discs = new DiscCatalog(fileSystem, "/econet-root", "RetroFS");
        FileServerSessionManager sessions = new FileServerSessionManager(fileSystem, users, discs, "/econet-root");
        FileServerHandleManager handles = new FileServerHandleManager();
        PathResolver paths = new PathResolver(fileSystem, discs, "/econet-root");
        BulkTransferManager bulk = new BulkTransferManager();
        FileServerContext context = new FileServerContext(
          fileSystem, users, discs, sessions, handles, paths, bulk,
          new FileServerConfiguration(), new FileServerClock(),
          "/econet-root", "RetroFS", FileServerCommandHandler.DefaultVersionString);

        _host = Host.CreateDefaultBuilder()
          .ConfigureServices(services =>
          {
              services.AddSingleton<IFileSystemBackend>(fileSystem);
              services.AddSingleton(context);
              services.AddSingleton(sessions);
              services.AddSingleton<FileServerCommandHandler>();
              services.AddSingleton<BulkTransferHandler>();
              services.AddSingleton<ITransport>(_transport);
              services.AddHostedService(sp =>
            new EconetFileServerService(
              new ITransport[] { _transport },
              sp.GetRequiredService<FileServerCommandHandler>(),
              sp.GetRequiredService<BulkTransferHandler>(),
              sp.GetRequiredService<FileServerSessionManager>(),
              NullLogger<EconetFileServerService>.Instance));
          })
          .Build();

        await _host.StartAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        _provider.Dispose();

        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _transport.Dispose();
    }

    [Test]
    public void MountFromConfig_WithCredentials_LogsInAndReadsFile()
    {
        AunFileServerClient client = new AunFileServerClient(_transport);
        client.Login("SYST", string.Empty);

        _provider.MountWithClient(client, "test-vol", readOnly: true, username: "SYST");

        UrnContext context = new UrnContext { VolumeId = "test-vol" };
        byte[] data = _provider.ReadFile(context, "HELLO");

        Assert.That(Encoding.ASCII.GetString(data), Does.Contain("Hello from RetroFS Econet"));
    }

    [Test]
    public void MountFromConfig_UnifiedEntry_ParsesHostAndCredentials()
    {
        UnifiedMountEntry entry = new UnifiedMountEntry
        {
            VolumeId = "config-vol",
            ProviderType = "econet",
            SourceUri = "econet://127.0.0.1:32768/",
            Username = "SYST",
            Password = string.Empty,
            IsReadOnly = true,
        };

        Assert.That(_provider.CanRestore(entry, out string? error), Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public void ListDirectory_ThroughProvider_ReturnsEntries()
    {
        AunFileServerClient client = new AunFileServerClient(_transport);
        client.Login("SYST", string.Empty);
        _provider.MountWithClient(client, "list-vol", readOnly: true, username: "SYST");

        UrnContext context = new UrnContext { VolumeId = "list-vol" };
        DirectoryEntry[] entries = _provider.ListDirectory(context, "/");

        Assert.That(entries.Length, Is.GreaterThan(0));
    }

    [Test]
    public void Provider_TypeAndSchemes_AreEconet()
    {
        Assert.That(_provider.Type, Is.EqualTo("econet"));
        Assert.That(_provider.SupportedSchemes, Does.Contain("econet"));
        Assert.That(_provider.SupportedSchemes, Does.Contain("aun"));
        Assert.That(_provider.IsRemote, Is.True);
    }
}
