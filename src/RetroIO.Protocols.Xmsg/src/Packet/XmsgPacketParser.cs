//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Packet
{
    /// <summary>
    /// Parses a raw XMSG information-field span into the read-only <see cref="XmsgPacketInfo"/>
    /// seam view. A thin wrapper over <see cref="XmsgFrame.Parse"/> that keeps the codec free of
    /// any direct dependency on the frame decoder's shape.
    /// </summary>
    public static class XmsgPacketParser
    {
        /// <summary>
        /// The minimum length of a well-formed XMSG information field (a bare SINTRAN header).
        /// </summary>
        public const int MinimumLength = SintranHeader.Size;

        /// <summary>
        /// Decodes an XMSG packet from an HDLC information-field span.
        /// </summary>
        /// <param name="data">
        /// The information-field bytes, starting at Marker 1 (offset 0).
        /// </param>
        /// <returns>
        /// The decoded <see cref="XmsgPacketInfo"/>.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="data"/> is shorter than a SINTRAN header.
        /// </exception>
        public static XmsgPacketInfo ParsePacket(ReadOnlySpan<byte> data)
        {
            if (data.Length < MinimumLength)
            {
                throw new ArgumentException(
                    "An XMSG packet requires at least a 13-byte SINTRAN header.", nameof(data));
            }

            // XmsgFrame.Parse retains the exact input as RawBytes, so a round-trip through the
            // packet view re-encodes byte-for-byte even for sub-protocol tails this model does not
            // structurally decode (TAD/PAD/DB) - the Phase 1 gate depends on that guarantee.
            XmsgFrame frame = XmsgFrame.Parse(data);
            return new XmsgPacketInfo(frame);
        }

        /// <summary>
        /// Attempts to decode an XMSG packet, returning <c>false</c> instead of throwing when the
        /// span is too short or does not begin with the SINTRAN Marker 1 (<c>0x21</c>).
        /// </summary>
        /// <param name="data">
        /// The candidate information-field bytes.
        /// </param>
        /// <param name="packet">
        /// On success, the decoded packet; otherwise <c>null</c>.
        /// </param>
        /// <returns>
        /// <c>true</c> when a packet was decoded; otherwise <c>false</c>.
        /// </returns>
        public static bool TryParsePacket(ReadOnlySpan<byte> data, out XmsgPacketInfo? packet)
        {
            if (data.Length < MinimumLength || data[0] != SintranHeader.Marker1Value)
            {
                packet = null;
                return false;
            }

            packet = ParsePacket(data);
            return true;
        }
    }
}
