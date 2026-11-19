//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The X.28 clear signal text (X.28 12/97 3.5.17.1 and Table 6): <c>CLR mnemonic C:cause D:diagnostic</c>.
    /// </summary>
    [TestFixture]
    public class X28ClearSignalTests
    {
        /// <summary>
        /// Every cause with a Table 6 mnemonic prints that mnemonic, the decimal cause and the decimal diagnostic.
        /// </summary>
        [TestCase((byte)0, "CLR DTE C:0 D:7")]
        [TestCase((byte)1, "CLR OCC C:1 D:7")]
        [TestCase((byte)3, "CLR INV C:3 D:7")]
        [TestCase((byte)5, "CLR NC C:5 D:7")]
        [TestCase((byte)9, "CLR DER C:9 D:7")]
        [TestCase((byte)11, "CLR NA C:11 D:7")]
        [TestCase((byte)13, "CLR NP C:13 D:7")]
        [TestCase((byte)17, "CLR RPE C:17 D:7")]
        [TestCase((byte)19, "CLR ERR C:19 D:7")]
        [TestCase((byte)25, "CLR RNA C:25 D:7")]
        [TestCase((byte)33, "CLR ID C:33 D:7")]
        [TestCase((byte)41, "CLR FNA C:41 D:7")]
        [TestCase((byte)57, "CLR SA C:57 D:7")]
        public void ClearSignal_KnownCause_HasMnemonic(byte cause, string expected)
        {
            Assert.That(X28.ClearSignal(cause, 7), Is.EqualTo(expected));
        }

        /// <summary>
        /// A cause with bit 8 set is "DTE originated" (the DTE's own code in the low bits): printed as DTE, cause 0.
        /// </summary>
        [Test]
        public void ClearSignal_DteOriginatedHighBit_IsDte()
        {
            Assert.That(X28.ClearSignal(0x85, 0), Is.EqualTo("CLR DTE C:0 D:0"));
        }

        /// <summary>
        /// A cause with no matching mnemonic prints only the numbers, never an invented mnemonic.
        /// </summary>
        [Test]
        public void ClearSignal_UnknownCause_HasNoMnemonic()
        {
            Assert.That(X28.ClearSignal(21, 3), Is.EqualTo("CLR C:21 D:3"));
            Assert.That(X28.ClearSignal(99, 0), Is.EqualTo("CLR C:99 D:0"));
        }

        /// <summary>
        /// Extended dialogue mode (X.28 (12/97) 3.5.17.1, Table 6): " - " and the cause's text; none for a cause
        /// without a mnemonic.
        /// </summary>
        [TestCase((byte)0x01, "CLR OCC C:1 D:0 - Call cleared, number busy")]
        [TestCase((byte)0x0D, "CLR NP C:13 D:0 - Call cleared, number not assigned")]
        [TestCase((byte)0x00, "CLR DTE C:0 D:0 - Call cleared, by remote device, data may be lost")]
        [TestCase((byte)0x05, "CLR NC C:5 D:0 - Call cleared, temporary network problem")]
        [TestCase((byte)21, "CLR C:21 D:0")]
        public void ClearSignal_Extended_AddsTheTable6Text(byte cause, string expected)
        {
            Assert.That(X28.ClearSignal(cause, 0, true), Is.EqualTo(expected));
        }

        /// <summary>X.28 3.5: extended dialogue mode when the high 4 bits of parameter 6 are not all zero; Table 7.</summary>
        [Test]
        public void ExtendedDialogue_IsTheHighFourBitsOfParameter6_AndConfirmationHasTheTable7Text()
        {
            Assert.That(X28.IsExtendedDialogue(1), Is.False);
            Assert.That(X28.IsExtendedDialogue(15), Is.False);
            Assert.That(X28.IsExtendedDialogue(16), Is.True);
            Assert.That(X28.IsExtendedDialogue(17), Is.True);
            Assert.That(X28.ClearConfirmation(false), Is.EqualTo("CLR CONF"));
            Assert.That(X28.ClearConfirmation(true), Is.EqualTo("CLR CONF - Call cleared, confirmed"));
        }

        /// <summary>
        /// The reset signal (X.28 3.5.7, Table 5) for each X.25 resetting cause (Table 5-8); the causes Table 5 has
        /// no word for give only RESET and the diagnostic.
        /// </summary>
        [TestCase((byte)0x00, (byte)0, false, "RESET DTE 0")]
        [TestCase((byte)0x85, (byte)3, false, "RESET DTE 3")]
        [TestCase((byte)0x05, (byte)0, true, "RESET ERR 0 - Reset by network, local procedure error, data may be lost")]
        [TestCase((byte)0x07, (byte)0, true, "RESET NC 0 - Reset due to temporary network problem, data may be lost")]
        [TestCase((byte)0x03, (byte)0, true, "RESET RPE 0 - Reset by network, remote procedure error, data may be lost")]
        [TestCase((byte)0x00, (byte)0, true, "RESET DTE 0 - Reset by remote device, data may be lost")]
        [TestCase((byte)0x09, (byte)0, true, "RESET 0")]
        public void ResetSignal_HasTheTable5CauseAndText(byte cause, byte diagnostic, bool extended, string expected)
        {
            Assert.That(X28.ResetSignal(cause, diagnostic, extended), Is.EqualTo(expected));
        }

        /// <summary>
        /// STAT (X.28 3.5.11): FREE with no call, ENGAGED with one (also at the prompt after the escape, where the
        /// call stays), with the extended dialogue text when parameter 6 is 16 or more.
        /// </summary>
        [Test]
        public void Status_IsFreeOrEngaged_WithTheExtendedText()
        {
            var x3 = new X3();
            var pad = new X28(x3);
            Assert.That(pad.StatusSignal(), Is.EqualTo("FREE"));
            pad.SwitchToDataTransferMode();
            Assert.That(pad.StatusSignal(), Is.EqualTo("ENGAGED"));
            Assert.That(x3.SetParameter(6, 17), Is.True);
            Assert.That(pad.StatusSignal(), Is.EqualTo("ENGAGED - Call Established"));
            pad.SwitchToCommandMode();
            Assert.That(pad.StatusSignal(), Is.EqualTo("FREE - No Call Established"));
        }

        /// <summary>
        /// CLR typed at the prompt with no call answers ERR, and no longer "CLEARED". (With a call up, X28 prints
        /// nothing and the PAD prints CLR CONF: PadClient.ClearCallFromTerminal.)
        /// </summary>
        [Test]
        public void ClearCommand_WithNoCall_IsErr()
        {
            var pad = new X28(new X3());
            var seen = new StringBuilder();
            pad.CommandResponse += (s, e) => seen.Append(e.Response);
            foreach (char ch in "CLR\r")
            {
                pad.ProcessCharacter(ch);
            }

            Assert.That(seen.ToString(), Is.EqualTo("ERR\r\n"));
        }

        /// <summary>
        /// The escape (DLE with parameter 1 = 1) during a call shows the PAD's prompt only (X.28 3.5.23), and the call stays.
        /// </summary>
        [Test]
        public void Escape_DuringACall_ShowsThePromptOnly()
        {
            var x3 = new X3();
            Assert.That(x3.SetParameter(1, 1), Is.True);
            var pad = new X28(x3);
            var seen = new StringBuilder();
            pad.CommandResponse += (s, e) => seen.Append(e.Response);
            pad.SwitchToDataTransferMode();

            pad.ProcessCharacter((char)0x10);

            Assert.That(seen.ToString(), Is.EqualTo("\r\n" + X28.PromptText));
            Assert.That(pad.StatusSignal(), Is.EqualTo("ENGAGED"));
        }

        /// <summary>
        /// A remote clear delivered to the PAD with cause and diagnostic shows the signal on the terminal.
        /// </summary>
        [Test]
        public void PadClient_DeliverClear_WithCause_PrintsSignal()
        {
            PadClient pad = new PadClient(NullLogger.Instance, new NullTransport());
            StringBuilder seen = new StringBuilder();
            pad.DataReceived += (s, e) => seen.Append(e.Data);

            pad.DeliverClear(1, 9, 0);

            Assert.That(seen.ToString(), Does.Contain("\r\nCLR DER C:9 D:0\r\n"));
        }

        /// <summary>
        /// A reset packet handed to the PAD (as the runtime does, OnEndpointReset) prints the reset signal with the
        /// cause and diagnostic read from the packet, and the call is not ended.
        /// </summary>
        [Test]
        public void PadClient_AResetPacket_PrintsTheResetSignal_AndTheCallStays()
        {
            PadClient pad = new PadClient(NullLogger.Instance, new NullTransport());
            StringBuilder seen = new StringBuilder();
            bool ended = false;
            pad.DataReceived += (s, e) => seen.Append(e.Data);
            pad.ConnectionStateChanged += (s, e) => ended = true;

            // LCN 0: the PAD's current channel before any call, so the packet is taken.
            var packet = new RetroIO.Protocols.X25.Models.X25Packet(
                RetroIO.Protocols.X25.Models.X25PacketBuilder.CreateResetRequest(0, 0x07, 5)) { LCN = 0 };
            pad.OnX25Event(new RetroIO.Protocols.X25.Models.X25PacketEventArgs { Packet = packet });

            Assert.That(seen.ToString(), Is.EqualTo("\r\nRESET NC 5\r\n"));
            Assert.That(ended, Is.False, "a reset does not end the call");
        }

        /// <summary>
        /// RESET typed at the prompt during a call (X.28 4.9.2.3) sends a reset with cause 0 on the call's channel and
        /// is acknowledged; a transport that cannot reset gives ERR, never a false acknowledgement.
        /// </summary>
        [TestCase(true, "")]
        [TestCase(false, "ERR\r\n")]
        public void ResetCommand_DuringACall_SendsTheReset(bool transportCanReset, string expectedAfterAck)
        {
            var transport = new ResetTransport { CanReset = transportCanReset };
            PadClient pad = new PadClient(NullLogger.Instance, transport);
            pad.NotifyCallAccepted(5);
            StringBuilder seen = new StringBuilder();
            pad.DataReceived += (s, e) => seen.Append(e.Data);

            foreach (char ch in "\u0010RESET\r")
            {
                pad.ProcessCharacter((byte)ch);
            }

            Assert.That(transport.Resets, Is.EqualTo(new[] { "5/0/0" }));
            string screen = seen.ToString();
            Assert.That(screen, Does.Not.Contain("RESET\r\n"), "the old answer");
            if (transportCanReset)
            {
                Assert.That(screen, Does.Not.Contain("ERR"));
            }
            else
            {
                Assert.That(screen, Does.Contain(expectedAfterAck));
            }
        }

        /// <summary>Types a line at the prompt (no call) and returns what the PAD answered.</summary>
        private static string Type(X28 pad, string line)
        {
            var seen = new StringBuilder();
            EventHandler<CommandResponseEventArgs> handler = (s, e) => seen.Append(e.Response);
            pad.CommandResponse += handler;
            foreach (char ch in line + "\r")
            {
                pad.ProcessCharacter(ch);
            }

            pad.CommandResponse -= handler;
            return seen.ToString();
        }

        /// <summary>
        /// PAR?, SET and SET? in the formats of X.28 3.5.4.1, 3.5.6.1 and 3.5.14, with the list separated by ", " (the
        /// X.28 example "SET 2:0, 3:2, 9:4" itself), INV for what is not valid, and SET acknowledged by a format
        /// effector only (3.3.2).
        /// </summary>
        [Test]
        public void ParameterCommands_AnswerInTheX28Formats()
        {
            var x3 = new X3();
            var pad = new X28(x3);

            Assert.That(Type(pad, "SET 2:0, 3:2, 9:4"), Is.EqualTo("\r\n"));
            Assert.That(x3.GetParameter(2), Is.EqualTo(0));
            Assert.That(x3.GetParameter(3), Is.EqualTo(2));
            Assert.That(x3.GetParameter(9), Is.EqualTo(4));

            Assert.That(Type(pad, "PAR? 2, 3"), Is.EqualTo("PAR 2:0, 3:2\r\n"));
            Assert.That(Type(pad, "PAR?9"), Is.EqualTo("PAR 9:4\r\n"));
            Assert.That(Type(pad, "SET? 2:1"), Is.EqualTo("PAR 2:1\r\n"));
            Assert.That(Type(pad, "PAR? 99"), Is.EqualTo("PAR 99:INV\r\n"));
            Assert.That(Type(pad, "SET 2:0, 99:1"), Is.EqualTo("PAR 99:INV\r\n"), "only the invalid entry is named");
            Assert.That(x3.GetParameter(2), Is.EqualTo(0), "the valid entry is still set");
            Assert.That(Type(pad, "PAR?"), Does.StartWith("PAR 1:"));
        }

        /// <summary>
        /// Extended dialogue mode (parameter 6 at 16 or more): a Table 8 name or its abbreviation in place of the number,
        /// in any case, and the name after each entry of the answer. Names are not taken outside that mode.
        /// </summary>
        [Test]
        public void ParameterNames_InExtendedDialogueMode()
        {
            var x3 = new X3();
            var pad = new X28(x3);
            Assert.That(Type(pad, "PAR? ech"), Is.EqualTo("PAR ECH:INV\r\n"), "no names outside extended mode (the line is read in upper case)");

            Type(pad, "SET 6:17");
            Assert.That(Type(pad, "SET? ech:0"), Is.EqualTo("PAR 2:0 - echo\r\n"));
            Assert.That(Type(pad, "PAR? ESCAPE, Ldis"), Is.EqualTo("PAR 1:" + x3.GetParameter(1) + " - escape, 18:" + x3.GetParameter(18) + " - Ldisplay\r\n"));
            Assert.That(Type(pad, "PAR? es"), Is.EqualTo("PAR ES:INV\r\n"), "shorter than the abbreviation");
            Assert.That(X28.ParameterName(29), Is.EqualTo("Xecho"));
            Assert.That(X28.ParameterName(30), Is.Null);
        }

        /// <summary>
        /// HELP with the subjects of X.28 Table 10; an unknown subject is ERR. PROF takes the ITU-T profile numbers 90
        /// and 91 (Table 3).
        /// </summary>
        [Test]
        public void Help_TakesTheTable10Subjects_AndProfilesTakeTheItuNumbers()
        {
            var x3 = new X3();
            var pad = new X28(x3);
            Assert.That(Type(pad, "HELP"), Does.StartWith("PAD commands:").And.Contain("HELP <subject>"));
            Assert.That(Type(pad, "HELP LIST"), Does.Contain("PROFILES").And.Contain("RESET"));
            Assert.That(Type(pad, "HELP COMMAND"), Does.Contain("SET? n:v"));
            Assert.That(Type(pad, "HELP PARAMETER 2"), Is.EqualTo("2:" + x3.GetParameter(2) + " - echo, Echo\r\n"));
            Assert.That(Type(pad, "HELP PARAMETER"), Does.Contain("6:" + x3.GetParameter(6) + " - signals, Control of PAD service signals\r\n"));
            Assert.That(Type(pad, "HELP CLR"), Does.StartWith("CLR "));
            Assert.That(Type(pad, "HELP PROFILE 91"), Does.StartWith("91: PAR 1:"));
            Assert.That(Type(pad, "HELP FROB"), Is.EqualTo("ERR\r\n"));

            var expected = new X3();
            expected.SetTransparentProfile();
            Type(pad, "PROF 91");
            Assert.That(x3.GetParameter(2), Is.EqualTo(expected.GetParameter(2)));
            Assert.That(x3.GetParameter(3), Is.EqualTo(expected.GetParameter(3)));
        }

        /// <summary>
        /// The host's X.29 invitation to clear (code 1, with the Q bit): the PAD clears the call and tells the terminal
        /// CLR PAD (X.28 Table 6, "Call cleared, remote request"), not the CLR CONF of the person's own clear.
        /// </summary>
        [Test]
        public void InvitationToClear_IsToldAsClrPad()
        {
            var transport = new ResetTransport();
            PadClient pad = new PadClient(NullLogger.Instance, transport);
            pad.NotifyCallAccepted(5);
            var seen = new StringBuilder();
            object gate = new object();
            pad.DataReceived += (s, e) => { lock (gate) { seen.Append(e.Data); } };

            pad.DeliverData(5, new byte[] { 0x01 }, true);

            Assert.That(System.Threading.SpinWait.SpinUntil(() => { lock (gate) { return seen.ToString().Contains("CLR PAD"); } }, 3000), Is.True, seen.ToString());
            Assert.That(seen.ToString(), Does.Not.Contain("CLR CONF"));
            Assert.That(transport.Cleared, Is.EqualTo(1));
            Assert.That(X28.PadClearSignal(true), Is.EqualTo("CLR PAD - Call cleared, remote request"));
        }

        private sealed class ResetTransport : IPadTransport
        {
            public bool CanReset;
            public int Cleared;
            public readonly System.Collections.Generic.List<string> Resets = new System.Collections.Generic.List<string>();
            public bool PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData) => true;
            public bool SendData(ushort lcn, byte[] data, bool mBit, bool qBit, bool dBit) => true;
            public void ForwardData(ushort lcn, byte[] data, int length) { }
            public void ClearCall(ushort lcn, byte cause, byte diagnostic) { System.Threading.Interlocked.Increment(ref Cleared); }

            public bool ResetCall(ushort lcn, byte cause, byte diagnostic)
            {
                Resets.Add(lcn + "/" + cause + "/" + diagnostic);
                return CanReset;
            }
        }

        private sealed class NullTransport : IPadTransport
        {
            public bool PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData) => true;
            public bool SendData(ushort lcn, byte[] data, bool mBit, bool qBit, bool dBit) => true;
            public void ForwardData(ushort lcn, byte[] data, int length) { }
            public void ClearCall(ushort lcn, byte cause, byte diagnostic) { }
        }
    }
}
