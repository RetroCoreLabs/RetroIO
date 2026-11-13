//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Link;
using RetroIO.Link.Framing;
using RetroIO.Protocols.X25.PacketLayer;

namespace RetroIO.Protocols.X25.Client
{
    /// <summary>
    /// One TCP connection of an <see cref="X25Client"/> with the framing codec and packet layer on it, and the
    /// circuits it carries. RFC 1613 section 5 gives each circuit a connection of its own, so a client has one
    /// of these for every call it placed, plus the one <c>Open</c> made.
    /// </summary>
    /// <remarks>
    /// Every member is used with the owning client's lock held, except <see cref="Transport"/> and <see cref="Codec"/>
    /// which are fixed when the leg is made.
    /// </remarks>
    internal sealed class X25ClientLeg
    {
        // RFC 1613 section 6: the logical channel number in an XOT packet "has no significance and has arbitrary values",
        // and a forwarding end "MUST set the packet's logical channel number to that used on the outgoing interface". So a
        // peer may answer our Call Request on a different channel number than we sent it on (x25emu does: a call placed on
        // channel 8 is answered on channel 1). The channel the peer chose for each answered call is kept here; the peer's
        // number is translated to ours on the way in and back on the way out.
        private readonly Dictionary<ushort, ushort> _wireToLocal = new Dictionary<ushort, ushort>();
        private readonly Dictionary<ushort, ushort> _localToWire = new Dictionary<ushort, ushort>();

        /// <summary>
        /// Creates a leg. The packet layer is set by the owner right after, because it needs this leg for its sink.
        /// </summary>
        /// <param name="isPrimary">True for the connection <c>Open</c> made.</param>
        /// <param name="transport">The TCP transport.</param>
        /// <param name="codec">The XOT framing codec on it.</param>
        internal X25ClientLeg(bool isPrimary, TcpLinkTransport transport, XotFrameCodec codec)
        {
            IsPrimary = isPrimary;
            Transport = transport;
            Codec = codec;
        }

        /// <summary>True for the connection <c>Open</c> made; it is not closed when a circuit clears.</summary>
        internal bool IsPrimary { get; }

        /// <summary>The TCP transport.</summary>
        internal TcpLinkTransport Transport { get; }

        /// <summary>The XOT framing codec.</summary>
        internal XotFrameCodec Codec { get; }

        /// <summary>The packet layer on this connection.</summary>
        internal X25Layer Layer { get; set; } = null!;

        /// <summary>
        /// True once a call we placed has used this connection. A connection carries one call we place, so the next call
        /// gets a new connection.
        /// </summary>
        internal bool CallPlaced { get; set; }

        /// <summary>
        /// True once the client itself is closing this connection; the transport's closed event is then no news.
        /// </summary>
        internal volatile bool Closing;

        /// <summary>
        /// True once the TCP connection has dropped under us. Its circuits have been reported cleared.
        /// </summary>
        internal bool Dead { get; set; }

        /// <summary>The circuits on this connection, by the packet layer's own channel number.</summary>
        internal Dictionary<ushort, X25Vc> Vcs { get; } = new Dictionary<ushort, X25Vc>();

        /// <summary>Calls placed and not yet answered (the packet layer's channel numbers).</summary>
        internal List<ushort> AwaitingAnswer { get; } = new List<ushort>();

        /// <summary>
        /// Forgets the channel the peer chose for a circuit.
        /// </summary>
        /// <param name="local">Our channel number for the circuit.</param>
        internal void ForgetChannel(ushort local)
        {
            if (_localToWire.TryGetValue(local, out ushort boundWire))
            {
                _localToWire.Remove(local);
                _wireToLocal.Remove(boundWire);
            }
        }

        /// <summary>
        /// Turns the channel number the peer used into the one our call has, when the peer answered on another number.
        /// </summary>
        /// <remarks>
        /// A packet on a channel we know nothing about is the answer to our call (Call Accepted, or a Clear for it) only
        /// when exactly one call of ours is waiting for an answer: then that number is bound to that call. With several
        /// calls waiting there is no way to tell whose answer it is, so it is passed on unchanged. (On a connection
        /// opened for one call there is never more than one.)
        /// </remarks>
        internal ReadOnlySpan<byte> TranslateInbound(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 3)
            {
                return frame;
            }

            ushort wire = (ushort)(((frame[0] & 0x0F) << 8) | frame[1]);
            if (wire == 0)
            {
                return frame;
            }

            ushort local;
            if (!_wireToLocal.TryGetValue(wire, out local))
            {
                bool answer = frame[2] == 0x0F || frame[2] == 0x13;
                if (!answer || AwaitingAnswer.Contains(wire) || AwaitingAnswer.Count != 1)
                {
                    return frame;
                }

                local = AwaitingAnswer[0];
                _wireToLocal[wire] = local;
                _localToWire[local] = wire;
            }

            if (frame[2] == 0x0F)
            {
                AwaitingAnswer.Remove(local);
            }

            if (local == wire)
            {
                return frame;
            }

            byte[] copy = frame.ToArray();
            copy[0] = (byte)((copy[0] & 0xF0) | (local >> 8));
            copy[1] = (byte)(local & 0xFF);
            return copy;
        }

        /// <summary>
        /// Turns our channel number into the one the peer chose for the call, if it chose another. The packet layer calls
        /// the sink with the client's lock already held.
        /// </summary>
        internal ReadOnlySpan<byte> TranslateOutbound(ReadOnlySpan<byte> packet)
        {
            if (packet.Length < 3 || _localToWire.Count == 0)
            {
                return packet;
            }

            ushort local = (ushort)(((packet[0] & 0x0F) << 8) | packet[1]);
            if (local == 0 || !_localToWire.TryGetValue(local, out ushort wire) || wire == local)
            {
                return packet;
            }

            byte[] copy = packet.ToArray();
            copy[0] = (byte)((copy[0] & 0xF0) | (wire >> 8));
            copy[1] = (byte)(wire & 0xFF);
            return copy;
        }
    }
}
