//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Programs.Commands;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Programs.Tests
{
    /// <summary>
    /// The command framework against the rules Ronny set on 2026-11-10, with CHAT-MON's own commands
    /// as the examples.
    /// </summary>
    public sealed class CommandTableTests
    {
        /// <summary>
        /// SINTRAN's abbreviation rule, as CHATMON.PLNC implements it: every part given, each a prefix.
        /// </summary>
        /// <param name="typed">
        /// What is typed.
        /// </param>
        /// <param name="matches">
        /// Whether it names LIST-TRUNKS.
        /// </param>
        [Theory]
        [InlineData("LIST-TRUNKS", true)]
        [InlineData("li-t", true)]
        [InlineData("L-T", true)]
        [InlineData("LI-TRU", true)]
        [InlineData("LIST-T", true)]
        [InlineData("L", false)]
        [InlineData("LIST", false)]
        [InlineData("-T", false)]
        [InlineData("L--T", false)]
        [InlineData("L-T-X", false)]
        [InlineData("LISTX-T", false)]
        public void Abbreviation_FollowsSintransRule(string typed, bool matches)
        {
            CommandDefinition command = new CommandDefinition("LIST-TRUNKS", "peers and states", _ => { });
            Assert.Equal(matches, command.Matches(typed));
        }

        /// <summary>
        /// An abbreviation that fits two commands is refused, and both are named.
        /// </summary>
        [Fact]
        public void Ambiguous_IsRefusedAndListsWhatItCouldMean()
        {
            CommandTable table = MonTable(new List<string>());

            CommandResult result = table.Execute("L-T");

            Assert.False(result.Succeeded);
            Assert.Null(result.Command);
            Assert.Contains(result.Lines, l => l.Text.Contains("LIST-TRUNKS", StringComparison.Ordinal));
            Assert.Contains(result.Lines, l => l.Text.Contains("LOCK-TRUNKS", StringComparison.Ordinal));
        }

        /// <summary>
        /// A command is found, its parameters are given to it, and its output lines come back.
        /// </summary>
        [Fact]
        public void Command_RunsWithItsParameters_AndGivesBackItsLines()
        {
            List<string> ran = new List<string>();
            CommandTable table = MonTable(ran);

            CommandResult result = table.Execute("ini lobby 50");

            Assert.True(result.Succeeded);
            Assert.Equal("INITIALIZE", result.Command!.Name);
            Assert.Equal("INITIALIZE lobby 50", ran[0]);
            Assert.Equal(new CommandOutputLine(CommandOutputKind.Text, "lobby keeps 50"), result.Lines[0]);
        }

        /// <summary>
        /// A missing required parameter fails the command, naming it and what it is. Nothing is asked.
        /// </summary>
        [Fact]
        public void MissingRequired_FailsAndNamesEveryMissingParameter()
        {
            List<string> ran = new List<string>();
            CommandTable table = MonTable(ran);

            CommandResult result = table.Execute("INITIALIZE");

            Assert.False(result.Succeeded);
            Assert.Empty(ran);
            Assert.Equal(CommandOutputKind.Error, result.Lines[0].Kind);
            Assert.Equal(
                "INITIALIZE: missing parameters room (the room), n (how many lines it keeps).",
                result.Lines[0].Text);
            Assert.Equal("Usage: INITIALIZE <room> <n>", result.Lines[1].Text);
        }

        /// <summary>
        /// An empty place between commas leaves an optional parameter out; the later ones still land.
        /// </summary>
        [Fact]
        public void EmptyPlaceBetweenCommas_LeavesThatParameterOut()
        {
            string?[] seen = new string?[3];
            CommandTable table = new CommandTable("T:");
            table.Register(
                "CMD",
                "three places",
                c => { seen[0] = c.Get("a"); seen[1] = c.Get("b"); seen[2] = c.Get("c"); },
                new CommandParameter("a", "first", true),
                new CommandParameter("b", "second", false),
                new CommandParameter("c", "third", false));

            Assert.True(table.Execute("CMD x,,z").Succeeded);
            Assert.Equal(new string?[] { "x", null, "z" }, seen);

            Assert.True(table.Execute("CMD x , y").Succeeded);
            Assert.Equal(new string?[] { "x", "y", null }, seen);

            Assert.True(table.Execute("CMD x,").Succeeded);
            Assert.Equal(new string?[] { "x", null, null }, seen);
        }

        /// <summary>
        /// A required place left empty with commas still counts as missing.
        /// </summary>
        [Fact]
        public void RequiredPlaceLeftEmpty_IsMissing()
        {
            CommandTable table = MonTable(new List<string>());

            CommandResult result = table.Execute("INITIALIZE ,50");

            Assert.False(result.Succeeded);
            Assert.Equal("INITIALIZE: missing parameter room (the room).", result.Lines[0].Text);
        }

        /// <summary>
        /// Too many parameters is refused, not silently dropped.
        /// </summary>
        [Fact]
        public void TooManyParameters_IsRefused()
        {
            CommandTable table = MonTable(new List<string>());

            Assert.Equal("STATUS: takes no parameters.", table.Execute("STATUS now").Lines[0].Text);
            Assert.Equal(
                "INITIALIZE: too many parameters - it takes at most 2.",
                table.Execute("INITIALIZE a 1 2").Lines[0].Text);
        }

        /// <summary>
        /// A rest-of-line parameter takes the words as typed, spaces and commas included.
        /// </summary>
        [Fact]
        public void RestOfLine_TakesEverythingLeft()
        {
            string? to = null;
            string? text = null;
            CommandTable table = new CommandTable("Chat commands:", "/");
            table.Register(
                "TELL",
                "a message to one person",
                c => { to = c.Get("name"); text = c.Get("text"); },
                new CommandParameter("name", "who", true),
                new CommandParameter("text", "what to say", true, takesRestOfLine: true));

            Assert.True(table.Execute("/tell D102!KARI hello, are you  there?").Succeeded);
            Assert.Equal("D102!KARI", to);
            Assert.Equal("hello, are you  there?", text);
        }

        /// <summary>
        /// A slash table tells commands from chat text, and runs a slash command.
        /// </summary>
        [Fact]
        public void SlashTable_TellsCommandsFromText()
        {
            CommandTable table = new CommandTable("Chat commands:", "/");
            table.Register("WHO", "who is in this room", c => c.Write("RONNY KARI"));

            Assert.False(table.IsCommand("hello everyone"));
            Assert.True(table.IsCommand("/who"));
            Assert.Equal("RONNY KARI", table.Execute("/WHO").Lines[0].Text);
        }

        /// <summary>
        /// HELP and ? list every command with its parameters, from the registrations alone.
        /// </summary>
        [Fact]
        public void Help_IsGeneratedFromTheRegistrations()
        {
            CommandTable table = MonTable(new List<string>());

            CommandResult help = table.Execute("?");

            Assert.True(help.Succeeded);
            Assert.Equal("CHAT-MON commands:", help.Lines[0].Text);
            Assert.Contains(help.Lines, l => l.Text.Contains("INITIALIZE <room> <n>", StringComparison.Ordinal)
                && l.Text.EndsWith("how much that room keeps", StringComparison.Ordinal));
            Assert.Contains(help.Lines, l => l.Text.Contains("START-TRUNK <sys>", StringComparison.Ordinal));
            Assert.Equal(table.Execute("HELP").Lines.Count, help.Lines.Count);
        }

        /// <summary>
        /// HELP with a command describes that command and each of its parameters.
        /// </summary>
        [Fact]
        public void HelpForOneCommand_DescribesItsParameters()
        {
            CommandTable table = MonTable(new List<string>());

            CommandResult help = table.Execute("HELP ini");

            Assert.Equal("INITIALIZE <room> <n>", help.Lines[0].Text);
            Assert.Equal("  room (required): the room", help.Lines[2].Text);
            Assert.Equal("  n (required): how many lines it keeps", help.Lines[3].Text);
        }

        /// <summary>
        /// An unknown command fails and says how to get the list.
        /// </summary>
        [Fact]
        public void UnknownCommand_FailsAndPointsAtHelp()
        {
            CommandTable table = MonTable(new List<string>());

            CommandResult result = table.Execute("FROB");

            Assert.False(result.Succeeded);
            Assert.Equal("No such command: FROB. Type HELP or ? for the commands.", result.Lines[0].Text);
        }

        /// <summary>
        /// A command that writes an error line counts as failed.
        /// </summary>
        [Fact]
        public void CommandThatFails_IsNotASuccess()
        {
            CommandTable table = MonTable(new List<string>());

            CommandResult result = table.Execute("START-TRUNK 0");

            Assert.False(result.Succeeded);
            Assert.Equal(CommandOutputKind.Error, result.Lines[0].Kind);
        }

        /// <summary>
        /// Registering two commands of one name, or a bad layout of parameters, is a program mistake.
        /// </summary>
        [Fact]
        public void BadRegistrations_AreRefused()
        {
            CommandTable table = MonTable(new List<string>());

            Assert.Throws<ArgumentException>(() => table.Register("status", "again", _ => { }));
            Assert.Throws<ArgumentException>(() => new CommandDefinition("A--B", "empty part", _ => { }));
            Assert.Throws<ArgumentException>(() => new CommandDefinition(
                "X", "rest not last", _ => { },
                new CommandParameter("a", "a", false, takesRestOfLine: true),
                new CommandParameter("b", "b", false)));
        }

        /// <summary>
        /// A table with some of CHAT-MON's commands.
        /// </summary>
        /// <param name="ran">
        /// Where each run is recorded.
        /// </param>
        /// <returns>
        /// The table.
        /// </returns>
        private static CommandTable MonTable(List<string> ran)
        {
            CommandTable table = new CommandTable("CHAT-MON commands:");
            table.Register("STATUS", "how it stands", c => { ran.Add("STATUS"); c.Write("running"); });
            table.Register("LIST-TRUNKS", "peers and states", c => ran.Add("LIST-TRUNKS"));
            table.Register("LOCK-TRUNKS", "learn no NEW machines", c => ran.Add("LOCK-TRUNKS"));
            table.Register(
                "START-TRUNK",
                "trunk to a peer",
                c =>
                {
                    if (c.Get("sys") == "0")
                    {
                        c.Fail("0 is not a system number.");
                        return;
                    }

                    ran.Add("START-TRUNK " + c.Get("sys"));
                },
                new CommandParameter("sys", "the peer's system number", true));
            table.Register(
                "INITIALIZE",
                "how much that room keeps",
                c =>
                {
                    ran.Add("INITIALIZE " + c.Get("room") + " " + c.Get("n"));
                    c.Write(c.Get("room") + " keeps " + c.Get("n"));
                },
                new CommandParameter("room", "the room", true),
                new CommandParameter("n", "how many lines it keeps", true));
            return table;
        }
    }
}
