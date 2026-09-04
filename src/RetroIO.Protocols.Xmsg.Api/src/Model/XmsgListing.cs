namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// One step of a walk over the task's own messages and ports (XFLMP).
    /// </summary>
    /// <remarks>
    /// <para><b>Two independent searches in one call</b></para>
    /// COSMOS Programmer Guide section 3.2.16 (line 10410). A single call answers two questions at
    /// once - the first message at or above the identifier asked for, and the first port at or
    /// above the port number asked for. They do not advance together and neither constrains the
    /// other; a walk that only cares about ports simply ignores the message half.
    /// <para><b>Zero is the end marker, not an error</b></para>
    /// Either field comes back zero when nothing at or above the request exists. Running off the
    /// end of the messages while ports remain is normal, and vice versa.
    /// </remarks>
    public readonly struct XmsgListing
    {
        /// <summary>
        /// Initialises the result.
        /// </summary>
        /// <param name="status">
        /// The completion status.
        /// </param>
        /// <param name="messageId">
        /// The message found, or zero.
        /// </param>
        /// <param name="messageSize">
        /// The size the message was reserved with.
        /// </param>
        /// <param name="portNumber">
        /// The port found, or zero.
        /// </param>
        public XmsgListing(XmsgStatus status, int messageId, int messageSize, int portNumber)
        {
            Status = status;
            MessageId = messageId;
            MessageSize = messageSize;
            PortNumber = portNumber;
        }

        /// <summary>
        /// Gets the completion status.
        /// </summary>
        public XmsgStatus Status { get; }

        /// <summary>
        /// Gets the first message identifier at or above the one requested, or zero when there is
        /// none higher.
        /// </summary>
        public int MessageId { get; }

        /// <summary>
        /// Gets the size that message was reserved with, in bytes.
        /// </summary>
        /// <remarks>
        /// What it was RESERVED with - by XFGET, by arriving from another task, or by XFALM - not
        /// how much has been written into it. Meaningless when <see cref="MessageId"/> is zero.
        /// </remarks>
        public int MessageSize { get; }

        /// <summary>
        /// Gets the first port number at or above the one requested, or zero when there is none
        /// higher.
        /// </summary>
        public int PortNumber { get; }

        /// <summary>
        /// Gets whether a message was found.
        /// </summary>
        public bool HasMessage
        {
            get { return MessageId != 0; }
        }

        /// <summary>
        /// Gets whether a port was found.
        /// </summary>
        public bool HasPort
        {
            get { return PortNumber != 0; }
        }
    }
}
