//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// X.75 call set-up and clearing packets between signalling terminals: the network utility length and field sit
    /// between the address block and the facility length. Every expected octet is worked out by hand from CCITT X.75
    /// (11/1988) 4.1 and 4.2 and the packet figures there.
    /// </summary>
    [TestFixture]
    public class X75PacketTests
    {
        // Channel 1, modulo 8 (GFI 0001, so octet 1 = 0x10), Call Request type 0x0B. Called 23420100 and calling
        // 20800001, 8 digits each: address lengths 0x88, then 16 BCD digits in 8 octets. Utilities: transit network
        // 2342 (41 23 42) and call identifier 00 00 01 (81 00 00 01), 7 octets behind a length octet 07. No user
        // facilities (facility length 00) and no call user data.
        private static readonly byte[] CallRequest =
        {
            0x10, 0x01, 0x0B,
            0x88, 0x23, 0x42, 0x01, 0x00, 0x20, 0x80, 0x00, 0x01,
            0x07, 0x41, 0x23, 0x42, 0x81, 0x00, 0x00, 0x01,
            0x00,
        };

        private static readonly byte[] Utilities = { 0x41, 0x23, 0x42, 0x81, 0x00, 0x00, 0x01 };

        [Test]
        public void ACallRequest_IsBuiltWithTheUtilityFieldAfterTheAddresses()
        {
            byte[] packet = X25PacketBuilder.CreateCallRequest(1, "23420100", "20800001", networkUtilities: Utilities);
            Assert.That(packet, Is.EqualTo(CallRequest));
        }

        [Test]
        public void ACallRequest_IsReadWithTheUtilityFieldAfterTheAddresses()
        {
            X25PacketInfo info = X25PacketParser.ParsePacket(CallRequest, x75: true);
            Assert.That(info.ParseError, Is.Null);
            Assert.That(info.PacketType, Is.EqualTo(X25PacketType.CallRequest));
            Assert.That(info.CalledAddress, Is.EqualTo("23420100"));
            Assert.That(info.CallingAddress, Is.EqualTo("20800001"));
            Assert.That(info.NetworkUtilities, Is.EqualTo(Utilities));
            Assert.That(info.Facilities, Is.Empty);
            Assert.That(info.CallUserData, Is.Empty);

            Assert.That(X75Utilities.TryDecode(info.NetworkUtilities, out X75Utilities u), Is.EqualTo(X75UtilityError.None));
            Assert.That(u.TransitNetworks, Is.EqualTo(new[] { "2342" }));
            Assert.That(u.CallIdentifier, Is.EqualTo(1));
        }

        [Test]
        public void AnX25CallRequest_IsUnchanged_WhenNoUtilitiesAreGiven()
        {
            // The same call as X.25: no utility octets, the facility length straight after the addresses.
            byte[] expected =
            {
                0x10, 0x01, 0x0B,
                0x88, 0x23, 0x42, 0x01, 0x00, 0x20, 0x80, 0x00, 0x01,
                0x00,
            };
            Assert.That(X25PacketBuilder.CreateCallRequest(1, "23420100", "20800001"), Is.EqualTo(expected));
            Assert.That(X25PacketParser.ParsePacket(expected).NetworkUtilities, Is.Empty);
        }

        [Test]
        public void ACallConnected_AlwaysCarriesTheUtilityField()
        {
            // Call Connected type 0x0F. X.75 5.4.1: the field is "present in all call request and call connected
            // packets": address lengths 00, utility length 03, window 2/2 (43 02 02), facility length 00.
            byte[] expected = { 0x10, 0x01, 0x0F, 0x00, 0x03, 0x43, 0x02, 0x02, 0x00 };
            byte[] packet = X25PacketBuilder.CreateCallAccepted(1, networkUtilities: new byte[] { 0x43, 0x02, 0x02 });
            Assert.That(packet, Is.EqualTo(expected));

            X25PacketInfo info = X25PacketParser.ParsePacket(expected, x75: true);
            Assert.That(info.ParseError, Is.Null);
            Assert.That(info.NetworkUtilities, Is.EqualTo(new byte[] { 0x43, 0x02, 0x02 }));

            // An empty field still has its length octet: 00, 00, 00.
            byte[] empty = X25PacketBuilder.CreateCallAccepted(1, networkUtilities: System.Array.Empty<byte>());
            Assert.That(empty, Is.EqualTo(new byte[] { 0x10, 0x01, 0x0F, 0x00, 0x00, 0x00 }));
        }

        [Test]
        public void AThreeOctetCallConnected_IsRefusedAsX75()
        {
            X25PacketInfo info = X25PacketParser.ParsePacket(new byte[] { 0x10, 0x01, 0x0F }, x75: true);
            Assert.That(info.ParseError, Is.Not.Null);
            Assert.That(X25PacketParser.ParsePacket(new byte[] { 0x10, 0x01, 0x0F }).ParseError, Is.Null, "fine in X.25");
        }

        [Test]
        public void AClearRequest_CarriesTheClearingNetwork_InTheExtendedFormat()
        {
            // Clear Request type 0x13, cause 05 "Network congestion" (Table 13/X.75), diagnostic 00; extended format
            // (4.2.3.3, Figure 7): address lengths 00, utility length 03, clearing network 2080 (4A 20 80), facility
            // length 00.
            byte[] expected = { 0x10, 0x01, 0x13, 0x05, 0x00, 0x00, 0x03, 0x4A, 0x20, 0x80, 0x00 };
            byte[] packet = X25PacketBuilder.CreateClearRequest(1, 0x05, 0x00, networkUtilities: new byte[] { 0x4A, 0x20, 0x80 });
            Assert.That(packet, Is.EqualTo(expected));

            X25PacketInfo info = X25PacketParser.ParsePacket(expected, x75: true);
            Assert.That(info.ParseError, Is.Null);
            Assert.That(info.ClearingCause, Is.EqualTo(0x05));
            Assert.That(info.NetworkUtilities, Is.EqualTo(new byte[] { 0x4A, 0x20, 0x80 }));
        }

        [Test]
        public void AClearRequestWithNothingExtra_StaysInTheBasicFormat()
        {
            byte[] packet = X25PacketBuilder.CreateClearRequest(1, 0x05, 0x00, networkUtilities: System.Array.Empty<byte>());
            Assert.That(packet, Is.EqualTo(new byte[] { 0x10, 0x01, 0x13, 0x05, 0x00 }));
        }

        [Test]
        public void AClearConfirmation_IsThreeOctets()
        {
            // X.75 Figure 8: the header and nothing else.
            Assert.That(X25PacketParser.ParsePacket(new byte[] { 0x10, 0x01, 0x17 }, x75: true).ParseError, Is.Null);
            Assert.That(X25PacketParser.ParsePacket(new byte[] { 0x10, 0x01, 0x17, 0x00, 0x00 }, x75: true).ParseError, Is.Not.Null);
        }

        [Test]
        public void AUtilityLengthOctetWithBit7Set_IsRefused()
        {
            // 4.2.1.4: bits 8 and 7 of the length octet are 0. 0x47 has bit 7 set.
            byte[] packet = (byte[])CallRequest.Clone();
            packet[12] = 0x47;
            Assert.That(X25PacketParser.ParsePacket(packet, x75: true).ParseError, Is.Not.Null);
        }

        [Test]
        public void AUtilityFieldLongerThanThePacket_IsRefused()
        {
            byte[] packet = (byte[])CallRequest.Clone();
            packet[12] = 0x09;
            Assert.That(X25PacketParser.ParsePacket(packet, x75: true).ParseError, Is.Not.Null);
        }

        [Test]
        public void AUtilityFieldOver63Octets_IsNotBuilt()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                X25PacketBuilder.CreateCallRequest(1, "1", "2", networkUtilities: new byte[64]));
        }
    }
}
