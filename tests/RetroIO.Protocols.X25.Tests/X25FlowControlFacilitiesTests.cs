// =============================================================================
// X.25 flow control parameter negotiation facilities - packet size and window
// size - encoded and decoded per the standard's own field layout.
//
// Every expected byte below is derived from the text, not from what the code
// happens to produce:
//
//   X.25 (10/96) 7.2 Table 7-1  - facility class from bits 8 and 7 of the code
//                                 octet: 00 one parameter octet, 01 two,
//                                 10 three, 11 variable with a length octet.
//   X.25 (10/96) Table 7-2      - packet size 01000010, window size 01000011.
//   X.25 (10/96) 7.3.1.1        - packet size: called direction in bits 4-1 of
//                                 the first octet, calling direction in bits 4-1
//                                 of the second, bits 8-5 zero.
//   X.75 5.4.3.5                - those four bits are "the logarithm to base 2
//                                 of the maximum number of octets".
//   X.25 (10/96) 7.3.1.2        - window size: called direction in bits 7-1 of
//                                 the first octet, calling direction in bits 7-1
//                                 of the second, bit 8 zero, "a value of zero is
//                                 not allowed".
//   X.25 (10/96) 6.10 / 7.3.1.2 - the default window is 2, and every network
//                                 provides 2.
// =============================================================================

using System;

using NUnit.Framework;

using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.DeleteMe;

namespace RetroIO.Protocols.X25.Tests
{
    [TestFixture]
    public sealed class X25FlowControlFacilitiesTests
    {
        [Test]
        public void Encode_produces_the_field_layout_the_standard_specifies()
        {
            // Window 7 both ways, 128-octet packets both ways.
            byte[] facilities = X25FlowControlFacilities.Encode(7, 7, 128, 128);

            Assert.That(facilities, Is.EqualTo(new byte[]
            {
                0x42, 7, 7,   // packet size:  log2(128) = 7 in each direction
                0x43, 7, 7    // window size:  7 in each direction
            }), "the encoded facility field does not match X.25 7.3.1.1 / 7.3.1.2");
        }

        [Test]
        public void Encode_keeps_the_two_directions_in_the_order_the_standard_gives()
        {
            // Asymmetric on purpose: octet 1 is the CALLED direction, octet 2 the
            // CALLING direction. Swapping them is invisible on a symmetric call
            // and wrong on every other one.
            byte[] facilities = X25FlowControlFacilities.Encode(
                windowFromCalled: 3, windowFromCalling: 5,
                packetSizeFromCalled: 256, packetSizeFromCalling: 64);

            Assert.Multiple(() =>
            {
                Assert.That(facilities[1], Is.EqualTo(8), "packet size from called should be log2(256) = 8");
                Assert.That(facilities[2], Is.EqualTo(6), "packet size from calling should be log2(64) = 6");
                Assert.That(facilities[4], Is.EqualTo(3), "window from called");
                Assert.That(facilities[5], Is.EqualTo(5), "window from calling");
            });
        }

        [Test]
        public void Encode_rejects_a_window_of_zero()
        {
            // X.25 7.3.1.2: "A value of zero is not allowed."
            Assert.Throws<ArgumentOutOfRangeException>(() => X25FlowControlFacilities.Encode(0, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => X25FlowControlFacilities.Encode(2, 0));
        }

        [Test]
        public void Encode_rejects_a_packet_size_that_is_not_a_defined_value()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => X25FlowControlFacilities.Encode(2, 2, 100, 128));
        }

        [Test]
        public void Decode_round_trips_what_Encode_produced()
        {
            byte[] facilities = X25FlowControlFacilities.Encode(3, 5, 256, 64);

            X25FlowControlParameters p = X25FlowControlFacilities.Decode(facilities);

            Assert.Multiple(() =>
            {
                Assert.That(p.HasWindowSize, Is.True);
                Assert.That(p.HasPacketSize, Is.True);
                Assert.That(p.WindowFromCalled, Is.EqualTo(3));
                Assert.That(p.WindowFromCalling, Is.EqualTo(5));
                Assert.That(p.PacketSizeFromCalled, Is.EqualTo(256));
                Assert.That(p.PacketSizeFromCalling, Is.EqualTo(64));
            });
        }

