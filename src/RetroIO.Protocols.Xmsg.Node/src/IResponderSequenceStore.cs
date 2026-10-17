//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Node
{
    /// <summary>
    /// Persists our outgoing datagram sequence (<c>Flags1</c>) per remote node, so it survives our
    /// process restarts. This is REQUIRED for correctness against a real machine: SINTRAN's XMSG
    /// keeps a persistent per-node-pair receive-sequence (XSRSQ) equal to the count of Data frames
    /// it has received from us since that pair-state was created; if we reset to 0x0000 on restart
    /// while the peer's XSRSQ has advanced, our frames are BEHIND-sequence and silently dropped.
    /// </summary>
    /// <remarks>
    /// See <c>SINTRAN/XMSG/DOC/XMSG-SEQUENCE-RESTART-ANSWER-2026-07-03.md</c>, Q1 option (a). Only
    /// Data frames (subtype 0x0E) advance the sequence; secure ACKs (0x03) do not. The value stored
    /// is the NEXT Flags1 we will use for the given remote node.
    /// </remarks>
    public interface IResponderSequenceStore
    {
        /// <summary>
        /// Loads the next outgoing <c>Flags1</c> to use for a remote node (0x0000 if none stored).
        /// </summary>
        /// <param name="remoteNode">
        /// The remote system (node) number, e.g. 100.
        /// </param>
        /// <returns>
        /// The next Flags1 value; 0x0000 for a first-ever contact.
        /// </returns>
        ushort LoadNextFlags1(ushort remoteNode);

        /// <summary>
        /// Persists the next outgoing <c>Flags1</c> for a remote node (called after each Data frame).
        /// </summary>
        /// <param name="remoteNode">
        /// The remote system (node) number.
        /// </param>
        /// <param name="nextFlags1">
        /// The next Flags1 value to use for the following frame.
        /// </param>
        void SaveNextFlags1(ushort remoteNode, ushort nextFlags1);
    }
}
