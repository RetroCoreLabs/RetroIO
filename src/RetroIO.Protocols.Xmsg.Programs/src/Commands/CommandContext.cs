//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Programs.Commands
{
    /// <summary>
    /// What a command's function is given: the parameters that were typed, and where to write its
    /// output lines.
    /// </summary>
    public sealed class CommandContext
    {
        private readonly string?[] _values;
        private readonly List<CommandOutputLine> _output;

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandContext"/> class.
        /// </summary>
        /// <param name="command">
        /// The command being run.
        /// </param>
        /// <param name="values">
        /// One value per parameter, in order; null where it was not given.
        /// </param>
        /// <param name="output">
        /// Where the output lines go.
        /// </param>
        internal CommandContext(CommandDefinition command, string?[] values, List<CommandOutputLine> output)
        {
            Command = command;
            _values = values;
            _output = output;
        }

        /// <summary>
        /// Gets the command being run.
        /// </summary>
        public CommandDefinition Command { get; }

        /// <summary>
        /// Gets a value indicating whether the command has written an error line.
        /// </summary>
        public bool Failed { get; private set; }

        /// <summary>
        /// Gets a parameter's value by its name.
        /// </summary>
        /// <param name="name">
        /// The parameter's name as registered. Letter case does not matter.
        /// </param>
        /// <returns>
        /// The typed value, or null when it was not given.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// The command has no parameter of that name - a mistake in the program, not in what was typed.
        /// </exception>
        public string? Get(string name)
        {
            IReadOnlyList<CommandParameter> parameters = Command.Parameters;
            for (int i = 0; i < parameters.Count; i++)
            {
                if (string.Equals(parameters[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return _values[i];
                }
            }

            throw new ArgumentException("The command " + Command.Name + " has no parameter called '" + name + "'.", nameof(name));
        }

        /// <summary>
        /// Gets a parameter's value by its name, or a default when it was not given.
        /// </summary>
        /// <param name="name">
        /// The parameter's name as registered.
        /// </param>
        /// <param name="otherwise">
        /// What to use when it was not given.
        /// </param>
        /// <returns>
        /// The typed value, or <paramref name="otherwise"/>.
        /// </returns>
        public string Get(string name, string otherwise)
        {
            return Get(name) ?? otherwise;
        }

        /// <summary>
        /// Writes an ordinary answer line.
        /// </summary>
        /// <param name="text">
        /// The words.
        /// </param>
        public void Write(string text)
        {
            _output.Add(new CommandOutputLine(CommandOutputKind.Text, text));
        }

        /// <summary>
        /// Writes a line worth noticing; the command still counts as done.
        /// </summary>
        /// <param name="text">
        /// The words.
        /// </param>
        public void Warn(string text)
        {
            _output.Add(new CommandOutputLine(CommandOutputKind.Warning, text));
        }

        /// <summary>
        /// Writes an error line; the command then counts as failed.
        /// </summary>
        /// <param name="text">
        /// The words.
        /// </param>
        public void Fail(string text)
        {
            Failed = true;
            _output.Add(new CommandOutputLine(CommandOutputKind.Error, text));
        }
    }
}
