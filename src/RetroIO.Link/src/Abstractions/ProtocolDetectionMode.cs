//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Link
{
    /// <summary>
    /// Protocol detection mode for a link.
    /// </summary>
    public enum ProtocolDetectionMode
    {
        /// <summary>All traffic on this link is X.25.</summary>
        X25Only,

        /// <summary>All traffic on this link is SINTRAN.</summary>
        SintranOnly,

        /// <summary>Detect protocol per frame using the 0x21 0x13 markers.</summary>
        AutoDetect
    }
}