        [Test]
        public void Decode_of_an_empty_field_gives_the_standard_defaults()
        {
            X25FlowControlParameters p = X25FlowControlFacilities.Decode(Array.Empty<byte>());

            // This is the case that matters most: a peer that negotiated nothing
            // authorises 2, not 7. Getting this wrong puts P(S) outside its window
            // and it resets the circuit (X.25 4.4.1.3).
            Assert.Multiple(() =>
            {
                Assert.That(p.HasWindowSize, Is.False);
                Assert.That(p.WindowFromCalling, Is.EqualTo(2), "the default window is 2 (X.25 6.10)");
                Assert.That(p.PacketSizeFromCalling, Is.EqualTo(128));
            });
        }

        [Test]
        public void Decode_steps_over_facilities_it_does_not_understand()
        {
            // A class A facility (one parameter octet), a class C one (three), a
            // class D one (a length octet then that many), and our window size
            // last. Walking by class is the only way to still be aligned when we
            // reach it - scanning for the byte 0x43 would have matched the 0x43
            // sitting inside the class C parameter below.
            byte[] facilities = new byte[]
            {
                0x01, 0xAA,                    // class A, one parameter octet
                0x80, 0x11, 0x43, 0x33,        // class C, three octets - one of them is 0x43
                0xC0, 0x02, 0xDE, 0xAD,        // class D, length 2
                0x43, 4, 6                     // window size: called 4, calling 6
            };

            X25FlowControlParameters p = X25FlowControlFacilities.Decode(facilities);

            Assert.Multiple(() =>
            {
                Assert.That(p.HasWindowSize, Is.True, "the window size facility was not found after the unknown ones");
                Assert.That(p.WindowFromCalled, Is.EqualTo(4));
                Assert.That(p.WindowFromCalling, Is.EqualTo(6));
            });
        }

        [Test]
        public void Decode_masks_the_reserved_top_bit_of_each_window_octet()
        {
            // X.25 7.3.1.2: "Bit 8 of each octet must be zero." A peer that sets it
            // anyway must not turn a window of 5 into 133.
            byte[] facilities = new byte[] { 0x43, 0x85, 0x83 };

            X25FlowControlParameters p = X25FlowControlFacilities.Decode(facilities);

            Assert.Multiple(() =>
            {
                Assert.That(p.WindowFromCalled, Is.EqualTo(5));
                Assert.That(p.WindowFromCalling, Is.EqualTo(3));
            });
        }

        [Test]
        public void Decode_survives_a_truncated_field()
        {
            // Claims two parameter octets, supplies one. Must not read past the end
            // and must not report a window it never saw.
            byte[] facilities = new byte[] { 0x43, 4 };

            X25FlowControlParameters p = X25FlowControlFacilities.Decode(facilities);

            Assert.That(p.HasWindowSize, Is.False);
            Assert.That(p.WindowFromCalling, Is.EqualTo(2), "a truncated field must fall back to the default");
        }

        [Test]
        public void Decode_treats_an_out_of_range_packet_size_code_as_the_default()
        {
            // log2 = 15 would be 32768 octets, which is not a defined value and is
            // far past any buffer here. X.25 defines 4 (16 octets) through 12.
            byte[] facilities = new byte[] { 0x42, 0x0F, 0x0F };

            X25FlowControlParameters p = X25FlowControlFacilities.Decode(facilities);

            Assert.That(p.PacketSizeFromCalled, Is.EqualTo(128));
            Assert.That(p.PacketSizeFromCalling, Is.EqualTo(128));
        }
    }
}
