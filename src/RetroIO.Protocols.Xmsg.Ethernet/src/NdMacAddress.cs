//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Ethernet
{
    /// <summary>
    /// Protocol family selected by the top two bits of the physical user code (MAC byte 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// CARVED FACT - <c>ND-60.197.01 EN Ethernet Basic Software Programmer Guide</c> section 2.4:
    /// the controller <b>does not check bits 7-6 of the destination address</b>, so
    /// "each Ethernet Interface has in fact four physical addresses". DIX (TCP/IP) and
    /// COSMOS traffic were designed to arrive at the SAME interface under different physical user
    /// addresses.
    /// </para>
    /// <para>
    /// UNKNOWN: every COSMOS frame captured on 2026-08-01 carries physical user <c>0x00</c>, i.e.
    /// bits 7-6 = <see cref="Ieee"/>, NOT the <see cref="NdCosmos"/> the table would suggest.
    /// Because the hardware ignores those bits when matching, both work, so no capture can
    /// distinguish "COSMOS does not use the encoding" from "COSMOS uses it only when more than one
    /// family is attached". Do not assume either.
    /// </para>
    /// </remarks>
    public enum NdPhysicalUserFamily
    {
        /// <summary>
        /// Bits 7-6 = <c>00</c>. IEEE. This is what every observed COSMOS frame uses.
        /// </summary>
        Ieee = 0,

        /// <summary>
        /// Bits 7-6 = <c>01</c>. DIX (Ethernet II framing, used by the TCP/IP product).
        /// </summary>
        Dix = 1,

        /// <summary>
        /// Bits 7-6 = <c>10</c>. A user-written protocol.
        /// </summary>
        UserProtocol = 2,

        /// <summary>
        /// Bits 7-6 = <c>11</c>. ND (COSMOS).
        /// </summary>
        NdCosmos = 3
    }

    /// <summary>
    /// A Norsk Data Ethernet station address: the ND vendor prefix, the ND system number, and a
    /// physical user code.
    /// </summary>
    /// <remarks>
    /// <para><b>Layout</b></para>
    /// CARVED FACT - <c>ND-60.197.01</c> section 2.4, confirmed on the wire 2026-08-01:
    ///  - bytes 0-2: <c>08 00 26</c>, the Xerox/ND vendor prefix (OUI).
    ///  - bytes 3-4: the ND system number, 16 bits, stored in <b>REVERSED byte order</b>.
    ///  - byte 5: the physical user code (see <see cref="NdPhysicalUserFamily"/>).
    /// <para>
    /// The manual's words: "ND system numbers, in the range 0 - 177777B, are contained (in
    /// reversed order) in characters 4-5 of the address string. Physical user numbers are contained
    /// in character 6." So the field is a full 16 bits - <c>0</c> to <c>65535</c>.
    /// </para>
    /// <para><b>The byte order is OPPOSITE to the SINTRAN header - this is the whole point of the type.</b></para>
    /// <para>
    /// In the SINTRAN header a node number is big-endian (node 100 = <c>00 64</c>); in the MAC the
    /// same number is little-endian (<c>64 00</c>). Nodes numbered below 256 make an 8-bit reading
    /// look correct - node 100 gives <c>08 00 26 64 00 00</c> and node 102 gives
    /// <c>08 00 26 66 00 00</c>, which are consistent with BOTH readings. That is why the
    /// conversion lives in a named, tested type instead of inline shifts, and why the tests use a
    /// system number above 255.
    /// </para>
    /// <para><b>The card supplies nothing</b></para>
    /// <para>
    /// The ND Ethernet II controller has no address PROM and no EPROM. SINTRAN writes these six
    /// bytes into card DRAM at <c>LNMAPHYSIC</c> (<c>0x1885E</c>) with host command 0 at bring-up.
    /// The MAC is entirely a software construction.
    /// </para>
    /// <para>
    /// Doc: <c>SINTRAN/XMSG/DOC/COSMOS-ETHERNET-TRANSPORT-FRAMING-2026-08-01.md</c> section 3 and
    /// <c>SINTRAN/XMSG/DOC/COSMOS-RE/MAC-ADDRESS-ASSIGNMENT-MULTI-CARD.md</c>.
    /// </para>
    /// </remarks>
    public readonly struct NdMacAddress : IEquatable<NdMacAddress>
    {
        /// <summary>
        /// Length of an Ethernet station address in bytes.
        /// </summary>
        public const int Length = 6;

        /// <summary>
        /// First byte of the Xerox/ND vendor prefix.
        /// </summary>
        public const byte Oui0 = 0x08;

        /// <summary>
        /// Second byte of the Xerox/ND vendor prefix.
        /// </summary>
        public const byte Oui1 = 0x00;

        /// <summary>
        /// Third byte of the Xerox/ND vendor prefix.
        /// </summary>
        public const byte Oui2 = 0x26;

        private readonly byte _b0;
        private readonly byte _b1;
        private readonly byte _b2;
        private readonly byte _b3;
        private readonly byte _b4;
        private readonly byte _b5;

        /// <summary>
        /// Initialises an address from six raw bytes, whatever they are.
        /// </summary>
        /// <param name="bytes">
        /// Exactly <see cref="Length"/> bytes, in wire order.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="bytes"/> is not <see cref="Length"/> bytes long.
        /// </exception>
        public NdMacAddress(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != Length)
            {
                throw new ArgumentException($"An Ethernet address is {Length} bytes, got {bytes.Length}.", nameof(bytes));
            }

            _b0 = bytes[0];
            _b1 = bytes[1];
            _b2 = bytes[2];
            _b3 = bytes[3];
            _b4 = bytes[4];
            _b5 = bytes[5];
        }

        /// <summary>
        /// Builds the station address for an ND system number.
        /// </summary>
        /// <param name="systemNumber">
        /// The ND system number, the full 16-bit range (the manual's <c>0 - 177777B</c>).
        /// </param>
        /// <param name="physicalUser">
        /// The physical user code placed in byte 5. Defaults to <c>0</c>, which is what every
        /// observed COSMOS frame uses.
        /// </param>
        /// <returns>
        /// The address <c>08 00 26</c> followed by the system number in reversed byte order and
        /// then <paramref name="physicalUser"/>.
        /// </returns>
        /// <remarks>
        /// Reversed byte order means the LOW byte of the system number lands in MAC byte 3 and the
        /// HIGH byte in byte 4. System 100 becomes <c>08 00 26 64 00 00</c>; system 9999
        /// (<c>0x270F</c>) becomes <c>08 00 26 0F 27 00</c>.
        /// </remarks>
        public static NdMacAddress FromSystemNumber(ushort systemNumber, byte physicalUser = 0)
        {
            Span<byte> raw = stackalloc byte[Length];
            raw[0] = Oui0;
            raw[1] = Oui1;
            raw[2] = Oui2;
            raw[3] = (byte)(systemNumber & 0xFF);          // LOW byte first - "reversed order"
            raw[4] = (byte)((systemNumber >> 8) & 0xFF);    // HIGH byte second
            raw[5] = physicalUser;
            return new NdMacAddress(raw);
        }

        /// <summary>
        /// Gets a value indicating whether the address carries the ND vendor prefix.
        /// </summary>
        public bool HasNdVendorPrefix => _b0 == Oui0 && _b1 == Oui1 && _b2 == Oui2;

        /// <summary>
        /// Gets the ND system number encoded in bytes 3-4, undoing the reversed byte order.
        /// </summary>
        /// <remarks>
        /// This value is only meaningful when <see cref="HasNdVendorPrefix"/> is true; use
        /// <see cref="TryGetSystemNumber"/> when the address may not be an ND one.
        /// </remarks>
        public ushort SystemNumber => (ushort)(_b3 | (_b4 << 8));

        /// <summary>
        /// Gets the physical user code from byte 5.
        /// </summary>
        public byte PhysicalUser => _b5;

        /// <summary>
        /// Gets the protocol family selected by the top two bits of <see cref="PhysicalUser"/>.
        /// </summary>
        public NdPhysicalUserFamily Family => (NdPhysicalUserFamily)((_b5 >> 6) & 0x03);

        /// <summary>
        /// Gets the ND system number when this is an ND address.
        /// </summary>
        /// <param name="systemNumber">
        /// Receives the system number, or <c>0</c> when the address is not an ND one.
        /// </param>
        /// <returns>
        /// True when the address carries the ND vendor prefix and the system number is valid.
        /// </returns>
        public bool TryGetSystemNumber(out ushort systemNumber)
        {
            if (!HasNdVendorPrefix)
            {
                systemNumber = 0;
                return false;
            }

            systemNumber = SystemNumber;
            return true;
        }

        /// <summary>
        /// Writes the six address bytes in wire order.
        /// </summary>
        /// <param name="destination">
        /// The buffer to write into; must be at least <see cref="Length"/> bytes.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="destination"/> is shorter than <see cref="Length"/>.
        /// </exception>
        public void Write(Span<byte> destination)
        {
            if (destination.Length < Length)
            {
                throw new ArgumentException($"Need {Length} bytes to write an Ethernet address.", nameof(destination));
            }

            destination[0] = _b0;
            destination[1] = _b1;
            destination[2] = _b2;
            destination[3] = _b3;
            destination[4] = _b4;
            destination[5] = _b5;
        }

        /// <summary>
        /// Indicates whether this address equals another.
        /// </summary>
        /// <param name="other">
        /// The address to compare with.
        /// </param>
        /// <returns>
        /// True when all six bytes match.
        /// </returns>
        public bool Equals(NdMacAddress other)
        {
            return _b0 == other._b0 && _b1 == other._b1 && _b2 == other._b2
                && _b3 == other._b3 && _b4 == other._b4 && _b5 == other._b5;
        }

        /// <summary>
        /// Indicates whether this address equals another object.
        /// </summary>
        /// <param name="obj">
        /// The object to compare with.
        /// </param>
        /// <returns>
        /// True when <paramref name="obj"/> is an equal <see cref="NdMacAddress"/>.
        /// </returns>
        public override bool Equals(object? obj)
        {
            return obj is NdMacAddress other && Equals(other);
        }

        /// <summary>
        /// Returns a hash code for this address.
        /// </summary>
        /// <returns>
        /// A hash derived from all six bytes.
        /// </returns>
        public override int GetHashCode()
        {
            int hash = _b0;
            hash = (hash << 5) ^ _b1;
            hash = (hash << 5) ^ _b2;
            hash = (hash << 5) ^ _b3;
            hash = (hash << 5) ^ _b4;
            hash = (hash << 5) ^ _b5;
            return hash;
        }

        /// <summary>
        /// Returns the conventional colon-separated hexadecimal form.
        /// </summary>
        /// <returns>
        /// The address as <c>xx:xx:xx:xx:xx:xx</c>.
        /// </returns>
        public override string ToString()
        {
            return string.Create(17, this, static (span, mac) =>
            {
                WriteHexPair(span, 0, mac._b0);
                span[2] = ':';
                WriteHexPair(span, 3, mac._b1);
                span[5] = ':';
                WriteHexPair(span, 6, mac._b2);
                span[8] = ':';
                WriteHexPair(span, 9, mac._b3);
                span[11] = ':';
                WriteHexPair(span, 12, mac._b4);
                span[14] = ':';
                WriteHexPair(span, 15, mac._b5);
            });
        }

        /// <summary>
        /// Writes one byte as two lowercase hexadecimal characters.
        /// </summary>
        /// <param name="span">
        /// The destination character span.
        /// </param>
        /// <param name="offset">
        /// The index of the first character to write.
        /// </param>
        /// <param name="value">
        /// The byte to format.
        /// </param>
        private static void WriteHexPair(Span<char> span, int offset, byte value)
        {
            const string Digits = "0123456789abcdef";
            span[offset] = Digits[(value >> 4) & 0x0F];
            span[offset + 1] = Digits[value & 0x0F];
        }
    }
}
