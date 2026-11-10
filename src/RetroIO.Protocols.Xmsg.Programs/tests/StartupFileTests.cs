//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;

using RetroIO.Protocols.Xmsg.Programs.Commands;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Programs.Tests
{
    /// <summary>
    /// The startup command file and SAVE.
    /// </summary>
    public sealed class StartupFileTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "startup-test-" + Guid.NewGuid().ToString("N") + ".cmd");
        private readonly List<string> _ran = new List<string>();

        /// <inheritdoc/>
        public void Dispose()
        {
            File.Delete(_path);
        }

        /// <summary>
        /// Every command line runs in order; blank lines and % comments are skipped; a failing line
        /// does not stop the rest.
        /// </summary>
        [Fact]
        public void RunAll_RunsEveryLine_SkipsComments_AndGoesOnPastAFailure()
        {
            File.WriteAllLines(_path, new[]
            {
                "% trunks to start at boot",
                "START-TRUNK 102",
                string.Empty,
                "START-TRUNK 0",
                "SET-NAME RETRO",
            });
            StartupFile file = new StartupFile(_path);

            IReadOnlyList<KeyValuePair<string, CommandResult>> results = file.RunAll(Table(file));

            Assert.Equal(3, results.Count);
            Assert.True(results[0].Value.Succeeded);
            Assert.False(results[1].Value.Succeeded);
            Assert.True(results[2].Value.Succeeded);
            Assert.Equal(new[] { "START-TRUNK 102", "SET-NAME RETRO" }, _ran);
        }

        /// <summary>
        /// A missing file is an empty one.
        /// </summary>
        [Fact]
        public void MissingFile_RunsNothing()
        {
            StartupFile file = new StartupFile(_path);

            Assert.Empty(file.RunAll(Table(file)));
        }

        /// <summary>
        /// SAVE runs the command and, when it worked, keeps the line; the next start runs it again.
        /// </summary>
        [Fact]
        public void Save_RunsAndKeepsTheLine()
        {
            StartupFile file = new StartupFile(_path);
            CommandTable table = Table(file);

            CommandResult saved = table.Execute("save st-t 102");

            Assert.True(saved.Succeeded);
            Assert.Equal(new[] { "START-TRUNK 102" }, _ran);
            Assert.Equal(new[] { "st-t 102" }, file.ReadCommands());

            _ran.Clear();
            file.RunAll(Table(file));
            Assert.Equal(new[] { "START-TRUNK 102" }, _ran);
        }

        /// <summary>
        /// A command that fails is not kept.
        /// </summary>
        [Fact]
        public void Save_OfAFailingCommand_KeepsNothing()
        {
            StartupFile file = new StartupFile(_path);

            CommandResult saved = Table(file).Execute("SAVE START-TRUNK 0");

            Assert.False(saved.Succeeded);
            Assert.Contains(saved.Lines, l => l.Text == "Not saved, because the command did not work.");
            Assert.Empty(file.ReadCommands());
        }

        /// <summary>
        /// A command not marked saveable is refused BEFORE it runs: LOCK-TRUNKS must never come back
        /// by itself after a restart (CHATSV.PLNC).
        /// </summary>
        [Fact]
        public void Save_OfACommandNotMarkedSaveable_IsRefusedAndDoesNotRun()
        {
            StartupFile file = new StartupFile(_path);

            CommandResult saved = Table(file).Execute("SAVE LO-T");

            Assert.False(saved.Succeeded);
            Assert.Empty(_ran);
            Assert.Empty(file.ReadCommands());
        }

        private CommandTable Table(StartupFile file)
        {
            CommandTable table = new CommandTable("CHAT-MON commands:");
            table.Register(new CommandDefinition(
                "START-TRUNK",
                "trunk to a peer",
                c =>
                {
                    if (c.Get("sys") == "0")
                    {
                        c.Fail("0 is not a system number.");
                        return;
                    }

                    _ran.Add("START-TRUNK " + c.Get("sys"));
                },
                new CommandParameter("sys", "the peer's system number", true))
            {
                Saveable = true,
            });
            table.Register(new CommandDefinition(
                "SET-NAME",
                "what this machine is called",
                c => _ran.Add("SET-NAME " + c.Get("name")),
                new CommandParameter("name", "the name", true))
            {
                Saveable = true,
            });
            table.Register("LOCK-TRUNKS", "learn no NEW machines", c => _ran.Add("LOCK-TRUNKS"));
            file.AddSaveCommand(table);
            return table;
        }
    }
}
