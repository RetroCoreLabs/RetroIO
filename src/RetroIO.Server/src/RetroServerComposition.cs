//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroIO.Management.Api;
using RetroIO.Protocols.DriveWire.Server;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Protocols.Tnfs.Server;
using RetroIO.Protocols.Xmsg.Hub.Server;
using RetroIO.Protocols.Xmsg.Plugins.Server;
using RetroIO.Server.Hosting;
using RetroIO.Server.Hosting.Configuration;

namespace RetroIO.Server;

/// <summary>
/// The single composition point for a RetroFS server.
/// </summary>
/// <remarks>
/// This is the whole server in one call. It exists so that every host is a thin shell: a console
/// app, a Windows service and a web app all register exactly the same services and therefore
/// behave identically. Before this, RetroServer.exe, the management API and RetroServeUI each
/// composed their own partial version, which is how the three of them ended up disagreeing about
/// what "the server" even was.
/// </remarks>
public static class RetroServerComposition
{
    /// <summary>
    /// Registers the protocol servers, identity, security, the filesystem backend and the
    /// management API.
    /// </summary>
    /// <param name="services">
    /// Service collection to add to.
    /// </param>
    /// <param name="configuration">
    /// Host configuration. Command-line and appsettings keys under <c>Tnfs:</c>, <c>Econet:</c> and
    /// <c>DriveWire:</c> override the saved protocol configuration files.
    /// </param>
    /// <param name="loggingBuilder">
    /// Host logging builder, so the management API can attach its in-memory log buffer and the
    /// Logs endpoints return this process's own log.
    /// </param>
    /// <param name="options">
    /// What to switch on. Null means the full server.
    /// </param>
    /// <returns>
    /// The same collection, for chaining.
    /// </returns>
    public static IServiceCollection AddRetroServer(
        this IServiceCollection services,
        IConfiguration configuration,
        ILoggingBuilder loggingBuilder,
        RetroServerOptions? options = null)
    {
        RetroServerOptions opts = options ?? new RetroServerOptions();

        string retroFsPath = RetroServerHostingExtensions.EnsureRetroFsDataPath();

        TnfsServerConfiguration tnfsConfig =
            ProtocolConfigurationLoader.LoadTnfsConfiguration(retroFsPath, configuration);
        EconetServerConfiguration econetConfig =
            ProtocolConfigurationLoader.LoadEconetConfiguration(retroFsPath, configuration);

        services.AddRetroIdentity(retroFsPath);
        services.AddRetroSecurity(retroFsPath);
        services.AddRetroFileSystemBackend(configuration, retroFsPath);

        // Both must be on: the host switch (code) and the Enabled switch in tnfs-config.json (the admin page).
        if (opts.EnableTnfs && tnfsConfig.Enabled)
        {
            services.AddTnfsServer(o =>
            {
                o.RootDirectory = tnfsConfig.RootDirectory;
                o.UdpPort = tnfsConfig.UdpPort;
                o.TcpPort = tnfsConfig.TcpPort;
                o.EnableUdp = tnfsConfig.EnableUdp;
                o.EnableTcp = tnfsConfig.EnableTcp;
                o.MaxTcpConnections = tnfsConfig.MaxTcpConnections;
                o.MaxSessions = tnfsConfig.MaxSessions;
                o.MaxSessionsPerIP = tnfsConfig.MaxSessionsPerIP;
                o.SessionTimeoutSeconds = tnfsConfig.SessionTimeoutSeconds;
            });

            if (opts.PrintBanners)
            {
                ServerHostingBanners.PrintTnfsConfiguration(tnfsConfig);
            }
        }

        // Both must be on: the host switch (code) and the Enabled switch in econet-config.json (the admin page).
        if (opts.EnableEconet && econetConfig.Enabled)
        {
            // When econet-config declares Discs[], serve each as its own VFS mount (composite
            // backend). No-op otherwise, which leaves the shared single-root backend in place.
            // MUST run before AddEconetFileServer so its default EconetFileSystem does not win.
            services.AddEconetVfsDiscs(econetConfig, Path.Combine(retroFsPath, "econet-vfs-cache"));

            services.AddEconetFileServer(o =>
            {
                o.RootDirectory = econetConfig.RootDirectory;
                o.DiscName = econetConfig.DiscName;
                o.VersionString = econetConfig.VersionString;
                o.UdpPort = econetConfig.UdpPort;
                o.Net = econetConfig.Net;
                o.Station = econetConfig.Station;
                o.AunStationOctetIndex = econetConfig.AunStationOctetIndex;
                o.EnableUdp = econetConfig.EnableUdp;
                o.DefaultWireProfile = econetConfig.WireProfile;
                o.WireProfileOverrides = econetConfig.ToWireProfileOverrides();
            });

            if (opts.PrintBanners)
            {
                ServerHostingBanners.PrintEconetConfiguration(econetConfig);
            }
        }

        if (opts.EnableDriveWire)
        {
            DriveWireServerConfiguration driveWireConfig =
                ProtocolConfigurationLoader.LoadDriveWireConfiguration(retroFsPath, configuration);

            if (driveWireConfig.Enabled)
            {
                services.AddDriveWireServer(o =>
                {
                    o.Port = driveWireConfig.Port;
                    o.BindAddress = driveWireConfig.BindAddress;
                    o.TransactionTimeoutMilliseconds = driveWireConfig.TransactionTimeoutMilliseconds;
                    o.NamedObjectDirectory = driveWireConfig.NamedObjectDirectory;
                    o.PrintDirectory = driveWireConfig.PrintDirectory;
                    for (int i = 0; i < driveWireConfig.Drives.Count; i++)
                    {
                        DriveWireDriveConfiguration drive = driveWireConfig.Drives[i];
                        o.Drives.Add(new DriveWireDriveOptions { Drive = drive.Drive, Path = drive.Path, ReadOnly = drive.ReadOnly });
                    }
                });

                if (opts.PrintBanners)
                {
                    ServerHostingBanners.PrintDriveWireConfiguration(driveWireConfig);
                }
            }
        }

        if (opts.EnableXmsgHub)
        {
            XmsgHubServerConfiguration hubConfig =
                ProtocolConfigurationLoader.LoadXmsgHubConfiguration(retroFsPath, configuration);

            if (hubConfig.Enabled)
            {
                services.AddXmsgHub(o =>
                {
                    o.Port = hubConfig.Port;
                    o.BindAddress = hubConfig.BindAddress;
                    o.Uplink = hubConfig.Uplink;
                    o.CaptureFile = hubConfig.CaptureFile;
                    o.FrameBufferSize = hubConfig.FrameBufferSize;
                    o.LogBufferSize = hubConfig.LogBufferSize;
                    o.StatusIntervalSeconds = hubConfig.StatusIntervalSeconds;
                });

                if (opts.PrintBanners)
                {
                    ServerHostingBanners.PrintXmsgHubConfiguration(hubConfig);
                }
            }
        }

        if (opts.EnableXmsgNode)
        {
            XmsgNodeServerConfiguration nodeConfig =
                ProtocolConfigurationLoader.LoadXmsgNodeConfiguration(retroFsPath, configuration);

            if (nodeConfig.Enabled)
            {
                services.AddXmsgNode(o =>
                {
                    // A number outside 1..65535 is left at zero, which the node refuses with a log line.
                    o.SystemNumber = nodeConfig.SystemNumber > 0 && nodeConfig.SystemNumber <= ushort.MaxValue
                        ? (ushort)nodeConfig.SystemNumber : (ushort)0;
                    o.PeerSystemNumber = nodeConfig.PeerSystemNumber > 0 && nodeConfig.PeerSystemNumber <= ushort.MaxValue
                        ? (ushort)nodeConfig.PeerSystemNumber : (ushort)0;
                    o.Ethernet = nodeConfig.Ethernet;
                    o.PluginBindAddress = nodeConfig.ProgramBindAddress;
                    o.PluginPort = nodeConfig.ProgramPort;
                    o.ProgramToken = nodeConfig.ProgramToken;

                    // A level name that is not one of RetroServer's own is reported and the default kept,
                    // never guessed at.
                    Microsoft.Extensions.Logging.LogLevel programLevel;
                    if (Enum.TryParse(nodeConfig.ProgramLogMinimum, true, out programLevel)
                        && Enum.IsDefined(programLevel))
                    {
                        o.ProgramLogMinimum = programLevel;
                    }
                    else
                    {
                        Console.WriteLine($"XMSG node: ProgramLogMinimum '{nodeConfig.ProgramLogMinimum}' is not a log level "
                            + "(Trace, Debug, Information, Warning, Error, Critical, None); Information is used.");
                    }
                    o.Motd = nodeConfig.Motd;
                    // The node's sequence and seed state lives under the data folder, never beside the exe.
                    o.StateDirectory = System.IO.Path.Combine(retroFsPath, "xmsg");
                });

                if (opts.PrintBanners)
                {
                    ServerHostingBanners.PrintXmsgNodeConfiguration(nodeConfig);
                }
            }
        }

        if (opts.EnableManagementApi)
        {
            // Added LAST so the protocol servers' own registrations win where they overlap --
            // notably IFileSystemBackend, which AddRetroManagementApi only supplies as a fallback.
            services.AddRetroManagementApi(loggingBuilder, opts.MaxLogEntries, opts.EnableSwagger);
        }

        return services;
    }

