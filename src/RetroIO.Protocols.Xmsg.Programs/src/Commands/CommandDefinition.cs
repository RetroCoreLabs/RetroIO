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
    /// A registered command: its name, what it does, its parameters and the function that runs it.
    /// </summary>
    public sealed class CommandDefinition
    {
        private readonly CommandParameter[] _parameters;
        private readonly string[] _parts;

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandDefinition"/> class.
        /// </summary>
        /// <param name="name">
        /// The full name, parts separated by '-', for example <c>LIST-TRUNKS</c>. Kept in upper case.
        /// </param>
        /// <param name="description">
        /// What the command does, for help.
        /// </param>
        /// <param name="run">
        /// The function that runs it. It reads its parameters from the context and writes its output
        /// lines there.
        /// </param>
        /// <param name="parameters">
        /// The parameters, in the order they are typed.
        /// </param>
        public CommandDefinition(string name, string description, Action<CommandContext> run, params CommandParameter[] parameters)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A command needs a name.", nameof(name));
            }

            Name = name.Trim().ToUpperInvariant();
            _parts = Name.Split('-');
            for (int i = 0; i < _parts.Length; i++)
            {
                if (_parts[i].Length == 0)
                {
                    throw new ArgumentException("An empty part in the command name '" + name + "'.", nameof(name));
                }

                if (_parts[i].IndexOf(' ') >= 0 || _parts[i].IndexOf(',') >= 0)
                {
                    throw new ArgumentException("A command name cannot hold a space or a comma: '" + name + "'.", nameof(name));
                }
            }

            Description = description ?? string.Empty;
            Run = run ?? throw new ArgumentNullException(nameof(run));
            _parameters = parameters ?? Array.Empty<CommandParameter>();

            for (int i = 0; i < _parameters.Length; i++)
            {
                if (_parameters[i] == null)
                {
                    throw new ArgumentException("A parameter of '" + name + "' is null.", nameof(parameters));
                }

                if (_parameters[i].TakesRestOfLine && i != _parameters.Length - 1)
                {
                    throw new ArgumentException(
                        "Only the last parameter of '" + name + "' may take the rest of the line.", nameof(parameters));
                }

                for (int j = 0; j < i; j++)
                {
                    if (string.Equals(_parameters[j].Name, _parameters[i].Name, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException(
                            "Two parameters of '" + name + "' are both called '" + _parameters[i].Name + "'.", nameof(parameters));
                    }
                }
            }
        }

        /// <summary>
        /// Gets the full name, in upper case.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets what the command does.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Gets the parameters, in the order they are typed.
        /// </summary>
        public IReadOnlyList<CommandParameter> Parameters => _parameters;

        /// <summary>
        /// Gets the function that runs the command.
        /// </summary>
        public Action<CommandContext> Run { get; }

        /// <summary>
        /// Gets a value indicating whether <c>SAVE</c> may write this command into the startup file.
        /// </summary>
        /// <remarks>
        /// Off unless the program says so. A command about the RUNNING system - LOCK-TRUNKS is the
        /// example CHATSV.PLNC gives - must never come back by itself after a restart, so a program has
        /// to choose to make a command saveable; forgetting to costs a refusal, never a wrong restart.
        /// </remarks>
        public bool Saveable { get; init; }

        /// <summary>
        /// The command as help shows it on one line: the name, then the parameters.
        /// </summary>
        /// <returns>
        /// For example <c>INITIALIZE &lt;room&gt; [n]</c>.
        /// </returns>
        public string Usage()
        {
            string usage = Name;
            for (int i = 0; i < _parameters.Length; i++)
            {
                usage += " " + _parameters[i].ToString();
            }

            return usage;
        }

        /// <summary>
        /// Tests a typed word against this command by SINTRAN's own abbreviation rule.
        /// </summary>
        /// <param name="word">
        /// The typed command word, without any prefix such as '/'.
        /// </param>
        /// <returns>
        /// True when the word names this command.
        /// </returns>
        /// <remarks>
        /// The rule as CHAT-MON states and implements it (CHATMON.PLNC, abbrevMatch): a command is a
        /// run of parts separated by '-'. What is typed must give EVERY part, and each typed part must be
        /// the start of the matching part. So LIST-TRUNKS answers to L-T, LI-TRU, LIST-T and LIST-TRUNKS;
        /// a bare L does not match it, and an empty part ("-T", "L--T") is not an abbreviation. Letter
        /// case does not matter.
        /// </remarks>
        public bool Matches(string word)
        {
            if (string.IsNullOrEmpty(word))
            {
                return false;
            }

            string[] typed = word.Split('-');
            if (typed.Length != _parts.Length)
            {
                return false;
            }

            for (int i = 0; i < typed.Length; i++)
            {
                if (typed[i].Length == 0 || typed[i].Length > _parts[i].Length)
                {
                    return false;
                }

                if (string.Compare(typed[i], 0, _parts[i], 0, typed[i].Length, StringComparison.OrdinalIgnoreCase) != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
