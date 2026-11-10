//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RetroIO.Protocols.Xmsg.Hub.Server
{
    /// <summary>
    /// Registers the hosted COSMOS Ethernet hub.
    /// </summary>
    public static class XmsgHubServiceExtensions
    {
        /// <summary>
        /// Adds the hub as a hosted service, and as a singleton so the management API can read it.
        /// </summary>
        /// <param name="services">
        /// The service collection.
        /// </param>
        /// <param name="configureOptions">
        /// Sets the hub's options.
        /// </param>
        /// <returns>
        /// The same collection.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="configureOptions"/> is null.
        /// </exception>
        public static IServiceCollection AddXmsgHub(this IServiceCollection services, Action<XmsgHubOptions> configureOptions)
        {
            ArgumentNullException.ThrowIfNull(configureOptions);

            XmsgHubOptions options = new XmsgHubOptions();
            configureOptions(options);

            services.AddSingleton(options);
            services.AddSingleton(sp => new XmsgHubService(
                sp.GetRequiredService<XmsgHubOptions>(),
                sp.GetService<ILoggerFactory>()));
            services.AddHostedService(sp => sp.GetRequiredService<XmsgHubService>());
            return services;
        }
    }
}
