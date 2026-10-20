//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroIO.Identity;
using RetroIO.Protocols.Tnfs.Server;
using RetroIO.Protocols.Tnfs.Server.Handlers;
using RetroIO.Protocols.Tnfs.Server.Transport;
using RetroIO.Security.Firewall;
using RetroIO.Security.Implementation.Auditing;
using RetroIO.Security.Implementation.RateLimiting;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Protocols.Tnfs.Server;

/// <summary>
/// Extension methods for registering TNFS server services.
/// </summary>
public static class TnfsServerExtensions
{
    /// <summary>
    /// Adds TNFS protocol server to the service collection.
    /// </summary>
    public static IServiceCollection AddTnfsServer(
        this IServiceCollection services,
        Action<TnfsServerOptions> configureOptions)
    {
        var options = new TnfsServerOptions();
        configureOptions(options);

        // Real request/byte/file-transfer counters, shared between TnfsServerService (writer)
        // and the Management API's TnfsController (reader) — see TnfsServerStatistics.
        services.AddSingleton<TnfsServerStatistics>();

        // Register session manager
        services.AddSingleton<SessionManager>(sp =>
            new SessionManager(
                sp.GetRequiredService<ILogger<SessionManager>>(),
                TimeSpan.FromSeconds(Protocol.TnfsConstants.SESSION_TIMEOUT),
                options.MaxSessionsPerIP,
                options.MaxSessions));

        // Register command handlers
        services.AddSingleton<SessionCommandHandler>(sp =>
            new SessionCommandHandler(
                sp.GetRequiredService<SessionManager>(),
                sp.GetRequiredService<ILogger<SessionCommandHandler>>(),
                options.RootDirectory,
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
                sp.GetRequiredService<IFileSystemBackend>(),
                sp.GetService<SecurityEventEmitter>()));

        // Register transports based on options
        if (options.EnableUdp)
        {
            services.AddSingleton<ITransport>(sp =>
                new UdpTransport(
                    options.UdpPort,
                    sp.GetRequiredService<ILogger<UdpTransport>>(),
                    sp.GetService<FirewallEngine>(),
                    sp.GetService<SecurityEventEmitter>()));
        }

        if (options.EnableTcp)
        {
            services.AddSingleton<ITransport>(sp =>
                new TcpTransport(
                    options.TcpPort,
                    sp.GetRequiredService<ILogger<TcpTransport>>(),
                    sp.GetService<FirewallEngine>(),
                    sp.GetService<SecurityEventEmitter>(),
                    options.MaxTcpConnections));
        }

        // Register the TNFS server background service with authentication event wiring
        services.AddHostedService(sp =>
        {
            SessionCommandHandler sessionHandler = sp.GetRequiredService<SessionCommandHandler>();
            RetroIDPAuthenticationProvider? authProvider = sp.GetService<RetroIDPAuthenticationProvider>();

            // Wire up authentication event if RetroIDP is available
            if (authProvider != null)
            {
                sessionHandler.OnAuthenticationRequest += (request) =>
                {
                    // Call RetroIDP synchronously (AuthenticateAsync is async but we need sync delegate)
                    // Using GetAwaiter().GetResult() is safe here as RetroIDP uses ValueTask
                    AuthenticationResult result = authProvider.AuthenticateAsync(request.Username, request.Password).GetAwaiter().GetResult();

                    if (result.IsSuccess && result.User != null)
                    {
                        return TnfsAuthenticationResponse.Success(result.User.UserId, result.User.Username, result.User.IsAdmin);
                    }

                    return TnfsAuthenticationResponse.Failed(result.ErrorMessage ?? "Authentication failed");
                };
            }

            return new TnfsServerService(
                sp.GetServices<ITransport>(),
                sessionHandler,
                sp.GetRequiredService<DirectoryCommandHandler>(),
                sp.GetRequiredService<FileCommandHandler>(),
                sp.GetRequiredService<SessionManager>(),
                sp.GetRequiredService<ILogger<TnfsServerService>>(),
                sp.GetService<InMemoryRateLimiter>(),
                sp.GetService<SecurityEventEmitter>(),
                sp.GetRequiredService<TnfsServerStatistics>());
        });

        return services;
    }
}

/// <summary>
/// Configuration options for TNFS server.
/// </summary>
public class TnfsServerOptions
{
    /// <summary>
    /// Root directory for TNFS file system.
    /// </summary>
    public string RootDirectory { get; set; } = "/tmp/tnfs";

    /// <summary>
    /// UDP port for TNFS server (default: 16384).
    /// </summary>
    public int UdpPort { get; set; } = 16384;

    /// <summary>
    /// TCP port for TNFS server (default: 16384).
    /// </summary>
    public int TcpPort { get; set; } = 16384;

    /// <summary>
    /// Enable UDP transport (default: true).
    /// </summary>
    public bool EnableUdp { get; set; } = true;

    /// <summary>
    /// Enable TCP transport (default: true).
    /// </summary>
    public bool EnableTcp { get; set; } = true;

    /// <summary>
    /// Gets or sets the most TCP connections open at one time (default: 4096, the reference daemon's MAX_TCP_CONN).
    /// </summary>
    public int MaxTcpConnections { get; set; } = Protocol.TnfsConstants.MAX_TCP_CONN;

    /// <summary>
    /// Gets or sets the most sessions open at one time (default: 4096, the reference daemon's MAX_SESSIONS).
    /// </summary>
    public int MaxSessions { get; set; } = Protocol.TnfsConstants.MAX_SESSIONS;

    /// <summary>
    /// Gets or sets the most sessions one IP address may hold (default: 4096, the reference daemon's MAX_SESSIONS_PER_IP).
    /// </summary>
    public int MaxSessionsPerIP { get; set; } = Protocol.TnfsConstants.MAX_SESSIONS_PER_IP;
}
