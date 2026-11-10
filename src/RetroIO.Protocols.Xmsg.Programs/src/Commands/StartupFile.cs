//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;

namespace RetroIO.Protocols.Xmsg.Programs.Commands
{
    /// <summary>
    /// A server's startup command file: the same commands an operator types in the manager, one per
    /// line, run by the server itself at every start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SINTRAN does it this way: CHATSV saves nothing, and the machine's boot file runs CHAT-MON
    /// commands at start (CHATSV.PLNC, the note on sysName). Here the server runs its own file, so the
    /// manager is not needed at boot (Ronny, 2026-11-10).
    /// </para>
    /// <para>
    /// A blank line, and a line starting with '%' (PLANC's comment sign), is skipped. <c>SAVE</c>
    /// (<see cref="AddSaveCommand"/>) appends a command that worked; only commands marked
    /// <see cref="CommandDefinition.Saveable"/> may be saved.
    /// </para>
    /// </remarks>
    public sealed class StartupFile
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="StartupFile"/> class.
        /// </summary>
        /// <param name="path">
        /// Where the file is. It does not have to exist: a missing file is an empty one.
        /// </param>
        public StartupFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A startup file needs a path.", nameof(path));
            }

            Path = path;
        }

        /// <summary>
        /// Gets where the file is.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Reads the command lines, without blank lines and comments.
        /// </summary>
        /// <returns>
        /// The lines in file order; none when the file does not exist.
        /// </returns>
        public IReadOnlyList<string> ReadCommands()
        {
            List<string> commands = new List<string>();
            if (!File.Exists(Path))
            {
                return commands;
            }

            string[] lines = File.ReadAllLines(Path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '%')
                {
                    continue;
                }

                commands.Add(line);
            }

            return commands;
        }

        /// <summary>
        /// Runs every command in the file, in order. A command that fails does not stop the rest, as a
        /// boot file on SINTRAN goes on past a failing line; every result is given back to be shown or
        /// logged.
        /// </summary>
        /// <param name="table">
        /// The server's commands.
        /// </param>
        /// <returns>
        /// One entry per command line: the line and what running it gave.
        /// </returns>
        public IReadOnlyList<KeyValuePair<string, CommandResult>> RunAll(CommandTable table)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            IReadOnlyList<string> commands = ReadCommands();
            List<KeyValuePair<string, CommandResult>> results = new List<KeyValuePair<string, CommandResult>>(commands.Count);
            for (int i = 0; i < commands.Count; i++)
            {
                results.Add(new KeyValuePair<string, CommandResult>(commands[i], table.Execute(commands[i])));
            }

            return results;
        }

        /// <summary>
        /// Appends one command line to the end of the file, making the file when it is not there.
        /// </summary>
        /// <param name="line">
        /// The command line, as typed.
        /// </param>
        public void Append(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new ArgumentException("Nothing to save.", nameof(line));
            }

            File.AppendAllText(Path, line.Trim() + Environment.NewLine);
        }

        /// <summary>
        /// Adds <c>SAVE &lt;command&gt;</c> to a table: it runs the command and, only when it worked and
        /// the command may be saved, appends the line to this file.
        /// </summary>
        /// <param name="table">
        /// The server's commands.
        /// </param>
        public void AddSaveCommand(CommandTable table)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            table.Register(
                "SAVE",
                "run a command and keep it in the startup file",
                c => Save(table, c),
                new CommandParameter("command", "the command line to run and keep", true, takesRestOfLine: true));
        }

        private void Save(CommandTable table, CommandContext context)
        {
            string line = context.Get("command") ?? string.Empty;

            // Look the command up FIRST, so one that may not be saved is refused before it runs.
            int end = 0;
            string word = line.StartsWith(table.Prefix, StringComparison.Ordinal) && table.Prefix.Length > 0 ? line.Substring(table.Prefix.Length) : line;
            while (end < word.Length && word[end] != ' ' && word[end] != ',')
            {
                end++;
            }

            IReadOnlyList<CommandDefinition> found = table.Find(word.Substring(0, end));
            if (found.Count == 1 && !found[0].Saveable)
            {
                context.Fail(found[0].Name + " is not kept in the startup file; it is about the running system only.");
                return;
            }

            CommandResult result = table.Execute(line);
            for (int i = 0; i < result.Lines.Count; i++)
            {
                CommandOutputLine output = result.Lines[i];
                if (output.Kind == CommandOutputKind.Error)
                {
                    context.Fail(output.Text);
                }
                else if (output.Kind == CommandOutputKind.Warning)
                {
                    context.Warn(output.Text);
                }
                else
                {
                    context.Write(output.Text);
                }
            }

            if (!result.Succeeded)
            {
                context.Fail("Not saved, because the command did not work.");
                return;
            }

            Append(line);
            context.Write("Saved in " + System.IO.Path.GetFileName(Path) + ".");
        }
    }
}
