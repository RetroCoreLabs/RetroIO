//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Hdlc
{
    /// <summary>
    /// One TCP segment payload extracted from a captured Ethernet/IPv4/TCP packet.
    /// </summary>
    /// <remarks>
    /// Segments carrying no payload (pure ACK / SYN / FIN) are not emitted. The
    /// <see cref="Sequence"/> is the raw 32-bit TCP sequence number used later to order
    /// segments within a <see cref="StreamKey"/> before reassembly.
    /// </remarks>
    public sealed class TcpSegment
    {
        /// <summary>
        /// The directional flow this segment belongs to.
        /// </summary>
        public readonly StreamKey Key;

        /// <summary>
        /// The raw 32-bit TCP sequence number of the first payload byte.
        /// </summary>
        public readonly uint Sequence;

        /// <summary>
        /// The order in which this segment was read from the capture, used as a stable
        /// tie-breaker when two segments share a sequence number.
        /// </summary>
        public readonly int Ordinal;

        /// <summary>
        /// The TCP payload bytes (never empty).
        /// </summary>
        public readonly byte[] Payload;

        /// <summary>
        /// Initialises a new TCP segment record.
        /// </summary>
        /// <param name="key">
        /// The directional flow key.
        /// </param>
        /// <param name="sequence">
        /// The raw 32-bit TCP sequence number.
        /// </param>
        /// <param name="ordinal">
        /// The capture read order, used as a stable sort tie-breaker.
        /// </param>
        /// <param name="payload">
        /// The TCP payload bytes.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="payload"/> is null.
        /// </exception>
        public TcpSegment(StreamKey key, uint sequence, int ordinal, byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            Key = key;
            Sequence = sequence;
            Ordinal = ordinal;
            Payload = payload;
        }
    }
}
