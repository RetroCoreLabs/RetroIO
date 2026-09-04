using System;

using RetroIO.Protocols.Xmsg.Packet;

namespace RetroIO.Protocols.Xmsg.Codec
{
    /// <summary>
    /// Up-event delegate: a complete XMSG packet arrived from the link identified by
    /// <paramref name="linkId"/> (sender/link-id first, per the seam's named-delegate rule).
    /// </summary>
    /// <param name="linkId">
    /// The identity of the link the packet came in on.
    /// </param>
    /// <param name="packet">
    /// The decoded read-only packet view.
    /// </param>
    public delegate void XmsgPacketReceived(string linkId, XmsgPacketInfo packet);

    /// <summary>
    /// The XMSG codec seam: parse bytes coming up from the link into packets, and encode packets
    /// going down to the transport. Pure parse/encode - it holds NO session or reliability state
    /// (that lives one layer up in <c>XmsgLayer</c>, per the plan's decision table).
    /// </summary>
    /// <remarks>
    /// Incoming bytes are surfaced as the <see cref="PacketReceived"/> up-event; outgoing packets
    /// are pushed via <see cref="SendPacket"/>. The seam deliberately matches the shape of the X.25
    /// codec so the two sit side-by-side under one composition root at migration time.
    /// </remarks>
    public interface IXmsgCodec
    {
        /// <summary>
        /// Occurs when <see cref="ProcessBytes"/> decodes a valid packet.
        /// </summary>
        event XmsgPacketReceived PacketReceived;

        /// <summary>
        /// Encodes an outgoing packet and sends it down through the held transport.
        /// </summary>
        /// <param name="packet">
        /// The packet to serialise and send.
        /// </param>
        void SendPacket(XmsgPacket packet);

        /// <summary>
        /// Processes one complete information field arriving from the link, raising the
        /// <see cref="PacketReceived"/> up-event when it decodes to a valid XMSG packet.
        /// </summary>
        /// <param name="data">
        /// The information-field bytes (SINTRAN header onward).
        /// </param>
        void ProcessBytes(ReadOnlySpan<byte> data);

        /// <summary>
        /// Resets any transient decode state (called on a link resync).
        /// </summary>
        void Reset();
    }
}
