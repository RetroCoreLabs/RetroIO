//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;

namespace RetroIO.Protocols.X25.Routing
{
    /// <summary>
    /// Turns the written form of the routing table (<see cref="X121NetworkConfig"/>, ports by name) into an
    /// <see cref="X121RoutingTable"/> and back. Every value is checked with <see cref="X121RouteRules"/>; a
    /// route that cannot be used stops the load with a message that names it, it is never skipped.
    /// </summary>
    public static class X121TableBuilder
    {
        /// <summary>
        /// The kind words used in files, in the order of <see cref="X121RouteType"/> where it has one.
        /// </summary>
        public static readonly string[] KindWords = { "exact", "prefix", "dnic", "dcc", "default", "hunt", "regex" };

        /// <summary>
        /// Reads a kind word.
        /// </summary>
        /// <param name="kind">
        /// One of <see cref="KindWords"/> (case ignored); <c>local</c> and <c>number</c> are taken as <c>exact</c>,
        /// <c>network</c> as <c>dnic</c>, <c>country</c> as <c>dcc</c>, <c>pattern</c> as <c>regex</c>.
        /// </param>
        /// <param name="type">
        /// The kind.
        /// </param>
        /// <returns>
        /// True when the word is known.
        /// </returns>
        public static bool TryParseKind(string kind, out X121RouteType type)
        {
            switch ((kind ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "exact":
                case "number":
                case "local":
                    type = X121RouteType.Exact;
                    return true;
                case "prefix":
                    type = X121RouteType.Prefix;
                    return true;
                case "dnic":
                case "network":
                    type = X121RouteType.Dnic;
                    return true;
                case "dcc":
                case "country":
                    type = X121RouteType.Dcc;
                    return true;
                case "default":
                    type = X121RouteType.Default;
                    return true;
                case "hunt":
                    type = X121RouteType.Hunt;
                    return true;
                case "regex":
                case "pattern":
                    type = X121RouteType.Regex;
                    return true;
                default:
                    type = X121RouteType.Exact;
                    return false;
            }
        }

        /// <summary>
        /// The word a kind is written with.
        /// </summary>
        /// <param name="type">
        /// The kind.
        /// </param>
        /// <returns>
        /// The word.
        /// </returns>
        public static string KindWord(X121RouteType type)
        {
            switch (type)
            {
                case X121RouteType.Exact: return "exact";
                case X121RouteType.Prefix: return "prefix";
                case X121RouteType.Dnic: return "dnic";
                case X121RouteType.Dcc: return "dcc";
                case X121RouteType.Default: return "default";
                case X121RouteType.Hunt: return "hunt";
                case X121RouteType.Regex: return "regex";
                default: return "pvc";
            }
        }

        /// <summary>
        /// Reads a direction word.
        /// </summary>
        /// <param name="word">
        /// <c>both</c> (or empty), <c>incoming</c> or <c>outgoing</c>.
        /// </param>
        /// <param name="direction">
        /// The direction.
        /// </param>
        /// <returns>
        /// True when the word is known.
        /// </returns>
        public static bool TryParseDirection(string? word, out X121RouteDirection direction)
        {
            switch ((word ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "":
                case "both":
                case "bidirectional":
                    direction = X121RouteDirection.Both;
                    return true;
                case "incoming":
                case "inbound":
                    direction = X121RouteDirection.Incoming;
                    return true;
                case "outgoing":
                case "outbound":
                    direction = X121RouteDirection.Outgoing;
                    return true;
                default:
                    direction = X121RouteDirection.Both;
                    return false;
            }
        }

        /// <summary>
        /// Puts the own network and the routes of a written table into a table, replacing the routes and hunt
        /// groups it had. The table's port names must be set first, unless <paramref name="addPort"/> is given.
        /// </summary>
        /// <param name="table">
        /// The table to fill.
        /// </param>
        /// <param name="config">
        /// The written table.
        /// </param>
        /// <param name="addPort">
        /// Called for a port name the table does not know, to give it a number; null refuses such a name.
        /// </param>
        /// <exception cref="InvalidDataException">
        /// A value is not valid; the message names the route.
        /// </exception>
        public static void Fill(X121RoutingTable table, X121NetworkConfig config, Func<string, int>? addPort = null)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            // Everything is built and checked first and only then put in the table, so a bad file leaves the
            // table as it was.
            List<X121Route> routes = new List<X121Route>();
            List<X121HuntGroup> groups = new List<X121HuntGroup>();
            for (int i = 0; i < config.Routes.Count; i++)
            {
                routes.Add(Build(table, config.Routes[i], i, groups, addPort));
            }

            try
            {
                table.SetOwnDnics(config.OwnDnics ?? new List<string>());
                table.InternationalPrefix = config.InternationalPrefix ?? string.Empty;
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("own network: " + ex.Message, ex);
            }

            table.ClearRoutes();
            for (int g = 0; g < groups.Count; g++)
            {
                table.AddHuntGroup(groups[g]);
            }

            for (int r = 0; r < routes.Count; r++)
            {
                table.AddRoute(routes[r]);
            }
        }

        private static X121Route Build(X121RoutingTable table, X121RouteConfig rc, int index, List<X121HuntGroup> groups, Func<string, int>? addPort)
        {
            string label = "route " + (index + 1) + (string.IsNullOrEmpty(rc.Name) ? string.Empty : " '" + rc.Name + "'");
            if (!TryParseKind(rc.Kind, out X121RouteType type))
            {
                throw new InvalidDataException(label + ": kind '" + rc.Kind + "' is not one of " + string.Join(", ", KindWords));
            }

            if (!TryParseDirection(rc.Direction, out X121RouteDirection direction))
            {
                throw new InvalidDataException(label + ": direction '" + rc.Direction + "' is not one of both, incoming, outgoing");
            }

            string match = (rc.Match ?? string.Empty).Trim();
            if (!X121RouteRules.CheckMatch(type, match, out string error))
            {
                throw new InvalidDataException(label + ": " + error);
            }

            X121Route route = new X121Route
            {
                Name = rc.Name ?? string.Empty,
                Note = rc.Note ?? string.Empty,
                Enabled = rc.Enabled,
                Type = type,
                Direction = direction,
                CallUserDataPattern = rc.CallUserDataPattern ?? string.Empty,
                Priority = rc.Priority,
            };

            switch (type)
            {
                case X121RouteType.Exact:
                case X121RouteType.Hunt:
                    route.Address = match;
                    break;
                case X121RouteType.Prefix:
                    route.Address = match;
                    route.PrefixLen = match.Length;
                    break;
                case X121RouteType.Dnic:
                    route.Dnic = match;
                    break;
                case X121RouteType.Dcc:
                    route.Dcc = match;
                    break;
                case X121RouteType.Regex:
                    route.Pattern = match;
                    break;
            }

            if (type == X121RouteType.Hunt)
            {
                if (rc.HuntPorts == null || rc.HuntPorts.Count == 0)
                {
                    throw new InvalidDataException(label + ": a hunt route needs its member ports");
                }

                X121HuntGroup hg = new X121HuntGroup { Id = groups.Count };
                for (int m = 0; m < rc.HuntPorts.Count; m++)
                {
                    hg.Members.Add(Port(table, rc.HuntPorts[m], label, addPort));
                }

                groups.Add(hg);
                route.HuntGroupId = hg.Id;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(rc.Port))
                {
                    throw new InvalidDataException(label + ": names no port");
                }

                route.DestPort = Port(table, rc.Port, label, addPort);
            }

            if (rc.BackupPorts != null)
            {
                for (int b = 0; b < rc.BackupPorts.Count; b++)
                {
                    if (!string.IsNullOrWhiteSpace(rc.BackupPorts[b]))
                    {
                        route.BackupPorts.Add(Port(table, rc.BackupPorts[b], label, addPort));
                    }
                }
            }

            if (!X121RouteRules.Check(route, out error))
            {
                throw new InvalidDataException(label + ": " + error);
            }

            return route;
        }

