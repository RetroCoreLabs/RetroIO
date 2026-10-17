//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// The 7CESC escape-control state (spec TAD-Message-Formats.md 4.3 / 22.12): whether terminal
    /// escape processing is enabled. It brackets the login/logout phase changes; the asker answers
    /// each CESC with a 7CERS.
    /// </summary>
    /// <remarks>
    /// On the wire a CESC message is <c>0E 01 value</c>. Escape is disabled during the login
    /// exchange and re-enabled once logged in.
    /// </remarks>
    public enum CescState : byte
    {
        /// <summary>
        /// Escape disabled (<c>0x00</c>) - sent with the username-accepted burst and in the logout ladder.
        /// </summary>
        EscapeDisabled = 0x00,

        /// <summary>
        /// Escape enabled (<c>0x01</c>) - sent with the password-accepted burst once logged in.
        /// </summary>
        EscapeEnabled = 0x01,
    }
}
