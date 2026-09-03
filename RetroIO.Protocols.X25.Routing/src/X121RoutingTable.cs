using System.Collections.Generic;

namespace RetroIO.Protocols.X25.Routing
{
    /// <summary>
    /// An X.121 routing table implementing the switch routing precedence, plus an additive
    /// <see cref="X121RouteType.Regex"/> rule so the same engine can express endpoint-style pattern routing.
    /// </summary>
    /// <remarks>
    /// Lookup precedence (using only the structured rule types):
    ///  - the first matching Exact / Hunt / Regex entry, in table order, wins immediately.
    ///  - otherwise the longest matching Prefix wins.
    ///  - otherwise a DNIC match (treated as a length-4 prefix, and only if no prefix matched).
    ///  - otherwise the Default route.
    ///  - a resolved port equal to the ingress port is rejected (never route back the way it came).
    /// PVC entries are held for configuration/reporting but skipped by dynamic lookup (handled at setup).
    /// </remarks>
    public sealed class X121RoutingTable
    {
        /// <summary>
        /// Number of leading digits compared for a DNIC match.
        /// </summary>
        public const int DnicLength = 4;

        private readonly List<X121Route> _routes = new List<X121Route>();
        private readonly List<X121HuntGroup> _huntGroups = new List<X121HuntGroup>();

        /// <summary>
        /// The routing entries, in evaluation order.
        /// </summary>
        public IReadOnlyList<X121Route> Routes => _routes;

        /// <summary>
        /// The hunt groups, indexed by <see cref="X121Route.HuntGroupId"/>.
        /// </summary>
        public IReadOnlyList<X121HuntGroup> HuntGroups => _huntGroups;

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
        /// Resolves the egress port for a called address, advancing hunt-group round-robin state.
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
            outPort = -1;

            int bestPrefix = -1;
            int bestPrefixLen = 0;
            int defaultPort = -1;

            for (int i = 0; i < _routes.Count; i++)
            {
                X121Route r = _routes[i];

                switch (r.Type)
                {
                    case X121RouteType.Exact:
                        if (calledAddress == r.Address)
                        {
                            outPort = r.DestPort;
                            return true;
                        }
                        break;

                    case X121RouteType.Regex:
                        // Extension rule, evaluated like Exact: first match in table order wins immediately.
                        if (r.RegexMatches(calledAddress))
                        {
                            outPort = r.DestPort;
                            return true;
                        }
                        break;

                    case X121RouteType.Hunt:
                        if (calledAddress == r.Address)
                        {
                            if (r.HuntGroupId >= 0 && r.HuntGroupId < _huntGroups.Count)
                            {
                                X121HuntGroup hg = _huntGroups[r.HuntGroupId];
                                if (hg.MemberCount > 0)
                                {
                                    outPort = hg.Members[hg.NextIdx % hg.MemberCount];
                                    hg.NextIdx++;
                                    return true;
                                }
                            }
                        }
                        break;

                    case X121RouteType.Prefix:
                        if (r.PrefixLen > 0 && StartsWithN(calledAddress, r.Address, r.PrefixLen))
                        {
                            // Longest prefix wins.
                            if (r.PrefixLen > bestPrefixLen)
                            {
                                bestPrefixLen = r.PrefixLen;
                                bestPrefix = r.DestPort;
                            }
                        }
                        break;

                    case X121RouteType.Dnic:
                        if (StartsWithN(calledAddress, r.Dnic, DnicLength))
                        {
                            // DNIC is lower priority than any prefix: only apply if none set yet.
                            if (bestPrefix < 0)
                            {
                                bestPrefix = r.DestPort;
                                bestPrefixLen = DnicLength;
                            }
                        }
                        break;

                    case X121RouteType.Default:
                        defaultPort = r.DestPort;
                        break;

                    case X121RouteType.Pvc:
                        // Handled separately at setup; not part of dynamic lookup.
                        break;
                }
            }

            // Don't route back to the ingress port.
            if (bestPrefix >= 0 && bestPrefix != inPort)
            {
                outPort = bestPrefix;
                return true;
            }

            if (defaultPort >= 0 && defaultPort != inPort)
            {
                outPort = defaultPort;
                return true;
            }

            return false;
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
        /// The candidate egress ports, primary first (empty if none).
        /// </returns>
        public IReadOnlyList<int> LookupMulti(string calledAddress, int inPort, int maxPorts)
        {
            List<int> result = new List<int>();

            int primary = -1;
            if (TryLookup(calledAddress, inPort, out int p))
            {
                primary = p;
                result.Add(primary);
            }

            for (int i = 0; i < _routes.Count && result.Count < maxPorts; i++)
            {
                X121Route r = _routes[i];
                if (r.BackupPort < 0 || r.BackupPort == inPort || r.BackupPort == primary)
                {
                    continue;
                }

                bool matches = false;
                switch (r.Type)
                {
                    case X121RouteType.Exact:
                        matches = calledAddress == r.Address;
                        break;
                    case X121RouteType.Prefix:
                        matches = r.PrefixLen > 0 && StartsWithN(calledAddress, r.Address, r.PrefixLen);
                        break;
                    case X121RouteType.Dnic:
                        matches = StartsWithN(calledAddress, r.Dnic, DnicLength);
                        break;
                    case X121RouteType.Default:
                        matches = true;
                        break;
                }

                if (!matches)
                {
                    continue;
                }

                bool dup = false;
                for (int j = 0; j < result.Count; j++)
                {
                    if (result[j] == r.BackupPort)
                    {
                        dup = true;
                        break;
                    }
                }

                if (!dup)
                {
                    result.Add(r.BackupPort);
                }
            }

            return result;
        }

        /// <summary>
        /// Compares the first <paramref name="n"/> characters of two strings: both must be at least
        /// <paramref name="n"/> long and equal over that span.
        /// </summary>
        /// <param name="value">
        /// The address being tested.
        /// </param>
        /// <param name="pattern">
        /// The prefix/DNIC to compare against.
        /// </param>
        /// <param name="n">
        /// Number of leading characters to compare.
        /// </param>
        /// <returns>
        /// True if both strings share their first <paramref name="n"/> characters.
        /// </returns>
        private static bool StartsWithN(string value, string pattern, int n)
        {
            if (value.Length < n || pattern.Length < n)
            {
                return false;
            }

            for (int k = 0; k < n; k++)
            {
                if (value[k] != pattern[k])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
