//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Packet;   // XmsgEnvelope.ComputeHeaderChecksum - the carved, 3595/3595 routine

namespace RetroIO.Protocols.Xmsg.Node.Seam
{
    /// <summary>
    /// The route-through rule: what a node changes in a SINTRAN datagram when it forwards that
    /// datagram between two other nodes.
    /// </summary>
    /// <remarks>
    /// <para><b>The rule, and how narrow it is</b></para>
    /// <para>
    /// A relay rewrites word 0 and word 6, and nothing else. The HOP COUNT in the low byte
    /// of word 0 is DECREMENTED - so word 0 <c>0x2113</c> becomes <c>0x2112</c> on the first hop,
    /// <c>0x2111</c> on the second - and the header checksum in word 6 is recomputed over the
    /// changed header. The endpoints, Flags 1, Flags 2, the subtype and the entire body are passed
    /// through BYTE FOR BYTE.
    /// </para>
    /// <para>
    /// Word 0 is the kernel's <c>XDROU</c>, "NETWORK INFO (VERSION, PROTOCOL, HOP COUNT)" - it is
    /// NOT two magic markers, which is how it was modelled while only one hop was ever exercised.
    /// A datagram arriving with a hop count of zero is DROPPED rather than forwarded.
    /// </para>
    /// <para>
    /// In particular the relay does not renumber Flags 1. That sequence belongs to the
    /// originating link, not to the relay, and acknowledgements are end-to-end: the far endpoint
    /// acknowledges the originator, not the node in the middle.
    /// </para>
    /// <para><b>Scope of the evidence</b></para>
    /// <para>
    /// The rule was read off relayed HDLC frames, and the transport-independence of the header was
    /// verified on 128/128 frames including relayed ones. What has NOT been observed is a
    /// relayed frame on the ETHERNET transport - COSMOS-ETHERNET-TRANSPORT-FRAMING-2026-08-01.md
    /// lists that as an open question, and names this exact topology as the experiment: D103 on
    /// HDLC to D100, D102 on Ethernet to D100. This class implements the HDLC-derived rule
    /// unchanged, on the reasoning that the header is transport-independent everywhere else it has
    /// been checked. If a live Ethernet relay capture disagrees, this is the class that is wrong.
    /// </para>
    /// </remarks>
    public static class SintranDatagramRelay
    {
        /// <summary>
        /// Byte offset of the version/protocol byte, which a relay leaves alone.
        /// </summary>
        /// <remarks>
        /// Word 0 is the kernel's <c>XDROU</c>, "NETWORK INFO (VERSION, PROTOCOL, HOP COUNT)". This
        /// byte is <c>X5VRS</c>: <c>20400</c> octal = <c>0x2100</c>, left byte <c>0x21</c>, meaning
        /// OP=0, VERSION=2, PROTOCOL=1. Carved from <c>XMSG-POFTABS-L03.SYMB</c>.
        /// </remarks>
        public const int VersionProtocolOffset = 0;

        /// <summary>
        /// Byte offset of the HOP COUNT, the only header byte outside the checksum a relay changes.
        /// </summary>
        public const int HopCountOffset = 1;

        /// <summary>
        /// Byte offset of Marker 1, which a relay leaves alone.
        /// </summary>
        [Obsolete("Word 0 is XDROU, not two markers. Use VersionProtocolOffset.")]
        public const int Marker1Offset = VersionProtocolOffset;

        /// <summary>
        /// Byte offset of Marker 2, the only header byte outside the checksum that a relay changes.
        /// </summary>
        [Obsolete("Byte 1 is a HOP COUNT, not a marker. Use HopCountOffset.")]
        public const int Marker2Offset = HopCountOffset;

        /// <summary>
        /// Byte offset of the header checksum's HIGH byte (word 6). Long mislabelled "Protocol ID".
        /// </summary>
        public const int ChecksumHighOffset = 12;

        /// <summary>
        /// Byte offset of the header checksum's LOW byte. Long mislabelled "Counter".
        /// </summary>
        public const int ChecksumLowOffset = 13;

        /// <summary>
        /// The full SINTRAN header size: SEVEN words, not the six-and-a-half of the superseded
        /// model. The XMSG sub-header starts at offset 14.
        /// </summary>
        public const int HeaderSize = 14;

