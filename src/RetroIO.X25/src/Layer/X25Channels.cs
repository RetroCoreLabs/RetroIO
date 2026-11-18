//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.PacketLayer
{
    /// <summary>
    /// The logical channel numbers X.25 allows, and the channel count a line gets when its configuration
    /// does not give one.
    /// </summary>
    /// <remarks>
    /// X.25 (10/96) 3.1: each virtual call or permanent virtual circuit has a logical channel group number
    /// "less than or equal to 15" and a logical channel number "less than or equal to 255", so a channel
    /// number is twelve bits, 0..4095; channel 0 is kept for restart and diagnostic packets (5.1.3.1). How
    /// many channels a line uses is agreed with the Administration at subscription (3.1, Annex A).
    /// Decided 2026-11-18: when the configuration agrees nothing, a line gets every channel from its base
    /// up to 4095. Until then the default was 16. A count that IS configured and runs past 4095 is refused
    /// by the checks that read it (<see cref="X25Config.Validate"/>, the switch config loader).
    /// </remarks>
    public static class X25Channels
    {
        /// <summary>The highest logical channel number, 4095 (twelve bits; X.25 3.1).</summary>
        public const ushort Highest = 4095;

        /// <summary>
        /// The channel count a line gets when none is configured: every channel from
        /// <paramref name="lciBase"/> up to <see cref="Highest"/>.
        /// </summary>
        /// <param name="lciBase">The line's first channel. 0 is taken as 1, the way the packet layer reads it.</param>
        /// <returns>
        /// The count; 0 when the base is above 4095, which every check then refuses as an empty range.
        /// </returns>
        public static ushort DefaultCount(ushort lciBase)
        {
            if (lciBase <= 1)
            {
                return Highest;
            }

            return lciBase > Highest ? (ushort)0 : (ushort)(Highest + 1 - lciBase);
        }
    }
}
