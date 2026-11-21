//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.X25.Routing
{
    /// <summary>
    /// The one X.121 routing table and its one lookup. The switch (<c>SwitchCallRouter</c>) and the
    /// runtime (<c>X25Router</c>: incoming calls, the calls the local PAD places, outgoing packets) all
    /// ask this table, so the same table gives the same answer everywhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The number analysis follows X.121 (10/2000), text <c>Specs\X25\T-REC-X.121-200010-I!!PDF-E.txt</c>:
    /// </para>
    ///  - The table knows its own network: one or more own DNICs and the international prefix
    ///    (<see cref="OwnDnics"/>, <see cref="InternationalPrefix"/>). A number that starts with the prefix is
    ///    an international number with the prefix in front; the prefix is taken off, because it "is not part
    ///    of the international X.121 format and is not signalled across internetwork or international
    ///    boundaries" (7.8.1, lines 557-561). When a prefix is set, a number without it is a national number
    ///    (the network terminal number, 7.6.2) and is completed with the first own DNIC
    ///    (international data number = DNIC + NTN, 7.6.1, line 508). With no prefix set, numbers are taken
    ///    as they come. A number that starts with an escape code, 8, 9 or 0, is from another numbering plan
    ///    and is carried forward unchanged (7.9 and Table 2), unless it starts with the prefix itself.
    ///  - One lookup over that international number, most exact first: an exact number or a hunt group, a
    ///    pattern route, the longest full-address prefix, a DNIC with the digit after it, a DNIC, a country
    ///    code (DCC), and last the default route - usually the X.75 gateway. An originating or transit
    ///    network reads the DCC, then the network digit, then the digit after the DNIC (7.12.1, 7.12.2,
    ///    7.12.6, lines 644-675); X.121 does not say how a full-address prefix shorter than a DNIC ranks
    ///    against a DNIC, so a prefix of any length ranks above every DNIC, as this table did before.
    ///  - A route that would send the call back out of the port it came in on is skipped and the search
    ///    goes on: that would send the call to the party that placed it.
    ///  - A disabled route is skipped. A route for the other direction, or whose call user data pattern does
    ///    not match, is skipped.
    ///  - The winning route's port must exist; if it is out of order its backup port is used; a hunt group
    ///    takes its next member that is in service. With none in service the answer is out of order.
    ///  - The number sent on is the full international number, without the prefix, to a port that asks for
    ///    it (a gateway: 7.12.6, 7.12.7 "the complete international data number should always be sent");
    ///    otherwise the number as it came in, or the national number when the port asks for that. Which of
    ///    those a local terminal wants is not in X.121 or X.25, so it is the port's setting.
    /// <para>
    /// PVC entries are held for configuration and reporting but skipped by the lookup; they are handled
    /// at setup.
    /// </para>
    /// <para>
    /// Changing the table (adding, deleting, setting the own network) is not thread safe and is done while
    /// nothing is looking up. Looking up from several threads at once is safe: the hunt cursor and the hit
    /// counters are changed with interlocked operations.
    /// </para>
    /// </remarks>
    public sealed partial class X121RoutingTable
    {
        /// <summary>
        /// Number of leading digits compared for a DNIC match (X.121 7.2: the DNIC is four digits).
        /// </summary>
        public const int DnicLength = 4;

        /// <summary>
        /// Number of leading digits compared for a country match (X.121 7.2: the DCC is three digits).
        /// </summary>
        public const int DccLength = 3;

        /// <summary>
        /// The port number that means "this node": a route with it as its port delivers the call to a local
        /// service (the runtime's TAD, a waiting program). Never a real port.
        /// </summary>
        public const int LocalPort = -2;

        /// <summary>
        /// The name <see cref="LocalPort"/> is shown and written with.
        /// </summary>
        public const string LocalPortName = "local";

        private readonly List<X121Route> _routes = new List<X121Route>();
        private readonly List<X121HuntGroup> _huntGroups = new List<X121HuntGroup>();
        private readonly List<string> _ownDnics = new List<string>();
        private readonly Dictionary<int, string> _portNames = new Dictionary<int, string>();
        private string _internationalPrefix = string.Empty;

        /// <summary>
        /// The routing entries, in the order they were added (which is not the lookup order; see
        /// <see cref="RoutesInLookupOrder"/>).
        /// </summary>
        public IReadOnlyList<X121Route> Routes => _routes;

        /// <summary>
        /// The hunt groups, indexed by <see cref="X121Route.HuntGroupId"/>.
        /// </summary>
        public IReadOnlyList<X121HuntGroup> HuntGroups => _huntGroups;

        /// <summary>
        /// This network's own DNICs (four digits, or five with the digit after the DNIC), first one first.
        /// A local number is completed with the first one's four DNIC digits. Empty when none is set.
        /// </summary>
        public IReadOnlyList<string> OwnDnics => _ownDnics;

        /// <summary>
        /// The international prefix of this network (X.121 7.8): a called number that starts with it is an
        /// international number with the prefix in front. Empty (the default) means the network uses no
        /// prefix and numbers are taken as they come.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown when the value is not 1 to 3 digits or empty.
        /// </exception>
        public string InternationalPrefix
        {
            get => _internationalPrefix;
            set
            {
                string v = value ?? string.Empty;
                if (!X121RouteRules.CheckInternationalPrefix(v, out string error))
                {
                    throw new ArgumentException(error, nameof(value));
                }

                _internationalPrefix = v;
            }
        }

        /// <summary>
        /// Sets the own DNICs, replacing any set before.
        /// </summary>
        /// <param name="dnics">
        /// The DNICs, first one first. Blank entries are left out.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when one of them is not a valid DNIC (<see cref="X121RouteRules.CheckDnic"/>).
        /// </exception>
        public void SetOwnDnics(IEnumerable<string> dnics)
        {
            if (dnics == null)
            {
                throw new ArgumentNullException(nameof(dnics));
            }

            List<string> checkedList = new List<string>();
            foreach (string d in dnics)
            {
                string t = (d ?? string.Empty).Trim();
                if (t.Length == 0)
                {
                    continue;
                }

                if (!X121RouteRules.CheckDnic(t, out string error))
                {
                    throw new ArgumentException("own DNIC " + t + ": " + error, nameof(dnics));
                }

                checkedList.Add(t);
            }

            _ownDnics.Clear();
            _ownDnics.AddRange(checkedList);
        }

        /// <summary>
        /// Gives a port a name, for the routing screen, the explanations and the config files.
        /// </summary>
        /// <param name="port">
        /// The port number.
        /// </param>
        /// <param name="name">
        /// The name; empty removes it.
        /// </param>
        public void SetPortName(int port, string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                _portNames.Remove(port);
                return;
            }

            _portNames[port] = name;
        }

        /// <summary>
        /// The name of a port: the one set with <see cref="SetPortName"/>, <c>local</c> for
        /// <see cref="LocalPort"/>, otherwise <c>port N</c>.
        /// </summary>
        /// <param name="port">
        /// The port number.
        /// </param>
        /// <returns>
        /// The name.
        /// </returns>
        public string GetPortName(int port)
        {
            if (_portNames.TryGetValue(port, out string? name))
            {
                return name;
            }

            if (port == LocalPort)
            {
                return LocalPortName;
            }

            return port < 0 ? "none" : "port " + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The named ports, by number.
        /// </summary>
        public IReadOnlyDictionary<int, string> PortNames => _portNames;

        /// <summary>
        /// Finds a port by its name or its number. The name compare ignores case. <c>local</c> is
        /// <see cref="LocalPort"/> unless a real port carries that name.
        /// </summary>
        /// <param name="nameOrNumber">
        /// A port name, or a port number written in digits.
        /// </param>
        /// <param name="port">
        /// The port number; -1 when not found.
        /// </param>
        /// <returns>
        /// True when found.
        /// </returns>
        public bool TryFindPort(string nameOrNumber, out int port)
        {
            port = -1;
            string s = (nameOrNumber ?? string.Empty).Trim();
            if (s.Length == 0)
            {
                return false;
            }

            foreach (KeyValuePair<int, string> kv in _portNames)
            {
                if (string.Equals(kv.Value, s, StringComparison.OrdinalIgnoreCase))
                {
                    port = kv.Key;
                    return true;
                }
            }

            if (string.Equals(s, LocalPortName, StringComparison.OrdinalIgnoreCase))
            {
                port = LocalPort;
                return true;
            }

            if (X121RouteRules.IsDigits(s) && s.Length <= 6)
            {
                port = int.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Appends a routing entry and returns its index.
        /// </summary>
        /// <param name="route">
        /// The entry to add.
        /// </param>
        /// <returns>
        /// The index the entry was added at.
        /// </returns>
        public int AddRoute(X121Route route)
        {
            if (route == null)
            {
                throw new ArgumentNullException(nameof(route));
            }

            _routes.Add(route);
            return _routes.Count - 1;
        }

        /// <summary>
        /// Deletes the routing entry at an index, shifting later entries down.
        /// </summary>
        /// <param name="index">
        /// Zero-based index to delete.
        /// </param>
        /// <returns>
        /// True if the index was valid and an entry was removed.
        /// </returns>
        public bool DeleteRouteAt(int index)
        {
            if (index < 0 || index >= _routes.Count)
            {
                return false;
            }

            _routes.RemoveAt(index);
            return true;
        }

        /// <summary>
        /// Deletes one routing entry.
        /// </summary>
        /// <param name="route">
        /// The entry.
        /// </param>
        /// <returns>
        /// True when it was in the table.
        /// </returns>
        public bool RemoveRoute(X121Route route)
        {
            return _routes.Remove(route);
        }

        /// <summary>
        /// Removes every route and hunt group. The own network and the port names stay.
        /// </summary>
        public void ClearRoutes()
        {
            _routes.Clear();
            _huntGroups.Clear();
        }

        /// <summary>
        /// Appends a hunt group.
        /// </summary>
        /// <param name="group">
        /// The hunt group to add.
        /// </param>
        public void AddHuntGroup(X121HuntGroup group)
        {
            _huntGroups.Add(group);
        }

        /// <summary>
        /// Resolves the egress port for a called address, advancing hunt-group round-robin state. Kept for
        /// callers that need only the port; it is <see cref="Resolve(string, int, Func{int, X121PortStatus}?, bool, X121RouteDirection, byte[])"/> with every port taken to be in
        /// service, and a local route counts as found.
        /// </summary>
        /// <param name="calledAddress">
        /// The X.121 address to route.
        /// </param>
        /// <param name="inPort">
        /// The ingress port (a resolved port equal to this is rejected).
        /// </param>
        /// <param name="outPort">
        /// Receives the resolved egress port on success.
        /// </param>
        /// <returns>
        /// True if a usable route was found.
        /// </returns>
        public bool TryLookup(string calledAddress, int inPort, out int outPort)
        {
            X121RouteResult r = Resolve(calledAddress, inPort, null);
            outPort = r.IsRouted ? r.Port : -1;
            return r.IsRouted;
        }

        /// <summary>
        /// Resolves the primary route plus any distinct backup ports for failover, in preference order.
        /// </summary>
        /// <param name="calledAddress">
        /// The X.121 address to route.
        /// </param>
        /// <param name="inPort">
        /// The ingress port.
        /// </param>
        /// <param name="maxPorts">
        /// Maximum number of candidate ports to return.
        /// </param>
        /// <returns>
        /// The candidate egress ports, primary first (empty if none): the winner's port, then the backup
        /// ports of every route that matches, in lookup order, without repeats and never the ingress port.
        /// </returns>
        public IReadOnlyList<int> LookupMulti(string calledAddress, int inPort, int maxPorts)
        {
            return LookupMulti(calledAddress, inPort, maxPorts, X121RouteDirection.Both, null);
        }
    }
}
