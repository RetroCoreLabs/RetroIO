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
//
// Written against the lenient wrapper that went with the retired engine on
// 2026-09-06 and carried over here unchanged in what they assert. The wrapper
// threw on a bad value; the live codec reports it as an X25FacilityError so a
// malformed field from a peer can be answered with a diagnostic rather than
// ending the call with an exception.
// =============================================================================

using System;

using NUnit.Framework;

using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Tests
{
    [TestFixture]
    public sealed class X25FacilitiesTests
    {
        [Test]
        public void Encode_produces_the_field_layout_the_standard_specifies()
        {
            // Window 7 both ways, 128-octet packets both ways.
            byte[] facilities = X25Facilities.ForRequest(window: 7, packetSize: 128).Encode();

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
            X25Facilities f = new X25Facilities
            {
                HasPacketSize = true,
                PacketSizeFromCalled = 256,
                PacketSizeFromCalling = 64,
                HasWindowSize = true,
                WindowFromCalled = 3,
                WindowFromCalling = 5,
            };

            byte[] facilities = f.Encode();

            Assert.Multiple(() =>
            {
                Assert.That(facilities[1], Is.EqualTo(8), "packet size from called should be log2(256) = 8");
                Assert.That(facilities[2], Is.EqualTo(6), "packet size from calling should be log2(64) = 6");
                Assert.That(facilities[4], Is.EqualTo(3), "window from called");
                Assert.That(facilities[5], Is.EqualTo(5), "window from calling");
            });
        }

        [Test]
        public void A_window_of_zero_is_refused_on_the_way_in()
        {
            // X.25 7.3.1.2: "A value of zero is not allowed."
            X25FacilityError error = X25Facilities.TryDecode(new byte[] { 0x43, 0, 2 }, out _);
            Assert.That(error, Is.EqualTo(X25FacilityError.WindowOutOfRange));

            error = X25Facilities.TryDecode(new byte[] { 0x43, 2, 0 }, out _);
            Assert.That(error, Is.EqualTo(X25FacilityError.WindowOutOfRange));
        }

        [Test]
        public void A_packet_size_outside_the_defined_range_is_refused_on_the_way_in()
        {
            // 7.3.1.1: the code is a logarithm, and only 4..12 are defined - 16 to
            // 4096 octets. A 100-octet packet size cannot even be expressed.
            X25FacilityError error = X25Facilities.TryDecode(new byte[] { 0x42, 3, 7 }, out _);
            Assert.That(error, Is.EqualTo(X25FacilityError.PacketSizeOutOfRange));

            error = X25Facilities.TryDecode(new byte[] { 0x42, 7, 13 }, out _);
            Assert.That(error, Is.EqualTo(X25FacilityError.PacketSizeOutOfRange));
        }

        [Test]
        public void Decode_round_trips_what_Encode_produced()
        {
            X25Facilities sent = new X25Facilities
            {
                HasPacketSize = true,
                PacketSizeFromCalled = 256,
                PacketSizeFromCalling = 64,
                HasWindowSize = true,
                WindowFromCalled = 3,
                WindowFromCalling = 5,
            };

            X25FacilityError error = X25Facilities.TryDecode(sent.Encode(), out X25Facilities p);

            Assert.Multiple(() =>
            {
                Assert.That(error, Is.EqualTo(X25FacilityError.None));
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
            X25FacilityError error = X25Facilities.TryDecode(Array.Empty<byte>(), out X25Facilities p);

            // This is the case that matters most: a peer that negotiated nothing
            // authorises 2, not 7. Getting this wrong puts P(S) outside its window
            // and it resets the circuit (X.25 4.4.1.3).
            Assert.Multiple(() =>
            {
                Assert.That(error, Is.EqualTo(X25FacilityError.None));
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

            X25FacilityError error = X25Facilities.TryDecode(facilities, out X25Facilities p);

            Assert.Multiple(() =>
            {
                Assert.That(error, Is.EqualTo(X25FacilityError.None));
                Assert.That(p.HasWindowSize, Is.True, "the window size facility was not found after the unknown ones");
                Assert.That(p.WindowFromCalled, Is.EqualTo(4));
                Assert.That(p.WindowFromCalling, Is.EqualTo(6));
            });
        }

        [Test]
        public void Facilities_it_does_not_understand_come_back_out_untouched()
        {
            // A switch that re-sends the field must not drop what it did not read:
            // the elements it does not act on are carried through in order.
            byte[] facilities = new byte[]
            {
                0x01, 0xAA,
                0x43, 4, 6,
                0xC0, 0x02, 0xDE, 0xAD,
            };

            X25FacilityError error = X25Facilities.TryDecode(facilities, out X25Facilities p);

            Assert.That(error, Is.EqualTo(X25FacilityError.None));
            Assert.That(p.PassThrough, Is.EqualTo(new byte[] { 0x01, 0xAA, 0xC0, 0x02, 0xDE, 0xAD }));
        }

        [Test]
        public void An_element_running_past_the_end_of_the_field_is_refused()
        {
            // 7.1: the class says how many parameter octets follow. A field that
            // ends inside one is malformed, not a field with a short last element.
            X25FacilityError error = X25Facilities.TryDecode(new byte[] { 0x43, 4 }, out _);
            Assert.That(error, Is.EqualTo(X25FacilityError.Truncated));
        }
    }
}
