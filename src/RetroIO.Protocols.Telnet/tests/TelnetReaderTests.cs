//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using Xunit;

namespace RetroIO.Protocols.Telnet.Tests
{
    public class TelnetReaderTests
    {
        // The host side of the real TK5 negotiation, captured byte for byte on 22 November 2026
        // (RetroTerm docs/spec/IBM3270/TN3270-NEGOTIATION.md section 3), followed by a short
        // record ending in IAC EOR. The record holds a 255 data byte to prove the undoubling.
        private static readonly byte[] Tk5Stream =
        {
            0xFF, 0xFD, 0x18,                   // IAC DO TERMINAL-TYPE
            0xFF, 0xFA, 0x18, 0x01, 0xFF, 0xF0, // IAC SB TERMINAL-TYPE SEND IAC SE
            0xFF, 0xFB, 0x00,                   // IAC WILL BINARY
            0xFF, 0xFD, 0x00,                   // IAC DO BINARY
            0xFF, 0xFB, 0x19, 0xFF, 0xFD, 0x19, // IAC WILL EOR, IAC DO EOR
            0xF5, 0x42, 0x11, 0x40, 0x40, 0xFF, 0xFF, 0xC1, // record with IAC IAC inside
            0xFF, 0xEF,                         // IAC EOR
        };

        private static readonly string[] Tk5Events =
        {
            "NEG 253 24",
            "SB 24 01",
            "NEG 251 0",
            "NEG 253 0",
            "NEG 251 25",
            "NEG 253 25",
            "DATA F5 42 11 40 40 FF C1",
            "EOR",
        };

        [Fact]
        public void TheTk5NegotiationAndARecordAreDecodedInOrder()
        {
            var handler = new RecordingHandler();
            var reader = new TelnetReader(handler);

            reader.Feed(Tk5Stream);

            Assert.Equal(Tk5Events, handler.Events);
        }

        [Fact]
        public void EveryPossibleSplitIntoTwoReadsGivesTheSameEvents()
        {
            // The TK5 capture showed the host's commands split differently on every run.
            for (int cut = 1; cut < Tk5Stream.Length; cut++)
            {
                var handler = new RecordingHandler();
                var reader = new TelnetReader(handler);

                reader.Feed(Tk5Stream.AsSpan(0, cut));
                reader.Feed(Tk5Stream.AsSpan(cut));

                Assert.Equal(Tk5Events, handler.Events);
            }
        }

        [Fact]
        public void OneByteAtATimeGivesTheSameEvents()
        {
            var handler = new RecordingHandler();
            var reader = new TelnetReader(handler);

            for (int i = 0; i < Tk5Stream.Length; i++)
            {
                reader.Feed(Tk5Stream.AsSpan(i, 1));
            }

            Assert.Equal(Tk5Events, handler.Events);
        }

        [Fact]
        public void AnIacIacInsideASubnegotiationIsOneParameterByte()
        {
            var handler = new RecordingHandler();
            var reader = new TelnetReader(handler);

            reader.Feed(new byte[] { 0xFF, 0xFA, 0x18, 0x00, 0xFF, 0xFF, 0x41, 0xFF, 0xF0 });

            Assert.Equal(new[] { "SB 24 00 FF 41" }, handler.Events);
        }

        [Fact]
        public void OtherCommandsAreReportedAndSplitTheData()
        {
            var handler = new RecordingHandler();
            var reader = new TelnetReader(handler);

            // data, IAC BRK, data, IAC NOP
            reader.Feed(new byte[] { 0x41, 0xFF, 0xF3, 0x42, 0xFF, 0xF1 });

            Assert.Equal(new[] { "DATA 41", "CMD 243", "DATA 42", "CMD 241" }, handler.Events);
        }

        [Fact]
        public void IacFollowedByAStrayByteInsideASubnegotiationEndsItAndStartsACommand()
        {
            // RFC 854/855 as read do not settle this; the choice is stated in TelnetReader.
            var handler = new RecordingHandler();
            var reader = new TelnetReader(handler);

            reader.Feed(new byte[] { 0xFF, 0xFA, 0x18, 0x01, 0xFF, 0xFB, 0x00 });

            Assert.Equal(new[] { "SB 24 01", "NEG 251 0" }, handler.Events);
        }

        [Fact]
        public void ALongRecordLargerThanTheStartBufferArrivesWhole()
        {
            var handler = new RecordingHandler();
            var reader = new TelnetReader(handler);
            var stream = new List<byte>();
            for (int i = 0; i < 3000; i++)
            {
                stream.Add((byte)(i % 200));
            }

            stream.Add(0xFF);
            stream.Add(0xEF);

            reader.Feed(stream.ToArray());

            IReadOnlyList<string> events = handler.Events;
            Assert.Equal(2, events.Count);
            Assert.Equal("EOR", events[1]);
            Assert.Equal((3000 * 3) - 1 + 5, events[0].Length); // "DATA " + 3000 two-digit bytes with spaces
        }
    }
}
