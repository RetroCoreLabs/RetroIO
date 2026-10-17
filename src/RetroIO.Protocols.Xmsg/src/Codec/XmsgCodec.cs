//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Packet;

namespace RetroIO.Protocols.Xmsg.Codec
{
    /// <summary>
    /// The concrete XMSG codec: parses one complete information field into an
    /// <see cref="XmsgPacketInfo"/> and raises it upward; serialises an <see cref="XmsgPacket"/> and
    /// sends it downward. Stateless - it holds nothing between frames.
    /// </summary>
    /// <remarks>
    /// The link below delivers already-deframed information fields (LAPB reassembles the HDLC
    /// I-frames into one information field before it reaches us), so <see cref="ProcessBytes"/>
    /// treats its input as exactly one packet. A span that is too short or does not begin with the
    /// SINTRAN Marker 1 is ignored rather than raised - the same log-and-drop policy the live node
    /// used, so stray keepalive/garbage never reaches a service handler.
    /// </remarks>
    public sealed class XmsgCodec : XmsgCodecBase
    {
        /// <summary>
        /// Initialises the codec with its link identity and downward transport.
        /// </summary>
        /// <param name="linkId">
        /// The link identity stamped on each up-event.
        /// </param>
        /// <param name="transport">
        /// The downward transport sink.
        /// </param>
        public XmsgCodec(string linkId, IXmsgTransport transport)
            : base(linkId, transport)
        {
        }

        /// <inheritdoc />
        public override void ProcessBytes(ReadOnlySpan<byte> data)
        {
            // One information field == one packet at this seam. Reject non-XMSG / short spans
            // quietly (keepalive RRs and framing noise never carry a SINTRAN header).
            if (XmsgPacketParser.TryParsePacket(data, out XmsgPacketInfo? packet) && packet != null)
            {
                RaisePacketReceived(packet);
            }
        }

        /// <inheritdoc />
        public override void SendPacket(XmsgPacket packet)
        {
            if (packet == null)
            {
                throw new ArgumentNullException(nameof(packet));
            }

            // Serialise once and hand the exact information-field bytes to the transport below.
            byte[] bytes = packet.ToBytes();
            Transport.Send(bytes);
        }
    }
}
