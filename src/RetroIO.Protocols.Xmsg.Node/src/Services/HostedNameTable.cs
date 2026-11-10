//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Globalization;

using RetroIO.Protocols.Xmsg.ListNames;
using RetroIO.Protocols.Xmsg.ListRouting;

namespace RetroIO.Protocols.Xmsg.Node.Services
{
    /// <summary>
    /// The name table a hosted node answers LIST-NAMES (<c>XSGNI</c>) from, built from what the host
    /// already knows: its own system, the systems in its routing table and its registered servers.
    /// </summary>
    /// <remarks>
    /// <para><b>What the rows are</b></para>
    /// <list type="bullet">
    ///   <item><description>This node's own system name at port word <c>0x0001</c>.</description></item>
    ///   <item><description>Every other system in the routing table, at port word <c>0x0001</c>.</description></item>
    ///   <item><description>Every registered server, at its real wire port word (<see cref="IXmsgServer.WirePort"/>), under this node's system number.</description></item>
    /// </list>
    /// <para><b>Names</b></para>
    /// <para>
    /// A system's name is <c>"D"</c> followed by its number in decimal (<c>D100</c>, <c>D19999</c>),
    /// because the routing table holds only numbers and every system name in the 2026-11-10 D100
    /// capture has that form. libxmsg uses the same default. The own name can be changed with
    /// <see cref="OwnSystemName"/>.
    /// </para>
    /// <para><b>No free-connection counts</b></para>
    /// <para>
    /// Server rows carry no free-connections count (reply parameter 3). The capture shows it only for
    /// some ports (<c>*XFTRA</c>, <c>*FA-FSA</c>, <c>*FA-FSA-I</c>) and NOT for <c>*TADADM</c>, which is
    /// the one server both D100 and this code have. Nothing in <see cref="IXmsgServer"/> says whether a
    /// server is such a connection port, so no count is sent rather than a guessed one.
    /// </para>
    /// <para><b>Always current</b></para>
    /// <para>
    /// The rows are rebuilt on every lookup from the server host's live server list, so a server
    /// registered later is listed at once. The routing table is the one handed in at construction;
    /// the node host has no way to change it later.
    /// </para>
    /// </remarks>
    public sealed class HostedNameTable : INameTable
    {
        // The port word of a system-name row (VERIFIED: D100 is 0x00640001).
        private const ushort SystemNamePortWord = 0x0001;

        private readonly ushort _ownSystem;
        private readonly IReadOnlyList<RoutingTableEntry> _routes;
        private readonly XmsgServerHost _serverHost;
        private string _ownSystemName;

        /// <summary>
        /// Initialises a name table for a hosted node.
        /// </summary>
        /// <param name="ownSystem">
        /// This node's system number.
        /// </param>
        /// <param name="routes">
        /// The routes this node advertises; every system in it other than this node gets a row.
        /// </param>
        /// <param name="serverHost">
        /// The server host whose registered servers are listed.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="routes"/> or <paramref name="serverHost"/> is null.
        /// </exception>
        public HostedNameTable(ushort ownSystem, IReadOnlyList<RoutingTableEntry> routes, XmsgServerHost serverHost)
        {
            _ownSystem = ownSystem;
            _routes = routes ?? throw new ArgumentNullException(nameof(routes));
            _serverHost = serverHost ?? throw new ArgumentNullException(nameof(serverHost));
            _ownSystemName = DefaultSystemName(ownSystem);
        }

        /// <summary>
        /// Gets or sets the name of this node's own system row. Defaults to <c>"D"</c> and the system
        /// number, for example <c>D19999</c>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown when the value is null or empty.
        /// </exception>
        public string OwnSystemName
        {
            get
            {
                return _ownSystemName;
            }

            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    throw new ArgumentException("The own system name must not be empty.", nameof(value));
                }

                _ownSystemName = value;
            }
        }

        /// <summary>
        /// Builds the default name of a system: <c>"D"</c> and its number in decimal.
        /// </summary>
        /// <param name="system">
        /// The system number.
        /// </param>
        /// <returns>
        /// The name, for example <c>D100</c>.
        /// </returns>
        public static string DefaultSystemName(ushort system)
        {
            return "D" + system.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Finds the first row whose magic number is greater than or equal to
        /// <paramref name="fromMagic"/>.
        /// </summary>
        /// <param name="fromMagic">
        /// The lower bound asked for.
        /// </param>
        /// <param name="entry">
        /// When this method returns <c>true</c>, the matching row; otherwise a default row.
        /// </param>
        /// <returns>
        /// <c>true</c> if a matching row exists; otherwise <c>false</c>.
        /// </returns>
        public bool TryLookupFrom(uint fromMagic, out NameTableEntry entry)
        {
            return new InMemoryNameTable(BuildRows()).TryLookupFrom(fromMagic, out entry);
        }

        /// <summary>
        /// Builds the table's rows as they stand now, in no particular order.
        /// </summary>
        /// <returns>
        /// The rows; one per system and one per registered server.
        /// </returns>
        public IReadOnlyList<NameTableEntry> BuildRows()
        {
            IReadOnlyList<XmsgServerInfo> servers = _serverHost.DescribeServers();
            List<NameTableEntry> rows = new List<NameTableEntry>(1 + _routes.Count + servers.Count);
            rows.Add(new NameTableEntry(Magic(_ownSystem, SystemNamePortWord), _ownSystemName));

            for (int i = 0; i < _routes.Count; i++)
            {
                ushort system = _routes[i].System;

                // System 0 is the "not known" answer of a routing reply, never a real system.
                if (system == 0 || HasSystem(rows, system))
                {
                    continue;
                }

                rows.Add(new NameTableEntry(Magic(system, SystemNamePortWord), DefaultSystemName(system)));
            }

            for (int i = 0; i < servers.Count; i++)
            {
                rows.Add(new NameTableEntry(Magic(_ownSystem, servers[i].WirePort), servers[i].Name));
            }

            return rows;
        }

        /// <summary>
        /// Packs a system number and a port word into a 32-bit magic number.
        /// </summary>
        private static uint Magic(ushort system, ushort portWord)
        {
            return ((uint)system << 16) | portWord;
        }

        /// <summary>
        /// Returns true when the rows already hold a system-name row for a system.
        /// </summary>
        private static bool HasSystem(List<NameTableEntry> rows, ushort system)
        {
            uint magic = Magic(system, SystemNamePortWord);
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Magic == magic)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
