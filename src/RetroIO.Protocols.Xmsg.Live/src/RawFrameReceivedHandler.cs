//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Live
{
    /// <summary>
    /// Receives the raw body of an FCS-valid LAPB frame taken off the transport.
    /// </summary>
    /// <remarks>
    /// Raised before the link state machine processes the frame, so the bytes are exactly what
    /// arrived after HDLC de-framing and byte-unstuffing: address, control, then the information
    /// field. The FCS has been checked and removed.
    /// </remarks>
    /// <param name="frameBytes">
    /// The de-framed LAPB frame body (address, control, information), without the FCS.
    /// </param>
    public delegate void RawFrameReceivedHandler(byte[] frameBytes);
}
