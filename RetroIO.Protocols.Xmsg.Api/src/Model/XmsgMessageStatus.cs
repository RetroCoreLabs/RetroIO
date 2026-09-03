namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The result of a message-status enquiry (XFMST).
    /// </summary>
    /// <remarks>
    /// Appendix A section 3.2.17. This is the call that turns an anonymous arrival into an
    /// addressable partner: the receive functions only hand back the 16-bit hashed magic number,
    /// which is a quick-check hint, while this returns the sender's full 32-bit magic number - the
    /// value needed to open a direct dialogue after an XROUT letter.
    /// Naming a message other than the current one also makes it the task-current message.
    /// </remarks>
    public readonly struct XmsgMessageStatus
    {
        /// <summary>
        /// Initialises a message-status result.
        /// </summary>
        /// <param name="status">
        /// The raw T-register value; positive values are XM* message types.
        /// </param>
        /// <param name="sender">
        /// The magic number of the port the message was sent from.
        /// </param>
        /// <param name="length">
        /// The message length in bytes.
        /// </param>
        public XmsgMessageStatus(XmsgStatus status, XmsgMagicNumber sender, int length)
        {
            Status = status;
            Sender = sender;
            Length = length;
        }

        /// <summary>
        /// Gets the raw completion status.
        /// </summary>
        public XmsgStatus Status { get; }

        /// <summary>
        /// Gets the magic number of the sending port.
        /// </summary>
        public XmsgMagicNumber Sender { get; }

        /// <summary>
        /// Gets the message length in bytes.
        /// </summary>
        public int Length { get; }

        /// <summary>
        /// Gets the type of the message.
        /// </summary>
        /// <returns>
        /// The XM* message type, or <c>null</c> when the call failed.
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
