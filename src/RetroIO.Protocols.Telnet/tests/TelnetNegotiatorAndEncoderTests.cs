//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Buffers;
using System.Collections.Generic;

using Xunit;

namespace RetroIO.Protocols.Telnet.Tests
{
    public class TelnetNegotiatorAndEncoderTests
    {
        private sealed class AcceptSet : ITelnetOptionPolicy
        {
            private readonly HashSet<byte> _local;
            private readonly HashSet<byte> _remote;

            public AcceptSet(byte[] local, byte[] remote)
            {
                _local = new HashSet<byte>(local);
                _remote = new HashSet<byte>(remote);
            }

            public bool AcceptLocal(byte option) => _local.Contains(option);

            public bool AcceptRemote(byte option) => _remote.Contains(option);
        }

        private static (TelnetOptionNegotiator Negotiator, List<string> Sent) Create(byte[] local, byte[] remote)
        {
            var sent = new List<string>();
            var negotiator = new TelnetOptionNegotiator(
                new AcceptSet(local, remote),
                (verb, option) => sent.Add(verb + " " + option));
            return (negotiator, sent);
        }

        [Fact]
        public void TheTk5HandshakeGetsTheSameAnswersTheCaptureShows()
        {
            // TN3270-NEGOTIATION.md section 3: the client that worked answered
            // DO TT -> WILL TT, WILL BIN -> DO BIN, DO BIN -> WILL BIN, WILL EOR -> DO EOR, DO EOR -> WILL EOR.
            byte[] both = { TelnetOption.Binary, TelnetOption.EndOfRecord };
            byte[] local = { TelnetOption.Binary, TelnetOption.EndOfRecord, TelnetOption.TerminalType };
            var (n, sent) = Create(local, both);

            n.ReceiveDo(TelnetOption.TerminalType);
            n.ReceiveWill(TelnetOption.Binary);
            n.ReceiveDo(TelnetOption.Binary);
            n.ReceiveWill(TelnetOption.EndOfRecord);
            n.ReceiveDo(TelnetOption.EndOfRecord);

            Assert.Equal(new[] { "251 24", "253 0", "251 0", "253 25", "251 25" }, sent);
            Assert.True(n.IsLocalEnabled(TelnetOption.Binary));
            Assert.True(n.IsRemoteEnabled(TelnetOption.Binary));
            Assert.True(n.IsLocalEnabled(TelnetOption.EndOfRecord));
            Assert.True(n.IsRemoteEnabled(TelnetOption.EndOfRecord));
        }

        [Fact]
        public void AnOptionWeDoNotOfferIsRefusedWithWont()
        {
            // TK5 answered an unasked DO TN3270E with DONT (ff fe 28). Our side refuses TN3270E.
            var (n, sent) = Create(new byte[0], new byte[0]);

            n.ReceiveDo(TelnetOption.Tn3270E);

            Assert.Equal(new[] { "252 40" }, sent);
            Assert.False(n.IsLocalEnabled(TelnetOption.Tn3270E));
        }

        [Fact]
        public void ARepeatedWillForAnEnabledOptionIsIgnoredSoNothingLoops()
        {
            // RFC 1143: "Upon receipt of WILL ... YES: Ignore."
            var (n, sent) = Create(new byte[0], new[] { TelnetOption.Binary });

            n.ReceiveWill(TelnetOption.Binary);
            n.ReceiveWill(TelnetOption.Binary);
            n.ReceiveWill(TelnetOption.Binary);

            Assert.Equal(new[] { "253 0" }, sent);
        }

        [Fact]
        public void OurOwnRequestIsNotAnsweredWhenTheOtherSideAgrees()
        {
            // RFC 1143: we sent DO (WANTYES, queue EMPTY); WILL arrives -> YES, nothing sent.
            var (n, sent) = Create(new byte[0], new byte[0]);

            Assert.True(n.RequestRemoteEnable(TelnetOption.Binary));
            n.ReceiveWill(TelnetOption.Binary);

            Assert.Equal(new[] { "253 0" }, sent);
            Assert.True(n.IsRemoteEnabled(TelnetOption.Binary));
        }

        [Fact]
        public void AQueuedOppositeRequestIsSentWhenTheFirstAnswerArrives()
        {
            // RFC 1143: WANTYES OPPOSITE + WILL -> him=WANTNO, himq=EMPTY, send DONT.
            var (n, sent) = Create(new byte[0], new byte[0]);

            n.RequestRemoteEnable(TelnetOption.Echo);
            Assert.True(n.RequestRemoteDisable(TelnetOption.Echo)); // queued
            n.ReceiveWill(TelnetOption.Echo);
            n.ReceiveWont(TelnetOption.Echo);

            Assert.Equal(new[] { "253 1", "254 1" }, sent);
            Assert.False(n.IsRemoteEnabled(TelnetOption.Echo));
        }

        [Fact]
        public void TurningOffBinaryByTheServerIsAcknowledged()
        {
            // RFC 1143: YES + WONT -> him=NO, send DONT. RFC 1576 s.4 then says leave 3270 mode.
            var (n, sent) = Create(new byte[0], new[] { TelnetOption.Binary });
            var changes = new List<string>();
            n.OptionChanged += (option, local, on) => changes.Add(option + " " + local + " " + on);

            n.ReceiveWill(TelnetOption.Binary);
            n.ReceiveWont(TelnetOption.Binary);

            Assert.Equal(new[] { "253 0", "254 0" }, sent);
            Assert.Equal(new[] { "0 False True", "0 False False" }, changes);
        }

        [Fact]
        public void DataBytesOf255AreDoubled()
        {
            var w = new ArrayBufferWriter<byte>();

            TelnetEncoder.WriteData(w, new byte[] { 0x7D, 0xFF, 0x40, 0xFF });

            Assert.Equal(new byte[] { 0x7D, 0xFF, 0xFF, 0x40, 0xFF, 0xFF }, w.WrittenSpan.ToArray());
        }

        [Fact]
        public void TheTerminalTypeReplyHasTheExactTk5Bytes()
        {
            // C>S ff fa 18 00 "IBM-3278-2" ff f0, from the TK5 capture.
            var w = new ArrayBufferWriter<byte>();
            byte[] name = System.Text.Encoding.ASCII.GetBytes("IBM-3278-2");
            byte[] parameters = new byte[name.Length + 1];
            parameters[0] = TelnetOption.TerminalTypeIs;
            name.CopyTo(parameters, 1);

            TelnetEncoder.WriteSubnegotiation(w, TelnetOption.TerminalType, parameters);

            Assert.Equal(
                "FF FA 18 00 49 42 4D 2D 33 32 37 38 2D 32 FF F0",
                RecordingHandler.Hex(w.WrittenSpan));
        }

        [Fact]
        public void EncodedOutputReadsBackToTheSameEvents()
        {
            var w = new ArrayBufferWriter<byte>();
            TelnetEncoder.WriteNegotiation(w, TelnetCommand.Will, TelnetOption.Binary);
            TelnetEncoder.WriteData(w, new byte[] { 0x01, 0xFF, 0x02 });
            TelnetEncoder.WriteCommand(w, TelnetCommand.EndOfRecord);
            var handler = new RecordingHandler();

            new TelnetReader(handler).Feed(w.WrittenSpan);

            Assert.Equal(new[] { "NEG 251 0", "DATA 01 FF 02", "EOR" }, handler.Events);
        }
    }
}
