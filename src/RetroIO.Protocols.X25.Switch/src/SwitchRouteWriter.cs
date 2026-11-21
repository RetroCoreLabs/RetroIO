//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Globalization;
using RetroIO.Protocols.X25.Common;
using RetroIO.Protocols.X25.Routing;

namespace RetroIO.Protocols.X25.Switch
{
    /// <summary>
    /// Writes a switch's route table back into the text of its .ini file: the [route.N] sections are replaced, every
    /// other line of the file stays exactly as it was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Made 2026-11-21 for the RetroX25 desktop routing screen, which shows and edits the built-in switch's routes
    /// (decided by Ronny 2026-11-21: they are saved into the switch's own .ini file, the one the switch reads at
    /// start-up). Until then nothing wrote [route.N] sections; the file was edited by hand only.
    /// </para>
    /// <para>
    /// What is kept and what is not:
    /// </para>
    ///  - Every line outside the [route.N] sections is kept: other sections, comments, blank lines.
    ///  - A [route.N] section runs from its header to its last key line. Comment and blank lines AFTER that last key
    ///    belong to whatever follows and are kept where they are. Comment lines BETWEEN the header and the last key are
    ///    part of the route and go with it: a route that is written again gets the keys only.
    ///  - The new sections go where the first old one was (or at the end of the file when there was none), numbered
    ///    0, 1, 2 in the order of the list, which is the order <see cref="SwitchConfigLoader"/> reads them in.
    /// <para>
    /// Each route is written in the short form the loader reads (one key that says what is matched, see
    /// <c>SwitchConfigLoader.ReadRoute</c>), except a prefix whose length is not the length of its digits, which needs
    /// the long form (<c>type = prefix</c>, <c>address</c>, <c>prefix_len</c>). A port is written by its name when the
    /// table finds that name again, otherwise by its number.
    /// </para>
    /// </remarks>
    public static class SwitchRouteWriter
    {
        /// <summary>
        /// Replaces the [route.N] sections of the file text with the routes of <paramref name="table"/>.
        /// </summary>
        /// <param name="lines">
        /// The file's lines; changed in place.
        /// </param>
        /// <param name="table">
        /// The switch's table: its routes, in their order, and its port names.
        /// </param>
        /// <remarks>
        /// The [hunt.N] and [pvc.N] sections are not touched. A hunt route names its group by number, and the groups
        /// stay as the file has them.
        /// </remarks>
        public static void ReplaceRoutes(List<string> lines, X121RoutingTable table)
        {
            ArgumentNullException.ThrowIfNull(table);
            ReplaceRoutes(lines, table.Routes, table);
        }

        /// <summary>
        /// Replaces the [route.N] sections of the file text with <paramref name="routes"/>.
        /// </summary>
        /// <param name="lines">
        /// The file's lines; changed in place.
        /// </param>
        /// <param name="routes">
        /// The routes, in the order they are to be read.
        /// </param>
        /// <param name="table">
        /// The switch's table, for the port names (<see cref="X121RoutingTable.GetPortName"/>).
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when an argument is null.
        /// </exception>
        public static void ReplaceRoutes(List<string> lines, IReadOnlyList<X121Route> routes, X121RoutingTable table)
        {
            ArgumentNullException.ThrowIfNull(lines);
            ArgumentNullException.ThrowIfNull(routes);
            ArgumentNullException.ThrowIfNull(table);

            int insertAt = RemoveRouteSections(lines);
            List<string> written = new List<string>();
            for (int i = 0; i < routes.Count; i++)
            {
                // A blank line between sections; none after a comment line, because a comment right above the first
                // route is that route's heading and stays against it.
                if (written.Count > 0 || (insertAt > 0 && IsKeyOrHeader(lines[insertAt - 1])))
                {
                    written.Add(string.Empty);
                }

                WriteRoute(written, i, routes[i], table);
            }

            lines.InsertRange(insertAt, written);
        }

