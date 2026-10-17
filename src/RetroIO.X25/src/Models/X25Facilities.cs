//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// Why a facility field was rejected. <see cref="None"/> means it decoded.
    /// </summary>
    public enum X25FacilityError
    {
        /// <summary>
        /// The field decoded.
        /// </summary>
        None,

        /// <summary>
        /// An element's parameter octets run past the end of the field (X.25 7.1, Table 7-1).
        /// </summary>
        Truncated,

        /// <summary>
        /// A window size of 0, or above 7 with modulo 8 numbering (7.3.1.2).
        /// </summary>
        WindowOutOfRange,

        /// <summary>
        /// A packet size code outside 4..12 (7.3.1.1).
        /// </summary>
        PacketSizeOutOfRange,

        /// <summary>
        /// A bit the clause says "must be zero" is set (bits 8-5 of a packet size octet, bit 8 of a window octet).
        /// </summary>
        ReservedBitsSet,

        /// <summary>
        /// The same X.25 facility code appears twice ("Facility code repeated", Annex E 66).
        /// </summary>
        RepeatedCode,
    }

    /// <summary>
    /// The facility field of a call set-up or clearing packet, as this stack reads and writes it:
    /// the two flow control facilities it acts on, decoded and range-checked, and every other element
    /// carried through untouched in the order it arrived.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ITU-T X.25 (10/96) clause 7.1 lays out the field: elements whose first octet is a facility
    /// code, the code's bits 8 and 7 giving the parameter length (Table 7-1: 00 one octet, 01 two,
    /// 10 three, 11 a length octet then that many). Two constructs interrupt that walk and both were
    /// misread by the codec this one replaces:
    /// </para>
    ///  - the marker - code 0x00 with one parameter octet - "used to separate requests for X.25 facilities ... from other categories". Nothing after a marker is an X.25 facility. The old codec read the marker as a class-A code and went on interpreting national facilities as X.25 ones, so a national 0x42 after the marker became our packet size (the C emulator's F-01-01);
    ///  - the extension - code 0xFF, "reserved for extension of the facility code. The octet following this octet indicates an extended facility code having the format A, B, C and D as defined above. Repetition of facility code 11111111 is permitted." The old codec read 0xFF as class D and the next octet as its length (F-01-02).
    /// <para>
    /// What is interpreted. Only packet size (0x42, 7.3.1.1) and window size (0x43, 7.3.1.2),
    /// because those are the two the engine changes its behaviour on. Everything else - reverse
    /// charging and fast select (0x01), closed user groups (0x03 / 0x47), NUI, charging, address
    /// extensions, markers and all that follows them, extended codes - is kept byte for byte in
    /// <see cref="PassThrough"/> and re-emitted after the interpreted elements. That is
    /// what a switch needs: a Call Request must
    /// leave the other side with the facilities it arrived with. Decided 2026-09-06; typed access to
    /// more facilities can be added without changing the bytes.
    /// </para>
    /// <para>
    /// What is rejected. A truncated element, a window of 0 or above 7 (this stack runs modulo
    /// 8), a packet size code outside 4..12, a set bit the clause says must be zero, and a repeated
    /// X.25 code. The old codec accepted window 0 (which blocks sending for ever) and window 8 (which
    /// never closes) and turned an invalid packet size into 128 silently (F-01-04). The caller decides
    /// what a rejection means - a Clear with "invalid facility request" on a Call Request, a Clear on
    /// a Call Connected.
    /// </para>
    /// <para>
    /// Absent facilities take the network defaults: packet size 128 ("All Administrations will
    /// provide a packet size of 128", 7.3.1.1) and window 2 ("All Administrations will provide a
    /// window size of 2", 7.3.1.2). <see cref="HasPacketSize"/> and <see cref="HasWindowSize"/> say
    /// whether a value was carried or defaulted.
    /// </para>
    /// </remarks>
    public sealed class X25Facilities
    {
        /// <summary>
        /// Facility code for packet size negotiation (X.25 Table 7-2: 01000010).
        /// </summary>
        public const byte PacketSizeCode = 0x42;

        /// <summary>
        /// Facility code for window size negotiation (X.25 Table 7-2: 01000011).
        /// </summary>
        public const byte WindowSizeCode = 0x43;

        /// <summary>
        /// The marker code (X.25 7.1: "The first octet of the marker is a facility code field and is set to zero").
        /// </summary>
        public const byte MarkerCode = 0x00;

        /// <summary>
        /// The extension code (X.25 7.1: "Facility code 11111111 is reserved for extension").
        /// </summary>
        public const byte ExtensionCode = 0xFF;

        /// <summary>
        /// X.25 7.3.1.1: network default packet size.
        /// </summary>
        public const int DefaultPacketSize = 128;

        /// <summary>
        /// X.25 7.3.1.2: network default window size.
        /// </summary>
        public const byte DefaultWindowSize = 2;

        /// <summary>
        /// Smallest packet size code (2^4 = 16 octets).
        /// </summary>
        public const int MinPacketSizeLog2 = 4;

        /// <summary>
        /// Largest packet size code (2^12 = 4096 octets).
        /// </summary>
        public const int MaxPacketSizeLog2 = 12;

        /// <summary>
        /// Largest window with modulo 8 sequence numbering.
        /// </summary>
        public const byte MaxWindowModulo8 = 7;

        /// <summary>
        /// Packet size for the direction from the called DTE (first parameter octet of 0x42).
        /// </summary>
        public int PacketSizeFromCalled { get; set; } = DefaultPacketSize;

        /// <summary>
        /// Packet size for the direction from the calling DTE (second parameter octet of 0x42).
        /// </summary>
        public int PacketSizeFromCalling { get; set; } = DefaultPacketSize;

        /// <summary>
        /// Window for the direction from the called DTE (first parameter octet of 0x43).
        /// </summary>
        public byte WindowFromCalled { get; set; } = DefaultWindowSize;

        /// <summary>
        /// Window for the direction from the calling DTE (second parameter octet of 0x43).
        /// </summary>
        public byte WindowFromCalling { get; set; } = DefaultWindowSize;

        /// <summary>
        /// True when a packet size facility was carried; false when the defaults apply.
        /// </summary>
        public bool HasPacketSize { get; set; }

        /// <summary>
        /// True when a window size facility was carried; false when the defaults apply.
        /// </summary>
        public bool HasWindowSize { get; set; }

        /// <summary>
        /// Every element that was not interpreted, byte for byte and in arrival order: unknown X.25
        /// codes, extended codes with their 0xFF prefixes, and any marker together with everything
        /// after it. Re-emitted unchanged by <see cref="Encode"/>.
        /// </summary>
        public byte[] PassThrough { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Decodes a facility field.
        /// </summary>
        /// <param name="field">
        /// The facility field octets (without the facility length octet that precedes them in the
        /// packet).
        /// </param>
        /// <param name="facilities">
        /// The decoded facilities when the result is <see cref="X25FacilityError.None"/>; otherwise
        /// whatever had been decoded before the error, for diagnostics only.
        /// </param>
        /// <returns>
        /// <see cref="X25FacilityError.None"/> when the field is valid, else the first problem found.
        /// </returns>
        public static X25FacilityError TryDecode(ReadOnlySpan<byte> field, out X25Facilities facilities)
        {
            facilities = new X25Facilities();

            // Pass-through is at most the whole field; assemble on the stack and copy once at the end.
            Span<byte> passThrough = field.Length <= 256 ? stackalloc byte[field.Length] : new byte[field.Length];
            int passLen = 0;

            int i = 0;
            while (i < field.Length)
            {
                byte code = field[i];

                // Marker: this element and everything after it belongs to another category.
                if (code == MarkerCode)
                {
                    if (i + 2 > field.Length)
                    {
                        return X25FacilityError.Truncated;
                    }

                    field.Slice(i).CopyTo(passThrough.Slice(passLen));
                    passLen += field.Length - i;
                    i = field.Length;
                    break;
                }

                // Extension: skip every 0xFF, then size the element by the EXTENDED code's class.
                int elementStart = i;
                bool extended = false;
                while (i < field.Length && field[i] == ExtensionCode)
                {
                    extended = true;
                    i++;
                }

                if (i >= field.Length)
                {
                    return X25FacilityError.Truncated;
                }

                byte effectiveCode = field[i];
                int paramStart = i + 1;
                int paramLength;
                switch (effectiveCode & 0xC0)
                {
                    case 0x00: paramLength = 1; break;
                    case 0x40: paramLength = 2; break;
                    case 0x80: paramLength = 3; break;
                    default:
                        if (paramStart >= field.Length)
                        {
                            return X25FacilityError.Truncated;
                        }

                        paramLength = field[paramStart];
                        paramStart++;
                        break;
                }

                int elementEnd = paramStart + paramLength;
                if (elementEnd > field.Length)
                {
                    return X25FacilityError.Truncated;
                }

                if (!extended && effectiveCode == PacketSizeCode)
                {
                    if (facilities.HasPacketSize)
                    {
                        return X25FacilityError.RepeatedCode;
                    }

                    X25FacilityError e = DecodePacketSize(field[paramStart], field[paramStart + 1], facilities);
                    if (e != X25FacilityError.None)
                    {
                        return e;
                    }
                }
                else if (!extended && effectiveCode == WindowSizeCode)
                {
                    if (facilities.HasWindowSize)
                    {
                        return X25FacilityError.RepeatedCode;
                    }

                    X25FacilityError e = DecodeWindowSize(field[paramStart], field[paramStart + 1], facilities);
                    if (e != X25FacilityError.None)
                    {
                        return e;
                    }
                }
                else
                {
                    field.Slice(elementStart, elementEnd - elementStart).CopyTo(passThrough.Slice(passLen));
                    passLen += elementEnd - elementStart;
                }

                i = elementEnd;
            }

            facilities.PassThrough = passLen == 0 ? Array.Empty<byte>() : passThrough.Slice(0, passLen).ToArray();
            return X25FacilityError.None;
        }

        /// <summary>
        /// Encodes the facilities: packet size, then window size (each only when
        /// <see cref="HasPacketSize"/> / <see cref="HasWindowSize"/> is set), then the pass-through
        /// bytes unchanged. X.25 7.1: "requests for X.25 facilities must precede the other requests".
        /// </summary>
        /// <returns>
        /// The facility field octets, without the length octet.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// A window or packet size to encode is outside what the clauses allow.
        /// </exception>
        public byte[] Encode()
        {
            int length = (HasPacketSize ? 3 : 0) + (HasWindowSize ? 3 : 0) + PassThrough.Length;
            byte[] field = new byte[length];
            int pos = 0;

            if (HasPacketSize)
            {
                field[pos++] = PacketSizeCode;
                field[pos++] = PacketSizeToLog2(PacketSizeFromCalled);
                field[pos++] = PacketSizeToLog2(PacketSizeFromCalling);
            }

            if (HasWindowSize)
            {
                field[pos++] = WindowSizeCode;
                field[pos++] = CheckWindow(WindowFromCalled);
                field[pos++] = CheckWindow(WindowFromCalling);
            }

            PassThrough.AsSpan().CopyTo(field.AsSpan(pos));
            return field;
        }

        /// <summary>
        /// Builds the flow control request this stack puts in a Call Request.
        /// </summary>
        public static X25Facilities ForRequest(byte window, int packetSize)
        {
            return new X25Facilities
            {
                HasPacketSize = true,
                PacketSizeFromCalled = packetSize,
                PacketSizeFromCalling = packetSize,
                HasWindowSize = true,
                WindowFromCalled = window,
                WindowFromCalling = window,
            };
        }

        private static X25FacilityError DecodePacketSize(byte fromCalled, byte fromCalling, X25Facilities into)
        {
            // 7.3.1.1: "Bits 8, 7, 6 and 5 of each octet must be zero."
            if ((fromCalled & 0xF0) != 0 || (fromCalling & 0xF0) != 0)
            {
                return X25FacilityError.ReservedBitsSet;
            }

            int a = fromCalled & 0x0F;
            int b = fromCalling & 0x0F;
            if (a < MinPacketSizeLog2 || a > MaxPacketSizeLog2 || b < MinPacketSizeLog2 || b > MaxPacketSizeLog2)
            {
                return X25FacilityError.PacketSizeOutOfRange;
            }

            into.PacketSizeFromCalled = 1 << a;
            into.PacketSizeFromCalling = 1 << b;
            into.HasPacketSize = true;
            return X25FacilityError.None;
        }

        private static X25FacilityError DecodeWindowSize(byte fromCalled, byte fromCalling, X25Facilities into)
        {
            // 7.3.1.2: "Bit 8 of each octet must be zero." "A value of zero is not allowed." "Window
            // sizes of 8 to 127 are only valid if extended sequence numbering is used."
            if ((fromCalled & 0x80) != 0 || (fromCalling & 0x80) != 0)
            {
                return X25FacilityError.ReservedBitsSet;
            }

            if (fromCalled == 0 || fromCalling == 0 || fromCalled > MaxWindowModulo8 || fromCalling > MaxWindowModulo8)
            {
                return X25FacilityError.WindowOutOfRange;
            }

            into.WindowFromCalled = fromCalled;
            into.WindowFromCalling = fromCalling;
            into.HasWindowSize = true;
            return X25FacilityError.None;
        }

        private static byte PacketSizeToLog2(int packetSize)
        {
            for (int log2 = MinPacketSizeLog2; log2 <= MaxPacketSizeLog2; log2++)
            {
                if ((1 << log2) == packetSize)
                {
                    return (byte)log2;
                }
            }

            throw new ArgumentOutOfRangeException(nameof(packetSize), packetSize,
                "X.25 7.3.1.1: the packet size must be a power of two from 16 to 4096.");
        }

        private static byte CheckWindow(byte window)
        {
            if (window == 0 || window > MaxWindowModulo8)
            {
                throw new ArgumentOutOfRangeException(nameof(window), window,
                    "X.25 7.3.1.2: the window must be 1..7 with modulo 8 numbering; zero is not allowed.");
            }

            return window;
        }
    }
}
