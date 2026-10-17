//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The options accepted by the send and return functions (XFSND, XFRTN).
    /// </summary>
    /// <remarks>
    /// Values are the corresponding <see cref="XmsgOption"/> bits, so a cast converts between the
    /// two; this enum exists only to express which subset those calls legally accept.
    /// <para><b>HighPriority and RemoteRoute are the same bit</b></para>
    /// XFHIP and XFRRO both occupy bit 13. That is not a mistake in this enum - it is why the
    /// manual documents an "acts as" rule for XFSND (appendix A section 3.2.11): the meaning of
    /// bit 13 is decided by whether <see cref="Route"/> is set.
    ///  - Route set: bit 13 means RemoteRoute, and the target XROUT's system number is taken from
    ///    the A register.
    ///  - Route clear: bit 13 means HighPriority, and the message is chained to the head of the
    ///    receiver's queue behind any other high-priority messages.
    /// Both names are provided so call sites can say what they mean; they compile to one bit.
    /// </remarks>
    [Flags]
    public enum XmsgSendFlags : ushort
    {
        /// <summary>
        /// No options.
        /// </summary>
        None = 0,

        /// <summary>
        /// XFSEC - send the message secure.
        /// </summary>
        /// <remarks>
        /// A secure message comes back to the sending port if it cannot be delivered, or if the
        /// receiving port closes while the message is port-current there. Non-secure messages are
        /// discarded and their buffers released in the same circumstances - silently.
        /// </remarks>
        Secure = XmsgOption.XFSEC,

        /// <summary>
        /// XFROU - route the message through the local XROUT task.
        /// </summary>
        /// <remarks>
        /// The destination magic number is ignored and the message body is read as XROUT service
        /// parameters (appendix B).
        /// </remarks>
        Route = XmsgOption.XFROU,

        /// <summary>
        /// XFFWD - forward the message without updating the sender information.
        /// </summary>
        /// <remarks>
        /// The receiver sees the message as coming directly from the previous sending port, which
        /// is what makes a relay transparent.
        /// </remarks>
        Forward = XmsgOption.XFFWD,

        /// <summary>
        /// XFBNC - bounce the message.
        /// </summary>
        /// <remarks>
        /// The receiver's next receive call, which would have taken this message, returns it to the
        /// sender instead.
        /// </remarks>
        Bounce = XmsgOption.XFBNC,

        /// <summary>
        /// XFHIP - queue the message ahead of normal-priority messages (when Route is clear).
        /// </summary>
        HighPriority = XmsgOption.XFHIP,

        /// <summary>
        /// XFRRO - route to a REMOTE XROUT (when Route is set); the same bit as HighPriority.
        /// </summary>
        RemoteRoute = XmsgOption.XFRRO,

        /// <summary>
        /// XFWTF - wait until the message has reached the receiver's input queue.
        /// </summary>
        /// <remarks>
        /// Only significant for a secure message sent to another system. Without it, an
        /// undeliverable secure message simply comes back to the sending port later.
        /// </remarks>
        Wait = XmsgOption.XFWTF,
    }
}
