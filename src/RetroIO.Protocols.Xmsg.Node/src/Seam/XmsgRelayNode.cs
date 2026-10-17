//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Node.Seam
{
    /// <summary>
    /// A node that speaks XMSG on more than one link and carries traffic between them.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is a host PER LINK, not one host with many links</b></para>
    /// <see cref="XmsgNodeHost"/> is the whole per-link stack: its codec, its XMSG layer, its
    /// responder sequence and its learned link id all belong to ONE peer. Two peers do not share
    /// any of that - each has its own datagram sequence - so "one host, several links" would mean
    /// one of everything inside it per link anyway. Composing hosts is the same thing without
    /// pretending otherwise, and it leaves the single-link path exactly as it is today.
    /// <para><b>The ordering problem, and why it is not one here</b></para>
    /// A relay must not answer traffic that is only passing through: secure-ACKing on the real
    /// destination's behalf corrupts the end-to-end sequence, and the captures show a real relay
    /// leaves acknowledgements alone. Both the relay and the host subscribe to the same link, so
    /// who sees a payload first would normally decide the outcome.
    /// <para>
    /// It does not, because the two now partition the traffic between them: every host added here
    /// gets <see cref="XmsgNodeHost.IgnoreDatagramsForOtherNodes"/> set, and the relay is told our
    /// node numbers so it passes those over. Whatever order the events fire in, exactly one of them
    /// acts on any given datagram.
    /// </para>
    /// <para><b>Routing is stated, never learned</b></para>
    /// The routes come from the caller's topology. There is no discovery here for the same reason
    /// <see cref="DatagramRelay"/> has none: how COSMOS distributes reachability has not been
    /// established from captures, and inventing it would be fabrication.
    /// </remarks>
    public sealed class XmsgRelayNode
    {
        private readonly List<XmsgNodeHost> _hosts = new List<XmsgNodeHost>();
        private readonly DatagramRelay _relay = new DatagramRelay();

        /// <summary>
        /// Gets the relay that carries datagrams between the links.
        /// </summary>
        /// <remarks>
        /// Exposed so a caller can subscribe to its events for logging, and read its counters.
        /// </remarks>
        public DatagramRelay Relay
        {
            get { return _relay; }
        }

        /// <summary>
        /// Gets the hosts, one per link.
        /// </summary>
        public IReadOnlyList<XmsgNodeHost> Hosts
        {
            get { return _hosts; }
        }

        /// <summary>
        /// Adds a link's node host and states which node numbers are reachable through that link.
        /// </summary>
        /// <param name="host">
        /// The per-link host. Its link is registered with the relay.
        /// </param>
        /// <param name="reachableNodes">
        /// The node numbers reachable through this host's link. May be empty for a link that only
        /// serves its own peer.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="host"/> or <paramref name="reachableNodes"/> is null.
        /// </exception>
        /// <remarks>
        /// Turns on <see cref="XmsgNodeHost.IgnoreDatagramsForOtherNodes"/> for
        /// <paramref name="host"/>. That is the whole reason a relaying node is safe: the host stops
        /// answering anything that is not addressed to it.
        /// </remarks>
        public void AddHost(XmsgNodeHost host, params ushort[] reachableNodes)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (reachableNodes == null)
            {
                throw new ArgumentNullException(nameof(reachableNodes));
            }

            // From here on this host answers only for itself. Set BEFORE the relay is given the
            // link, so there is no window where both would act on the same datagram.
            host.IgnoreDatagramsForOtherNodes = true;

            _hosts.Add(host);
            _relay.AddLocalNode(host.NodeNumber);
            _relay.AddLink(host.Link, reachableNodes);
        }

        /// <summary>
        /// Pumps every host once.
        /// </summary>
        /// <returns>
        /// The total number of items the hosts processed.
        /// </returns>
        /// <remarks>
        /// Each host is pumped even if an earlier one threw nothing useful, so a quiet link never
        /// starves a busy one.
        /// </remarks>
        public int Pump()
        {
            int total = 0;

            for (int i = 0; i < _hosts.Count; i++)
            {
                total += _hosts[i].Pump();
            }

            return total;
        }
    }
}