        // Removes every [route.N] section (header to last key line) and returns where the first one began, or the end
        // of the file when there was none.
        private static int RemoveRouteSections(List<string> lines)
        {
            int first = -1;
            int i = 0;
            while (i < lines.Count)
            {
                if (!IsRouteHeader(lines[i]))
                {
                    i++;
                    continue;
                }

                int lastKey = i;
                int j = i + 1;
                while (j < lines.Count && !IsHeader(lines[j]))
                {
                    if (IsKeyLine(lines[j]))
                    {
                        lastKey = j;
                    }

                    j++;
                }

                lines.RemoveRange(i, lastKey - i + 1);
                if (first < 0)
                {
                    first = i;
                }

                // Blank lines left in front of a removed section would pile up; one stays.
                while (i < lines.Count && i > 0 && Trim(lines[i]).Length == 0 && Trim(lines[i - 1]).Length == 0)
                {
                    lines.RemoveAt(i);
                }
            }

            return first < 0 ? lines.Count : first;
        }

        private static void WriteRoute(List<string> into, int index, X121Route r, X121RoutingTable table)
        {
            into.Add("[route." + index.ToString(CultureInfo.InvariantCulture) + "]");
            if (r.Name.Length > 0)
            {
                into.Add("name = " + r.Name);
            }

            if (r.Note.Length > 0)
            {
                into.Add("note = " + r.Note);
            }

            switch (r.Type)
            {
                case X121RouteType.Exact:
                    into.Add("number = " + r.Address);
                    break;
                case X121RouteType.Prefix:
                    if (r.PrefixLen == r.Address.Length)
                    {
                        into.Add("prefix = " + r.Address);
                    }
                    else
                    {
                        into.Add("type = prefix");
                        into.Add("address = " + r.Address);
                        into.Add("prefix_len = " + r.PrefixLen.ToString(CultureInfo.InvariantCulture));
                    }

                    break;
                case X121RouteType.Dnic:
                    into.Add("dnic = " + r.Dnic);
                    break;
                case X121RouteType.Dcc:
                    into.Add("dcc = " + r.Dcc);
                    break;
                case X121RouteType.Regex:
                    into.Add("regex = " + r.Pattern);
                    break;
                case X121RouteType.Hunt:
                    into.Add("hunt = " + r.Address);
                    into.Add("hunt_group = " + r.HuntGroupId.ToString(CultureInfo.InvariantCulture));
                    break;
                case X121RouteType.Default:
                    into.Add("default = yes");
                    break;
                default:
                    throw new ArgumentException("route type " + r.Type + " cannot be written as a [route.N] section", nameof(r));
            }

            if (r.Type != X121RouteType.Hunt)
            {
                into.Add("port = " + PortWord(r.DestPort, table));
            }

            if (r.BackupPorts.Count > 0)
            {
                string[] words = new string[r.BackupPorts.Count];
                for (int b = 0; b < words.Length; b++)
                {
                    words[b] = PortWord(r.BackupPorts[b], table);
                }

                into.Add("backup_ports = " + string.Join(", ", words));
            }

            if (r.Direction == X121RouteDirection.Incoming)
            {
                into.Add("direction = incoming");
            }
            else if (r.Direction == X121RouteDirection.Outgoing)
            {
                into.Add("direction = outgoing");
            }

            if (!r.Enabled)
            {
                into.Add("enabled = no");
            }
        }

        // The port's name when the table finds that same port by it; otherwise its number.
        private static string PortWord(int port, X121RoutingTable table)
        {
            if (table.PortNames.TryGetValue(port, out string? name) && table.TryFindPort(name, out int back) && back == port)
            {
                return name;
            }

            return port.ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsHeader(string line)
        {
            string p = Trim(line);
            return p.Length > 0 && p[0] == '[';
        }

        private static bool IsRouteHeader(string line)
        {
            string p = Trim(line);
            if (!p.StartsWith("[route.", StringComparison.Ordinal))
            {
                return false;
            }

            int end = p.IndexOf(']');
            if (end <= 7)
            {
                return false;
            }

            for (int i = 7; i < end; i++)
            {
                if (p[i] < '0' || p[i] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        // A key line as IniConfig.Parse reads one: not blank, not a comment, not a header, with an equals sign.
        private static bool IsKeyLine(string line)
        {
            string p = Trim(line);
            return p.Length > 0 && p[0] != '#' && p[0] != ';' && p[0] != '[' && p.IndexOf('=') >= 0;
        }

        private static bool IsKeyOrHeader(string line)
        {
            return IsKeyLine(line) || IsHeader(line);
        }

        private static string Trim(string s)
        {
            return s.Trim(' ', '\t', '\r', '\n');
        }
    }
}
