//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// Why an X.75 network utility field was rejected. <see cref="None"/> means it decoded.
    /// </summary>
    public enum X75UtilityError
    {
        /// <summary>
        /// The field decoded.
        /// </summary>
        None,

        /// <summary>
        /// The field is longer than 63 octets (X.75 4.2.1.5: "The maximum length of this field is 63 octets").
        /// </summary>
        TooLong,

        /// <summary>
        /// An element's parameter octets run past the end of the field (X.75 5.4.1, Figure 21).
        /// </summary>
        Truncated,

        /// <summary>
        /// A network identification semi-octet is not a decimal digit (X.75 5.4.3.1 / 5.4.3.9: binary coded decimal).
        /// </summary>
        InvalidDigit,

        /// <summary>
        /// A bit X.75 says is "unassigned and set to 0" is set (bit 8 of a window octet, 5.4.3.4; bits 8-5 of a packet
        /// size octet, 5.4.3.5).
        /// </summary>
        ReservedBitsSet,

        /// <summary>
        /// A utility that may appear only once appears twice. Only the transit network identification may repeat
        /// (5.4.1: "such as the RPOA selection or transit network identification ... multiple utility elements with an
        /// identical utility code"); RPOA selection is carried untouched, so its repeats are not checked here.
        /// </summary>
        RepeatedCode,
    }

    /// <summary>
    /// The network utility field that X.75 adds to Call Request, Call Connected and Clear Request packets between
    /// signalling terminals (STEs): the utilities a switch between networks acts on, decoded, and every other element
    /// carried through untouched in the order it arrived.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Source: CCITT X.75 (11/1988), the Blue Book text. The field sits after the address field, behind a length octet
    /// whose bits 6-1 give its length and whose bits 8 and 7 are 0 (4.2.1.4); it is at most 63 octets (4.2.1.5). The
    /// user facility field (X.25 coding) follows it with its own length octet. This class codes the field only, not
    /// its length octet.
    /// </para>
    /// <para>
    /// Elements (5.4.1, Figure 21): a utility code whose bits 8 and 7 give the parameter length - 00 one octet,
    /// 01 two, 10 three, 11 a length octet then that many (at most 61). Code 0xFF is "reserved for extension": the
    /// next octet is the real code, and 0xFF may repeat. The utility marker, code 0x00 with one parameter octet 0x00
    /// (5.4.3.16), separates the X.75 utilities "from non-X.75 utilities that may be agreed bilaterally" (5.3.18):
    /// nothing after it is read as an X.75 utility.
    /// </para>
    /// <para>
    /// Interpreted (codes from Table 21/X.75):
    /// </para>
    ///  - transit network identification, 0x41: two octets, a DNIC (or INIC) as four BCD digits, high digit first (5.4.3.1). It may repeat, one element per transit network, in the order the call crossed them (5.3.1);
    ///  - call identifier, 0x81: three octets, 24 bits of binary data (5.4.3.2), high octet first - X.75 does not say the octet order; the high octet first is how every other multi-octet binary value in X.75 is coded (5.4.3.11, 5.4.3.12), and that is an assumption;
    ///  - throughput class indication, 0x02: one octet, bits 4-1 the class from the calling STE, bits 8-5 the class from the called STE (5.4.3.3). The four-bit class codes are kept as codes; Table 22 maps them to bit/s;
    ///  - window size indication, 0x43: first octet from the called STE, second from the calling STE, bits 7-1 each, bit 8 "unassigned and set to 0" (5.4.3.4);
    ///  - packet size indication, 0x42: first octet from the called STE, second from the calling STE, bits 4-1 the base-2 logarithm of the data field length, bits 8-5 "unassigned and set to 0" (5.4.3.5);
    ///  - clearing network identification code, 0x4A: two octets, the DNIC of the network that cleared, four BCD digits (5.4.3.9).
    /// <para>
    /// Everything else - call identifier excepted, all the class D utilities (closed user group 0xC3 / 0xC7, NUI
    /// 0xC6), RPOA selection 0x44, tariffs 0x07, transit delay 0x49 / 0x4B, fast select and reverse charging 0x01,
    /// called line address modified 0x08, extended codes, and the marker with all that follows - is kept byte for
    /// byte in <see cref="PassThrough"/> and written after the interpreted elements.
    /// </para>
    /// <para>
    /// Range checks that depend on the link (window 8 to 127 "only valid for calls which employ extended numbering",
    /// the packet sizes "subject to a bilateral agreement") are the caller's: this class checks only what the coding
    /// itself forbids.
    /// </para>
    /// </remarks>
    public sealed class X75Utilities
    {
        /// <summary>
        /// Transit network identification (Table 21/X.75: 01000001).
        /// </summary>
        public const byte TransitNetworkCode = 0x41;

        /// <summary>
        /// Call identifier (Table 21/X.75: 10000001).
        /// </summary>
        public const byte CallIdentifierCode = 0x81;

        /// <summary>
        /// Throughput class indication (Table 21/X.75: 00000010).
        /// </summary>
        public const byte ThroughputClassCode = 0x02;

        /// <summary>
        /// Window size indication (Table 21/X.75: 01000011).
        /// </summary>
        public const byte WindowSizeCode = 0x43;

        /// <summary>
        /// Packet size indication (Table 21/X.75: 01000010).
        /// </summary>
        public const byte PacketSizeCode = 0x42;

        /// <summary>
        /// Clearing network identification code (Table 21/X.75: 01001010).
        /// </summary>
        public const byte ClearingNetworkCode = 0x4A;

        /// <summary>
        /// Utility marker (Table 21/X.75: 00000000).
        /// </summary>
        public const byte MarkerCode = 0x00;

        /// <summary>
        /// Extension code (5.4.1: "Utility code 11111111 is reserved for extension of the utility code").
        /// </summary>
        public const byte ExtensionCode = 0xFF;

        /// <summary>
        /// Longest network utility field (4.2.1.5).
        /// </summary>
        public const int MaxFieldLength = 63;

        /// <summary>
        /// Largest call identifier: 24 bits (5.4.3.2).
        /// </summary>
        public const int MaxCallIdentifier = 0xFFFFFF;

        private List<string> _transitNetworks = new List<string>();

        /// <summary>
        /// The transit networks, each as its four-digit DNIC or INIC, in the order the call crossed them (5.3.1).
        /// </summary>
        public List<string> TransitNetworks
        {
            get => _transitNetworks;
            set => _transitNetworks = value ?? new List<string>();
        }

        /// <summary>
        /// True when a call identifier was carried.
        /// </summary>
        public bool HasCallIdentifier { get; set; }

        /// <summary>
        /// The call identifier, 0 to <see cref="MaxCallIdentifier"/>. Zero means "not significant" (5.3.2 Note).
        /// </summary>
        public int CallIdentifier { get; set; }

        /// <summary>
        /// True when a throughput class indication was carried.
        /// </summary>
        public bool HasThroughputClass { get; set; }

        /// <summary>
        /// Throughput class code for transmission from the calling STE (bits 4-1, 5.4.3.3).
        /// </summary>
        public byte ThroughputFromCalling { get; set; }

        /// <summary>
        /// Throughput class code for transmission from the called STE (bits 8-5, 5.4.3.3).
        /// </summary>
        public byte ThroughputFromCalled { get; set; }

        /// <summary>
        /// True when a window size indication was carried.
        /// </summary>
        public bool HasWindowSize { get; set; }

        /// <summary>
        /// Window for transmission from the called STE (first octet, 5.4.3.4).
        /// </summary>
        public byte WindowFromCalled { get; set; }

        /// <summary>
        /// Window for transmission from the calling STE (second octet, 5.4.3.4).
        /// </summary>
        public byte WindowFromCalling { get; set; }

        /// <summary>
        /// True when a packet size indication was carried.
        /// </summary>
        public bool HasPacketSize { get; set; }

        /// <summary>
        /// Base-2 logarithm of the data field length from the called STE (first octet, 5.4.3.5).
        /// </summary>
        public byte PacketSizeLog2FromCalled { get; set; }

        /// <summary>
        /// Base-2 logarithm of the data field length from the calling STE (second octet, 5.4.3.5).
        /// </summary>
        public byte PacketSizeLog2FromCalling { get; set; }

        /// <summary>
        /// The DNIC of the network that cleared the call (5.4.3.9), or empty when none was carried.
        /// </summary>
        public string ClearingNetwork { get; set; } = string.Empty;

        /// <summary>
        /// Every element not interpreted, byte for byte and in arrival order, including any marker and everything after
        /// it. Written unchanged by <see cref="Encode"/>.
        /// </summary>
        public byte[] PassThrough { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Decodes a network utility field.
        /// </summary>
        /// <param name="field">The field octets, without the length octet that comes before them in the packet.</param>
        /// <param name="utilities">
        /// The decoded utilities when the result is <see cref="X75UtilityError.None"/>; otherwise what had been decoded
        /// before the error, for diagnostics only.
        /// </param>
        /// <returns><see cref="X75UtilityError.None"/>, or the first problem found.</returns>
        public static X75UtilityError TryDecode(ReadOnlySpan<byte> field, out X75Utilities utilities)
        {
            utilities = new X75Utilities();
            if (field.Length > MaxFieldLength)
            {
                return X75UtilityError.TooLong;
            }

            Span<byte> passThrough = stackalloc byte[MaxFieldLength];
            int passLen = 0;

            int i = 0;
            while (i < field.Length)
            {
                byte code = field[i];

                // The marker: it and everything after it are not X.75 utilities (5.3.18).
                if (code == MarkerCode)
                {
                    if (i + 2 > field.Length)
                    {
                        return X75UtilityError.Truncated;
                    }

                    field.Slice(i).CopyTo(passThrough.Slice(passLen));
                    passLen += field.Length - i;
                    break;
                }

                // Extension: skip every 0xFF; the element is sized by the extended code's class (5.4.1).
                int elementStart = i;
                bool extended = false;
                while (i < field.Length && field[i] == ExtensionCode)
                {
                    extended = true;
                    i++;
                }

                if (i >= field.Length)
                {
                    return X75UtilityError.Truncated;
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
                            return X75UtilityError.Truncated;
                        }

                        paramLength = field[paramStart];
                        paramStart++;
                        break;
                }

                int elementEnd = paramStart + paramLength;
                if (elementEnd > field.Length)
                {
                    return X75UtilityError.Truncated;
                }

                ReadOnlySpan<byte> p = field.Slice(paramStart, paramLength);
                X75UtilityError e = extended
                    ? X75UtilityError.None
                    : DecodeOne(effectiveCode, p, utilities);
                if (e != X75UtilityError.None)
                {
                    return e;
                }

                if (extended || !Interprets(effectiveCode))
                {
                    field.Slice(elementStart, elementEnd - elementStart).CopyTo(passThrough.Slice(passLen));
                    passLen += elementEnd - elementStart;
                }

                i = elementEnd;
            }

            utilities.PassThrough = passLen == 0 ? Array.Empty<byte>() : passThrough.Slice(0, passLen).ToArray();
            return X75UtilityError.None;
        }

        /// <summary>
        /// Encodes the field: transit networks in order, call identifier, throughput class, window size, packet size,
        /// clearing network (each only when present), then <see cref="PassThrough"/> unchanged. X.75 sets no order for
        /// the utilities; this one keeps the marker and what follows it last, where 5.3.18 needs it.
        /// </summary>
        /// <returns>The field octets, without the length octet.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// A value cannot be coded (a network that is not four digits, a call identifier over 24 bits, a throughput
        /// class over 15, a window over 127, a packet size code over 15), or the field would pass 63 octets.
        /// </exception>
        public byte[] Encode()
        {
            int length = (_transitNetworks.Count * 3)
                + (HasCallIdentifier ? 4 : 0)
                + (HasThroughputClass ? 2 : 0)
                + (HasWindowSize ? 3 : 0)
                + (HasPacketSize ? 3 : 0)
                + (ClearingNetwork.Length != 0 ? 3 : 0)
                + PassThrough.Length;
            if (length > MaxFieldLength)
            {
                throw new ArgumentOutOfRangeException(nameof(length), length,
                    "X.75 4.2.1.5: the network utility field is at most 63 octets.");
            }

            byte[] field = new byte[length];
            int pos = 0;

            for (int t = 0; t < _transitNetworks.Count; t++)
            {
                field[pos++] = TransitNetworkCode;
                WriteNetwork(_transitNetworks[t], field.AsSpan(pos, 2));
                pos += 2;
            }

            if (HasCallIdentifier)
            {
                if (CallIdentifier < 0 || CallIdentifier > MaxCallIdentifier)
                {
                    throw new ArgumentOutOfRangeException(nameof(CallIdentifier), CallIdentifier,
                        "X.75 5.4.3.2: the call identifier is 24 bits.");
                }

                field[pos++] = CallIdentifierCode;
                field[pos++] = (byte)(CallIdentifier >> 16);
                field[pos++] = (byte)(CallIdentifier >> 8);
                field[pos++] = (byte)CallIdentifier;
            }

            if (HasThroughputClass)
            {
                if (ThroughputFromCalling > 0x0F || ThroughputFromCalled > 0x0F)
                {
                    throw new ArgumentOutOfRangeException(nameof(ThroughputFromCalling),
                        "X.75 5.4.3.3: each throughput class is four bits.");
                }

                field[pos++] = ThroughputClassCode;
                field[pos++] = (byte)((ThroughputFromCalled << 4) | ThroughputFromCalling);
            }

            if (HasWindowSize)
            {
                if (WindowFromCalled > 0x7F || WindowFromCalling > 0x7F)
                {
                    throw new ArgumentOutOfRangeException(nameof(WindowFromCalled),
                        "X.75 5.4.3.4: each window is bits 7 to 1; bit 8 is 0.");
                }

                field[pos++] = WindowSizeCode;
                field[pos++] = WindowFromCalled;
                field[pos++] = WindowFromCalling;
            }

            if (HasPacketSize)
            {
                if (PacketSizeLog2FromCalled > 0x0F || PacketSizeLog2FromCalling > 0x0F)
                {
                    throw new ArgumentOutOfRangeException(nameof(PacketSizeLog2FromCalled),
                        "X.75 5.4.3.5: each packet size is bits 4 to 1; bits 8 to 5 are 0.");
                }

                field[pos++] = PacketSizeCode;
                field[pos++] = PacketSizeLog2FromCalled;
                field[pos++] = PacketSizeLog2FromCalling;
            }

            if (ClearingNetwork.Length != 0)
            {
                field[pos++] = ClearingNetworkCode;
                WriteNetwork(ClearingNetwork, field.AsSpan(pos, 2));
                pos += 2;
            }

            PassThrough.AsSpan().CopyTo(field.AsSpan(pos));
            return field;
        }

        private static bool Interprets(byte code)
        {
            return code == TransitNetworkCode || code == CallIdentifierCode || code == ThroughputClassCode
                || code == WindowSizeCode || code == PacketSizeCode || code == ClearingNetworkCode;
        }

        // Reads one interpreted element into the result; any other code is left for the pass-through.
        private static X75UtilityError DecodeOne(byte code, ReadOnlySpan<byte> p, X75Utilities into)
        {
            switch (code)
            {
                case TransitNetworkCode:
                {
                    if (!TryReadNetwork(p, out string dnic))
                    {
                        return X75UtilityError.InvalidDigit;
                    }

                    into._transitNetworks.Add(dnic);
                    return X75UtilityError.None;
                }

                case CallIdentifierCode:
                    if (into.HasCallIdentifier)
                    {
                        return X75UtilityError.RepeatedCode;
                    }

                    into.HasCallIdentifier = true;
                    into.CallIdentifier = (p[0] << 16) | (p[1] << 8) | p[2];
                    return X75UtilityError.None;

                case ThroughputClassCode:
                    if (into.HasThroughputClass)
                    {
                        return X75UtilityError.RepeatedCode;
                    }

                    into.HasThroughputClass = true;
                    into.ThroughputFromCalling = (byte)(p[0] & 0x0F);
                    into.ThroughputFromCalled = (byte)(p[0] >> 4);
                    return X75UtilityError.None;

                case WindowSizeCode:
                    if (into.HasWindowSize)
                    {
                        return X75UtilityError.RepeatedCode;
                    }

                    if ((p[0] & 0x80) != 0 || (p[1] & 0x80) != 0)
                    {
                        return X75UtilityError.ReservedBitsSet;
                    }

                    into.HasWindowSize = true;
                    into.WindowFromCalled = p[0];
                    into.WindowFromCalling = p[1];
                    return X75UtilityError.None;

                case PacketSizeCode:
                    if (into.HasPacketSize)
                    {
                        return X75UtilityError.RepeatedCode;
                    }

                    if ((p[0] & 0xF0) != 0 || (p[1] & 0xF0) != 0)
                    {
                        return X75UtilityError.ReservedBitsSet;
                    }

                    into.HasPacketSize = true;
                    into.PacketSizeLog2FromCalled = p[0];
                    into.PacketSizeLog2FromCalling = p[1];
                    return X75UtilityError.None;

                case ClearingNetworkCode:
                {
                    if (into.ClearingNetwork.Length != 0)
                    {
                        return X75UtilityError.RepeatedCode;
                    }

                    if (!TryReadNetwork(p, out string dnic))
                    {
                        return X75UtilityError.InvalidDigit;
                    }

                    into.ClearingNetwork = dnic;
                    return X75UtilityError.None;
                }

                default:
                    return X75UtilityError.None;
            }
        }

        // Four BCD digits in two octets, the high digit in bits 8-5 of the first octet (5.4.3.1, 5.4.3.9).
        private static bool TryReadNetwork(ReadOnlySpan<byte> p, out string dnic)
        {
            Span<char> digits = stackalloc char[4];
            for (int k = 0; k < 4; k++)
            {
                int nibble = (k & 1) == 0 ? p[k >> 1] >> 4 : p[k >> 1] & 0x0F;
                if (nibble > 9)
                {
                    dnic = string.Empty;
                    return false;
                }

                digits[k] = (char)('0' + nibble);
            }

            dnic = new string(digits);
            return true;
        }

        private static void WriteNetwork(string dnic, Span<byte> into)
        {
            if (dnic == null || dnic.Length != 4)
            {
                throw new ArgumentOutOfRangeException(nameof(dnic), dnic,
                    "X.75 5.3: a network is named by four digits (its DNIC or INIC).");
            }

            for (int k = 0; k < 4; k++)
            {
                int d = dnic[k] - '0';
                if (d < 0 || d > 9)
                {
                    throw new ArgumentOutOfRangeException(nameof(dnic), dnic,
                        "X.75 5.4.3.1: each of the four network digits is a decimal digit.");
                }
            }

            into[0] = (byte)(((dnic[0] - '0') << 4) | (dnic[1] - '0'));
            into[1] = (byte)(((dnic[2] - '0') << 4) | (dnic[3] - '0'));
        }
    }
}
