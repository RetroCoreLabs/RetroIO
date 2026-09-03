namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The result of a port-status enquiry (XFPST).
    /// </summary>
    /// <remarks>
    /// Appendix A section 3.1.3: the T register carries the message type of the FIRST message in
    /// the port queue (or zero when there is none), the A register a hashed magic number that
    /// identifies the remote port well enough for a quick "do I know this partner" check, the D
    /// register the message identifier, and the X register the queue length. The X register is
    /// always meaningful; A and D only when a message is actually waiting.
    /// </remarks>
    public readonly struct XmsgPortStatus
    {
        /// <summary>
        /// Initialises a port-status result.
        /// </summary>
        /// <param name="status">
        /// The raw T-register status; positive values are message types.
        /// </param>
        /// <param name="remotePort">
        /// The hashed magic number of the remote port, meaningful only when a message waits.
        /// </param>
        /// <param name="message">
        /// The waiting message's identifier, meaningful only when a message waits.
        /// </param>
        /// <param name="queueLength">
        /// The number of messages queued for the port; always meaningful.
        /// </param>
        public XmsgPortStatus(
            XmsgStatus status,
            XmsgHashedMagicNumber remotePort,
            XmsgMessageIdentifier message,
            int queueLength)
        {
            Status = status;
            RemotePort = remotePort;
            Message = message;
            QueueLength = queueLength;
        }

        /// <summary>
        /// Gets the raw completion status.
        /// </summary>
        public XmsgStatus Status { get; }

        /// <summary>
        /// Gets the hashed magic number of the remote port that sent the waiting message.
        /// </summary>
        public XmsgHashedMagicNumber RemotePort { get; }

        /// <summary>
        /// Gets the identifier of the waiting message.
        /// </summary>
        public XmsgMessageIdentifier Message { get; }

        /// <summary>
        /// Gets the number of messages currently queued for the port.
        /// </summary>
        public int QueueLength { get; }

        /// <summary>
        /// Gets a value indicating whether a message is waiting on the port.
        /// </summary>
        public bool HasMessage
        {
            get { return Status.IsSuccess; }
        }

        /// <summary>
        /// Gets the type of the waiting message.
        /// </summary>
        /// <returns>
        /// The XM* message type, or <c>null</c> when no message is waiting.
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
    }
}
