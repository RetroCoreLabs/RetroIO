//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Link.Models
{
    /// <summary>
    /// Frame direction.
    /// </summary>
    public enum FrameDirection
    {
        /// <summary>
        /// Transmitted.
        /// </summary>
        Transmit = 0,

        /// <summary>
        /// Received.
        /// </summary>
        Receive = 1,

        /// <summary>
        /// Internal.
        /// </summary>
        Internal = 2
    }
}
