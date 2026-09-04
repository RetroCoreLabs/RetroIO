using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Node.Seam
{
    /// <summary>
    /// Reports a datagram that was forwarded from one link to another.
    /// </summary>
    /// <param name="fromLink">
    /// The link the datagram arrived on.
    /// </param>
    /// <param name="toLink">
    /// The link it was forwarded out of.
    /// </param>
    /// <param name="destinationNode">
    /// The destination node number the routing decision was made on.
    /// </param>
    public delegate void DatagramRelayed(ILink fromLink, ILink toLink, ushort destinationNode);

    /// <summary>
    /// Reports a datagram that could not be forwarded, with the reason.
    /// </summary>
    /// <param name="fromLink">
    /// The link the datagram arrived on.
    /// </param>
    /// <param name="destinationNode">
    /// The destination node number, when one could be read.
    /// </param>
    /// <param name="reason">
    /// A short human-readable reason (for logs).
    /// </param>
    public delegate void DatagramNotRelayed(ILink fromLink, ushort destinationNode, string reason);

    /// <summary>
    /// Forwards SINTRAN datagrams between links, making this node a route-through relay: a
    /// datagram arriving on one link addressed to a node reachable on another is re-marked as
    /// relayed and sent out of that other link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what lets a node in the middle carry traffic for two others that cannot reach each
    /// other directly. In the live topology D103 reaches D19999 through D100, because D103 has only
    /// an HDLC line and D19999 is only on the Ethernet segment.
    /// </para>
    /// <para><b>Routing is a static table, and deliberately so</b></para>
    /// <para>
    /// Each link is registered against the node numbers reachable through it. There is NO route
    /// learning and NO route discovery: how COSMOS distributes reachability is not something this
    /// project has established from captures, and inventing a protocol for it would be fabrication
    /// of exactly the kind that has cost this project time before. A caller that knows its topology
    /// states it; a caller that does not, cannot relay.
    /// </para>
    /// <para><b>What it will not do</b></para>
    /// <para>
    /// A datagram is never sent back out of the link it came from (that would be a routing loop),
    /// and a datagram addressed to a node this relay has no route for is dropped and reported
    /// rather than flooded - with no reachability protocol established, flooding would be a guess.
    /// </para>
    /// </remarks>
    public sealed class DatagramRelay
    {
        // Destination node number -> the link to reach it on. A plain dictionary, because the
        // routing table is stated by the caller and does not change under traffic.
        private readonly Dictionary<ushort, ILink> _routes = new Dictionary<ushort, ILink>();
        private readonly List<ILink> _links = new List<ILink>();

        // Node numbers this process itself answers for. A datagram addressed to one of these is
        // NOT relay traffic and is not a drop - somebody else on this link is going to handle it.
        private readonly HashSet<ushort> _localNodes = new HashSet<ushort>();
        private readonly object _gate = new object();

        /// <summary>
        /// Occurs when a datagram has been forwarded.
        /// </summary>
        public event DatagramRelayed? Relayed;

        /// <summary>
        /// Occurs when a datagram arrived but was not forwarded.
        /// </summary>
        public event DatagramNotRelayed? NotRelayed;

        /// <summary>
        /// Gets the number of datagrams forwarded.
        /// </summary>
        public long DatagramsRelayed { get; private set; }

        /// <summary>
        /// Gets the number of datagrams passed over because they were addressed to this process.
        /// </summary>
        /// <remarks>
        /// Counted separately from <see cref="DatagramsDropped"/> on purpose. Traffic for our own
        /// node is the NORMAL case on a shared link and is not a routing failure; folding it into
        /// the drop count would make that number useless for spotting a real topology mistake.
        /// </remarks>
        public long DatagramsForUs { get; private set; }

        /// <summary>
        /// Gets the number of datagrams dropped because no route was known, the route pointed back
        /// at the arrival link, the datagram was malformed, or the outgoing link refused it.
        /// </summary>
        public long DatagramsDropped { get; private set; }

        /// <summary>
        /// Registers a link and the node numbers reachable through it, and begins forwarding
        /// datagrams that arrive on it.
        /// </summary>
        /// <param name="link">
        /// The link to forward through.
        /// </param>
        /// <param name="reachableNodes">
        /// The node numbers reachable on that link. A node already registered is re-pointed at this
        /// link.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="link"/> or <paramref name="reachableNodes"/> is null.
        /// </exception>
        public void AddLink(ILink link, params ushort[] reachableNodes)
        {
            if (link == null)
            {
                throw new ArgumentNullException(nameof(link));
            }

            if (reachableNodes == null)
            {
                throw new ArgumentNullException(nameof(reachableNodes));
            }

            lock (_gate)
            {
                if (!_links.Contains(link))
                {
                    _links.Add(link);
                    link.PayloadReceived += OnPayloadReceived;
                }

                for (int i = 0; i < reachableNodes.Length; i++)
                {
                    _routes[reachableNodes[i]] = link;
                }
            }
        }

        /// <summary>
        /// Declares a node number this process answers for, so datagrams addressed to it are passed
        /// over instead of being treated as unroutable.
        /// </summary>
        /// <param name="node">
        /// The local node number.
        /// </param>
        /// <remarks>
        /// Without this, every datagram meant for our own node would look like "no route" and be
        /// counted a drop - which on a busy link would bury a genuine routing mistake under
        /// thousands of false ones.
        /// </remarks>
        public void AddLocalNode(ushort node)
        {
            lock (_gate)
            {
                _localNodes.Add(node);
            }
        }

        /// <summary>
        /// Stops forwarding datagrams that arrive on a link and removes every route through it.
        /// </summary>
        /// <param name="link">
        /// The link to remove. Removing a link that was never added is a no-op.
        /// </param>
        public void RemoveLink(ILink link)
        {
            if (link == null)
            {
                return;
            }

            lock (_gate)
            {
                if (!_links.Remove(link))
                {
                    return;
                }

                link.PayloadReceived -= OnPayloadReceived;

                // Drop every route that pointed at this link. The keys are copied out first because
                // a dictionary cannot be modified while it is being walked - and walking the COPY
                // means the removal can happen in the same pass, with no second list to hold the
                // orphans.
                ushort[] routed = new ushort[_routes.Count];
                _routes.Keys.CopyTo(routed, 0);

                for (int i = 0; i < routed.Length; i++)
                {
                    ILink? target;
                    if (_routes.TryGetValue(routed[i], out target) && ReferenceEquals(target, link))
                    {
                        _routes.Remove(routed[i]);
                    }
                }
            }
        }

        /// <summary>
        /// Routes one datagram that arrived on a link, forwarding it when a route is known.
        /// </summary>
        /// <param name="fromLink">
        /// The link the datagram arrived on.
        /// </param>
        /// <param name="datagram">
        /// The datagram bytes, starting at Marker 1.
        /// </param>
        /// <param name="length">
        /// The number of valid bytes.
        /// </param>
        /// <returns>
        /// True when the datagram was forwarded.
        /// </returns>
        /// <remarks>
        /// Exposed rather than kept private so a caller can feed a datagram in directly - and so a
        /// test can drive the routing decision without a live link.
        /// </remarks>
        public bool Route(ILink fromLink, byte[] datagram, int length)
        {
            if (fromLink == null || datagram == null || length < SintranDatagramRelay.HeaderSize)
            {
                CountDrop(fromLink, 0, "malformed or too short");
                return false;
            }

            ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(datagram, 0, length);
            ushort destination = SintranDatagramRelay.GetDestinationNode(span);

            ILink? outgoing;
            lock (_gate)
            {
                // Addressed to us: not relay traffic at all. Counted apart from drops, because the
                // node host sharing this link is about to answer it.
                if (_localNodes.Contains(destination))
                {
                    DatagramsForUs++;
                    return false;
                }

                _routes.TryGetValue(destination, out outgoing);
            }

            if (outgoing == null)
            {
                CountDrop(fromLink, destination, "no route");
                return false;
            }

            // Never send a datagram back out of the link it arrived on.
            if (ReferenceEquals(outgoing, fromLink))
            {
                CountDrop(fromLink, destination, "route points back at the arrival link");
                return false;
            }

            // The whole relay rule: mark it relayed and recompute the header checksum. Endpoints,
            // Flags 1, Flags 2 and body are untouched, so acknowledgements stay end-to-end.
            byte[]? relayed = SintranDatagramRelay.ToRelayed(span);
            if (relayed == null)
            {
                CountDrop(fromLink, destination, "not a SINTRAN datagram");
                return false;
            }

            if (!outgoing.SendData(relayed))
            {
                CountDrop(fromLink, destination, "outgoing link refused the datagram");
                return false;
            }

            lock (_gate)
            {
                DatagramsRelayed++;
            }

            Relayed?.Invoke(fromLink, outgoing, destination);
            return true;
        }

        /// <summary>
        /// Handles a payload delivered up by a registered link.
        /// </summary>
        /// <param name="link">
        /// The link the payload arrived on.
        /// </param>
        /// <param name="payload">
        /// The payload buffer.
        /// </param>
        /// <param name="length">
        /// The number of valid bytes.
        /// </param>
        private void OnPayloadReceived(ILink link, byte[] payload, int length)
        {
            Route(link, payload, length);
        }

        /// <summary>
        /// Counts a dropped datagram and reports why.
        /// </summary>
        /// <param name="fromLink">
        /// The link the datagram arrived on, or null when the caller passed no link (the drop is
        /// still counted; there is simply nobody to name in the report).
        /// </param>
        /// <param name="destination">
        /// The destination node number, when one could be read.
        /// </param>
        /// <param name="reason">
        /// A short human-readable reason.
        /// </param>
        private void CountDrop(ILink? fromLink, ushort destination, string reason)
        {
            lock (_gate)
            {
                DatagramsDropped++;
            }

            if (fromLink != null)
            {
                NotRelayed?.Invoke(fromLink, destination, reason);
            }
        }
    }
}
