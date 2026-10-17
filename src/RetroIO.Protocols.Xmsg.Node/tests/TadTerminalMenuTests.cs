//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Node;
using RetroIO.Protocols.Xmsg.SubProtocol;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Verifies the pure terminal menu logic (message-of-the-day, prompt, and the
    /// 1 Time / 2 Date / 3 Echo / 4 Disconnect commands) against a fixed clock, plus the
    /// TAD chain serializer used to carry the terminal text on the wire.
    /// </summary>
    public sealed class TadTerminalMenuTests
    {
        // A fixed clock so date/time output is deterministic under test.
        private static readonly DateTime Clock = new DateTime(2026, 7, 2, 14, 33, 7);

        [Fact]
        public void Motd_ContainsDateAndTime()
        {
            TadTerminalMenu menu = new TadTerminalMenu();
            string motd = menu.BuildMotd(Clock);

            Assert.Contains("02 JULY 2026", motd);
            Assert.Contains("14:33:07", motd);
            Assert.Contains("Node 103", motd);
        }

        [Fact]
        public void Greeting_HasMotdMenuAndPrompt()
        {
            TadTerminalMenu menu = new TadTerminalMenu();
            string greeting = menu.BuildGreeting(Clock);

            Assert.Contains("1. Time", greeting);
            Assert.Contains("4. Disconnect", greeting);
            Assert.EndsWith("# ", greeting);
        }

        [Fact]
        public void Command1_ReturnsTime_NoDisconnect()
        {
            TadTerminalMenu menu = new TadTerminalMenu();
            TadMenuResult result = menu.Handle("1", Clock);

            Assert.Contains("14:33:07", result.Output);
            Assert.False(result.Disconnect);
            Assert.EndsWith("# ", result.Output);
        }

        [Fact]
        public void Command2_ReturnsDate_NoDisconnect()
        {
            TadTerminalMenu menu = new TadTerminalMenu();
            TadMenuResult result = menu.Handle("2", Clock);

            Assert.Contains("02 JULY 2026", result.Output);
            Assert.False(result.Disconnect);
        }

        [Fact]
        public void Command3_EchoesIpsumLorum()
        {
            TadTerminalMenu menu = new TadTerminalMenu();
            TadMenuResult result = menu.Handle("3", Clock);

            Assert.Contains("IPSUM LORUM", result.Output);
            Assert.False(result.Disconnect);
        }

        [Fact]
        public void Command4_Disconnects()
        {
            TadTerminalMenu menu = new TadTerminalMenu();
            TadMenuResult result = menu.Handle("4", Clock);

            Assert.True(result.Disconnect);
        }

        [Fact]
        public void Help_ShowsMenu()
        {
            TadTerminalMenu menu = new TadTerminalMenu();
            TadMenuResult result = menu.Handle("help", Clock);

            Assert.Contains("1. Time", result.Output);
            Assert.Contains("4. Disconnect", result.Output);
            Assert.False(result.Disconnect);
        }

        [Fact]
        public void UnknownCommand_EchoedVerbatim()
        {
            TadTerminalMenu menu = new TadTerminalMenu();
            TadMenuResult result = menu.Handle("frobnicate", Clock);

            Assert.Contains("unknown command: frobnicate", result.Output);
            Assert.False(result.Disconnect);
        }

        [Fact]
        public void TadChain_SerializesBdat_RoundTrips()
        {
            // Build a BDAT (0x01) message carrying "OK" and confirm the on-wire bytes and the
            // parse round-trip: opcode 0x01, count 0x02, data 'O' 'K'.
            byte[] text = new byte[] { (byte)'O', (byte)'K' };
            TadChain outbound = new TadChain();
            outbound.Add(0x01, text);
            byte[] wire = outbound.ToBytes();

            Assert.Equal(new byte[] { 0x01, 0x02, (byte)'O', (byte)'K' }, wire);

            TadChain parsed = TadChain.Parse(wire);
            Assert.Single(parsed.Messages);
            Assert.Equal(0x01, parsed.Messages[0].Opcode);
            Assert.Equal("BDAT", parsed.Messages[0].OpcodeName);
            Assert.Equal(text, parsed.Messages[0].Data);
        }
    }
}
