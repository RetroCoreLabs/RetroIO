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
using RetroIO.Protocols.Econet.Server;
using RetroIO.Protocols.Tnfs.Server;
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
    /// Host configuration. Command-line and appsettings keys under <c>Tnfs:</c> and <c>Econet:</c>
    /// override the saved protocol configuration files.
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

        if (opts.EnableTnfs)
        {
            services.AddTnfsServer(o =>
            {
                o.RootDirectory = tnfsConfig.RootDirectory;
                o.UdpPort = tnfsConfig.UdpPort;
                o.TcpPort = tnfsConfig.TcpPort;
                o.EnableUdp = tnfsConfig.EnableUdp;
                o.EnableTcp = tnfsConfig.EnableTcp;
            });

            if (opts.PrintBanners)
            {
                ServerHostingBanners.PrintTnfsConfiguration(tnfsConfig);
            }
        }

        if (opts.EnableEconet)
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
