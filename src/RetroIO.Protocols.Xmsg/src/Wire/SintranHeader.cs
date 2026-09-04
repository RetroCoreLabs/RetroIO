using System;

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// The fixed 14-byte (SEVEN WORD) SINTRAN header that prefixes every LAPB information field.
    /// </summary>
    /// <remarks>
    /// <para><b>Layout</b></para>
    /// Byte offsets (see XMSG-PROTOCOL.md section 4):
    ///  - <c>0</c> Marker 1, always <c>0x21</c>.
    ///  - <c>1</c> Marker 2, <c>0x13</c> normal or <c>0x12</c> relay.
    ///  - <c>2</c> Packet Type, <c>0x00</c> in all observed XMSG traffic.
    ///  - <c>3</c> Packet Subtype (message kind, not a length).
    ///  - <c>4</c>..<c>5</c> Destination node number (big-endian).
    ///  - <c>6</c>..<c>7</c> Source node number (big-endian).
    ///  - <c>8</c>..<c>9</c> Flags 1 (datagram sequence / broadcast marker).
    ///  - <c>10</c>..<c>11</c> Flags 2 (frame-class word; equals the 16-bit XMCSM on data frames).
    ///  - <c>12</c>..<c>13</c> Word 6, the header CHECKSUM (see <see cref="Checksum"/>).
    /// <para>
    /// CORRECTED 2026-08-04. This class used to model the header as THIRTEEN bytes, ending at the
    /// checksum's high byte, and the low half at offset 13 was handed to the XMSG sub-header as a
    /// phantom "Counter". Word 6 is a ones-complement sum of words 0-5 with END-AROUND carry, then
    /// complemented - carved from the XMSG kernel at <c>137314</c> and verified on 3595 of 3595
    /// captured frames. Worked example, the D102 FA reply of 2026-08-04:
    /// <c>21 13 00 19 00 66 00 64 FF FF 00 01 DE 08</c> sums to <c>0x21F7</c> and
    /// <c>~0x21F7 = 0xDE08</c>, exactly bytes 12-13. So the header is 14 bytes and the XMSG
    /// sub-header starts at absolute offset 14, where its constant <c>0x21 0x00</c> marker sits.
    /// </para>
    /// </remarks>
    public sealed class SintranHeader
    {
        /// <summary>
        /// Serialised size of the SINTRAN header in bytes: SEVEN WORDS.
        /// </summary>
        public const int Size = 14;

        /// <summary>
        /// Marker 1 constant that opens every SINTRAN header.
        /// </summary>
        public const byte Marker1Value = 0x21;

        /// <summary>
        /// Marker 2 value for a normal (non-relayed) frame.
        /// </summary>
        public const byte Marker2Normal = 0x13;

        /// <summary>
        /// Marker 2 value for a relay frame (a node forwarding between two others).
        /// </summary>
        public const byte Marker2Relay = 0x12;

        /// <summary>
        /// Gets or sets the Marker 1 byte (offset 0). Normally <see cref="Marker1Value"/>.
        /// </summary>
        public byte Marker1 { get; set; } = Marker1Value;

        /// <summary>
        /// Gets or sets the Marker 2 byte (offset 1). Normally <see cref="Marker2Normal"/>.
        /// </summary>
        public byte Marker2 { get; set; } = Marker2Normal;

        /// <summary>
        /// Gets or sets the Packet Type byte (offset 2). <c>0x00</c> in observed traffic.
        /// </summary>
        public byte PacketType { get; set; }

        /// <summary>
        /// Gets or sets the Packet Subtype (offset 3) identifying the message kind.
        /// </summary>
        public SintranPacketSubtype Subtype { get; set; }

        /// <summary>
        /// Gets or sets the destination node number (offsets 4-5, big-endian).
        /// </summary>
        public ushort DestinationNode { get; set; }

        /// <summary>
        /// Gets or sets the source node number (offsets 6-7, big-endian).
        /// </summary>
        public ushort SourceNode { get; set; }

        /// <summary>
        /// Gets or sets Flags 1 (offsets 8-9): the datagram sequence number, or
        /// <c>0xFFFF</c> on broadcast/reachability frames.
        /// </summary>
        public ushort Flags1 { get; set; }

        /// <summary>
        /// Gets or sets Flags 2 (offsets 10-11): the frame-class word.
        /// </summary>
        public ushort Flags2 { get; set; }

        /// <summary>
        /// Gets or sets header word 6 (offsets 12-13): the ones-complement checksum over words 0-5.
        /// </summary>
        /// <remarks>
        /// This is the ONE stored field for those two bytes. Compute it with
        /// <see cref="Packet.XmsgEnvelope.ComputeHeaderChecksum(SintranHeader)"/>; it is derived, never chosen.
        /// A wrong value here is a corrupt header and is what the XMSG 24B / <c>PERF_CONNCT</c>
        /// peer crash really is.
        /// </remarks>
        public ushort Checksum { get; set; }

        /// <summary>
        /// Gets or sets the checksum HIGH byte (offset 12) under its historical name.
        /// </summary>
        /// <remarks>
        /// <para>
        /// COMPATIBILITY VIEW over <see cref="Checksum"/> - it stores nothing of its own. The
        /// <see cref="SintranProtocolId"/> member names (<c>Tad</c>, <c>Routing</c>, <c>Pad</c>,
        /// ...) are MISNOMERS: they record which traffic happened to produce that checksum byte in
        /// the captures, not a sub-protocol selector. Setting this rewrites the checksum's high
        /// half and leaves the low half alone, so a caller that sets it after the real checksum
        /// CORRUPTS the header. Dispatch on the subtype and on XMCSM instead.
        /// </para>
        /// </remarks>
        [Obsolete("Offset 12 is the header checksum high byte, not a protocol selector. Use SintranHeader.Checksum (XmsgEnvelope.ComputeHeaderChecksum).")]
        public SintranProtocolId ProtocolId
        {
            get { return (SintranProtocolId)(byte)(Checksum >> 8); }
            set { Checksum = (ushort)(((ushort)value << 8) | (Checksum & 0x00FF)); }
        }

        /// <summary>
        /// Gets or sets the checksum LOW byte (offset 13) under its historical name.
        /// </summary>
        /// <remarks>
        /// COMPATIBILITY VIEW over <see cref="Checksum"/>. This byte was long modelled as the XMSG
        /// sub-header's per-direction "Counter"; it is not a counter and it does not belong to the
        /// sub-header at all. See the class remarks for the worked checksum example.
        /// </remarks>
        [Obsolete("Offset 13 is the header checksum low byte, not a sub-header counter. Use SintranHeader.Checksum.")]
        public byte Counter
        {
            get { return (byte)(Checksum & 0x00FF); }
            set { Checksum = (ushort)((Checksum & 0xFF00) | value); }
        }

        /// <summary>
        /// Parses a SINTRAN header from the first <see cref="Size"/> bytes of a span.
        /// </summary>
        /// <param name="source">
        /// The frame bytes; must contain at least <see cref="Size"/> bytes.
        /// </param>
        /// <returns>
        /// The parsed <see cref="SintranHeader"/>.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="source"/> is shorter than <see cref="Size"/> bytes.
        /// </exception>
        public static SintranHeader Parse(ReadOnlySpan<byte> source)
        {
            if (source.Length < Size)
            {
                throw new ArgumentException("SINTRAN header requires at least 14 bytes.", nameof(source));
            }

            SintranHeader header = new SintranHeader();
            header.Marker1 = source[0];
            header.Marker2 = source[1];
            header.PacketType = source[2];
            header.Subtype = (SintranPacketSubtype)source[3];
            // Offsets rather than two-byte slices: the helper names the byte order and takes the
            // offset itself, so the reader sees the layout as a column of addresses.
            header.DestinationNode = NdEndian.GetBe16(source, 4);
            header.SourceNode = NdEndian.GetBe16(source, 6);
            header.Flags1 = NdEndian.GetBe16(source, 8);
            header.Flags2 = NdEndian.GetBe16(source, 10);
            header.Checksum = NdEndian.GetBe16(source, 12);
            return header;
        }

        /// <summary>
        /// Serialises this header into the first <see cref="Size"/> bytes of a span.
        /// </summary>
        /// <param name="destination">
        /// The destination span; must have room for at least <see cref="Size"/> bytes.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="destination"/> is shorter than <see cref="Size"/> bytes.
        /// </exception>
        public void Serialize(Span<byte> destination)
        {
            if (destination.Length < Size)
            {
                throw new ArgumentException("SINTRAN header requires at least 14 bytes.", nameof(destination));
            }

            destination[0] = Marker1;
            destination[1] = Marker2;
            destination[2] = PacketType;
            destination[3] = (byte)Subtype;
            NdEndian.PutBe16(destination, 4, DestinationNode);
            NdEndian.PutBe16(destination, 6, SourceNode);
            NdEndian.PutBe16(destination, 8, Flags1);
            NdEndian.PutBe16(destination, 10, Flags2);
            NdEndian.PutBe16(destination, 12, Checksum);
        }

        /// <summary>
        /// Serialises this header into a freshly allocated 14-byte array.
        /// </summary>
        /// <returns>
        /// A new array containing the serialised header.
        /// </returns>
        public byte[] ToArray()
        {
            byte[] buffer = new byte[Size];
            Serialize(buffer);
            return buffer;
        }
    }
}
