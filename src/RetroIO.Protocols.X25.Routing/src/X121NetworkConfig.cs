//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;

namespace RetroIO.Protocols.X25.Routing
{
    /// <summary>
    /// The routing table as it is written in a configuration file (the runtime's JSON file): the own
    /// network and the routes, with ports named by their names. <see cref="X121TableBuilder"/> turns it
    /// into an <see cref="X121RoutingTable"/> and back.
    /// </summary>
    public sealed class X121NetworkConfig
    {
        /// <summary>
        /// This network's own DNICs, first one first (4 digits, or 5 with the digit after the DNIC).
        /// </summary>
        public List<string> OwnDnics { get; set; } = new List<string>();

        /// <summary>
        /// The international prefix (X.121 7.8); empty for none.
        /// </summary>
        public string InternationalPrefix { get; set; } = string.Empty;

        /// <summary>
        /// The routes, in the order they are written. The lookup order does not depend on it, except
        /// between two routes of the same kind and length (see <see cref="X121RoutingTable"/>).
        /// </summary>
        public List<X121RouteConfig> Routes { get; set; } = new List<X121RouteConfig>();
    }

    /// <summary>
    /// One route as it is written in a configuration file.
    /// </summary>
    public sealed class X121RouteConfig
    {
        /// <summary>
        /// The name people know the route by.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The kind, one of: <c>exact</c> (a local number or a full address), <c>prefix</c>, <c>dnic</c>,
        /// <c>dcc</c>, <c>default</c>, <c>hunt</c>, <c>regex</c>.
        /// </summary>
        public string Kind { get; set; } = "exact";

        /// <summary>
        /// The digits the route matches (the number, the prefix, the DNIC or the DCC), or the pattern of a
        /// regex route; empty for a default route.
        /// </summary>
        public string Match { get; set; } = string.Empty;

        /// <summary>
        /// The port the call goes to, by name (or by number); <c>local</c> for this node. Not used by a
        /// hunt route.
        /// </summary>
        public string Port { get; set; } = string.Empty;

        /// <summary>
        /// The ports tried in this order when <see cref="Port"/> is out of order (X.110 3.5: alternative
        /// route 1, alternative route 2, ...); empty for none.
        /// </summary>
        public List<string> BackupPorts { get; set; } = new List<string>();

        /// <summary>
        /// The member ports of a hunt route, in turn order.
        /// </summary>
        public List<string> HuntPorts { get; set; } = new List<string>();

        /// <summary>
        /// False to keep the route without using it.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// A free note.
        /// </summary>
        public string Note { get; set; } = string.Empty;

        /// <summary>
        /// Which calls the route is for: <c>both</c> (the default), <c>incoming</c> or <c>outgoing</c>.
        /// </summary>
        public string Direction { get; set; } = "both";

        /// <summary>
        /// An optional regular expression the call user data must match as well; empty for none.
        /// </summary>
        public string CallUserDataPattern { get; set; } = string.Empty;

        /// <summary>
        /// Decides between two routes that are otherwise equal; higher wins (<see cref="X121Route.Priority"/>).
        /// 0 for none. Kept from the older route list.
        /// </summary>
        public int Priority { get; set; }
    }
}