        private static int Port(X121RoutingTable table, string name, string label, Func<string, int>? addPort)
        {
            string n = (name ?? string.Empty).Trim();
            if (table.TryFindPort(n, out int port))
            {
                return port;
            }

            if (addPort != null && n.Length > 0)
            {
                return addPort(n);
            }

            throw new InvalidDataException(label + ": port '" + n + "' is not a port of this node");
        }

        /// <summary>
        /// Writes a table in its written form, ports by name. PVC entries are left out; they belong to the
        /// switch file's own <c>[pvc.N]</c> sections.
        /// </summary>
        /// <param name="table">
        /// The table.
        /// </param>
        /// <returns>
        /// The written form, routes in table order.
        /// </returns>
        public static X121NetworkConfig ToConfig(X121RoutingTable table)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            X121NetworkConfig cfg = new X121NetworkConfig { InternationalPrefix = table.InternationalPrefix };
            cfg.OwnDnics.AddRange(table.OwnDnics);
            for (int i = 0; i < table.Routes.Count; i++)
            {
                X121Route r = table.Routes[i];
                if (r.Type == X121RouteType.Pvc)
                {
                    continue;
                }

                X121RouteConfig rc = new X121RouteConfig
                {
                    Name = r.Name,
                    Kind = KindWord(r.Type),
                    Match = r.MatchText,
                    Enabled = r.Enabled,
                    Note = r.Note,
                    Direction = r.Direction == X121RouteDirection.Both ? "both" : r.Direction == X121RouteDirection.Incoming ? "incoming" : "outgoing",
                    CallUserDataPattern = r.CallUserDataPattern,
                    Priority = r.Priority,
                };

                if (r.Type == X121RouteType.Hunt)
                {
                    if (r.HuntGroupId >= 0 && r.HuntGroupId < table.HuntGroups.Count)
                    {
                        List<int> members = table.HuntGroups[r.HuntGroupId].Members;
                        for (int m = 0; m < members.Count; m++)
                        {
                            rc.HuntPorts.Add(table.GetPortName(members[m]));
                        }
                    }
                }
                else
                {
                    rc.Port = table.GetPortName(r.DestPort);
                }

                for (int b = 0; b < r.BackupPorts.Count; b++)
                {
                    rc.BackupPorts.Add(table.GetPortName(r.BackupPorts[b]));
                }

                cfg.Routes.Add(rc);
            }

            return cfg;
        }
    }
}
