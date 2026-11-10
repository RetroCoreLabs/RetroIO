//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Programs.Commands
{
    /// <summary>
    /// What kind of line a command gives back, so every front end can show it the same way.
    /// </summary>
    public enum CommandOutputKind
    {
        /// <summary>
        /// An ordinary answer line.
        /// </summary>
        Text = 0,

        /// <summary>
        /// The command ran, but something is worth noticing.
        /// </summary>
        Warning = 1,

        /// <summary>
        /// The command failed, or could not be run at all.
        /// </summary>
        Error = 2,
    }
}
