//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Plugins.Server
{
    /// <summary>
    /// Settings for <see cref="XmsgNodeService"/>: the XMSG node a server program runs, and the
    /// address client programs connect to.
    /// </summary>
    public sealed class XmsgNodeServiceOptions
    {
        /// <summary>
        /// Gets or sets this node's own system number. Required; zero is refused.
        /// </summary>
        /// <remarks>
        /// It must be a number no other machine uses on the same segment, and it must not be the
        /// number an <c>xmsgrunner</c> on this box uses: each node keeps its own sequence state with
        /// each peer, and two processes with one number break it for both.
        /// </remarks>
        public ushort SystemNumber { get; set; }

        /// <summary>
        /// Gets or sets the system number of the one machine this node talks to. Required.
        /// </summary>
        /// <remarks>
        /// One peer only for now: the Ethernet link binds a single peer (RetroIO <c>EthernetLink</c>).
        /// Many peers on one node is Phase 2 of the RetroServer XMSG plan.
        /// </remarks>
        public ushort PeerSystemNumber { get; set; }

        /// <summary>
        /// Gets or sets the Ethernet segment to join, in the form
        /// <c>EthernetBackendFactory.FromSpec</c> takes. Default <c>tcp:127.0.0.1:5010</c>, the hub
        /// RetroServer itself runs.
        /// </summary>
        public string Ethernet { get; set; } = "tcp:127.0.0.1:5010";

        /// <summary>
        /// Gets or sets the address the client-program port listens on. Default <c>127.0.0.1</c>:
        /// this machine only, because the connection has no login yet.
        /// </summary>
        public string PluginBindAddress { get; set; } = "127.0.0.1";

        /// <summary>
        /// Gets or sets the port client programs connect to (gRPC over HTTP/2 without TLS).
        /// Default 5011.
        /// </summary>
        public int PluginPort { get; set; } = 5011;

        /// <summary>
        /// Gets or sets the secret every program must send when it connects, or null or empty for
        /// none (insecure mode). When set it is always checked, also on 127.0.0.1.
        /// </summary>
        public string? ProgramToken { get; set; }

        /// <summary>
        /// Gets or sets the lowest level kept from the programs' log lines. Default Information.
        /// </summary>
        public Microsoft.Extensions.Logging.LogLevel ProgramLogMinimum { get; set; } = Microsoft.Extensions.Logging.LogLevel.Information;

        /// <summary>
        /// Gets or sets the folder for the node's state files. Required.
        /// </summary>
        public string? StateDirectory { get; set; }

        /// <summary>
        /// Gets or sets the middle line of the TAD login banner, or null for the built-in one.
        /// </summary>
        public string? Motd { get; set; }
    }
}
