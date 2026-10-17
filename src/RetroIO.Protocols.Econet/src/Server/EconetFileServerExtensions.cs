//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
using RetroIO.Protocols.Econet.Server.Transport;
using RetroIO.Security.Firewall;
using RetroIO.Security.Implementation.Auditing;
using RetroIO.Security.Implementation.RateLimiting;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Protocols.Econet.Server;

public static class EconetFileServerExtensions
{
    public static IServiceCollection AddEconetFileServer(
        this IServiceCollection services,
        Action<EconetFileServerOptions> configureOptions)
    {
        EconetFileServerOptions options = new EconetFileServerOptions();
        configureOptions(options);
        services.AddSingleton(options);

        // The Econet file server reads/writes through EconetFileSystem, which may be a VFS-disc composite
        // when econet-config declares Discs[]. Default (back-compat) = the globally shared backend. The
        // composition root registers an override BEFORE this call, so TryAdd here does not clobber it.
        services.TryAddSingleton(sp => new EconetFileSystem(sp.GetRequiredService<IFileSystemBackend>()));

        services.AddSingleton(sp =>
            new DiscCatalog(
                sp.GetRequiredService<EconetFileSystem>().Backend,
                options.RootDirectory,
                options.DiscName));

        services.AddSingleton(sp =>
        {
            IFileSystemBackend fileSystem = sp.GetRequiredService<EconetFileSystem>().Backend;
            FileServerBootstrap.EnsureLayout(fileSystem, options.RootDirectory, options.DiscName);
            IRetroIDP idp = sp.GetRequiredService<IRetroIDP>();
            return new EconetUserDirectory(idp);
        });

        services.AddSingleton(sp =>
            new FileServerSessionManager(
                sp.GetRequiredService<EconetFileSystem>().Backend,
                sp.GetRequiredService<EconetUserDirectory>(),
                sp.GetRequiredService<DiscCatalog>(),
                options.RootDirectory));

        services.AddSingleton<FileServerHandleManager>();
        services.AddSingleton<BulkTransferManager>();
        services.AddSingleton<FileServerConfiguration>();
        services.AddSingleton<FileServerClock>();

        services.AddSingleton(sp =>
            new PathResolver(
                sp.GetRequiredService<EconetFileSystem>().Backend,
                sp.GetRequiredService<DiscCatalog>(),
                options.RootDirectory));

        services.AddSingleton(sp =>
        {
            IFileSystemBackend fileSystem = sp.GetRequiredService<EconetFileSystem>().Backend;
            return new FileServerContext(
                fileSystem,
                sp.GetRequiredService<EconetUserDirectory>(),
                sp.GetRequiredService<DiscCatalog>(),
                sp.GetRequiredService<FileServerSessionManager>(),
                sp.GetRequiredService<FileServerHandleManager>(),
                sp.GetRequiredService<PathResolver>(),
                sp.GetRequiredService<BulkTransferManager>(),
                sp.GetRequiredService<FileServerConfiguration>(),
                sp.GetRequiredService<FileServerClock>(),
                options.RootDirectory,
                options.DiscName,
                options.VersionString ?? FileServerCommandHandler.DefaultVersionString,
                sp.GetService<IEconetActivityLog>(),
                sp.GetService<SecurityEventEmitter>(),
                options.BuildWireProfileResolver());
        });

        services.AddSingleton<FileServerCommandHandler>();
        services.AddSingleton<BulkTransferHandler>();

        services.AddSingleton(sp =>
            new AunAddressMapper(options.Net, options.AunStationOctetIndex));

        if (options.EnableUdp)
        {
            services.AddSingleton<ITransport>(sp =>
                new UdpAunTransport(
                    options.UdpPort,
                    sp.GetRequiredService<AunAddressMapper>(),
                    sp.GetRequiredService<ILogger<UdpAunTransport>>(),
                    sp.GetService<FirewallEngine>(),
                    sp.GetService<SecurityEventEmitter>()));
        }

        services.AddHostedService(sp =>
            new EconetFileServerService(
                ResolveTransports(sp),
                sp.GetRequiredService<FileServerCommandHandler>(),
                sp.GetRequiredService<BulkTransferHandler>(),
                sp.GetRequiredService<FileServerSessionManager>(),
                sp.GetRequiredService<ILogger<EconetFileServerService>>(),
                sp.GetService<InMemoryRateLimiter>(),
                sp.GetService<SecurityEventEmitter>(),
                sp.GetService<IEconetStatisticsTracker>(),
                sp.GetRequiredService<FileServerConfiguration>()));

        return services;
    }

    private static ITransport[] ResolveTransports(IServiceProvider serviceProvider)
    {
        IEnumerable<ITransport> transports = serviceProvider.GetServices<ITransport>();
        List<ITransport> list = new List<ITransport>();
        IEnumerator<ITransport> enumerator = transports.GetEnumerator();
        while (enumerator.MoveNext())
            list.Add(enumerator.Current);

        return list.ToArray();
    }
}

public sealed class EconetFileServerOptions
{
    public string RootDirectory { get; set; } = "/tmp/econet-root";
    public string DiscName { get; set; } = "RetroFS";
    public string? VersionString { get; set; }
    public int UdpPort { get; set; } = 32768;
    public byte Net { get; set; } = 1;
    public byte Station { get; set; } = 254;
    public byte AunStationOctetIndex { get; set; } = 3;
    public bool EnableUdp { get; set; } = true;

    /// <summary>
    /// Server-level default wire-format profile (task 0.4). Defaults to <see cref="WireProfile.LaterFS"/>
    /// so existing golden bytes are unchanged. A composition root sets this from econet-config.
    /// </summary>
    public WireProfile DefaultWireProfile { get; set; } = WireProfile.LaterFS;

    /// <summary>
    /// Optional per-machine wire-profile overrides (net, station → profile). An exact match wins over
    /// <see cref="DefaultWireProfile"/>; empty means "default for everyone".
    /// </summary>
    public WireProfileResolver.MachineOverride[]? WireProfileOverrides { get; set; }

    /// <summary>Builds the resolver these two settings describe (used by the context factory).</summary>
    public WireProfileResolver BuildWireProfileResolver()
    {
        // Fast path: an unconfigured server (default profile, no overrides) shares the singleton so it
        // is byte-identical to before this feature existed.
        if (DefaultWireProfile == WireProfile.LaterFS &&
            (WireProfileOverrides == null || WireProfileOverrides.Length == 0))
        {
            return WireProfileResolver.AllLaterFs;
        }

        return new WireProfileResolver(DefaultWireProfile, WireProfileOverrides);
    }
}
