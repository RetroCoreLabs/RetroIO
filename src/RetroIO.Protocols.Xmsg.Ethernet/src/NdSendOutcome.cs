//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Ethernet
{
    /// <summary>
    /// What actually happened to a datagram handed to the link.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is not a bool</b></para>
    /// <para>
    /// It was one, and it reported <see langword="true"/> both for a frame put on the wire and for a
    /// frame parked in the send queue. Those are opposite outcomes to anybody upstream, and telling
    /// them apart is not a nicety: on 2026-08-17 a live TAD connect from D100 hung because two reply
    /// frames were queued behind a closed window, and every layer above - the transport, the runner's
    /// refusal diagnostic, the "answered with N frame(s)" line - reported a healthy exchange while
    /// the terminal sat waiting. See <c>DOC/TAD-CONNECT-QUEUED-NOT-SENT-2026-08-17.md</c>.
    /// </para>
    /// <para>
    /// A caller that genuinely does not care can still treat <see cref="Transmitted"/> and
    /// <see cref="Queued"/> alike. The point is that it has to say so.
    /// </para>
    /// </remarks>
    public enum NdSendOutcome
    {
        /// <summary>
        /// The link would not take it at all: no peer yet, an empty payload, or the queue is full.
        /// </summary>
        Refused = 0,

        /// <summary>
        /// On the wire now.
        /// </summary>
        Transmitted = 1,

        /// <summary>
        /// Accepted but NOT sent - it is waiting for the send window to open.
        /// </summary>
        /// <remarks>
        /// Nothing is wrong with queueing by itself; it is how the window is respected. It becomes a
        /// fault when it does not end, which is why the queue depth is worth logging with it - a
        /// depth that climbs and never falls means the peer has stopped acknowledging us.
        /// </remarks>
        Queued = 2,
    }
}
