//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Programs.Commands
{
    /// <summary>
    /// What running one typed line gave: whether it worked, which command it was, and the output lines.
    /// </summary>
    public sealed class CommandResult
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CommandResult"/> class.
        /// </summary>
        /// <param name="command">
        /// The command that was found, or null when none was.
        /// </param>
        /// <param name="succeeded">
        /// True when the command was found, its parameters were right, and it wrote no error line.
        /// </param>
        /// <param name="lines">
        /// Zero or more output lines, in the order they were written.
        /// </param>
        internal CommandResult(CommandDefinition? command, bool succeeded, IReadOnlyList<CommandOutputLine> lines)
        {
            Command = command;
            Succeeded = succeeded;
            Lines = lines;
        }

        /// <summary>
        /// Gets the command that was found, or null when none was (unknown, ambiguous or empty line).
        /// </summary>
        public CommandDefinition? Command { get; }

        /// <summary>
        /// Gets a value indicating whether the command ran and did not fail.
        /// </summary>
        public bool Succeeded { get; }

        /// <summary>
        /// Gets the output lines, in the order they were written.
        /// </summary>
        public IReadOnlyList<CommandOutputLine> Lines { get; }
    }
}
