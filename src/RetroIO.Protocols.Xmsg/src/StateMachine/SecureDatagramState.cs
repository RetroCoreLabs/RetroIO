//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// States of the XFSEC secure-datagram delivery model.
    /// </summary>
    /// <remarks>
    /// Drives the secure-message send side (XMSG-PROTOCOL.md section 6.1): a routed
    /// data message must be acknowledged or the sender retransmits, and on resend
    /// exhaustion the message is returned to the sender as type
    /// <see cref="XmsgMessageType.XMTRE"/>.
    /// </remarks>
    public enum SecureDatagramState : int
    {
        /// <summary>
        /// No message is outstanding.
        /// </summary>
        Idle = 0,

        /// <summary>
        /// A data message has been sent and is awaiting its delivery ACK.
        /// </summary>
        AwaitingAck = 1,

        /// <summary>
        /// The message was acknowledged and delivery is complete.
        /// </summary>
        Delivered = 2,

        /// <summary>
        /// Delivery failed; the message was returned to the sender with a negative reason.
        /// </summary>
        Returned = 3,
    }
}
