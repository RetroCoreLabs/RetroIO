using System;

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// The XMSG (DC/XMSG) sub-header carried by a data message (subtype
    /// <see cref="SintranPacketSubtype.Data"/>) immediately after the 14-byte SINTRAN header.
    /// </summary>
    /// <remarks>
    /// <para><b>Layout</b></para>
    /// Offsets relative to the start of the sub-header, which is ABSOLUTE offset 14 on the wire.
    /// The field names come from the XMSG kernel symbol list
    /// (<c>SINTRAN/NPL-SOURCE/SYMBOLS/L07/XMSG-SYMBOL-LIST.SYMB.TXT</c>), where the block is seven
    /// words carrying the warning <c>"Next block is sent directly over link. Do NOT split up!"</c>:
    ///  - <c>0</c>..<c>1</c> XMTHD - the constant marker <c>0x21 0x00</c> (absolute 14-15).
    ///  - <c>2</c> XMSTA high half - Frame Flags (absolute 16).
    ///  - <c>3</c> XMSTA low half - Role / 5M* state (absolute 17).
    ///  - <c>4</c>..<c>5</c> XMDSY destination system, big-endian (absolute 18-19).
    ///  - <c>6</c>..<c>7</c> XMDPT destination port, big-endian (absolute 20-21).
    ///  - <c>8</c>..<c>9</c> XMSSY source system, big-endian (absolute 22-23).
    ///  - <c>10</c>..<c>11</c> XMSPT source port, big-endian (absolute 24-25).
    ///  - <c>12</c>..<c>13</c> XMCSM, ONE word, and the sub-header ENDS here (absolute 26-27).
    /// <para>
    /// CORRECTED 2026-08-04. This class used to be NINETEEN bytes starting at absolute 13, with a
    /// phantom <c>Counter</c> at its offset 0 (really the SINTRAN header checksum's low byte), a
    /// 32-bit <c>ControlService</c> (really XMCSM plus the first word of the MESSAGE BODY), and
    /// <c>Pad</c> + <c>UserDataLength</c> at its tail (really body bytes 2 and 3). 13 + 19 = 32,
    /// four bytes past the true body start of 28. Evidence, all independent:
    /// <c>XM5HE=7</c> words / <c>XM5HL=16</c> octal = 14 bytes in the symbol file; Flags 2 equals
    /// the 16-bit XMCSM on 1449 of 1449 data frames; and the word at absolute 28-29 is plainly
    /// application-layer (<c>0x07D2</c> FA ConnectionConfirm, <c>0x0041</c> XSLET, <c>0x014B</c>
    /// XSGSY, <c>0x0100</c> XRSOK). See <c>FaBodyOffsetTests</c>, which proves on a real captured
    /// datagram that the file-access body parses at 28 and NOT at 32.
    /// </para>
    /// </remarks>
    public sealed class XmsgSubHeader
    {
        /// <summary>
        /// Serialised size of the full XMSG sub-header in bytes: SEVEN WORDS.
        /// </summary>
        public const int Size = 14;

        /// <summary>
        /// First marker byte of the constant <c>0x21 0x00</c> sub-header marker.
        /// </summary>
        public const byte MarkerByte0 = 0x21;

        /// <summary>
        /// Second marker byte of the constant <c>0x21 0x00</c> sub-header marker.
        /// </summary>
        public const byte MarkerByte1 = 0x00;

        /// <summary>
        /// Gets or sets the frame-flags byte (offset 2, absolute 16).
        /// </summary>
        public byte FrameFlags { get; set; }

        /// <summary>
        /// Gets or sets the role byte (offset 3, absolute 17): low nibble 4 = asker, 0 = responder.
        /// </summary>
        public byte Role { get; set; }

        /// <summary>
        /// Gets or sets the XMDSY destination system number (offsets 4-5, big-endian).
        /// </summary>
        public ushort DestinationSystem { get; set; }

        /// <summary>
        /// Gets or sets the XMDPT destination port (offsets 6-7, big-endian).
        /// </summary>
        public ushort DestinationPort { get; set; }

        /// <summary>
        /// Gets or sets the XMSSY source system number (offsets 8-9, big-endian).
        /// </summary>
        public ushort SourceSystem { get; set; }

        /// <summary>
        /// Gets or sets the XMSPT source port (offsets 10-11, big-endian).
        /// </summary>
        public ushort SourcePort { get; set; }

        /// <summary>
        /// Gets or sets XMCSM (offsets 12-13, absolute 26-27), the LAST word of the sub-header.
        /// </summary>
        /// <remarks>
        /// The kernel comments this field <c>"datagram checksum; if not checksum, then message
        /// size"</c>, and it equals the SINTRAN header's Flags 2 on 1449 of 1449 captured data
        /// frames. It is NOT a service dispatch key - the service lives in the message body that
        /// starts at absolute 28.
        /// </remarks>
        public ushort Xmcsm { get; set; }

        /// <summary>
        /// Parses an XMSG sub-header from the first <see cref="Size"/> bytes of a span.
        /// </summary>
        /// <param name="source">
        /// The bytes starting at absolute wire offset 14; must contain at least
        /// <see cref="Size"/> bytes.
        /// </param>
        /// <returns>
        /// The parsed <see cref="XmsgSubHeader"/>.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="source"/> is shorter than <see cref="Size"/> bytes.
        /// </exception>
        public static XmsgSubHeader Parse(ReadOnlySpan<byte> source)
        {
            if (source.Length < Size)
            {
                throw new ArgumentException("XMSG sub-header requires at least 14 bytes.", nameof(source));
            }

            XmsgSubHeader sub = new XmsgSubHeader();
            // source[0..1] is the fixed 0x21 0x00 marker - retained implicitly on serialise.
            sub.FrameFlags = source[2];
            sub.Role = source[3];
            sub.DestinationSystem = NdEndian.GetBe16(source, 4);
            sub.DestinationPort = NdEndian.GetBe16(source, 6);
            sub.SourceSystem = NdEndian.GetBe16(source, 8);
            sub.SourcePort = NdEndian.GetBe16(source, 10);
            sub.Xmcsm = NdEndian.GetBe16(source, 12);
            return sub;
        }

        /// <summary>
        /// Serialises this sub-header into the first <see cref="Size"/> bytes of a span.
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
                throw new ArgumentException("XMSG sub-header requires at least 14 bytes.", nameof(destination));
            }

            destination[0] = MarkerByte0;
            destination[1] = MarkerByte1;
            destination[2] = FrameFlags;
            destination[3] = Role;
            NdEndian.PutBe16(destination, 4, DestinationSystem);
            NdEndian.PutBe16(destination, 6, DestinationPort);
            NdEndian.PutBe16(destination, 8, SourceSystem);
            NdEndian.PutBe16(destination, 10, SourcePort);
            NdEndian.PutBe16(destination, 12, Xmcsm);
        }
    }
}
