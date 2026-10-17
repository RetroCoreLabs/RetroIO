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
    /// Down-facing contract of a framing codec (delimiting/escaping only, no FCS).
    /// </summary>
    /// <remarks>
    /// A framing codec sits between the transport (below) and the LAPB link layer (above). It carries
    /// an opaque payload and does NOT interpret it, so the same codec can frame X.25 packets, SINTRAN
    /// messages, or LAPB frames. Selectable implementations are SLIP (RFC 1055), HDLC/PPP (RFC 1662),
    /// and XOT (RFC 1613 length-prefix).
    ///
    /// This interface is the "down" half of the seam — calls made INTO the codec:
    ///  - <see cref="SendFrame"/> from the layer above (frame out).
    ///  - <see cref="ProcessBytes"/> from the transport below (bytes in).
    /// The "up" half — a de-framed frame delivered to the layer above — is the FrameReceived event on
    /// <see cref="FrameCodecBase"/> (events up, interfaces down).
    /// </remarks>
    public interface IFrameCodec
    {
        /// <summary>
        /// Frames a single outgoing frame and writes it to the transport below.
        /// </summary>
        /// <param name="frame">
        /// The raw payload to frame (opaque to the codec). Only valid for the duration of the call.
        /// </param>
        void SendFrame(ReadOnlySpan<byte> frame);

        /// <summary>
        /// Feeds raw bytes received from the transport into the codec's incremental de-framer.
        /// </summary>
        /// <param name="data">
        /// Bytes as they arrived from the medium; may contain zero, part of one, or several frames.
        /// Only valid for the duration of the call.
        /// </param>
        void ProcessBytes(ReadOnlySpan<byte> data);

        /// <summary>
        /// Discards any partially received frame and resets the receive state machine.
        /// </summary>
        void Reset();
    }
}
