//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The kind of SINTRAN program that owns an XMSG task, which decides whether it may become
    /// privileged (XFPRV).
    /// </summary>
    /// <remarks>
    /// <para>
    /// COSMOS Programmer Guide ND-60.164.3, appendix A section 4.8 (Make Calling Task
    /// Privileged): "the caller must be either a driver, direct task, foreground program or
    /// background program, logged in as user SYSTEM." The error text for XEPVR (Privilege request
    /// refused) is more exact about foreground programs: "foreground program on ring 2 or 3".
    /// The XMPFPRV routine page says the same as section 4.8.
    /// </para>
    /// <para>
    /// The guide does not say what a ring 0 or ring 1 foreground program, or a background program
    /// logged in as anyone but SYSTEM, gets. Those are listed here so a caller can say what it
    /// is, and they are refused, because the guide names only the kinds that are allowed.
    /// </para>
    /// </remarks>
    public enum XmsgTaskKind
    {
        /// <summary>
        /// The kind of program was not stated, so no privilege is granted.
        /// </summary>
        Unspecified = 0,

        /// <summary>
        /// A device driver. May become privileged.
        /// </summary>
        Driver = 1,

        /// <summary>
        /// A direct task. May become privileged.
        /// </summary>
        DirectTask = 2,

        /// <summary>
        /// A foreground program on ring 1. Not named by the guide, so refused.
        /// </summary>
        ForegroundRing1 = 3,

        /// <summary>
        /// A foreground program on ring 2. May become privileged.
        /// </summary>
        ForegroundRing2 = 4,

        /// <summary>
        /// A foreground program on ring 3. May become privileged.
        /// </summary>
        ForegroundRing3 = 5,

        /// <summary>
        /// A background program logged in as user SYSTEM. May become privileged.
        /// </summary>
        BackgroundSystemUser = 6,

        /// <summary>
        /// A background program logged in as any other user. Refused.
        /// </summary>
        BackgroundOtherUser = 7,
    }
}
