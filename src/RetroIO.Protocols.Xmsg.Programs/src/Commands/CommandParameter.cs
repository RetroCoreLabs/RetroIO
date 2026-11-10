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
    /// One parameter of a command: its name, what it is, and whether it must be given.
    /// </summary>
    public sealed class CommandParameter
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CommandParameter"/> class.
        /// </summary>
        /// <param name="name">
        /// The name shown in help and in "missing parameter" messages, for example <c>room</c>.
        /// </param>
        /// <param name="description">
        /// What the parameter is, in a few words.
        /// </param>
        /// <param name="required">
        /// True when the command fails without it.
        /// </param>
        /// <param name="takesRestOfLine">
        /// True when it takes everything left on the line, spaces and commas included - the text of
        /// <c>/tell NAME text</c>. Only the last parameter may.
        /// </param>
        public CommandParameter(string name, string description, bool required, bool takesRestOfLine = false)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A parameter needs a name.", nameof(name));
            }

            Name = name;
            Description = description ?? string.Empty;
            Required = required;
            TakesRestOfLine = takesRestOfLine;
        }

        /// <summary>
        /// Gets the name shown in help and in "missing parameter" messages.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets what the parameter is.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Gets a value indicating whether the command fails without it.
        /// </summary>
        public bool Required { get; }

        /// <summary>
        /// Gets a value indicating whether it takes everything left on the line.
        /// </summary>
        public bool TakesRestOfLine { get; }

        /// <summary>
        /// Required ones are shown as <c>&lt;name&gt;</c>, optional ones as <c>[name]</c>.
        /// </summary>
        /// <returns>
        /// The parameter as help shows it.
        /// </returns>
        public override string ToString()
        {
            return Required ? "<" + Name + ">" : "[" + Name + "]";
        }
    }
}
