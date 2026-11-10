//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Programs.Commands
{
    /// <summary>
    /// One line a command gave back.
    /// </summary>
    public readonly struct CommandOutputLine : IEquatable<CommandOutputLine>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CommandOutputLine"/> struct.
        /// </summary>
        /// <param name="kind">
        /// What kind of line it is.
        /// </param>
        /// <param name="text">
        /// The words.
        /// </param>
        public CommandOutputLine(CommandOutputKind kind, string text)
        {
            Kind = kind;
            Text = text ?? string.Empty;
        }

        /// <summary>
        /// Gets what kind of line it is.
        /// </summary>
        public CommandOutputKind Kind { get; }

        /// <summary>
        /// Gets the words.
        /// </summary>
        public string Text { get; }

        /// <inheritdoc/>
        public bool Equals(CommandOutputLine other)
        {
            return Kind == other.Kind && string.Equals(Text, other.Text, StringComparison.Ordinal);
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            return obj is CommandOutputLine other && Equals(other);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            return HashCode.Combine(Kind, Text);
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Kind == CommandOutputKind.Text ? Text : Kind + ": " + Text;
        }
    }
}
