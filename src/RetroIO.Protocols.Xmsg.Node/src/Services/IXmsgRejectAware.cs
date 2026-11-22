//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Node.Services
{
    /// <summary>
    /// A server that wants to know when a remote node refused one of the frames it sent, with an
    /// error frame (subtype 07) instead of an ACK.
    /// </summary>
    /// <remarks>
    /// A real ND machine answers data for a port nobody owns with a bare error frame, Flags 2 the
    /// negative XMSG error (XEIMA <c>0xFFED</c>), at the refused frame's own Flags 1 (10 of 10 rejects
    /// in four captures; libxmsg 0755090). A server whose tasks send SECURE messages needs this to
    /// hand the message back to the task, as SINTRAN does (RetroIO BUGS.md, kernel bridge, part 1).
    /// </remarks>
    public interface IXmsgRejectAware
    {
        /// <summary>
        /// Called when a remote node refused one of our frames.
        /// </summary>
        /// <param name="remoteNode">
        /// The node that refused it.
        /// </param>
        /// <param name="rejectedFlags1">
        /// The Flags 1 of the refused frame, as the error frame carries it.
        /// </param>
        /// <param name="error">
        /// The error from the error frame's Flags 2.
        /// </param>
        void NotifyRejected(ushort remoteNode, ushort rejectedFlags1, XmsgError error);
    }
}
