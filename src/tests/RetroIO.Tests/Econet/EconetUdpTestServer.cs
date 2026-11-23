//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroIO.Identity;
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

    /// <summary>The server's logged-in sessions, for tests that check none is left.</summary>
    public FileServerSessionManager Sessions { get; }

    private EconetUdpTestServer(IHost host, EconetActivityLog activityLog, IRetroIDP identityProvider, int udpPort, FileServerSessionManager sessions)
    {
        Sessions = sessions;
        _host = host;
        ActivityLog = activityLog;
        Monitor = new EconetTestMonitor(activityLog);
        IdentityProvider = identityProvider;
        UdpPort = udpPort;
    }

    public static async Task<EconetUdpTestServer> StartAsync(
        int udpPort = EconetTestPorts.UnitTestServerUdp,
        CancellationToken cancellationToken = default,
        LogLevel minimumLogLevel = LogLevel.Information)
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

        UdpAunTransport transport = new UdpAunTransport(
            udpPort,
            mapper,
            NullLogger<UdpAunTransport>.Instance);

        // The default host logs to the console, and NUnit keeps every console line of a test in
        // memory. A long measurement passes a higher level so that copy is not counted as the
        // server's memory (it was 250 KB a round, 2026-11-23).
        IHost host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(minimumLogLevel))
            .ConfigureServices(services =>
            {
                services.AddSingleton<IFileSystemBackend>(fileSystem);
                services.AddSingleton(context);
                services.AddSingleton(sessions);
                services.AddSingleton(activityLog);
                services.AddSingleton<FileServerCommandHandler>();
                services.AddSingleton<BulkTransferHandler>();
                services.AddSingleton<ITransport>(transport);
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

        // The host's start can return before the transport has bound its port; a client that
        // sends first gets "port unreachable" back. Started means listening.
        await transport.Listening.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        return new EconetUdpTestServer(host, activityLog, identityProvider, udpPort, sessions);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(TimeSpan.FromSeconds(5));
        foreach (ITransport transport in _host.Services.GetServices<ITransport>())
            transport.Dispose();

        _host.Dispose();
    }
}
