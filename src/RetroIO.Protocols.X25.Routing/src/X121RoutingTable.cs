using System.Collections.Generic;

namespace RetroIO.Protocols.X25.Routing
{
    /// <summary>
    /// An X.121 routing table implementing the switch routing precedence, plus an additive
    /// <see cref="X121RouteType.Regex"/> rule so the same engine can express endpoint-style pattern routing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lookup runs as four passes, in this order, and stops at the first pass that produces a port
    /// the call may actually go to:
    /// </para>
    /// <list type="number">
    /// <item><description>Exact, Hunt and Regex entries, in table order.</description></item>
    /// <item><description>The longest matching Prefix.</description></item>
    /// <item><description>A DNIC match, which is the whole of this pass: it ranks below every
    /// prefix, however short, so it cannot be reached while any prefix matched.</description></item>
    /// <item><description>The Default route.</description></item>
    /// </list>
    /// <para>
    /// A port equal to the ingress port is never the answer: routing a call back out of the
    /// interface it arrived on sends it to the party that placed it. Such a candidate is skipped and
    /// the search carries on, so a hunt group with one member on the ingress port still finds
    /// another, and an exact route pointing back the way the call came falls through to the prefix
    /// and default routes instead of forming a loop.
    /// </para>
    /// <para>
    /// Written as separate passes on purpose. The single loop this replaces mixed the passes
    /// together and got two things wrong: an Exact or Regex hit returned without the ingress check
    /// at all, and a DNIC seen before a prefix claimed a match length of four, so a three-digit
    /// prefix later in the table lost to it and the same table gave different answers depending on
    /// the order it was written in.
    /// </para>
    /// <para>
    /// PVC entries are held for configuration and reporting but skipped by dynamic lookup; they are
    /// handled at setup.
    /// </para>
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
            if (TryNamedRoutes(calledAddress, inPort, out outPort))
            {
                return true;
            }

            if (TryLongestPrefix(calledAddress, inPort, out outPort))
            {
                return true;
            }

            if (TryDnic(calledAddress, inPort, out outPort))
            {
                return true;
            }

            return TryDefault(inPort, out outPort);
        }

        /// <summary>
        /// Pass one: Exact, Hunt and Regex entries, in table order.
        /// </summary>
        /// <param name="calledAddress">
        /// The X.121 address to route.
        /// </param>
        /// <param name="inPort">
        /// The port the call arrived on.
        /// </param>
        /// <param name="outPort">
        /// The port to route to.
        /// </param>
        /// <returns>
        /// True when one of them matched and named a port other than the ingress port.
        /// </returns>
        private bool TryNamedRoutes(string calledAddress, int inPort, out int outPort)
        {
            outPort = -1;

            for (int i = 0; i < _routes.Count; i++)
            {
                X121Route r = _routes[i];

                if (r.Type == X121RouteType.Exact)
                {
                    if (calledAddress == r.Address && r.DestPort != inPort)
                    {
                        outPort = r.DestPort;
                        return true;
                    }

                    continue;
                }

                if (r.Type == X121RouteType.Regex)
                {
                    // Extension rule, ranked with Exact: first match in table order wins.
                    if (r.RegexMatches(calledAddress) && r.DestPort != inPort)
                    {
                        outPort = r.DestPort;
                        return true;
                    }

                    continue;
                }

                if (r.Type != X121RouteType.Hunt || calledAddress != r.Address)
                {
                    continue;
                }

                if (r.HuntGroupId < 0 || r.HuntGroupId >= _huntGroups.Count)
                {
                    continue;
                }

                X121HuntGroup hg = _huntGroups[r.HuntGroupId];
                if (hg.MemberCount == 0)
                {
                    continue;
                }

                // Round-robin over the members, skipping any that would send the call back out of
                // the port it arrived on. The cursor advances for every member tried, so a busy
                // group keeps rotating rather than stalling on the one that cannot be used.
                for (int tried = 0; tried < hg.MemberCount; tried++)
                {
                    int member = hg.Members[hg.NextIdx % hg.MemberCount];
                    hg.NextIdx++;

                    if (member != inPort)
                    {
                        outPort = member;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Pass two: the longest matching prefix.
        /// </summary>
        /// <param name="calledAddress">
        /// The X.121 address to route.
        /// </param>
        /// <param name="inPort">
        /// The port the call arrived on.
        /// </param>
        /// <param name="outPort">
        /// The port to route to.
        /// </param>
        /// <returns>
        /// True when a prefix matched and named a port other than the ingress port.
        /// </returns>
        private bool TryLongestPrefix(string calledAddress, int inPort, out int outPort)
        {
            outPort = -1;
            int bestLen = 0;

            for (int i = 0; i < _routes.Count; i++)
            {
                X121Route r = _routes[i];
                if (r.Type != X121RouteType.Prefix || r.PrefixLen <= 0)
                {
                    continue;
                }

                if (r.DestPort == inPort || r.PrefixLen <= bestLen)
                {
                    continue;
                }

                if (StartsWithN(calledAddress, r.Address, r.PrefixLen))
                {
                    bestLen = r.PrefixLen;
                    outPort = r.DestPort;
                }
            }

            return outPort >= 0;
        }

        /// <summary>
        /// Pass three: a DNIC match, which is the first four digits of the address.
        /// </summary>
        /// <remarks>
        /// A whole pass of its own rather than a prefix of length four, because it ranks below every
        /// prefix however short. Treating it as a length-four prefix made a three-digit prefix lose
        /// to it.
        /// </remarks>
        /// <param name="calledAddress">
        /// The X.121 address to route.
        /// </param>
        /// <param name="inPort">
        /// The port the call arrived on.
        /// </param>
        /// <param name="outPort">
        /// The port to route to.
        /// </param>
        /// <returns>
        /// True when a DNIC matched and named a port other than the ingress port.
        /// </returns>
        private bool TryDnic(string calledAddress, int inPort, out int outPort)
        {
            outPort = -1;

            for (int i = 0; i < _routes.Count; i++)
            {
                X121Route r = _routes[i];
                if (r.Type != X121RouteType.Dnic || r.DestPort == inPort)
                {
                    continue;
                }

                if (StartsWithN(calledAddress, r.Dnic, DnicLength))
                {
                    outPort = r.DestPort;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Pass four: the default route.
        /// </summary>
        /// <param name="inPort">
        /// The port the call arrived on.
        /// </param>
        /// <param name="outPort">
        /// The port to route to.
        /// </param>
        /// <returns>
        /// True when a default route exists and is not the ingress port.
        /// </returns>
        private bool TryDefault(int inPort, out int outPort)
        {
            outPort = -1;

            for (int i = 0; i < _routes.Count; i++)
            {
                X121Route r = _routes[i];
                if (r.Type == X121RouteType.Default && r.DestPort != inPort)
                {
                    outPort = r.DestPort;
                    return true;
                }
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
