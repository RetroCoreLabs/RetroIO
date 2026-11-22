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
