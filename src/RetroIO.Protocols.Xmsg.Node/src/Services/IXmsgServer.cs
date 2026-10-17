//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Node.Services
{
    /// <summary>
    /// A named, registered XMSG <b>server</b>: a long-running program in XROUT's registry (what
    /// <c>list-servers</c> shows) that owns a name and a well-known logical port and answers XSLET
    /// letters addressed to it by name. <c>*TADADM</c> (the TAD terminal server, logical port 2) is
    /// the first implementation; <c>*XM-FIDO</c> (the file server, logical port 4) will be a second.
    /// </summary>
    /// <remarks>
    /// <para><b>Vocabulary (XMSG-PROTOCOL.md section 7).</b></para>
    /// A SERVER is a named registered program (this interface). A SERVICE is the numbered XROUT verb in
    /// the XMCSM low byte - XSLET <c>0x41</c> "send a letter", XSGSY <c>0x4B</c> "get routing info" - and
    /// lives in the node's router, NOT here. A FUNCTION (XFSND, XFRCV, ...) is a MON 200B API call and
    /// never appears on the wire.
    /// <para><b>Dispatch.</b></para>
    /// Every server request arrives at port 0. The router forks on the XMCSM low byte: an XSLET letter
    /// is parsed for its target name and routed to the matching server via <see cref="Handle"/>; session
    /// data (which arrives at a session port the accept advertised, never port 0) is routed to the server
    /// that <see cref="OwnsPort"/>s it. The server never parses ports or builds envelopes - it uses the
    /// <see cref="IXmsgServerTransport"/> handed to it, which fills in Flags 1, Counter and channel.
    /// </remarks>
    public interface IXmsgServer
    {
        /// <summary>
        /// Gets the registered server name, including the leading <c>*</c> (for example <c>*TADADM</c>).
        /// Matched case-insensitively against the target name inside an XSLET letter.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the well-known logical port this server registers on (for example 2 for <c>*TADADM</c>,
        /// 4 for <c>*XM-FIDO</c>). The wire reply-from port is <c>(LogicalPort shifted left 7) | incarnation</c>.
        /// </summary>
        int LogicalPort { get; }

        /// <summary>
        /// Gets the minted wire reply-from port (<c>(LogicalPort shifted left 7) | incarnation</c>) shown by
        /// <c>list servers</c>.
        /// </summary>
        ushort WirePort { get; }

        /// <summary>
        /// Gets the number of currently-active sessions on this server.
        /// </summary>
        int SessionCount { get; }

        /// <summary>
        /// Gets the maximum concurrent sessions this server accepts (Free SPs = capacity - count).
        /// </summary>
        int SessionCapacity { get; }

        /// <summary>
        /// Handles a datagram routed to this server: either an XSLET letter to port 0 that named this
        /// server (a connect, or a list-systems query), or session data that arrived at one of this
        /// server's session ports.
        /// </summary>
        /// <param name="incoming">
        /// The received datagram.
        /// </param>
        /// <param name="transport">
        /// The node transport used to build any reply frames.
        /// </param>
        /// <returns>
        /// The frames to transmit in response, in order (possibly empty).
        /// </returns>
        IReadOnlyList<XmsgFrame> Handle(XmsgFrame incoming, IXmsgServerTransport transport);

        /// <summary>
        /// Returns true when the given wire port is a session port this server currently owns, so the
        /// router can route session data (which is ports-only after the accept) to it.
        /// </summary>
        /// <param name="port">
        /// The destination wire port of an incoming datagram.
        /// </param>
        /// <returns>
        /// True when this server owns a session on that port.
        /// </returns>
        bool OwnsPort(ushort port);

        /// <summary>
        /// Drains any asynchronous output this server has queued (for example TAD tty inject / wall
        /// text) into frames to transmit. Called by the node each pump cycle so queued text flushes to
        /// the remote clients without waiting for those clients to send anything.
        /// </summary>
        /// <param name="transport">
        /// The node transport used to build the frames.
        /// </param>
        /// <returns>
        /// The queued frames to transmit, in order (empty when nothing is pending).
        /// </returns>
        IReadOnlyList<XmsgFrame> DrainPending(IXmsgServerTransport transport);

        /// <summary>
        /// Notifies the server that a remote node ACKed (subtype <c>0x03</c>) one of our frames, so a
        /// server streaming a multi-chunk reply can release its flow-control window and send the next chunk.
        /// </summary>
        /// <remarks>
        /// Called by the host for every incoming ACK. A server that does no windowed output ignores it.
        /// After this the host drains via <see cref="DrainPending"/>, so the released chunk is transmitted.
        /// </remarks>
        /// <param name="remoteNode">
        /// The node that ACKed.
        /// </param>
        /// <param name="ackedFlags1">
        /// The Flags 1 the ACK echoes (identifies the acknowledged frame).
        /// </param>
        void NotifyAck(ushort remoteNode, ushort ackedFlags1);

        /// <summary>
        /// Gets whether this server's windowed output advances on the remote's <b>ACK</b> (subtype 0x03),
        /// so the host should drain it immediately after an incoming ACK. Servers whose output is instead
        /// paced by a different in-band signal (for example the TAD sentinel stream, which advances on
        /// 100's 7DUMM commit) return false so an ACK does NOT prematurely release the next frame.
        /// </summary>
        bool AdvancesOutputOnAck { get; }
    }
}
