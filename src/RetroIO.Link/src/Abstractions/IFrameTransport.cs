//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Link.Framing
{
    /// <summary>
    /// Down-facing transport sink for a framing codec.
    /// </summary>
    /// <remarks>
    /// A framing codec frames an outgoing frame (byte-stuffing or length-prefix) and hands the
    /// resulting bytes to the transport below it through this interface. This is the "down" half of
    /// the framing sublayer seam; the "up" half is the FrameReceived event on <see cref="FrameCodecBase"/>.
    /// Implementations are the concrete links (serial, TCP) or, in tests, a capturing loopback.
    /// </remarks>
    public interface IFrameTransport
    {
        /// <summary>
        /// Writes fully framed bytes to the underlying medium.
        /// </summary>
        /// <param name="bytes">
        /// The framed bytes to transmit. The span is only valid for the duration of the call;
        /// implementations that need to retain it must copy.
        /// </param>
        void Send(ReadOnlySpan<byte> bytes);
    }
}
