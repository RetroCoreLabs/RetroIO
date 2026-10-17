//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Live
{
    /// <summary>
    /// Which LAPB dialect a link speaks. The frame engine, the timers and the state table are the
    /// same for both; what differs is the address byte and what may ride in an information field.
    /// </summary>
    public enum LapbVariant
    {
        /// <summary>
        /// The Norsk Data dialect (<c>DOC\LAPB-REQUIREMENTS.md</c>, capture-verified): address
        /// <c>0x01</c> on link-management frames and <c>0x09</c> on data-transfer frames, with bit
        /// <c>0x80</c> set when an information field has an odd byte count; SABM, UA, DISC, DM and
        /// the supervisory frames carry the sender's two-octet node number as their information
        /// field. This is what the ND-100 machines on the bench speak.
        /// </summary>
        Nd = 0,

        /// <summary>
        /// ITU-T X.25 (10/96) clause 2. Address A (<c>0x03</c>) on DCE commands and DTE responses,
        /// address B (<c>0x01</c>) on DTE commands and DCE responses (2.4.2); no information field
        /// on any frame but I and FRMR (2.3.4); the FRMR diagnostic carries the C/R bit (2.3.4.10).
        /// This is what an X.25 network expects, and what the links under the X.25 stack must speak.
        /// </summary>
        Itu = 1,
    }
}
