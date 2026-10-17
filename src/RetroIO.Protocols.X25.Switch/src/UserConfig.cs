//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Switch
{
    /// <summary>
    /// A management user account for the switch's authenticated PAD/NMC surface.
    /// </summary>
    public sealed class UserConfig
    {
        /// <summary>
        /// Login name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Password (plain text).
        /// </summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// Privilege level: 0 = operator, 1 = admin.
        /// </summary>
        public int Level { get; set; }
    }
}
