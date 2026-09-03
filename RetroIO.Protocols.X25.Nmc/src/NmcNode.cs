using System.Collections.Generic;

namespace RetroIO.Protocols.X25.Nmc
{
    /// <summary>
    /// A switch discovered on the management plane, as it is walked: identity, live counters, neighbours,
    /// and routing table. The discovery engine populates and updates it; a viewer renders it.
    /// </summary>
    public sealed class NmcNode
    {
        /// <summary>
        /// The stable key for this node in the discovery model (the management address it was reached on).
        /// </summary>
        public string Key { get; }

        /// <summary>
        /// The management X.121 address the node answers on.
        /// </summary>
        public string MgmtAddr { get; }

        /// <summary>
        /// The discovery depth (0 for the seed), used by a radial layout.
        /// </summary>
        public int Depth { get; set; }

        /// <summary>
        /// True once the node has authenticated and answered at least one request.
        /// </summary>
        public bool Reachable { get; set; }

        /// <summary>
        /// The node's identity, or null until its <c>INFO</c> arrives.
        /// </summary>
        public NmcNodeInfo? Info { get; set; }

        /// <summary>
        /// The node's latest counters, or null until its first <c>STATS</c> arrives.
        /// </summary>
        public NmcStats? Stats { get; set; }

        /// <summary>
        /// The node's neighbours (one per trunk with a declared peer).
        /// </summary>
        public List<NmcNeighbour> Neighbours { get; } = new List<NmcNeighbour>();

        /// <summary>
        /// The node's routing table.
        /// </summary>
        public List<NmcRoute> Routes { get; } = new List<NmcRoute>();

        /// <summary>
        /// Creates a node keyed by its management address.
        /// </summary>
        /// <param name="mgmtAddr">
        /// The management address the node was reached on; also its <see cref="Key"/>.
        /// </param>
        public NmcNode(string mgmtAddr)
        {
            Key = mgmtAddr;
            MgmtAddr = mgmtAddr;
        }

        /// <summary>
        /// The display name: the node's reported name once known, else its management address.
        /// </summary>
        public string DisplayName => Info != null && Info.Name.Length > 0 ? Info.Name : MgmtAddr;
    }
}
