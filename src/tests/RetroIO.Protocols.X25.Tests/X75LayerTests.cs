//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.PacketLayer;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The packet layer as an X.75 signalling terminal (STE) on a link between networks. Every expected value is taken
    /// by hand from CCITT X.75 (11/1988): 3.1.4 (channel search, collision), 4.2 (utility field), Table 13 (clearing
    /// causes), Table C-3 and Annex F a) row 40 (collision diagnostic 116), Figure 8 (clear confirmation).
    /// </summary>
    [TestFixture]
    public class X75LayerTests
    {
        private sealed class RecordingSink : IX25PacketSink
        {
            public readonly List<byte[]> Sent = new List<byte[]>();

            public X25SendResult SendPacket(ReadOnlySpan<byte> packet)
            {
                Sent.Add(packet.ToArray());
                return X25SendResult.Sent;
            }

            public byte[] Last => Sent[Sent.Count - 1];
        }

        private RecordingSink _sink = null!;

        private X25Layer Ste(bool fromHighest)
        {
            _sink = new RecordingSink();
            return new X25Layer(_sink, new X25Config
            {
                LocalAddress = "24220001",
                IsDte = false,
                IsSte = true,
                SearchFromHighest = fromHighest,
                LciBase = 1,
                LciCount = 16,
            });
        }

        /// <summary>
        /// A restart confirmation with nothing to confirm (state r1) is ERROR (r3) # 17 on the network side: X.75 Table
        /// C.2 restarts with network congestion (00000011, Table 25), X.25 Table C-2 with local procedure error. A DTE
        /// has no table and drops it. Added 2026-11-22; before, every side dropped it.
        /// </summary>
        /// <param name="ste">
        /// True for a switch between networks.
        /// </param>
        /// <param name="dte">
        /// True for a DTE.
        /// </param>
        /// <param name="cause">
        /// The restarting cause expected, or -1 for no restart at all.
        /// </param>
        [TestCase(true, false, 0x03)]
        [TestCase(false, false, 0x01)]
        [TestCase(false, true, -1)]
        public void AnUnaskedRestartConfirmation_IsRestartedWithDiagnostic17OnTheNetworkSide(bool ste, bool dte, int cause)
        {
            _sink = new RecordingSink();
            X25Layer layer = new X25Layer(_sink, new X25Config { LocalAddress = "2422", IsDte = dte, IsSte = ste, LciBase = 1, LciCount = 4 });
            int before = _sink.Sent.Count;
            layer.ProcessPacket(X25PacketBuilder.CreateRestartConfirmation());

            if (cause < 0)
            {
                Assert.That(_sink.Sent.Count, Is.EqualTo(before));
                return;
            }

            X25PacketInfo sent = X25PacketParser.ParsePacket(_sink.Last, x75: ste);
            Assert.That(sent.PacketType, Is.EqualTo(X25PacketType.RestartRequest));
            Assert.That(sent.RestartCause, Is.EqualTo((byte)cause));
            Assert.That(sent.DiagnosticCode, Is.EqualTo((byte)17));
            Assert.That(layer.RestartOutstanding, Is.True, "r3: waiting for the other side's confirmation");
        }

        /// <summary>
        /// X.75 Table C.1: a packet on an unassigned channel, one under 2 octets, or one with a bad GFI is DISCARD. X.75
        /// has no Diagnostic packet, so an STE sends nothing where an X.25 DCE sends one (# 36, # 38, # 40). Added
        /// 2026-11-23; before, an STE sent X.25 Diagnostic packets.
        /// </summary>
        /// <param name="ste">
        /// True for a switch between networks, false for an X.25 DCE.
        /// </param>
        [TestCase(true)]
        [TestCase(false)]
        public void ABadPacket_IsDroppedBetweenNetworks_AndDiagnosedOnX25(bool ste)
        {
            _sink = new RecordingSink();
            X25Layer layer = new X25Layer(_sink, new X25Config { LocalAddress = "2422", IsDte = false, IsSte = ste, LciBase = 1, LciCount = 4 });
            int before = _sink.Sent.Count;

            // RR on channel 100, outside 1..4; then a 1-octet packet; then a GFI of 00.
            layer.ProcessPacket(new byte[] { 0x10, 0x64, 0x01 });
            layer.ProcessPacket(new byte[] { 0x10 });
            layer.ProcessPacket(new byte[] { 0x00, 0x01, 0x01 });

            int expected = ste ? 0 : 3;
            Assert.That(_sink.Sent.Count - before, Is.EqualTo(expected));
            for (int i = before; i < _sink.Sent.Count; i++)
            {
                Assert.That(_sink.Sent[i][2], Is.EqualTo((byte)0xF1), "a Diagnostic packet");
            }
        }

        /// <summary>
        /// X.75 Table C.3, read from the 10/96 PDF page 102, row by row: each ERROR clears the channel with "network
        /// congestion" (00000101) and the diagnostic the table gives, each DISCARD sends nothing. Added 2026-11-23; it
        /// failed first on 9 rows: diagnostics 21 and 22 swapped on the network side, a Call Request in p7 discarded
        /// instead of ERROR # 26, a restart packet in p7 cleared instead of discarded, a Clear Confirmation in p1 cleared
        /// instead of discarded.
        /// </summary>
        /// <param name="state">
        /// p1 ready, p2 a call from the other STE not yet answered, p3 our call not yet answered, p4 data transfer, p7
        /// our Clear Request not yet confirmed.
        /// </param>
        /// <param name="packet">
        /// The packet received: connected, clearconf, data, rr, call, restart.
        /// </param>
        /// <param name="diagnostic">
        /// The ERROR diagnostic, or -1 for DISCARD.
        /// </param>
        [TestCase("p1", "connected", 20)]
        [TestCase("p1", "data", 20)]
        [TestCase("p1", "rr", 20)]
        [TestCase("p1", "restart", 41)]
        [TestCase("p1", "clearconf", -1)]
        [TestCase("p2", "call", 21)]
        [TestCase("p2", "connected", 21)]
        [TestCase("p2", "clearconf", 21)]
        [TestCase("p2", "data", 21)]
        [TestCase("p2", "restart", 41)]
        [TestCase("p3", "clearconf", 22)]
        [TestCase("p3", "data", 22)]
        [TestCase("p3", "restart", 41)]
        [TestCase("p4", "call", 23)]
        [TestCase("p4", "connected", 23)]
        [TestCase("p4", "clearconf", 23)]
        [TestCase("p7", "call", 26)]
        [TestCase("p7", "connected", -1)]
        [TestCase("p7", "data", -1)]
        [TestCase("p7", "restart", -1)]
        public void TableC3_EachRow_AtAnSte(string state, string packet, int diagnostic)
        {
            X25Layer ste = Ste(fromHighest: false);
            ushort lci;
            switch (state)
            {
                case "p1":
                    lci = 3;
                    break;
                case "p2":
                    lci = 3;
                    ste.ProcessPacket(X25PacketBuilder.CreateCallRequest(lci, "2624", "2422", networkUtilities: Array.Empty<byte>()));
                    break;
                default:
                    lci = ste.PlaceCall("2624");
                    Assert.That(lci, Is.EqualTo((ushort)1));
                    if (state != "p3")
                    {
                        ste.ProcessPacket(X25PacketBuilder.CreateCallAccepted(lci, networkUtilities: Array.Empty<byte>()));
                        Assert.That(ste.TryGetCallParameters(lci, out _), Is.True);
                    }

                    if (state == "p7")
                    {
                        Assert.That(ste.ClearCall(lci, 0x05, 0), Is.True);
                    }

                    break;
            }

            byte[] bytes = packet switch
            {
                "connected" => X25PacketBuilder.CreateCallAccepted(lci, networkUtilities: Array.Empty<byte>()),
                "clearconf" => X25PacketBuilder.CreateClearConfirmation(lci),
                "data" => new byte[] { 0x10, (byte)lci, 0x00, 0x41 },
                "rr" => new byte[] { 0x10, (byte)lci, 0x01 },
                "call" => X25PacketBuilder.CreateCallRequest(lci, "2624", "2422", networkUtilities: Array.Empty<byte>()),
                "restart" => new byte[] { 0x10, (byte)lci, 0xFB, 0x00, 0x00 },
                _ => throw new ArgumentException(packet),
            };

            int before = _sink.Sent.Count;
            ste.ProcessPacket(bytes);

            if (diagnostic < 0)
            {
                Assert.That(_sink.Sent.Count, Is.EqualTo(before), "DISCARD: nothing sent");
                return;
            }

            Assert.That(_sink.Sent.Count, Is.EqualTo(before + 1));
            X25PacketInfo sent = X25PacketParser.ParsePacket(_sink.Last, x75: true);
            Assert.That(sent.PacketType, Is.EqualTo(X25PacketType.ClearRequest));
            Assert.That(sent.LCN, Is.EqualTo(lci));
            Assert.That(sent.ClearingCause, Is.EqualTo((byte)0x05), "network congestion");
            Assert.That(sent.DiagnosticCode, Is.EqualTo((byte)diagnostic));
        }

        /// <summary>
        /// X.25 Table C-3 (10/96 PDF page 127): data in p3 "DCE waiting" (the DCE's own call unanswered) is ERROR # 22,
        /// data in p2 "DTE waiting" (a call the DCE received, unanswered) is ERROR # 21; a DTE names the same channel
        /// states the other way round. Added 2026-11-23, when the network side had them swapped.
        /// </summary>
        /// <param name="dte">
        /// True for a DTE, false for a DCE.
        /// </param>
        /// <param name="ownCall">
        /// True when this side placed the call.
        /// </param>
        /// <param name="diagnostic">
        /// The diagnostic expected in the clear.
        /// </param>
        [TestCase(false, true, 22)]
        [TestCase(false, false, 21)]
        [TestCase(true, true, 21)]
        [TestCase(true, false, 22)]
        public void DataBeforeTheAnswer_NamesTheStateFromTheDteSide(bool dte, bool ownCall, int diagnostic)
        {
            _sink = new RecordingSink();
            X25Layer layer = new X25Layer(_sink, new X25Config { LocalAddress = "2422", IsDte = dte, LciBase = 1, LciCount = 4 });
            ushort lci;
            if (ownCall)
            {
                lci = layer.PlaceCall("2624");
            }
            else
            {
                lci = 2;
                layer.ProcessPacket(X25PacketBuilder.CreateCallRequest(lci, "2422", "2624"));
            }

            layer.ProcessPacket(new byte[] { 0x10, (byte)lci, 0x00, 0x41 });
            X25PacketInfo sent = X25PacketParser.ParsePacket(_sink.Last);
            Assert.That(sent.PacketType, Is.EqualTo(X25PacketType.ClearRequest));
            Assert.That(sent.DiagnosticCode, Is.EqualTo((byte)diagnostic));
        }

        /// <summary>
        /// X.75 Table C.4 (10/96 PDF page 103): on a call in data transfer, a restart packet on its channel is ERROR (d3)
        /// # 41 and an unknown packet type ERROR (d3) # 33 - a Reset Request with network congestion (00000111, Table
        /// 15), the call stays - and both are DISCARD while our own reset is outstanding (d3). Added 2026-11-23; before,
        /// an STE cleared the call.
        /// </summary>
        /// <param name="packet">
        /// restart or unknown.
        /// </param>
        /// <param name="diagnostic">
        /// The reset diagnostic expected.
        /// </param>
        [TestCase("restart", 41)]
        [TestCase("unknown", 33)]
        public void InDataTransfer_ARestartOrUnknownPacket_ResetsTheCall_AtAnSte(string packet, int diagnostic)
        {
            X25Layer ste = Ste(fromHighest: false);
            ushort lci = ste.PlaceCall("2624");
            ste.ProcessPacket(X25PacketBuilder.CreateCallAccepted(lci, networkUtilities: Array.Empty<byte>()));
            byte[] bytes = packet == "restart"
                ? new byte[] { 0x10, (byte)lci, 0xFB, 0x00, 0x00 }
                : new byte[] { 0x10, (byte)lci, 0x0D };

            ste.ProcessPacket(bytes);
            X25PacketInfo sent = X25PacketParser.ParsePacket(_sink.Last, x75: true);
            Assert.That(sent.PacketType, Is.EqualTo(X25PacketType.ResetRequest));
            Assert.That(sent.ResetCause, Is.EqualTo((byte)0x07), "network congestion");
            Assert.That(sent.DiagnosticCode, Is.EqualTo((byte)diagnostic));
            Assert.That(ste.TryGetCallParameters(lci, out _), Is.True, "the call is still there");

            // d3: the same packet again is DISCARD.
            int before = _sink.Sent.Count;
            ste.ProcessPacket(bytes);
            Assert.That(_sink.Sent.Count, Is.EqualTo(before));
        }

        [Test]
        public void TheChannelSearchDirection_IsThePortSetting()
        {
            // 3.1.4: one STE takes the lowest ready channel, the other the highest; channels 1..16 here.
            Assert.That(Ste(fromHighest: false).PlaceCall("26245890040004"), Is.EqualTo((ushort)1));
            Assert.That(Ste(fromHighest: true).PlaceCall("26245890040004"), Is.EqualTo((ushort)16));
        }

        [Test]
        public void ACallRequest_CarriesTheUtilityFieldItWasGiven()
        {
            X25Layer ste = Ste(fromHighest: false);
            byte[] utilities = { 0x81, 0x00, 0x00, 0x01 };
            X25CallParameters p = new X25CallParameters(2, 2, 128, 128, null, utilities);
            ushort lci = ste.PlaceCall("2624", "2422", null, p);
            Assert.That(lci, Is.EqualTo((ushort)1));

            // Channel 1: 10 01, type 0B. Address lengths: called 4 digits, calling 4 digits = 0x44; 2624 2422 in BCD
            // = 26 24 24 22. Then the utility field: the call identifier as given (81 00 00 01), and the window and
            // packet sizes as utilities (X.75 5.1: not in the facility field) - window 2/2 is 43 02 02 (5.4.3.4), packet
            // 128/128 is 42 07 07 (5.4.3.5, log2 128 = 7). 10 octets, length 0A. Then an empty user facility field, 00.
            byte[] expected =
            {
                0x10, 0x01, 0x0B, 0x44, 0x26, 0x24, 0x24, 0x22,
                0x0A, 0x81, 0x00, 0x00, 0x01, 0x43, 0x02, 0x02, 0x42, 0x07, 0x07,
                0x00,
            };
            Assert.That(_sink.Last, Is.EqualTo(expected));
            Assert.That(X25PacketParser.ParsePacket(_sink.Last, x75: true).ParseError, Is.Null);
        }

        [Test]
        public void AnIncomingCallsUtilityField_IsKeptForTheSwitch()
        {
            X25Layer ste = Ste(fromHighest: false);
            byte[] utilities = { 0x41, 0x23, 0x42, 0x81, 0x00, 0x00, 0x01 };
            ste.ProcessPacket(X25PacketBuilder.CreateCallRequest(5, "26245890040004", "242226500000", networkUtilities: utilities));

            Assert.That(ste.TryGetCallParameters(5, out X25CallParameters p), Is.True);
            Assert.That(p.NetworkUtilities, Is.EqualTo(utilities));
        }

        [Test]
        public void TheAnswer_IsACallConnectedWithAUtilityField()
        {
            X25Layer ste = Ste(fromHighest: false);
            ste.ProcessPacket(X25PacketBuilder.CreateCallRequest(5, "2624", "2422", networkUtilities: Array.Empty<byte>()));
            Assert.That(ste.AcceptCall(5, new X25CallParameters(2, 2, 128, 128, null, Array.Empty<byte>())), Is.True);

            // 5.4.1: the field is present in every Call Connected. The accept states the values it runs with, window 2/2
            // and packet 128/128, and between networks they are utilities (43 02 02, 42 07 07; length 06): header
            // 10 05 0F, address lengths 00, the utility field, user facility length 00.
            byte[] sent = _sink.Last;
            Assert.That(sent, Is.EqualTo(new byte[] { 0x10, 0x05, 0x0F, 0x00, 0x06, 0x43, 0x02, 0x02, 0x42, 0x07, 0x07, 0x00 }));
            Assert.That(X25PacketParser.ParsePacket(sent, x75: true).ParseError, Is.Null);

            // A plain accept of a call that asked for nothing still has the (empty) field: ... 00 00 00.
            ste.ProcessPacket(X25PacketBuilder.CreateCallRequest(6, "2624", "2422", networkUtilities: Array.Empty<byte>()));
            Assert.That(ste.AcceptCall(6), Is.True);
            Assert.That(_sink.Last, Is.EqualTo(new byte[] { 0x10, 0x06, 0x0F, 0x00, 0x00, 0x00 }));
        }

        [Test]
        public void ACallCollision_ClearsOurCallWithNetworkCongestion116()
        {
            X25Layer ste = Ste(fromHighest: true);
            byte cause = 0xFF;
            byte diagnostic = 0xFF;
            ste.Cleared += (_, _, c, d) =>
            {
                cause = c;
                diagnostic = d;
            };

            ushort lci = ste.PlaceCall("2624");
            Assert.That(lci, Is.EqualTo((ushort)16));

            // The far STE's Call Request arrives on the same channel 16 (octets 10 10) while ours is outstanding.
            ste.ProcessPacket(X25PacketBuilder.CreateCallRequest(16, "2422", "2624", networkUtilities: Array.Empty<byte>()));

            // 3.1.4 and Table C-3: clear with network congestion (05, Table 13) and diagnostic 116 (0x74).
            Assert.That(_sink.Last, Is.EqualTo(new byte[] { 0x10, 0x10, 0x13, 0x05, 0x74 }));
            Assert.That(cause, Is.EqualTo(0x05));
            Assert.That(diagnostic, Is.EqualTo(116));
            Assert.That(ste.Circuits[16].State, Is.EqualTo(X25VcState.ClearRequest));
        }

        [Test]
        public void WindowAndPacketSizeUtilities_AreNegotiatedLikeFacilities()
        {
            X25Layer ste = Ste(fromHighest: false);

            // The calling STE asks for window 7/7 and packet 256/256 in utilities (43 07 07, 42 08 08) beside call id 1.
            byte[] asked = { 0x81, 0x00, 0x00, 0x01, 0x43, 0x07, 0x07, 0x42, 0x08, 0x08 };
            ste.ProcessPacket(X25PacketBuilder.CreateCallRequest(5, "2624", "2422", networkUtilities: asked));
            Assert.That(ste.TryGetCallParameters(5, out X25CallParameters p), Is.True);
            Assert.That(p.WindowSizeOut, Is.EqualTo(7));
            Assert.That(p.PacketSizeIn, Is.EqualTo(256));

            // A plain accept answers the same sizes, again as utilities: 10 05 0F, address lengths 00, utilities
            // 43 07 07 42 08 08 (length 06), user facility length 00.
            Assert.That(ste.AcceptCall(5), Is.True);
            Assert.That(_sink.Last, Is.EqualTo(new byte[] { 0x10, 0x05, 0x0F, 0x00, 0x06, 0x43, 0x07, 0x07, 0x42, 0x08, 0x08, 0x00 }));
        }

        /// <summary>
        /// X.75 5.3.3: "No indication of throughput classes should be present in the user facility field of the call
        /// request". The X.25 facility 02 goes out as utility 02 with the same octet (X.25 7.3.2, X.75 5.4.3.3: bits 4-1
        /// from the calling side, 8-5 from the called side). Added 2026-11-22; before, it stayed a facility.
        /// </summary>
        [Test]
        public void ThroughputClass_LeavesAsAUtility_NotAFacility()
        {
            X25Layer ste = Ste(fromHighest: false);
            X25CallParameters p = new X25CallParameters(2, 2, 128, 128, new byte[] { 0x02, 0xAB }, Array.Empty<byte>());
            Assert.That(ste.PlaceCall("2624", "2422", null, p), Is.EqualTo((ushort)1));

            X25PacketInfo info = X25PacketParser.ParsePacket(_sink.Last, x75: true);
            Assert.That(X75Utilities.TryDecode(info.NetworkUtilities, out X75Utilities u), Is.EqualTo(X75UtilityError.None));
            Assert.That(u.HasThroughputClass, Is.True);
            Assert.That(u.ThroughputFromCalling, Is.EqualTo((byte)0x0B));
            Assert.That(u.ThroughputFromCalled, Is.EqualTo((byte)0x0A));
            Assert.That(info.Facilities, Is.Empty, "no throughput class in the user facility field");
        }

        [Test]
        public void AThroughputClassUtility_ReachesTheSwitchAsTheX25Facility()
        {
            X25Layer ste = Ste(fromHighest: false);
            ste.ProcessPacket(X25PacketBuilder.CreateCallRequest(5, "2624", "2422",
                networkUtilities: new byte[] { 0x81, 0x00, 0x00, 0x01, 0x02, 0xAB }));

            Assert.That(ste.TryGetCallParameters(5, out X25CallParameters p), Is.True);
            Assert.That(p.TryGetFacility(0x02, out byte[] parameter), Is.True);
            Assert.That(parameter, Is.EqualTo(new byte[] { 0xAB }));
        }

        /// <summary>
        /// X.75 5.3.3 in the answer: the confirmed throughput class is utility 02 in the Call Connected, not a facility.
        /// Added 2026-11-22.
        /// </summary>
        [Test]
        public void TheAnswer_CarriesTheThroughputClassAsAUtility()
        {
            X25Layer ste = Ste(fromHighest: false);
            ste.ProcessPacket(X25PacketBuilder.CreateCallRequest(5, "2624", "2422",
                networkUtilities: new byte[] { 0x81, 0x00, 0x00, 0x01, 0x02, 0xAB }));
            Assert.That(ste.AcceptCall(5, new X25CallParameters(2, 2, 128, 128, new byte[] { 0x02, 0x9A }, Array.Empty<byte>())), Is.True);

            X25PacketInfo info = X25PacketParser.ParsePacket(_sink.Last, x75: true);
            Assert.That(X75Utilities.TryDecode(info.NetworkUtilities, out X75Utilities u), Is.EqualTo(X75UtilityError.None));
            Assert.That(u.HasThroughputClass, Is.True);
            Assert.That(u.ThroughputFromCalling, Is.EqualTo((byte)0x0A));
            Assert.That(u.ThroughputFromCalled, Is.EqualTo((byte)0x09));
            Assert.That(info.Facilities, Is.Empty, "no throughput class in the user facility field");
        }

        /// <summary>
        /// X.75 5.3.3: a confirmed class higher than the one asked, in either direction, clears the call with Network
        /// congestion (Table 13: 00000101); the same or lower is accepted. Asked: 0x55 (class 5 each way).
        /// </summary>
        [TestCase((byte)0x55, false)]
        [TestCase((byte)0x34, false)]
        [TestCase((byte)0x56, true)]
        [TestCase((byte)0x65, true)]
        public void AConfirmedThroughputClassHigherThanAsked_ClearsTheCall(byte confirmed, bool clears)
        {
            X25Layer ste = Ste(fromHighest: false);
            X25CallParameters p = new X25CallParameters(2, 2, 128, 128, new byte[] { 0x02, 0x55 }, new byte[] { 0x81, 0x00, 0x00, 0x01 });
            Assert.That(ste.PlaceCall("2624", "2422", null, p), Is.EqualTo((ushort)1));
            int before = _sink.Sent.Count;

            ste.ProcessPacket(X25PacketBuilder.CreateCallAccepted(1, null,
                networkUtilities: new byte[] { 0x02, confirmed, 0x43, 0x02, 0x02, 0x42, 0x07, 0x07 }));

            bool sawClear = false;
            for (int i = before; i < _sink.Sent.Count; i++)
            {
                if (_sink.Sent[i].Length > 3 && _sink.Sent[i][2] == 0x13)
                {
                    sawClear = true;
                    Assert.That(_sink.Sent[i][3], Is.EqualTo((byte)0x05), "Network congestion");
                }
            }

            Assert.That(sawClear, Is.EqualTo(clears));
        }

        /// <summary>
        /// X.75 5.3.1: a Clear Request that is the direct answer to a Call Request names every transit network of that
        /// Call Request, in order; one sent after the call connected names none. Added 2026-11-22.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void ARefusal_CarriesTheTransitList_AClearAfterConnectingDoesNot(bool connectFirst)
        {
            X25Layer ste = Ste(fromHighest: false);

            // Transit networks 2624 then 2080 (41 26 24, 41 20 80, 5.4.3.1), then the call identifier 81 00 00 01.
            ste.ProcessPacket(X25PacketBuilder.CreateCallRequest(5, "2624", "2422",
                networkUtilities: new byte[] { 0x41, 0x26, 0x24, 0x41, 0x20, 0x80, 0x81, 0x00, 0x00, 0x01 }));
            if (connectFirst)
            {
                Assert.That(ste.AcceptCall(5), Is.True);
            }

            Assert.That(ste.ClearCall(5, 0x0D, 67, null), Is.True);

            X25PacketInfo info = X25PacketParser.ParsePacket(_sink.Last, x75: true);
            Assert.That(X75Utilities.TryDecode(info.NetworkUtilities, out X75Utilities u), Is.EqualTo(X75UtilityError.None));
            if (connectFirst)
            {
                Assert.That(info.NetworkUtilities, Is.Empty, "not after the call connected");
            }
            else
            {
                Assert.That(u.TransitNetworks, Is.EqualTo(new[] { "2624", "2080" }));
                Assert.That(u.HasCallIdentifier, Is.False, "only the transit list");
            }
        }

        [Test]
        public void ReverseCharging_LeavesAsAUtility_NotAFacility()
        {
            X25Layer ste = Ste(fromHighest: false);

            // The switch hands on the X.25 facility 01 with reverse charging (bit 1) and the X.25 (10/96) ICRD bits 6 and 5
            // set: 0x31. X.75 5.3.9: no reverse charging in the user facility field; 5.4.3.6: utility 01, bits 6 and 5
            // unassigned and 0, so the parameter is 0x01.
            X25CallParameters p = new X25CallParameters(2, 2, 128, 128, new byte[] { 0x01, 0x31 }, Array.Empty<byte>());
            Assert.That(ste.PlaceCall("2624", "2422", null, p), Is.EqualTo((ushort)1));

            // Utility field: window 43 02 02, packet 42 07 07, then the reverse charging utility 01 01 (8 octets); the
            // user facility field is empty (00).
            byte[] expected =
            {
                0x10, 0x01, 0x0B, 0x44, 0x26, 0x24, 0x24, 0x22,
                0x08, 0x43, 0x02, 0x02, 0x42, 0x07, 0x07, 0x01, 0x01,
                0x00,
            };
            Assert.That(_sink.Last, Is.EqualTo(expected));
        }

        [Test]
        public void AReverseChargingUtility_ReachesTheSwitchAsTheX25Facility()
        {
            X25Layer ste = Ste(fromHighest: false);
            ste.ProcessPacket(X25PacketBuilder.CreateCallRequest(5, "2624", "2422",
                networkUtilities: new byte[] { 0x81, 0x00, 0x00, 0x01, 0x01, 0x01 }));

            Assert.That(ste.TryGetCallParameters(5, out X25CallParameters p), Is.True);
            Assert.That(p.TryGetFacility(0x01, out byte[] parameter), Is.True);
            Assert.That(parameter, Is.EqualTo(new byte[] { 0x01 }));
        }

        [Test]
        public void ARelayedCall_SendsReverseChargingOnce()
        {
            X25Layer ste = Ste(fromHighest: false);

            // A transit switch hands on both the utility field it received (with 01 01) and the facility it made from it.
            X25CallParameters p = new X25CallParameters(2, 2, 128, 128, new byte[] { 0x01, 0x01 },
                new byte[] { 0x81, 0x00, 0x00, 0x01, 0x01, 0x01 });
            ste.PlaceCall("2624", "2422", null, p);

            X25PacketInfo info = X25PacketParser.ParsePacket(_sink.Last, x75: true);
            Assert.That(X75Utilities.TryDecode(info.NetworkUtilities, out X75Utilities u), Is.EqualTo(X75UtilityError.None));
            Assert.That(u.PassThrough, Is.EqualTo(new byte[] { 0x01, 0x01 }), "once, not twice (Annex F a) row 15)");
            Assert.That(info.Facilities, Is.Empty);
        }

        [Test]
        public void AClearingCauseX75DoesNotHave_GoesOutAsNetworkCongestion()
        {
            X25Layer ste = Ste(fromHighest: false);
            ushort lci = ste.PlaceCall("2624");

            // 0x13 is the X.25 "local procedure error"; X.75 Table 13 has no such cause (4.2.3.1: change it to 05).
            Assert.That(ste.ClearCall(lci, 0x13, 0x00, null), Is.True);
            Assert.That(_sink.Last, Is.EqualTo(new byte[] { 0x10, 0x01, 0x13, 0x05, 0x00 }));

            // Number busy (01) is in Table 13 and goes as it is.
            lci = ste.PlaceCall("2624");
            Assert.That(ste.ClearCall(lci, 0x01, 0x00, null), Is.True);
            Assert.That(_sink.Last[3], Is.EqualTo(0x01));
        }

        [Test]
        public void AClearConfirmation_IsThreeOctets_EvenWithChargingToReport()
        {
            X25Layer ste = Ste(fromHighest: false);
            ste.ClearConfirmationFacilities = _ => new byte[] { 0xC1, 0x02, 0x00, 0x01 };
            ste.ProcessPacket(X25PacketBuilder.CreateCallRequest(5, "2624", "2422", networkUtilities: Array.Empty<byte>()));
            ste.AcceptCall(5, new X25CallParameters(2, 2, 128, 128, null, Array.Empty<byte>()));

            ste.ProcessPacket(X25PacketBuilder.CreateClearRequest(5, 0x00, 0x00));

            // X.75 Figure 8: header only.
            Assert.That(_sink.Last, Is.EqualTo(new byte[] { 0x10, 0x05, 0x17 }));
        }

        [Test]
        public void AnSteThatSaysItIsADte_IsRefused()
        {
            X25Config config = new X25Config { IsSte = true, IsDte = true };
            Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());
        }
    }
}
