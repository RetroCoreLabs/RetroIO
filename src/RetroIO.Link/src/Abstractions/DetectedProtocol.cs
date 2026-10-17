//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Link
{
    /// <summary>
    /// Detected protocol type from a LAPB I-frame payload.
    /// </summary>
    public enum DetectedProtocol
    {
        /// <summary>
        /// X.25 packet (no SINTRAN markers).
        /// </summary>
        X25,

        /// <summary>
        /// SINTRAN frame (has 0x21 0x13 markers).
        /// </summary>
        Sintran
    }
}
