//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The X.75 network utility field. Every expected octet is worked out by hand from CCITT X.75 (11/1988): the
    /// utility codes from Table 21, the class lengths from Figure 21, and each parameter from 5.4.3.
    /// </summary>
    [TestFixture]
    public class X75UtilitiesTests
    {
        // A call from network 2342 that crossed 2342 then 2080 (two transit network identifications, 5.3.1, each
        // 0x41 then four BCD digits, 5.4.3.1); call identifier 0x123456 (0x81, three octets, 5.4.3.2); throughput
        // class 10 from the calling STE in bits 4-1 and 9 from the called STE in bits 8-5 (0x02, 5.4.3.3) = 0x9A;
        // window 2 each way (0x43, 5.4.3.4); packet size 2^7 = 128 each way (0x42, 5.4.3.5).
        private static readonly byte[] TwoTransitNetworks =
        {
            0x41, 0x23, 0x42,
            0x41, 0x20, 0x80,
            0x81, 0x12, 0x34, 0x56,
            0x02, 0x9A,
            0x43, 0x02, 0x02,
            0x42, 0x07, 0x07,
        };

        [Test]
        public void ACallRequestField_IsReadFieldByField()
        {
            Assert.That(X75Utilities.TryDecode(TwoTransitNetworks, out X75Utilities u), Is.EqualTo(X75UtilityError.None));
            Assert.That(u.TransitNetworks, Is.EqualTo(new[] { "2342", "2080" }), "in the order the call crossed them");
            Assert.That(u.HasCallIdentifier, Is.True);
            Assert.That(u.CallIdentifier, Is.EqualTo(0x123456));
            Assert.That(u.HasThroughputClass, Is.True);
            Assert.That(u.ThroughputFromCalling, Is.EqualTo(10));
            Assert.That(u.ThroughputFromCalled, Is.EqualTo(9));
            Assert.That(u.WindowFromCalled, Is.EqualTo(2));
            Assert.That(u.WindowFromCalling, Is.EqualTo(2));
            Assert.That(u.PacketSizeLog2FromCalled, Is.EqualTo(7));
            Assert.That(u.PacketSizeLog2FromCalling, Is.EqualTo(7));
            Assert.That(u.ClearingNetwork, Is.Empty);
            Assert.That(u.PassThrough, Is.Empty);
        }

        [Test]
        public void ACallRequestField_IsBuiltOctetForOctet()
        {
            X75Utilities u = new X75Utilities
            {
                HasCallIdentifier = true,
                CallIdentifier = 0x123456,
                HasThroughputClass = true,
                ThroughputFromCalling = 10,
                ThroughputFromCalled = 9,
                HasWindowSize = true,
                WindowFromCalled = 2,
                WindowFromCalling = 2,
                HasPacketSize = true,
                PacketSizeLog2FromCalled = 7,
                PacketSizeLog2FromCalling = 7,
            };
            u.TransitNetworks.Add("2342");
            u.TransitNetworks.Add("2080");

            Assert.That(u.Encode(), Is.EqualTo(TwoTransitNetworks));
        }

        [Test]
        public void TheClearingNetwork_IsRead_AndWritten()
        {
            // 0x4A, then DNIC 2405 as BCD (5.4.3.9).
            byte[] field = { 0x4A, 0x24, 0x05 };
            Assert.That(X75Utilities.TryDecode(field, out X75Utilities u), Is.EqualTo(X75UtilityError.None));
            Assert.That(u.ClearingNetwork, Is.EqualTo("2405"));
            Assert.That(u.Encode(), Is.EqualTo(field));
        }

        [Test]
        public void UtilitiesNotInterpreted_AreCarriedUntouched_AfterTheOnesThatAre()
        {
            // NUI (class D 0xC6, length 3, "ABC"), RPOA selection 0x44 for 2080, an extended code (0xFF then a class B
            // code 0x41 - extended, so NOT a transit network), then the utility marker 0x00 0x00 and a bilateral 0x42
            // after it, which is not the X.75 packet size (5.3.18).
            byte[] field =
            {
                0xC6, 0x03, 0x41, 0x42, 0x43,
                0x81, 0x00, 0x00, 0x2A,
                0x44, 0x20, 0x80,
                0xFF, 0x41, 0x12, 0x34,
                0x00, 0x00, 0x42, 0x0F, 0x0F,
            };

            Assert.That(X75Utilities.TryDecode(field, out X75Utilities u), Is.EqualTo(X75UtilityError.None));
            Assert.That(u.CallIdentifier, Is.EqualTo(42));
            Assert.That(u.TransitNetworks, Is.Empty, "the extended 0x41 is another utility");
            Assert.That(u.HasPacketSize, Is.False, "the 0x42 after the marker is not X.75's");

            byte[] expected =
            {
                0x81, 0x00, 0x00, 0x2A,
                0xC6, 0x03, 0x41, 0x42, 0x43,
                0x44, 0x20, 0x80,
                0xFF, 0x41, 0x12, 0x34,
                0x00, 0x00, 0x42, 0x0F, 0x0F,
            };
            Assert.That(u.Encode(), Is.EqualTo(expected));
        }

        [TestCase(new byte[] { 0x81, 0x12, 0x34 }, X75UtilityError.Truncated, TestName = "Call identifier with two octets")]
        [TestCase(new byte[] { 0xC6, 0x05, 0x41 }, X75UtilityError.Truncated, TestName = "Class D longer than the field")]
        [TestCase(new byte[] { 0xFF }, X75UtilityError.Truncated, TestName = "Extension code with nothing after it")]
        [TestCase(new byte[] { 0x00 }, X75UtilityError.Truncated, TestName = "Marker with no parameter")]
        [TestCase(new byte[] { 0x41, 0x2A, 0x42 }, X75UtilityError.InvalidDigit, TestName = "Transit network digit A")]
        [TestCase(new byte[] { 0x4A, 0x24, 0x0F }, X75UtilityError.InvalidDigit, TestName = "Clearing network digit F")]
        [TestCase(new byte[] { 0x43, 0x82, 0x02 }, X75UtilityError.ReservedBitsSet, TestName = "Window with bit 8 set")]
        [TestCase(new byte[] { 0x42, 0x17, 0x07 }, X75UtilityError.ReservedBitsSet, TestName = "Packet size with bit 5 set")]
        [TestCase(new byte[] { 0x81, 0, 0, 1, 0x81, 0, 0, 2 }, X75UtilityError.RepeatedCode, TestName = "Two call identifiers")]
        [TestCase(new byte[] { 0x02, 0x11, 0x02, 0x22 }, X75UtilityError.RepeatedCode, TestName = "Two throughput classes")]
        public void AFieldTheCodingForbids_IsRefused(byte[] field, X75UtilityError expected)
        {
            Assert.That(X75Utilities.TryDecode(field, out _), Is.EqualTo(expected));
        }

        [Test]
        public void AFieldOver63Octets_IsRefused_BothWays()
        {
            // 21 transit networks are 63 octets: the most the field holds (4.2.1.5).
            byte[] full = new byte[63];
            for (int i = 0; i < 63; i += 3)
            {
                full[i] = 0x41;
                full[i + 1] = 0x23;
                full[i + 2] = 0x42;
            }

            Assert.That(X75Utilities.TryDecode(full, out X75Utilities u), Is.EqualTo(X75UtilityError.None));
            Assert.That(u.TransitNetworks.Count, Is.EqualTo(21));
            Assert.That(u.Encode(), Is.EqualTo(full));

            byte[] tooLong = new byte[64];
            Assert.That(X75Utilities.TryDecode(tooLong, out _), Is.EqualTo(X75UtilityError.TooLong));

            u.TransitNetworks.Add("2080");
            Assert.Throws<ArgumentOutOfRangeException>(() => u.Encode());
        }

        [Test]
        public void ValuesTheCodingCannotHold_AreRefusedWhenBuilding()
        {
            X75Utilities u = new X75Utilities { HasCallIdentifier = true, CallIdentifier = 0x1000000 };
            Assert.Throws<ArgumentOutOfRangeException>(() => u.Encode(), "over 24 bits");

            u = new X75Utilities();
            u.TransitNetworks.Add("234");
            Assert.Throws<ArgumentOutOfRangeException>(() => u.Encode(), "three digits");

            u = new X75Utilities();
            u.TransitNetworks.Add("23A2");
            Assert.Throws<ArgumentOutOfRangeException>(() => u.Encode(), "not a digit");

            u = new X75Utilities { HasWindowSize = true, WindowFromCalled = 128, WindowFromCalling = 2 };
            Assert.Throws<ArgumentOutOfRangeException>(() => u.Encode(), "window over 127");

            u = new X75Utilities { HasThroughputClass = true, ThroughputFromCalling = 16 };
            Assert.Throws<ArgumentOutOfRangeException>(() => u.Encode(), "throughput class over 15");
        }

        [Test]
        public void AnEmptyField_IsValid_AndBuildsEmpty()
        {
            Assert.That(X75Utilities.TryDecode(ReadOnlySpan<byte>.Empty, out X75Utilities u), Is.EqualTo(X75UtilityError.None));
            Assert.That(u.Encode(), Is.Empty);
        }
    }
}
