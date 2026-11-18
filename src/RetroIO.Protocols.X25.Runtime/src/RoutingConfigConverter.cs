//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Protocols.X25.Routing;

namespace RetroIO.Protocols.X25.Runtime
{
    /// <summary>
    /// Reads the routing section of the runtime's JSON file into the written form of the one routing table
    /// (<see cref="X121NetworkConfig"/>), so the runtime and the switch decide from the same kind of table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The new keys (<c>OwnDnics</c>, <c>InternationalPrefix</c>, <c>Routes</c>) are taken as they are. Every
    /// entry of the older <c>RoutingTable</c> list is converted and added after them:
    /// </para>
    ///  - the address pattern by <see cref="X121LegacyPattern"/>: <c>.*</c> the default route, plain digits an
    ///    exact number, <c>digits.*</c> a prefix, anything else a pattern route;
    ///  - <c>Type = Local</c> goes to this node (port <c>local</c>); <c>Interface</c> to the transport named in
    ///    <c>Destination</c>; <c>Gateway</c> to the line of the known host whose machine id is the
    ///    <c>Destination</c> when that host is reached directly, otherwise to a port named by the
    ///    <c>Destination</c> itself, which no line has, so the lookup says there is no such port;
    ///  - <c>Priority</c> is kept: it decides between two routes that are otherwise equal, never against a more
    ///    exact one (it used to be the whole ranking);
    ///  - a disabled entry stays, disabled (it used to be dropped when the runtime started).
    /// <para>
    /// <c>DefaultGateway</c> (a machine id) becomes the default route when a directly reached known host has
    /// that id. Until 2026-11-18 it was only written to the log and no call ever used it.
    /// </para>
    /// </remarks>
    public static class RoutingConfigConverter
    {
        /// <summary>
        /// The name of the default route made from <c>DefaultGateway</c>.
        /// </summary>
        public const string DefaultGatewayRouteName = "Default gateway";

        /// <summary>
        /// Reads a routing section.
        /// </summary>
        /// <param name="routing">
        /// The routing section of the runtime's configuration.
        /// </param>
        /// <returns>
        /// The written table: new routes first, then the converted old ones.
        /// </returns>
        public static X121NetworkConfig ToNetworkConfig(X25RoutingConfig routing)
        {
            ArgumentNullException.ThrowIfNull(routing);

            X121NetworkConfig net = new X121NetworkConfig
            {
                InternationalPrefix = routing.InternationalPrefix ?? string.Empty,
            };

            if (routing.OwnDnics != null)
            {
                net.OwnDnics.AddRange(routing.OwnDnics);
            }

            if (routing.Routes != null)
            {
                for (int i = 0; i < routing.Routes.Count; i++)
                {
                    net.Routes.Add(routing.Routes[i]);
                }
            }

            if (routing.RoutingTable != null)
            {
                for (int i = 0; i < routing.RoutingTable.Count; i++)
                {
                    net.Routes.Add(FromEntry(routing.RoutingTable[i], routing.KnownHosts));
                }
            }

            if (!string.IsNullOrWhiteSpace(routing.DefaultGateway)
                && TryDirectLine(routing.KnownHosts, routing.DefaultGateway!, out string line))
            {
                net.Routes.Add(new X121RouteConfig
                {
                    Name = DefaultGatewayRouteName,
                    Kind = "default",
                    Port = line,
                    Note = "made from DefaultGateway " + routing.DefaultGateway,
                });
            }

            return net;
        }

        /// <summary>
        /// One entry of the older list as a route of the one table.
        /// </summary>
        /// <param name="entry">
        /// The old entry.
        /// </param>
        /// <param name="knownHosts">
        /// The address book, for a gateway entry; may be null.
        /// </param>
        /// <returns>
        /// The route.
        /// </returns>
        public static X121RouteConfig FromEntry(X25RouteEntry entry, List<KnownHostEntry>? knownHosts)
        {
            ArgumentNullException.ThrowIfNull(entry);

            X121LegacyPattern.Convert(entry.X121AddressPattern, out X121RouteType type, out string match);
            string port;
            switch (entry.Type)
            {
                case RouteType.Local:
                    port = X121RoutingTable.LocalPortName;
                    break;
                case RouteType.Gateway:
                    port = TryDirectLine(knownHosts, entry.Destination, out string line) ? line : entry.Destination;
                    break;
                default:
                    port = entry.Destination;
                    break;
            }

            return new X121RouteConfig
            {
                Name = entry.Name,
                Kind = X121TableBuilder.KindWord(type),
                Match = match,
                Port = port,
                Enabled = entry.Enabled,
                Note = entry.Description ?? string.Empty,
                CallUserDataPattern = entry.CallUserDataPattern ?? string.Empty,
                Priority = entry.Priority,
            };
        }

        // The line a known host is reached on directly, by its machine id.
        private static bool TryDirectLine(List<KnownHostEntry>? hosts, string machineId, out string line)
        {
            line = string.Empty;
            if (hosts == null)
            {
                return false;
            }

            for (int i = 0; i < hosts.Count; i++)
            {
                KnownHostEntry h = hosts[i];
                if (h.Enabled && h.Reachability == HostReachability.Direct && h.Via.Length > 0
                    && string.Equals(h.MachineId, machineId, StringComparison.Ordinal))
                {
                    line = h.Via;
                    return true;
                }
            }

            return false;
        }
    }
}
