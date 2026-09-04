namespace RetroIO.Protocols.X25.Nmc
{
    /// <summary>
    /// A switch's identity, decoded from an <c>INFO</c> management response.
    /// </summary>
    public sealed class NmcNodeInfo
    {
        /// <summary>
        /// The node's DNIC (the stable identity used to dedup discovered nodes).
        /// </summary>
        public string Dnic { get; set; } = string.Empty;

        /// <summary>
        /// The friendly node name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The node's location text, or empty.
        /// </summary>
        public string Location { get; set; } = string.Empty;

        /// <summary>
        /// The node's free-form map coordinates hint, or empty.
        /// </summary>
        public string Coords { get; set; } = string.Empty;
    }

    /// <summary>
    /// A switch's aggregate counters, decoded from a <c>STATS</c> management response.
    /// </summary>
    public sealed class NmcStats
    {
        /// <summary>
        /// The node's DNIC.
        /// </summary>
        public string Dnic { get; set; } = string.Empty;

        /// <summary>
        /// The number of ports.
        /// </summary>
        public int Ports { get; set; }

        /// <summary>
        /// The number of active virtual circuits (stitches).
        /// </summary>
        public int Stitches { get; set; }

        /// <summary>
        /// The lifetime calls received.
        /// </summary>
        public long Calls { get; set; }

        /// <summary>
        /// The lifetime packets received.
        /// </summary>
        public long PacketsIn { get; set; }

        /// <summary>
        /// The lifetime packets sent.
        /// </summary>
        public long PacketsOut { get; set; }

        /// <summary>
        /// The lifetime bytes switched.
        /// </summary>
        public long Bytes { get; set; }

        /// <summary>
        /// The node uptime in seconds.
        /// </summary>
        public long UptimeSeconds { get; set; }
    }

    /// <summary>
    /// A neighbour reachable over a trunk, decoded from a <c>NEIGHBOUR</c> topology response. Its
    /// <see cref="MgmtAddr"/> is what the discovery walk calls to reach the neighbour.
    /// </summary>
    public sealed class NmcNeighbour
    {
        /// <summary>
        /// The neighbour's declared name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The neighbour's in-band management X.121 address, or empty when it has none (a walk stub).
        /// </summary>
        public string MgmtAddr { get; set; } = string.Empty;

        /// <summary>
        /// The local trunk port id the neighbour is reached through (correlates to a route's out-port).
        /// </summary>
        public int Via { get; set; }

        /// <summary>
        /// True when the trunk to this neighbour is currently linked.
        /// </summary>
        public bool Up { get; set; }
    }

    /// <summary>
    /// One routing-table entry, decoded from a <c>ROUTE</c> management response.
    /// </summary>
    public sealed class NmcRoute
    {
        /// <summary>
        /// The route type: <c>exact</c>, <c>prefix</c>, <c>dnic</c>, <c>default</c>, <c>hunt</c>, or <c>pvc</c>.
        /// </summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// The matched X.121 address or prefix (empty for a default route).
        /// </summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>
        /// The out-port a matching call is forwarded to.
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// The prefix length for a prefix route, else 0.
        /// </summary>
        public int PrefixLen { get; set; }
    }
}