        /// <summary>
        /// The version/protocol byte that opens every SINTRAN header: OP=0, VERSION=2, PROTOCOL=1.
        /// </summary>
        public const byte VersionProtocolValue = 0x21;

        /// <summary>
        /// The hop count a sender puts in the header before any relay has touched it.
        /// </summary>
        public const byte InitialHopCount = 0x13;

        // Marker1Value, Marker2Normal and Marker2Relay were deleted 2026-09-04 with the rest of the
        // [Obsolete] set. Byte 0 is the XDROU version/protocol byte (VersionProtocolValue) and
        // byte 1 is a decrementing hop count (InitialHopCount, HopsTaken, IsRelayed) - neither is
        // a "marker", and "Marker2Relay" only ever described the value ONE hop produces, so a
        // twice-forwarded datagram at 0x11 did not match it.

        /// <summary>
        /// Rewrites a datagram in place into its relayed form: marks it relayed and recomputes the
        /// header checksum.
        /// </summary>
        /// <param name="datagram">
        /// The datagram, starting at Marker 1. Modified in place; only bytes 1, 12 and 13 change.
        /// </param>
        /// <returns>
        /// True when the datagram was rewritten; false when it is too short, does not open with
        /// the version/protocol byte, or has NO HOPS LEFT - in which case it is left untouched and
        /// must not be forwarded.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Rewriting IN PLACE is deliberate: a relay copies nothing, so there is no opportunity for
        /// a body to be silently truncated or padded on the way through.
        /// </para>
        /// <para><b>It DECREMENTS the hop count; it used to assign 0x12</b></para>
        /// <para>
        /// Assigning was indistinguishable from decrementing for the only case ever exercised - one
        /// hop, <c>0x13</c> to <c>0x12</c> - and wrong for every case beyond it: a twice-forwarded
        /// datagram stayed at <c>0x12</c>, so the count could never fall and a datagram going round
        /// a loop would circulate for ever. A hop count exists to stop exactly that, so counting is
        /// the whole point of the field.
        /// </para>
        /// <para>
        /// Returning false at zero is the other half of it. A datagram whose hops are exhausted is
        /// DROPPED, not forwarded with a wrapped count - which is what <c>byte</c> arithmetic would
        /// otherwise do, turning 0 into 255 and granting it 255 more hops.
        /// </para>
        /// </remarks>
        public static bool MakeRelayed(Span<byte> datagram)
        {
            if (!IsWellFormed(datagram))
            {
                return false;
            }

            if (datagram[HopCountOffset] == 0)
            {
                return false;
            }

            datagram[HopCountOffset] -= 1;
            WriteChecksum(datagram);
            return true;
        }

        /// <summary>
        /// Produces the relayed form of a datagram as a new array, leaving the original untouched.
        /// </summary>
        /// <param name="datagram">
        /// The datagram, starting at Marker 1.
        /// </param>
        /// <returns>
        /// A new array holding the relayed datagram, or null when the input is too short or does
        /// not open with Marker 1.
        /// </returns>
        public static byte[]? ToRelayed(ReadOnlySpan<byte> datagram)
        {
            if (!IsWellFormed(datagram))
            {
                return null;
            }

            byte[] copy = datagram.ToArray();
            MakeRelayed(copy);
            return copy;
        }

        /// <summary>
        /// Gets a value indicating whether a datagram is marked as having been relayed.
        /// </summary>
        /// <param name="datagram">
        /// The datagram, starting at Marker 1.
        /// </param>
        /// <returns>
        /// True when the hop count has fallen below its initial value, that is the datagram has
        /// been forwarded at least once.
        /// </returns>
        /// <remarks>
        /// This used to test <c>== 0x12</c>, which answered FALSE for a datagram forwarded twice
        /// (<c>0x11</c>) - it reported a well-travelled datagram as fresh. Comparing against the
        /// initial value counts any number of hops.
        /// </remarks>
        public static bool IsRelayed(ReadOnlySpan<byte> datagram)
        {
            return IsWellFormed(datagram) && datagram[HopCountOffset] < InitialHopCount;
        }

        /// <summary>
        /// Gets how many times a datagram has been forwarded.
        /// </summary>
        /// <param name="datagram">
        /// The datagram, starting at the version/protocol byte.
        /// </param>
        /// <returns>
        /// The number of hops taken, or -1 when the datagram is not well formed.
        /// </returns>
        public static int HopsTaken(ReadOnlySpan<byte> datagram)
        {
            if (!IsWellFormed(datagram))
            {
                return -1;
            }

            return InitialHopCount - datagram[HopCountOffset];
        }

