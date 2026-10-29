//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.DriveWire.Drives;

namespace RetroIO.Protocols.DriveWire.Server;

/// <summary>
/// Registers the DriveWire server with a host.
/// </summary>
public static class DriveWireServerExtensions
{
    /// <summary>
    /// Adds the DriveWire server: its settings, drive table and counters as singletons, and the server as a hosted service.
    /// </summary>
    /// <param name="services">
    /// The service collection.
    /// </param>
    /// <param name="configureOptions">
    /// Sets the server settings.
    /// </param>
    /// <returns>
    /// The same collection, for chaining.
    /// </returns>
    /// <remarks>
    /// The service is registered both as itself and as a hosted service, so the management API can find it to report on it
    /// and to stop and start it.
    /// </remarks>
    public static IServiceCollection AddDriveWireServer(
        this IServiceCollection services,
        Action<DriveWireServerOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        DriveWireServerOptions options = new DriveWireServerOptions();
        configureOptions(options);

        services.AddSingleton(options);
        services.AddSingleton<DriveWireDriveTable>();
        services.AddSingleton<DriveWireServerStatistics>();
        services.AddSingleton(sp => new DriveWireServerService(
            sp.GetRequiredService<DriveWireServerOptions>(),
            sp.GetRequiredService<DriveWireDriveTable>(),
            sp.GetRequiredService<DriveWireServerStatistics>(),
            sp.GetService<ILoggerFactory>(),
            sp.GetService<TimeProvider>()));
        services.AddHostedService(sp => sp.GetRequiredService<DriveWireServerService>());

        return services;
    }
}
