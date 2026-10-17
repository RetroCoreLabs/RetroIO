//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Live
{
    /// <summary>
    /// The connection state of a modulo-8 LAPB ABM link (ND LAPB spec section 6.1).
    /// </summary>
    /// <remarks>
    /// Peer-busy (set by RNR, cleared by RR/REJ) and the reject condition (one REJ per gap) are
    /// boolean flags WITHIN <see cref="Connected"/>, not separate states.
    /// </remarks>
    public enum LapbLayerState
    {
        /// <summary>
        /// No link established; no sequence state is valid.
        /// </summary>
        Disconnected,

        /// <summary>
        /// A SABM has been transmitted and the matching UA is awaited.
        /// </summary>
        SabmSent,

        /// <summary>
        /// The link is established; <c>V(S)</c>, <c>V(R)</c> and <c>V(A)</c> are being maintained.
        /// </summary>
        Connected,

        /// <summary>
        /// A DISC has been transmitted and the matching UA or DM is awaited.
        /// </summary>
        DiscSent,

        /// <summary>
        /// A FRMR has been transmitted and a SABM or DISC from the peer is awaited (section 6.1).
        /// </summary>
        FrmrSent,
    }
}
