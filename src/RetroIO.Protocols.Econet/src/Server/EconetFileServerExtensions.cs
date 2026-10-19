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

/// <summary>
/// Registration of the Econet file server in a service collection.
/// </summary>
public static class EconetFileServerExtensions
{
    /// <summary>
    /// Registers the Econet file server services, the AUN address mapper, the UDP transport when enabled, and the hosted service that runs the server.
    /// </summary>
    /// <param name="services">
    /// The service collection to add to.
    /// </param>
    /// <param name="configureOptions">
    /// A method that sets the options.
    /// </param>
    /// <returns>
    /// The same service collection.
    /// </returns>
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
                options.BuildWireProfileResolver(),
                options.Net,
                options.Station);
        });

        services.AddSingleton<FileServerCommandHandler>();
        services.AddSingleton<BulkTransferHandler>();

        // One counter object, shared by EconetFileServerService (writer) and the Management API's
        // EconetController and LiveDataService (readers). Both service types resolve to the same instance.
        services.AddSingleton<EconetServerStatistics>();
        services.AddSingleton<IEconetStatisticsTracker>(sp => sp.GetRequiredService<EconetServerStatistics>());

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

/// <summary>
/// Settings of the Econet file server.
/// </summary>
public sealed class EconetFileServerOptions
{
    /// <summary>
    /// Gets or sets the root directory that holds the disc directories. The default is /tmp/econet-root.
    /// </summary>
    public string RootDirectory { get; set; } = "/tmp/econet-root";
    /// <summary>
    /// Gets or sets the name of the default disc. The default is RetroFS.
    /// </summary>
    public string DiscName { get; set; } = "RetroFS";
    /// <summary>
    /// Gets or sets the version text the server reports, or null to use the default version text.
    /// </summary>
    public string? VersionString { get; set; }
    /// <summary>
    /// Gets or sets the UDP port of the AUN transport. The default is 32768.
    /// </summary>
    public int UdpPort { get; set; } = 32768;
    /// <summary>
    /// Gets or sets the network number. The default is 1.
    /// </summary>
    public byte Net { get; set; } = 1;
    /// <summary>
    /// Gets or sets the station number of the server. The default is 254.
    /// The server reports its own address, Net and this station, in the text of the PiBridge shut down reply
    /// (PiFS utilities/fs.c line 4301). An AUN datagram has no station field (it holds the packet type, the port,
    /// the control byte, a pad byte and the sequence number), so the station is not written into any UDP datagram;
    /// the address a client sends to is the IP address of the host, not this number.
    /// </summary>
    public byte Station { get; set; } = 254;
    /// <summary>
    /// Gets or sets the index, 0 to 3, of the IPv4 address byte that holds the station number of a client. The default is 3.
    /// </summary>
    public byte AunStationOctetIndex { get; set; } = 3;
    /// <summary>
    /// Gets or sets a value indicating whether the UDP transport is registered. The default is true.
    /// </summary>
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

    /// <summary>
    /// Builds the resolver these two settings describe (used by the context factory).
    /// </summary>
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
