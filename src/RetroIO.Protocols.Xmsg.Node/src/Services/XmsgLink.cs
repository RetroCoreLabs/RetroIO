//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Node.Services
{
    /// <summary>
    /// The node's per-remote-node link state: the envelope seed and the single continuous outgoing
    /// datagram sequence (Flags 1) shared by every server and session that sends to that node.
    /// </summary>
    /// <remarks>
    /// The outgoing Flags 1 is ONE counter per link (per remote node) - every datagram the node
    /// originates to that node, across ALL servers (*TADADM, *XM-FIDO, ...) and all their sessions,
    /// advances it by one, exactly as the real protocol interleaves everything on one direction. It is
    /// loaded once from the persistent store on first contact and continues in memory thereafter; the
    /// store is advanced by the peer's ACKs. This is transport state - servers never see it.
    /// </remarks>
    public sealed class XmsgLink
    {
        /// <summary>
        /// Initialises the link state.
        /// </summary>
        /// <param name="remoteNode">
        /// The remote node number (for example 100).
        /// </param>
        /// <param name="seed">
        /// The per-link envelope seed learned from the first frame seen on the link.
        /// </param>
        /// <param name="nextFlags1">
        /// The next outgoing datagram sequence, loaded from the persistent store.
        /// </param>
        public XmsgLink(ushort remoteNode, byte seed, ushort nextFlags1)
        {
            RemoteNode = remoteNode;
            Seed = seed;
            NextFlags1 = nextFlags1;
        }

        /// <summary>
        /// Gets the remote node number.
        /// </summary>
        public ushort RemoteNode { get; }

        /// <summary>
        /// Gets or sets the per-link envelope seed.
        /// </summary>
        public byte Seed { get; set; }

        /// <summary>
        /// Gets or sets the next outgoing datagram sequence (Flags 1), advanced once per originated frame.
        /// </summary>
        public ushort NextFlags1 { get; set; }
    }
}
