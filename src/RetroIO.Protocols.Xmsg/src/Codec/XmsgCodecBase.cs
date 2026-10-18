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
    /// Shared base for an <see cref="IXmsgCodec"/>: owns the up-event (<see cref="PacketReceived"/>),
    /// the raise helper, the held-below <see cref="IXmsgTransport"/>, and the link identity. Concrete
    /// codecs implement only the parse (<see cref="ProcessBytes"/>) and encode (<see cref="SendPacket"/>).
    /// </summary>
    /// <remarks>
    /// This realises the seam's events-up / interfaces-down rule: the codec calls DOWN through
    /// <see cref="Transport"/> and signals UP through the named delegate <see cref="XmsgPacketReceived"/>
    /// (sender/link-id first parameter, no <c>EventHandler</c>/<c>EventArgs</c>). The base is abstract
    /// so a test double and the real <see cref="XmsgCodec"/> share the identical up-event contract.
    /// </remarks>
    public abstract class XmsgCodecBase : IXmsgCodec
    {
        private readonly IXmsgTransport _transport;
        private readonly string _linkId;

        /// <summary>
        /// Occurs when <see cref="ProcessBytes"/> decodes a valid packet. Declared on
        /// <see cref="IXmsgCodec"/> so the layer above subscribes through the codec interface; the
        /// delegate type <see cref="XmsgPacketReceived"/> lives at namespace scope.
        /// </summary>
        public event XmsgPacketReceived? PacketReceived;

        /// <summary>
        /// Initialises the base with the downward transport and the link identity stamped on
        /// every up-event.
        /// </summary>
        /// <param name="linkId">
        /// The link identity (for example <c>"hdlc:127.0.0.1:10362"</c>).
        /// </param>
        /// <param name="transport">
        /// The downward transport sink.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="linkId"/> or <paramref name="transport"/> is null.
        /// </exception>
        protected XmsgCodecBase(string linkId, IXmsgTransport transport)
        {
            _linkId = linkId ?? throw new ArgumentNullException(nameof(linkId));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        /// <summary>
        /// Gets the link identity stamped on each up-event.
        /// </summary>
        public string LinkId
        {
            get { return _linkId; }
        }

        /// <summary>
        /// Gets the downward transport a concrete codec sends encoded bytes to.
        /// </summary>
        protected IXmsgTransport Transport
        {
            get { return _transport; }
        }

        /// <summary>
        /// Encodes an outgoing packet and sends it down through the held transport.
        /// </summary>
        /// <param name="packet">
        /// The packet to serialise and send.
        /// </param>
        public abstract void SendPacket(XmsgPacket packet);

        /// <summary>
        /// Processes one complete information field arriving from the link, raising the
        /// <see cref="PacketReceived"/> up-event when it decodes to a valid XMSG packet.
        /// </summary>
        /// <param name="data">
        /// The information-field bytes (SINTRAN header onward).
        /// </param>
        public abstract void ProcessBytes(ReadOnlySpan<byte> data);

        /// <summary>
        /// Resets any transient decode state (called on a link resync).
        /// </summary>
        /// <remarks>
        /// The base holds no decode state, so this implementation does nothing. A concrete codec
        /// that buffers data overrides it.
        /// </remarks>
        public virtual void Reset()
        {
            // No transient decode state in the base; concrete codecs override if they buffer.
        }

        /// <summary>
        /// Raises the <see cref="PacketReceived"/> up-event, stamping this codec's link id.
        /// </summary>
        /// <param name="packet">
        /// The decoded packet to surface upward.
        /// </param>
        protected void RaisePacketReceived(XmsgPacketInfo packet)
        {
            PacketReceived?.Invoke(_linkId, packet);
        }
    }
}