    /// <summary>
    /// Adds the middleware the server's HTTP surface needs, when the management API is enabled.
    /// </summary>
    /// <param name="app">
    /// The host's application builder.
    /// </param>
    /// <param name="options">
    /// The same options passed to <see cref="AddRetroServer"/>.
    /// </param>
    /// <returns>
    /// The same builder, for chaining.
    /// </returns>
    public static IApplicationBuilder UseRetroServer(
        this IApplicationBuilder app,
        RetroServerOptions? options = null)
    {
        RetroServerOptions opts = options ?? new RetroServerOptions();

        if (opts.EnableManagementApi)
        {
            app.UseRetroManagementApi();
        }

        return app;
    }

    /// <summary>
    /// Maps the management API routes, when it was registered.
    /// </summary>
    /// <param name="endpoints">
    /// Endpoint route builder from the host.
    /// </param>
    /// <param name="options">
    /// The same options passed to <see cref="AddRetroServer"/>.
    /// </param>
    /// <returns>
    /// The same builder, for chaining.
    /// </returns>
    /// <remarks>
    /// The protocol servers need no mapping: they are hosted services listening on their own
    /// sockets, not HTTP endpoints.
    /// </remarks>
    public static IEndpointRouteBuilder MapRetroServer(
        this IEndpointRouteBuilder endpoints,
        RetroServerOptions? options = null)
    {
        RetroServerOptions opts = options ?? new RetroServerOptions();

        if (opts.EnableManagementApi)
        {
            endpoints.MapRetroManagementApi();
        }

        return endpoints;
    }
}
