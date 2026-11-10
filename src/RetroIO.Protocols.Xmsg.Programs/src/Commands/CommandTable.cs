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
    /// A program's commands: register them once, then run typed lines against them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same table serves every place a command can be typed: a client's slash commands
    /// (<c>/join</c>, with <see cref="Prefix"/> "/"), a manager's commands (<c>LIST-TRUNKS</c>, no
    /// prefix), and the lines of a server's startup file.
    /// </para>
    /// <para><b>The rules, as Ronny set them on 2026-11-10</b></para>
    /// <list type="bullet">
    /// <item><description>A command is found by SINTRAN's abbreviation rule (see
    /// <see cref="CommandDefinition.Matches"/>), in any letter case. An abbreviation that fits more
    /// than one command is refused, and the commands it could mean are listed.</description></item>
    /// <item><description>Parameters are separated by spaces or commas. An empty place between two
    /// commas means "not given": <c>CMD a,,c</c> gives the first and the third.</description></item>
    /// <item><description>Nothing is asked for. A missing required parameter fails the command, and
    /// the message names every one that is missing and what it is. (SINTRAN would ask for it; Ronny
    /// chose a clear failure instead.)</description></item>
    /// <item><description>A command gives back zero or more output lines (<see cref="CommandResult"/>),
    /// so every front end shows the same thing.</description></item>
    /// <item><description><c>HELP</c> and <c>?</c> are built in and generated from the
    /// registrations; <c>HELP &lt;command&gt;</c> describes one command and its parameters.</description></item>
    /// </list>
    /// </remarks>
    public sealed class CommandTable
    {
        private readonly List<CommandDefinition> _commands = new List<CommandDefinition>();

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandTable"/> class.
        /// </summary>
        /// <param name="title">
        /// The heading of the help list, for example <c>CHAT-MON commands:</c>.
        /// </param>
        /// <param name="prefix">
        /// What a command line starts with: "/" for a client's slash commands, empty for a manager.
        /// </param>
        public CommandTable(string title, string prefix = "")
        {
            Title = title ?? string.Empty;
            Prefix = prefix ?? string.Empty;
            Register(new CommandDefinition(
                "HELP",
                "this list, or one command in full",
                RunHelp,
                new CommandParameter("command", "the command to describe", false)));
        }

        /// <summary>
        /// Gets the heading of the help list.
        /// </summary>
        public string Title { get; }

        /// <summary>
        /// Gets what a command line starts with ("/" for slash commands, or empty).
        /// </summary>
        public string Prefix { get; }

        /// <summary>
        /// Gets the registered commands, in the order they were registered (HELP first).
        /// </summary>
        public IReadOnlyList<CommandDefinition> Commands => _commands;

        /// <summary>
        /// Adds a command.
        /// </summary>
        /// <param name="command">
        /// The command.
        /// </param>
        /// <returns>
        /// This table, so registrations can be chained.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// A command of that name is already registered.
        /// </exception>
        public CommandTable Register(CommandDefinition command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            for (int i = 0; i < _commands.Count; i++)
            {
                if (string.Equals(_commands[i].Name, command.Name, StringComparison.Ordinal))
                {
                    throw new ArgumentException("The command " + command.Name + " is already registered.", nameof(command));
                }
            }

            _commands.Add(command);
            return this;
        }

        /// <summary>
        /// Adds a command made from its parts.
        /// </summary>
        /// <param name="name">
        /// The full name, for example <c>LIST-TRUNKS</c>.
        /// </param>
        /// <param name="description">
        /// What it does.
        /// </param>
        /// <param name="run">
        /// The function that runs it.
        /// </param>
        /// <param name="parameters">
        /// Its parameters, in typed order.
        /// </param>
        /// <returns>
        /// This table.
        /// </returns>
        public CommandTable Register(string name, string description, Action<CommandContext> run, params CommandParameter[] parameters)
        {
            return Register(new CommandDefinition(name, description, run, parameters));
        }

        /// <summary>
        /// Tells whether a typed line is a command at all, rather than, say, chat text.
        /// </summary>
        /// <param name="line">
        /// The typed line.
        /// </param>
        /// <returns>
        /// True when the line starts with <see cref="Prefix"/> (always, when there is no prefix) and
        /// is not blank.
        /// </returns>
        public bool IsCommand(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            return Prefix.Length == 0 || line.TrimStart().StartsWith(Prefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Finds the commands a typed word could mean.
        /// </summary>
        /// <param name="word">
        /// The command word, without the prefix.
        /// </param>
        /// <returns>
        /// None (unknown), one (found) or several (ambiguous).
        /// </returns>
        public IReadOnlyList<CommandDefinition> Find(string word)
        {
            List<CommandDefinition> found = new List<CommandDefinition>();
            if (word == "?")
            {
                // ? IS NOT AN ABBREVIATION OF ANYTHING (CHATMON.PLNC says the same); it is HELP.
                found.Add(_commands[0]);
                return found;
            }

            for (int i = 0; i < _commands.Count; i++)
            {
                if (_commands[i].Matches(word))
                {
                    found.Add(_commands[i]);
                }
            }

            return found;
        }

        /// <summary>
        /// Runs one typed line.
        /// </summary>
        /// <param name="line">
        /// The line as typed, with the prefix when there is one.
        /// </param>
        /// <returns>
        /// The command found, whether it worked, and its output lines. A blank line gives a success
        /// with no lines and no command.
        /// </returns>
        /// <remarks>
        /// An exception thrown by a command's function is NOT caught here: a defect in a command must
        /// show, not become an error line that looks like an ordinary refusal.
        /// </remarks>
        public CommandResult Execute(string line)
        {
            List<CommandOutputLine> output = new List<CommandOutputLine>();
            string text = (line ?? string.Empty).Trim();
            if (Prefix.Length > 0 && text.StartsWith(Prefix, StringComparison.Ordinal))
            {
                text = text.Substring(Prefix.Length).TrimStart();
            }

            if (text.Length == 0)
            {
                return new CommandResult(null, true, output);
            }

            // The command word ends at the first space or comma.
            int end = 0;
            while (end < text.Length && text[end] != ' ' && text[end] != ',')
            {
                end++;
            }

            string word = text.Substring(0, end);
            IReadOnlyList<CommandDefinition> found = Find(word);
            if (found.Count == 0)
            {
                output.Add(new CommandOutputLine(
                    CommandOutputKind.Error,
                    "No such command: " + word + ". Type " + Prefix + "HELP or " + Prefix + "? for the commands."));
                return new CommandResult(null, false, output);
            }

            if (found.Count > 1)
            {
                output.Add(new CommandOutputLine(CommandOutputKind.Error, word + " is ambiguous - it could be:"));
                for (int i = 0; i < found.Count; i++)
                {
                    output.Add(new CommandOutputLine(CommandOutputKind.Error, "  " + Prefix + found[i].Name));
                }

                return new CommandResult(null, false, output);
            }

            CommandDefinition command = found[0];
            string?[] values;
            string? problem = SplitParameters(command, text.Substring(end), out values);
            if (problem != null)
            {
                output.Add(new CommandOutputLine(CommandOutputKind.Error, command.Name + ": " + problem));
                return new CommandResult(command, false, output);
            }

            string? missing = Missing(command, values);
            if (missing != null)
            {
                output.Add(new CommandOutputLine(CommandOutputKind.Error, command.Name + ": " + missing));
                output.Add(new CommandOutputLine(CommandOutputKind.Error, "Usage: " + Prefix + command.Usage()));
                return new CommandResult(command, false, output);
            }

            CommandContext context = new CommandContext(command, values, output);
            command.Run(context);
            return new CommandResult(command, !context.Failed, output);
        }

        /// <summary>
        /// The help list: the heading, then one line per command with its usage and what it does.
        /// </summary>
        /// <returns>
        /// The lines.
        /// </returns>
        public IReadOnlyList<string> HelpLines()
        {
            int width = 0;
            for (int i = 0; i < _commands.Count; i++)
            {
                width = Math.Max(width, Prefix.Length + _commands[i].Usage().Length);
            }

            List<string> lines = new List<string>();
            if (Title.Length > 0)
            {
                lines.Add(Title);
            }

            for (int i = 0; i < _commands.Count; i++)
            {
                lines.Add("  " + (Prefix + _commands[i].Usage()).PadRight(width) + "  " + _commands[i].Description);
            }

            lines.Add("Shorten each part: " + Example() + ". Upper or lower case, either is fine.");
            return lines;
        }

        /// <summary>
        /// One command in full: its usage, what it does, and each parameter.
        /// </summary>
        /// <param name="command">
        /// The command.
        /// </param>
        /// <returns>
        /// The lines.
        /// </returns>
        public IReadOnlyList<string> HelpLines(CommandDefinition command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            List<string> lines = new List<string>();
            lines.Add(Prefix + command.Usage());
            lines.Add("  " + command.Description);
            for (int i = 0; i < command.Parameters.Count; i++)
            {
                CommandParameter parameter = command.Parameters[i];
                lines.Add("  " + parameter.Name + (parameter.Required ? " (required)" : " (optional)") + ": " + parameter.Description);
            }

            return lines;
        }

        /// <summary>
        /// Splits what follows the command word into one value per parameter.
        /// </summary>
        /// <param name="command">
        /// The command.
        /// </param>
        /// <param name="rest">
        /// Everything after the command word.
        /// </param>
        /// <param name="values">
        /// One value per parameter; null where none was given.
        /// </param>
        /// <returns>
        /// Null when it fits, or what is wrong.
        /// </returns>
        private static string? SplitParameters(CommandDefinition command, string rest, out string?[] values)
        {
            IReadOnlyList<CommandParameter> parameters = command.Parameters;
            values = new string?[parameters.Count];
            List<string?> places = new List<string?>();
            int restIndex = parameters.Count > 0 && parameters[parameters.Count - 1].TakesRestOfLine ? parameters.Count - 1 : -1;

            // lastWasComma starts TRUE: a comma straight after the command word ("CMD ,b") leaves the
            // FIRST place empty, exactly as one between two values does.
            bool lastWasComma = true;
            int start = -1;
            int i = 0;
            while (i <= rest.Length)
            {
                char c = i < rest.Length ? rest[i] : '\0';
                bool atEnd = i == rest.Length;
                if (start < 0)
                {
                    if (atEnd)
                    {
                        break;
                    }

                    if (c == ' ')
                    {
                        i++;
                        continue;
                    }

                    if (c == ',')
                    {
                        if (lastWasComma)
                        {
                            places.Add(null);
                        }

                        lastWasComma = true;
                        i++;
                        continue;
                    }

                    if (places.Count == restIndex)
                    {
                        // The last parameter takes the rest of the line as typed, commas and all.
                        places.Add(rest.Substring(i).TrimEnd());
                        break;
                    }

                    start = i;
                    i++;
                    continue;
                }

                if (atEnd || c == ' ' || c == ',')
                {
                    places.Add(rest.Substring(start, i - start));
                    start = -1;
                    lastWasComma = c == ',';
                    i++;
                    continue;
                }

                i++;
            }

            if (places.Count > parameters.Count)
            {
                return parameters.Count == 0
                    ? "takes no parameters."
                    : "too many parameters - it takes at most " + parameters.Count + ".";
            }

            for (int p = 0; p < places.Count; p++)
            {
                values[p] = places[p];
            }

            return null;
        }

        /// <summary>
        /// Names every required parameter that was not given.
        /// </summary>
        /// <param name="command">
        /// The command.
        /// </param>
        /// <param name="values">
        /// The values given.
        /// </param>
        /// <returns>
        /// Null when nothing is missing, or the message.
        /// </returns>
        private static string? Missing(CommandDefinition command, string?[] values)
        {
            string? message = null;
            int count = 0;
            for (int i = 0; i < values.Length; i++)
            {
                CommandParameter parameter = command.Parameters[i];
                if (parameter.Required && string.IsNullOrEmpty(values[i]))
                {
                    message = (message == null ? string.Empty : message + ", ") + parameter.Name + " (" + parameter.Description + ")";
                    count++;
                }
            }

            if (message == null)
            {
                return null;
            }

            return (count == 1 ? "missing parameter " : "missing parameters ") + message + ".";
        }

        private void RunHelp(CommandContext context)
        {
            string? asked = context.Get("command");
            if (string.IsNullOrEmpty(asked))
            {
                IReadOnlyList<string> all = HelpLines();
                for (int i = 0; i < all.Count; i++)
                {
                    context.Write(all[i]);
                }

                return;
            }

            string word = Prefix.Length > 0 && asked.StartsWith(Prefix, StringComparison.Ordinal) ? asked.Substring(Prefix.Length) : asked;
            IReadOnlyList<CommandDefinition> found = Find(word);
            if (found.Count != 1)
            {
                context.Fail(found.Count == 0 ? "No such command: " + word + "." : word + " is ambiguous.");
                return;
            }

            IReadOnlyList<string> lines = HelpLines(found[0]);
            for (int i = 0; i < lines.Count; i++)
            {
                context.Write(lines[i]);
            }
        }

        /// <summary>
        /// An abbreviation example taken from this table's own commands, so help never names a command
        /// the program does not have.
        /// </summary>
        /// <returns>
        /// For example <c>LI-T is LIST-TRUNKS</c>.
        /// </returns>
        private string Example()
        {
            for (int i = 0; i < _commands.Count; i++)
            {
                string[] parts = _commands[i].Name.Split('-');
                if (parts.Length > 1)
                {
                    string shortened = string.Empty;
                    for (int p = 0; p < parts.Length; p++)
                    {
                        shortened += (p == 0 ? string.Empty : "-") + parts[p].Substring(0, Math.Min(p == 0 ? 2 : 1, parts[p].Length));
                    }

                    return Prefix + shortened + " is " + Prefix + _commands[i].Name;
                }
            }

            return Prefix + "HE is " + Prefix + "HELP";
        }
    }
}