        /// <summary>
        /// Recomputes the header checksum of a datagram and writes it into words 6's two bytes.
        /// </summary>
        /// <param name="datagram">
        /// The datagram, starting at Marker 1. Only bytes 12 and 13 are modified.
        /// </param>
        /// <returns>
        /// True when the checksum was written; false when the datagram is too short or does not
        /// open with Marker 1.
        /// </returns>
        public static bool WriteChecksum(Span<byte> datagram)
        {
            if (!IsWellFormed(datagram))
            {
                return false;
            }

            ushort checksum = XmsgEnvelope.ComputeHeaderChecksum(
                ReadWord(datagram, 0),
                ReadWord(datagram, 2),
                ReadWord(datagram, 4),
                ReadWord(datagram, 6),
                ReadWord(datagram, 8),
                ReadWord(datagram, 10));

            datagram[ChecksumHighOffset] = (byte)(checksum >> 8);
            datagram[ChecksumLowOffset] = (byte)(checksum & 0xFF);
            return true;
        }

        /// <summary>
        /// Gets a value indicating whether a datagram's header checksum matches its header.
        /// </summary>
        /// <param name="datagram">
        /// The datagram, starting at Marker 1.
        /// </param>
        /// <returns>
        /// True when the checksum word is correct for the six header words that precede it.
        /// </returns>
        public static bool HasValidChecksum(ReadOnlySpan<byte> datagram)
        {
            if (!IsWellFormed(datagram))
            {
                return false;
            }

            ushort expected = XmsgEnvelope.ComputeHeaderChecksum(
                ReadWord(datagram, 0),
                ReadWord(datagram, 2),
                ReadWord(datagram, 4),
                ReadWord(datagram, 6),
                ReadWord(datagram, 8),
                ReadWord(datagram, 10));

            return ReadWord(datagram, ChecksumHighOffset) == expected;
        }

        /// <summary>
        /// Gets the destination node number from a datagram (word 2, big-endian).
        /// </summary>
        /// <param name="datagram">
        /// The datagram, starting at Marker 1.
        /// </param>
        /// <returns>
        /// The destination node number, or 0 when the datagram is malformed.
        /// </returns>
        public static ushort GetDestinationNode(ReadOnlySpan<byte> datagram)
        {
            return IsWellFormed(datagram) ? ReadWord(datagram, 4) : (ushort)0;
        }

        /// <summary>
        /// Gets the source node number from a datagram (word 3, big-endian).
        /// </summary>
        /// <param name="datagram">
        /// The datagram, starting at Marker 1.
        /// </param>
        /// <returns>
        /// The source node number, or 0 when the datagram is malformed.
        /// </returns>
        public static ushort GetSourceNode(ReadOnlySpan<byte> datagram)
        {
            return IsWellFormed(datagram) ? ReadWord(datagram, 6) : (ushort)0;
        }

        /// <summary>
        /// Gets a value indicating whether a datagram is long enough to hold a SINTRAN header and
        /// opens with Marker 1.
        /// </summary>
        /// <param name="datagram">
        /// The datagram, starting at Marker 1.
        /// </param>
        /// <returns>
        /// True when the datagram can be treated as a SINTRAN datagram.
        /// </returns>
        private static bool IsWellFormed(ReadOnlySpan<byte> datagram)
        {
            return datagram.Length >= HeaderSize
                && datagram[VersionProtocolOffset] == VersionProtocolValue;
        }

        /// <summary>
        /// Reads a big-endian 16-bit word at a byte offset.
        /// </summary>
        /// <param name="datagram">
        /// The datagram bytes.
        /// </param>
        /// <param name="offset">
        /// The byte offset of the word's high byte.
        /// </param>
        /// <returns>
        /// The word value.
        /// </returns>
        private static ushort ReadWord(ReadOnlySpan<byte> datagram, int offset)
        {
            // Node numbers and every other header field are big-endian on the wire. (The MAC on the
            // Ethernet transport stores the system number the OTHER way round - see NdMacAddress.)
            return (ushort)((datagram[offset] << 8) | datagram[offset + 1]);
        }
    }
}
