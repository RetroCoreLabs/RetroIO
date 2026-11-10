//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Servers.Fa;
using RetroIO.Protocols.Xmsg.Servers.Tad;

namespace RetroIO.Protocols.Xmsg.Hosting
{
    /// <summary>
    /// What <see cref="XmsgNodeBuilder.Build"/> needs besides the link, the node number and the
    /// routes.
    /// </summary>
    public sealed class XmsgNodeOptions
    {
        /// <summary>
        /// Gets or sets the folder that holds the node's state files (see
        /// <see cref="XmsgStateFiles"/>). Required.
        /// </summary>
        /// <remarks>
        /// The runner passes the folder of its executable, which is where these files have always
        /// been. A service passes its data folder. Two processes must never share one, because each
        /// file is the record of ONE node's conversation with its peers.
        /// </remarks>
        public string? StateDirectory { get; set; }

        /// <summary>
        /// Gets or sets the accounts the TAD terminal server accepts. Null means none.
        /// </summary>
        public TadUserDirectory? TadUsers { get; set; }

        /// <summary>
        /// Gets or sets the middle line of the TAD login banner, or null for the built-in one.
        /// </summary>
        public string? Motd { get; set; }

        /// <summary>
        /// Gets or sets the COSMOS file server to register, or null for none. Build one with
        /// <see cref="XmsgNodeBuilder.CreateFileServer"/>.
        /// </summary>
        public FaServer? FileServer { get; set; }

        /// <summary>
        /// Gets or sets where the node, its servers and its stores write their lines. Null
        /// discards them.
        /// </summary>
        public XmsgLogHandler? Log { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether payloads are queued and handled inside
        /// <c>Pump()</c> rather than on the link's own thread. See
        /// <see cref="Node.Seam.XmsgNodeHost.DeliverInboundOnPump"/>.
        /// </summary>
        /// <remarks>
        /// Turn it on for an Ethernet link, whose backend delivers on its own receive thread. The
        /// HDLC adapter delivers on the same thread that pumps, so it does not need it.
        /// </remarks>
        public bool DeliverInboundOnPump { get; set; }

        /// <summary>
        /// Gets or sets the clock the TAD server uses, or null for the local time.
        /// </summary>
        public Func<DateTime>? Clock { get; set; }

        /// <summary>
        /// Gets or sets more servers to register after the TAD and file servers, or null for none.
        /// </summary>
        /// <remarks>
        /// RetroServer adds the bridge that client programs connect through
        /// (<c>XmsgKernelServer</c>, see <c>RetroIO.Protocols.Xmsg.Plugins</c>).
        /// </remarks>
        public IXmsgServer[]? ExtraServers { get; set; }
    }
}
