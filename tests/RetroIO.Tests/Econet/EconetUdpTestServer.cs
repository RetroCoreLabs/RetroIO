//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RetroIO.Protocols.Econet.FileServer;
using RetroIO.Protocols.Econet.FileServer.Bulk;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Protocols.Econet.FileServer.Discs;
using RetroIO.Protocols.Econet.FileServer.Handlers;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.FileServer.Paths;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Protocols.Econet.Server.Transport;
using RetroIO.Identity;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Tests.TNFS;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Runs <see cref="EconetFileServerService"/> with <see cref="UdpAunTransport"/> for integration-style unit tests.
/// </summary>
internal sealed class EconetUdpTestServer : IAsyncDisposable
{
    private readonly IHost _host;

    public EconetActivityLog ActivityLog { get; }
    public EconetTestMonitor Monitor { get; }
    public IRetroIDP IdentityProvider { get; }
    public int UdpPort { get; }

    private EconetUdpTestServer(IHost host, EconetActivityLog activityLog, IRetroIDP identityProvider, int udpPort)
    {
        _host = host;
        ActivityLog = activityLog;
        Monitor = new EconetTestMonitor(activityLog);
        IdentityProvider = identityProvider;
        UdpPort = udpPort;
    }

    public static async Task<EconetUdpTestServer> StartAsync(
        int udpPort = EconetTestPorts.UnitTestServerUdp,
        CancellationToken cancellationToken = default)
    {
        EconetActivityLog activityLog = new EconetActivityLog();
        MockFileSystemBackend fileSystem = new MockFileSystemBackend();
        EconetSampleCatalogFixture.Seed(fileSystem);

        EconetUserDirectory users = EconetTestUsers.CreateDirectory(out IRetroIDP identityProvider);
        DiscCatalog discs = new DiscCatalog(fileSystem, "/econet-root", "RetroFS");
        FileServerSessionManager sessions = new FileServerSessionManager(fileSystem, users, discs, "/econet-root");
        FileServerHandleManager handles = new FileServerHandleManager();
        PathResolver paths = new PathResolver(fileSystem, discs, "/econet-root");
        BulkTransferManager bulk = new BulkTransferManager();
        FileServerContext context = new FileServerContext(
            fileSystem,
            users,
            discs,
            sessions,
            handles,
            paths,
            bulk,
            new FileServerConfiguration(),
            new FileServerClock(),
            "/econet-root",
            "RetroFS",
            FileServerCommandHandler.DefaultVersionString,
            activityLog);

        AunAddressMapper mapper = new AunAddressMapper(network: 1);

        IHost host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IFileSystemBackend>(fileSystem);
                services.AddSingleton(context);
                services.AddSingleton(sessions);
                services.AddSingleton(activityLog);
                services.AddSingleton<FileServerCommandHandler>();
                services.AddSingleton<BulkTransferHandler>();
                services.AddSingleton<ITransport>(new UdpAunTransport(
                    udpPort,
                    mapper,
                    NullLogger<UdpAunTransport>.Instance));
                services.AddHostedService(sp =>
                    new EconetFileServerService(
                        new ITransport[] { sp.GetRequiredService<ITransport>() },
                        sp.GetRequiredService<FileServerCommandHandler>(),
                        sp.GetRequiredService<BulkTransferHandler>(),
                        sp.GetRequiredService<FileServerSessionManager>(),
                        NullLogger<EconetFileServerService>.Instance));
            })
            .Build();

        await host.StartAsync(cancellationToken);
        return new EconetUdpTestServer(host, activityLog, identityProvider, udpPort);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(TimeSpan.FromSeconds(5));
        foreach (ITransport transport in _host.Services.GetServices<ITransport>())
            transport.Dispose();

        _host.Dispose();
    }
}
