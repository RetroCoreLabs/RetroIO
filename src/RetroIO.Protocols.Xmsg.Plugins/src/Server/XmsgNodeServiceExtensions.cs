//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RetroIO.Protocols.Xmsg.Plugins.Server
{
    /// <summary>
    /// Registers <see cref="XmsgNodeService"/> with a host.
    /// </summary>
    public static class XmsgNodeServiceExtensions
    {
        /// <summary>
        /// Adds the XMSG node as a hosted service.
        /// </summary>
        /// <param name="services">
        /// The service collection.
        /// </param>
        /// <param name="configureOptions">
        /// Fills in the node's settings.
        /// </param>
        /// <returns>
        /// <paramref name="services"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="configureOptions"/> is null.
        /// </exception>
        public static IServiceCollection AddXmsgNode(this IServiceCollection services, Action<XmsgNodeServiceOptions> configureOptions)
        {
            ArgumentNullException.ThrowIfNull(configureOptions);

            XmsgNodeServiceOptions options = new XmsgNodeServiceOptions();
            configureOptions(options);

            services.AddSingleton(options);
            services.AddSingleton(sp => new XmsgNodeService(
                sp.GetRequiredService<XmsgNodeServiceOptions>(),
                sp.GetService<ILoggerFactory>()));
            services.AddHostedService(sp => sp.GetRequiredService<XmsgNodeService>());
            return services;
        }
    }
}
