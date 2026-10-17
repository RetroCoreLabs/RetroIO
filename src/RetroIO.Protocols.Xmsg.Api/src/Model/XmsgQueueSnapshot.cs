//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The three register masks returned by <c>XFGSM</c>: which of a task's ports have a message
    /// waiting, and what the first one on each is.
    /// </summary>
    /// <remarks>
    /// <para><b>Bit order is REVERSED against open order</b></para>
    /// Bit 0 is the LAST port opened, bit 1 the one before it, and so on. That is ND's own wording
    /// and their worked example: a task opening ports 6, 9 then 3 gets port 3 on bit 0, port 9 on
    /// bit 1 and port 6 on bit 2. It is the opposite of what "the 16 first opened ports" suggests,
    /// so it is stated here rather than left to be rediscovered.
    /// <para><b>Where this comes from</b></para>
    /// <c>X-MESSAGE 210373L</c>, the version-L program description, section 6.3 - "General Status
    /// Multiple", listed there as a NEW function. That document is why this could be implemented at
    /// all: <c>XFGSM</c> has no section in the COSMOS Programmer Guide's Appendix A, and was
    /// recorded here for months as blocked on evidence rather than on effort.
    /// <para><b>Only the first 16 ports</b></para>
    /// The masks are 16 bits, so a task holding more than 16 ports sees only 16 of them. ND does not
    /// say what happens past that and no capture shows it, so nothing is invented: ports beyond the
    /// sixteenth simply do not appear.
    /// </remarks>
    public readonly struct XmsgQueueSnapshot
    {
        /// <summary>
        /// Initialises the snapshot.
        /// </summary>
        /// <param name="queued">
        /// Ports with any message waiting (ND's A-register).
        /// </param>
        /// <param name="routerFirst">
        /// Ports whose first waiting message came from XROUT (ND's D-register).
        /// </param>
        /// <param name="returnedFirst">
        /// Ports whose first waiting message is a returned message (ND's X-register).
        /// </param>
        public XmsgQueueSnapshot(ushort queued, ushort routerFirst, ushort returnedFirst)
        {
            Queued = queued;
            RouterFirst = routerFirst;
            ReturnedFirst = returnedFirst;
        }

        /// <summary>
        /// Gets the mask of ports with any message waiting - ND's A-register.
        /// </summary>
        public ushort Queued { get; }

        /// <summary>
        /// Gets the mask of ports whose FIRST waiting message is of type <c>XMROU</c>, sent by
        /// XROUT - ND's D-register.
        /// </summary>
        public ushort RouterFirst { get; }

        /// <summary>
        /// Gets the mask of ports whose FIRST waiting message is of type <c>XMTRE</c>, a returned
        /// message - ND's X-register.
        /// </summary>
        public ushort ReturnedFirst { get; }

        /// <summary>
        /// Reports whether the port at a bit position has a message waiting.
        /// </summary>
        /// <param name="bit">
        /// The bit position, 0 being the port opened most recently.
        /// </param>
        /// <returns>
        /// True when that port has any message queued.
        /// </returns>
        public bool HasMessage(int bit)
        {
            if (bit < 0 || bit > 15)
            {
                return false;
            }

            return (Queued & (1 << bit)) != 0;
        }

        /// <summary>
        /// Reports the type of the first waiting message on the port at a bit position.
        /// </summary>
        /// <param name="bit">
        /// The bit position, 0 being the port opened most recently.
        /// </param>
        /// <returns>
        /// <c>XMROU</c> or <c>XMTRE</c> when the corresponding mask says so; otherwise
        /// <c>XMTNO</c>.
        /// </returns>
        /// <remarks>
        /// ND is explicit that a bit set in NEITHER of the two type masks means the first message is
        /// one of <c>XMTNO</c>, <c>XMHIP</c> or <c>XMBNC</c> - three types this snapshot cannot tell
        /// apart. <c>XMTNO</c> is returned for that case as the ordinary one, and the caller that
        /// needs the difference must ask the port itself.
        /// </remarks>
        public XmsgMessageType FirstMessageType(int bit)
        {
            if (bit < 0 || bit > 15)
            {
                return XmsgMessageType.XMTNO;
            }

            int mask = 1 << bit;

            if ((RouterFirst & mask) != 0)
            {
                return XmsgMessageType.XMROU;
            }

            if ((ReturnedFirst & mask) != 0)
            {
                return XmsgMessageType.XMTRE;
            }

            return XmsgMessageType.XMTNO;
        }
    }
}
