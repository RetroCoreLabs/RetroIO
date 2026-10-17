//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The result of receiving a message (XFRCV, XFRRH or XFRRE).
    /// </summary>
    /// <remarks>
    /// Appendix A sections 3.2.13 to 3.2.15. All three functions share the same output shape:
    /// T carries the message type (or a status of zero or less), A the hashed magic number of the
    /// remote port, D the message identifier, and X a per-function extra:
    ///  - XFRCV and XFRRE put the message LENGTH in bytes in X.
    ///  - XFRRH puts the FIRST TWO USER BYTES in X instead; if the message is shorter than two
    ///    bytes those two bytes are random, so check <see cref="Length"/> before trusting them.
    ///  - when the type is XMTRE (a secure message that came back undelivered), X carries the
    ///    REASON as a negative error code instead of a length. That case is surfaced as
    ///    <see cref="ReturnReason"/>.
    /// A successful receive makes the message task-current, and additionally port-current when it
    /// was sent secure.
    /// </remarks>
    public readonly struct XmsgReceiveResult
    {
        /// <summary>
        /// Initialises a receive result.
        /// </summary>
        /// <param name="status">
        /// The raw T-register value; positive values are XM* message types.
        /// </param>
        /// <param name="remotePort">
        /// The hashed magic number of the sending port.
        /// </param>
        /// <param name="message">
        /// The identifier of the received message buffer.
        /// </param>
        /// <param name="extra">
        /// The raw X-register value: a message length, the first two user bytes, or a negative
        /// return reason, depending on the function and the message type.
        /// </param>
        public XmsgReceiveResult(
            XmsgStatus status,
            XmsgHashedMagicNumber remotePort,
            XmsgMessageIdentifier message,
            int extra)
        {
            Status = status;
            RemotePort = remotePort;
            Message = message;
            Extra = extra;
        }

        /// <summary>
        /// Gets the raw completion status.
        /// </summary>
        public XmsgStatus Status { get; }

        /// <summary>
        /// Gets the hashed magic number of the port that sent the message.
        /// </summary>
        public XmsgHashedMagicNumber RemotePort { get; }

        /// <summary>
        /// Gets the identifier of the received message buffer.
        /// </summary>
        public XmsgMessageIdentifier Message { get; }

        /// <summary>
        /// Gets the raw X-register value as returned by the function.
        /// </summary>
        public int Extra { get; }

        /// <summary>
        /// Gets a value indicating whether a message was received.
        /// </summary>
        public bool Received
        {
            get { return Status.IsSuccess; }
        }

        /// <summary>
        /// Gets the type of the received message.
        /// </summary>
        /// <returns>
        /// The XM* message type, or <c>null</c> when nothing was received.
        /// </returns>
        public XmsgMessageType? MessageType
        {
            get
            {
                if (!Status.IsSuccess)
                {
                    return null;
                }

                return (XmsgMessageType)Status.Value;
            }
        }

        /// <summary>
        /// Gets the message length in bytes, for the functions that report one.
        /// </summary>
        /// <returns>
        /// The length, or <c>null</c> when nothing was received or the X register carries
        /// something else (a return reason, or the first two user bytes from XFRRH).
        /// </returns>
        public int? Length
        {
            get
            {
                if (!Status.IsSuccess || Status.Value == (int)XmsgMessageType.XMTRE)
                {
                    return null;
                }

                return Extra;
            }
        }

        /// <summary>
        /// Gets the reason a secure message came back undelivered.
        /// </summary>
        /// <returns>
        /// The XE* error code carried in X for an XMTRE message, or <c>null</c> for any other
        /// message type.
        /// </returns>
        public XmsgError? ReturnReason
        {
            get
            {
                if (!Status.IsSuccess || Status.Value != (int)XmsgMessageType.XMTRE)
                {
                    return null;
                }

                return (XmsgError)Extra;
            }
        }
    }
}
