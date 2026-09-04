using System;

namespace RetroIO.Protocols.Xmsg.Packet
{
    /// <summary>
    /// An outgoing XMSG packet ready to serialise onto the wire. A thin, immutable wrapper over a
    /// built <see cref="XmsgFrame"/>; <see cref="ToBytes"/> yields the exact information-field bytes.
    /// </summary>
    /// <remarks>
    /// Constructed by <see cref="XmsgPacketBuilder"/>. Keeping the built frame inside the packet
    /// lets the codec send it with a single <c>ToBytes()</c> call while the builder retains the
    /// full, verified serialisation behaviour of <see cref="XmsgFrame.ToArray"/> underneath.
    /// </remarks>
    public sealed class XmsgPacket
    {
        private readonly XmsgFrame _frame;

        /// <summary>
        /// Initialises an outgoing packet around a built frame.
        /// </summary>
        /// <param name="frame">
        /// The frame to send; must not be null and must have a header.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="frame"/> or its header is null.
        /// </exception>
        public XmsgPacket(XmsgFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (frame.Header == null)
            {
                throw new ArgumentNullException(nameof(frame), "Frame header is null.");
            }

            _frame = frame;
        }

        /// <summary>
        /// Gets the underlying frame (for inspection; serialisation goes through <see cref="ToBytes"/>).
        /// </summary>
        public XmsgFrame Frame
        {
            get { return _frame; }
        }

        /// <summary>
        /// Gets the transport-level packet type (the SINTRAN subtype at header offset 3).
        /// </summary>
        public XmsgPacketType Type
        {
            get { return (XmsgPacketType)(byte)_frame.Header.Subtype; }
        }

        /// <summary>
        /// Gets the destination node number.
        /// </summary>
        public ushort DestinationNode
        {
            get { return _frame.Header.DestinationNode; }
        }

        /// <summary>
        /// Gets the source node number.
        /// </summary>
        public ushort SourceNode
        {
            get { return _frame.Header.SourceNode; }
        }

        /// <summary>
        /// Gets the HIGH byte of the header checksum (offset 12), under the historical
        /// <see cref="SintranProtocolId"/> names.
        /// </summary>
        /// <remarks>
        /// It is NOT a sub-protocol selector or a channel, whatever the enum members are called:
        /// offsets 12-13 are one ones-complement checksum over header words 0-5. The names record
        /// which traffic happened to produce that byte in the captures. Dispatch on the subtype and
        /// on XMCSM instead.
        /// </remarks>
        public SintranProtocolId ProtocolId
        {
            get { return (SintranProtocolId)(byte)(_frame.Header.Checksum >> 8); }
        }

        /// <summary>
        /// Serialises this packet into a new information-field byte array.
        /// </summary>
        /// <returns>
        /// The serialised information field.
        /// </returns>
        public byte[] ToBytes()
        {
            return _frame.ToArray();
        }
    }
}
