//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Node;
using RetroIO.Protocols.Xmsg.Node.Tad;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Verifies the TAD connect-accept our responder builds for the exact live connect request
    /// captured from machine 100, byte-comparing it to the structure of the known-good responder
    /// accept from the 2026-04-09-0100-tad-connect-to-d102-from-d103-via-d100 capture (the DA connect matching our role).
    /// </summary>
    /// <remarks>
    /// <para><b>Read this before treating a failure here as "the protocol changed"</b></para>
    /// <para>
    /// The INPUTS are real: every <c>reqBytes</c> string below is a live connect request captured off
    /// the wire. The EXPECTED strings are not all real in the same way - the header and addressing
    /// are held against the capture, but the parameter trailers were written down from our own
    /// builder's output at the time, because no capture of a real accept in OUR role exists.
    /// </para>
    /// <para>
    /// So these are regression tests over our own bytes, not statements of protocol truth. A failure
    /// means "the accept changed", which is worth knowing; it does not by itself mean the new bytes
    /// are wrong. The accept parameter trailer is still open question B3 in
    /// <c>DOC/protocols/tad-wire.json</c>, and it stays open because TADADM - the program that builds
    /// it - lives on segment SG36 and is absent from the version J source listing. The rule-shaped
    /// tests in <c>TwoNodeTerminalTests</c> are the better pattern where a rule exists to state.
    /// </para>
    /// </remarks>
    public sealed class TadConnectAcceptTests
    {
        private readonly ITestOutputHelper _output;

        public TadConnectAcceptTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ConnectAccept_EchoesConnectSequenceAndChannel()
        {
            // The exact live connect request 100 -> 103 (proto DA, f1=0x0000, counter=0x13).
            byte[] reqBytes = Convert.FromHexString(
                "2113000E0067006400000400DA13210086E400670000006402F7040000410010FF072A54414441444D00FE0444313033");
            XmsgFrame request = XmsgFrame.Parse(reqBytes);

            TadTerminalResponder responder = new TadTerminalResponder(103, () => new DateTime(2026, 7, 2));
            System.Collections.Generic.IReadOnlyList<XmsgFrame> frames = responder.OnConnect(request);

            Assert.Single(frames);
            byte[] accept = frames[0].ToArray();
            _output.WriteLine("accept: " + Convert.ToHexString(accept));

            // The accept ECHOES the connect's channel (DA), Flags1 (0x0000) and counter (0x13) - the
            // form 100 accepts. A high own-sequence D8 accept was REFUTED live (100 replied XENSE).
            // Addressing: from our TADADM 0x0156 to 100's port 0x02F7; params 01020000 0202000A.
            byte[] expected = Convert.FromHexString(
                "2113000E00640067" + "0000" + "0400" + "DA" + "13" + "2100" + "86" + "40"
                + "0064" + "02F7" + "0067" + "0156" + "04000041" + "00" + "08"
                + "01020000" + "0202000A");

            Assert.Equal(expected, accept);
        }

        [Fact]
        public void ConnectAck_RidesConnectChannelPlus4_EchoesFlags1()
        {
            // The exact live connect request 100 -> 103 on the PAD channel DA (0xDA), f1=0x0000.
            // VERIFIED rule: the secure ACK for this session rides connect-channel + 4 = DE (0xDE),
            // NOT the connect's own DA channel (the old +0 echo crashed 100 with XXPER).
            byte[] reqBytes = Convert.FromHexString(
                "2113000E0067006400000400DA13210086E400670000006402F7040000410010FF072A54414441444D00FE0444313033");
            XmsgFrame request = XmsgFrame.Parse(reqBytes);

            // Node 103; the ACK trailing byte is derived from the connect counter, not the seed.
            XmsgNode node = new XmsgNode(103, 0x00);
            node.TadResponder = new TadTerminalResponder(103, () => new DateTime(2026, 7, 2));
            node.AcknowledgeTadFrames = true;

            System.Collections.Generic.IReadOnlyList<XmsgFrame> frames = node.HandleFrames(request);

            // First frame must be the 0x03 delivery ACK; the second the connect-accept.
            Assert.True(frames.Count >= 2);
            byte[] ack = frames[0].ToArray();
            _output.WriteLine("ack: " + Convert.ToHexString(ack));

            // ACK layout (14 bytes): 21 13 00 03 | dst=0064 | src=0067 | f1=0000 | flags2=0001
            //   | proto=DE (connect DA + 4) | trailing=1D (connect counter 0x13 + 0x0A, VERIFIED
            //   rule). NOT the seed and NOT 0x00 (0x00 is the malformed ACK that crashed 100).
            byte[] expected = Convert.FromHexString("211300030064006700000001" + "DE" + "1D");
            Assert.Equal(expected, ack);

            // And the ACK channel the responder learned is exactly DE.
            Assert.Equal(SintranProtocolId.Routing, node.TadResponder.AckChannel);
        }

        [Fact]
        public void PortAssign_EchoesSessionSetupAndAssignsOurPort()
        {
            // Establish the session first (accept), then feed the session-setup 100 sends next.
            byte[] connectBytes = Convert.FromHexString(
                "2113000E0067006400000400DA13210086E400670000006402F7040000410010FF072A54414441444D00FE0444313033");
            TadTerminalResponder responder = new TadTerminalResponder(103, () => new DateTime(2026, 7, 2));
            responder.OnConnect(XmsgFrame.Parse(connectBytes));

            // The exact session-setup 100 -> 103 (proto DA, f1=0x0001, counter=0x12, XMCSM 04000000).
            byte[] setupBytes = Convert.FromHexString(
                "2113000E0067006400010400DA122100868400670156006402F704000000000906001B001C0100FF00");
            XmsgFrame setup = XmsgFrame.Parse(setupBytes);
            Assert.True(responder.IsSessionSetup(setup));

            byte[] assign = responder.OnSessionSetup(setup)[0].ToArray();
            _output.WriteLine("port-assign: " + Convert.ToHexString(assign));

            // Port-assign ECHOES the setup: channel DA, f1 0x0001, counter 0x12. From TADADM 0x0156
            // to 100's port 0x02F7; trailer assigns our system 0x67 and session port 0x0211.
            byte[] expected = Convert.FromHexString(
                "2113000E00640067" + "0001" + "0400" + "DA" + "12" + "2100" + "86" + "40"
                + "0064" + "02F7" + "0067" + "0156" + "04000000" + "00" + "18"
                + "00" + "0705" + "00006702" + "11" + "1F03" + "4C0000"
                + "00" + "0B02" + "0300" + "1502" + "0108" + "FF00");

            Assert.Equal(expected, assign);
        }

        [Fact]
        public void SessionBringup_BuildsDummOnDerivedDdChannel()
        {
            // The DUMM is now computed by the VERIFIED seed model (XmsgEnvelope). A fresh responder at
            // epoch 0 sends terminal data on 0xDD (NOT DB - DB was an epoch-2 artifact). This connect's
            // seed = Counter(0x13) + Flags1(0x0000) + (Flags2&0xFF=0x00) = 0x13.
            byte[] connectBytes = Convert.FromHexString(
                "2113000E0067006400000400DA13210086E400670000006402F7040000410010FF072A54414441444D00FE0444313033");
            TadTerminalResponder responder = new TadTerminalResponder(103, () => new DateTime(2026, 7, 2));
            responder.SendTerminalBringup = true;
            responder.OnConnect(XmsgFrame.Parse(connectBytes));   // accept: our Flags1 0x0000

            byte[] setupBytes = Convert.FromHexString(
                "2113000E0067006400010400DA122100868400670156006402F704000000000906001B001C0100FF00");
            System.Collections.Generic.IReadOnlyList<XmsgFrame> frames =
                responder.OnSessionSetup(XmsgFrame.Parse(setupBytes));   // port-assign: 0x0001; DUMM: 0x0002

            // port-assign + exactly one bring-up frame (the DUMM).
            Assert.Equal(2, frames.Count);

            byte[] dumm = frames[1].ToArray();
            _output.WriteLine("dumm: " + Convert.ToHexString(dumm));

            // DUMM: Flags1 0x0002 (our own sequence), Flags2 0x0108 -> baseLow = seed(0x13)-8 = 0x0B ->
            // Counter = 0x0B - 0x0002 = 0x09; epoch 0 -> channel 0xDE-1-0 = DD. Built via TadOp.Dumm.
            byte[] expected = Convert.FromHexString(
                "2113000E00640067" + "0002" + "0108" + "DD" + "09" + "2100" + "92" + "00"
                + "0064" + "02F7" + "0067" + "0211" + "01080000" + "00" + "02" + "1800");
            Assert.Equal(expected, dumm);

            // Sanity on the derived channel byte (offset 12) and the preserved TAD trailer.
            Assert.Equal((byte)SintranProtocolId.Tad, dumm[12]);    // DD
            Assert.EndsWith("1800", Convert.ToHexString(dumm));
            Assert.Equal(dumm.Length - 32, dumm[31]);         // XMLEN (offset 31) == trailer length
        }
    }
}
