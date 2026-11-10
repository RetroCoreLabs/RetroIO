//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Node;
using RetroIO.Protocols.Xmsg.Node.Seam;
using RetroIO.Protocols.Xmsg.Servers.Fa;
using RetroIO.Protocols.Xmsg.Servers.Tad;

namespace RetroIO.Protocols.Xmsg.Hosting
{
    /// <summary>
    /// A node built by <see cref="XmsgNodeBuilder.Build"/>: the host and the parts a caller needs
    /// to reach after construction.
    /// </summary>
    public sealed class XmsgHostedNode
    {
        /// <summary>
        /// Initialises the node.
        /// </summary>
        /// <param name="host">
        /// The node host.
        /// </param>
        /// <param name="tadServer">
        /// The TAD terminal server registered on it.
        /// </param>
        /// <param name="fileServer">
        /// The COSMOS file server registered on it, or null when there is none.
        /// </param>
        /// <param name="sequenceStore">
        /// The persisted outgoing datagram sequence.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="host"/>, <paramref name="tadServer"/> or
        /// <paramref name="sequenceStore"/> is null.
        /// </exception>
        public XmsgHostedNode(
            XmsgNodeHost host, TadServer tadServer, FaServer? fileServer, IResponderSequenceStore sequenceStore)
        {
            Host = host ?? throw new ArgumentNullException(nameof(host));
            TadServer = tadServer ?? throw new ArgumentNullException(nameof(tadServer));
            FileServer = fileServer;
            SequenceStore = sequenceStore ?? throw new ArgumentNullException(nameof(sequenceStore));
        }

        /// <summary>
        /// Gets the node host. Call its <c>Pump()</c> every 20 ms.
        /// </summary>
        public XmsgNodeHost Host { get; }

        /// <summary>
        /// Gets the TAD terminal server (<c>*TADADM</c>).
        /// </summary>
        public TadServer TadServer { get; }

        /// <summary>
        /// Gets the COSMOS file server (<c>*FA-SERVER</c>), or null when there is none.
        /// </summary>
        public FaServer? FileServer { get; }

        /// <summary>
        /// Gets the persisted outgoing datagram sequence.
        /// </summary>
        public IResponderSequenceStore SequenceStore { get; }
    }
}
