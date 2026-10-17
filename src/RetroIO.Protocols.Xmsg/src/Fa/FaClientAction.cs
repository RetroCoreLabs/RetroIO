//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// What a client should do next in a file-access session.
    /// </summary>
    /// <remarks>
    /// Not a flags enum: a session does exactly one thing at a time. The values are the steps of
    /// the exchange model, not bits to combine.
    /// </remarks>
    public enum FaClientAction
    {
        /// <summary>
        /// Nothing to do - the session is waiting for the peer.
        /// </summary>
        Wait = 0,

        /// <summary>
        /// Send the connect letter to <c>*FA-SERVER</c> and wait for its confirmation.
        /// </summary>
        SendConnectLetter = 1,

        /// <summary>
        /// Send the next request in the ladder.
        /// </summary>
        SendRequest = 2,

        /// <summary>
        /// Acknowledge the reply just received, with a short acknowledgement.
        /// </summary>
        /// <remarks>
        /// A reply is a NEW exchange, so it is answered like any other message. Skipping this is
        /// one of the two ways to fall behind the peer's exchange count and draw an XENSE reject.
        /// </remarks>
        SendShortAck = 3,

        /// <summary>
        /// Send the file content, after the write request has been answered.
        /// </summary>
        SendData = 4,

        /// <summary>
        /// Send the client's Release, which ends the conversation and frees the server's seat.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This was called <c>SendClose</c> until 2026-08-18, and the name was wrong in the one way
        /// that mattered. Close (<c>0x07C0</c>) is the SERVER's message; the client's is
        /// Release (<c>0x0782</c>). The driver built a Close for years because the name told
        /// it to, and that single mistake is what leaked a connection seat on every transfer.
        /// </para>
        /// <para>
        /// Why it matters far more than a teardown formality: the seat is not returned by anything
        /// we send. XROUT spends a seat to deliver the connect letter and marks the server's port
        /// with <c>5PKOC</c> ("kick XROUT on close"); when the server later CLOSES that port the
        /// XMSG kernel sees the bit and kicks XROUT, which restores the count. So the client's only
        /// lever is to make the server conclude the session - and a Close does not, because the
        /// server is waiting to send one, not to receive one. See
        /// <c>DOC\COSMOS-RE\CARVE-ANSWER-FA-SEAT-RETURN-2026-08-18.md</c>.
        /// </para>
        /// </remarks>
        SendRelease = 5,

        /// <summary>
        /// The session finished successfully.
        /// </summary>
        Done = 6,

        /// <summary>
        /// The session failed. See the session's failure text.
        /// </summary>
        Failed = 7,
    }
}
