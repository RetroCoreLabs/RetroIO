//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Packet
{
    /// <summary>
    /// The full field set of a data (subtype <c>0x0E</c>) XMSG packet: SINTRAN header addressing,
    /// the XMSG sub-header envelope, and the trailing payload. Passed to
    /// <see cref="XmsgPacketBuilder.CreateData"/> so a data packet is built from one intention-
    /// revealing value instead of a dozen positional arguments.
    /// </summary>
    /// <remarks>
    /// These are the exact fields the captured connect handshake is built from (see the
    /// conn-to-d102 accept / port-assign vectors). Field NAMES follow the wire glossary:
    /// XMDSY/XMDPT = destination system/port, XMSSY/XMSPT = source system/port, XMCSM = the
    /// control/service dispatch word. The channel (Protocol ID) and the datagram sequence
    /// (Flags 1) live in the SINTRAN header; the per-direction Counter lives in the sub-header.
    /// </remarks>
    public struct XmsgDataFields
    {
        /// <summary>
        /// Destination node number (SINTRAN header offsets 4-5).
        /// </summary>
        public ushort DestinationNode;

        /// <summary>
        /// Source node number (SINTRAN header offsets 6-7).
        /// </summary>
        public ushort SourceNode;

        /// <summary>
        /// Flags 1 - the per-direction datagram sequence (offsets 8-9).
        /// </summary>
        public ushort Flags1;

        /// <summary>
        /// Flags 2 (offsets 10-11) - a COPY of the 16-bit XMCSM; <c>0x0400</c> on the setup frames.
        /// </summary>
        /// <remarks>
        /// <para>
        /// CARVED FACT, 2026-07-31 - this is not an independent "frame-class word". It equals the
        /// true 16-bit XMCSM at wire 26-27 on 1449 of 1449 data frames in the corpus. See
        /// <see cref="XmcsmWithBodyWord"/> for the full word layout carved from the kernel symbols.
        /// </para>
        /// <para>
        /// The kernel comments XMCSM as <c>"datagram checksum; if not checksum, then message
        /// size"</c>, and that is CARVED as literally true - the code is at kernel address
        /// <c>134013</c> in <c>XMSG-KERNEL-L03.BPUN</c>:
        /// </para>
        /// <para>
        /// It first stores <c>XMSIZ</c> (<c>0o144</c>) into <c>XMCSM</c> (<c>0o142</c>) - the SIZE
        /// size, which is the default. Two <c>BSKP</c> flag-bit tests then decide: if either is set it
        /// keeps the size, otherwise it saves <c>XMSTA</c>, ZEROES both <c>XMSTA</c> and
        /// <c>XMCSM</c>, calls the routine at <c>137335</c>, and stores the result into
        /// <c>XMCSM</c>. Zeroing the field before computing and storing the result back is the
        /// standard checksum-over-block idiom, and it is why <c>XMSTA</c> is excluded from the
        /// covered bytes.
        /// </para>
        /// <para>
        /// The routine at <c>137335</c> is a ones-complement sum with END-AROUND CARRY
        /// (<c>RADD SA DD</c> followed by <c>RADD ADC CLD SD DD</c>) over <c>XMLEN/2</c> words
        /// starting at <c>XMDAB</c>/<c>XMDAW</c>, with an odd-length tail path. So <c>XMLEN</c>
        /// (<c>0o147</c>) IS a real length field - it bounds the checksum - it simply is not at
        /// wire 30-31 where this layout once put it.
        /// </para>
        /// <para>
        /// This explains the corpus: 718 frames satisfy <c>XMCSM AND 0x1FF == bodyLen</c>, and
        /// 731 carry a CONSTANT (<c>0x0080</c>, <c>0x0064</c>) while the body varies. The
        /// constants are <c>XMSIZ</c> - the message BUFFER size, fixed per
        /// message class - not the body length.
        /// </para>
        /// <para>
        /// UNKNOWN: which flag bit picks size or checksum. The decision is the <c>BSKP ONE 110 DA</c> /
        /// <c>BSKP ONE 120 DA</c> pair at <c>134016</c>/<c>134021</c> on a value fetched by
        /// <c>LDATX</c>. The location is carved; the bit identity is not. Do NOT assume a frame's
        /// XMCSM is a length without checking - roughly half are checksums.
        /// </para>
        /// <para>
        /// This matters for the envelope arithmetic: <see cref="XmsgEnvelope.BaseLowSigned"/>
        /// subtracts the low byte of this field, so on file-server frames it is subtracting a BYTE
        /// COUNT. Any reasoning that treats it as a pure class selector will be wrong on exactly
        /// that traffic.
        /// </para>
        /// </remarks>
        public ushort Flags2;

        /// <summary>
        /// SINTRAN header word 6 (offsets 12-13): the ones-complement checksum over words 0-5.
        /// </summary>
        /// <remarks>
        /// Derived, never chosen - <see cref="XmsgPacketBuilder.CreateSessionData"/> computes it.
        /// </remarks>
        public ushort Checksum;

        /// <summary>
        /// The HIGH byte of <see cref="Checksum"/> - wire offset 12.
        /// </summary>
        /// <remarks>
        /// Was called <c>ProtocolId</c>. There is no protocol-selector byte: offsets 12-13 are one
        /// ones-complement checksum over header words 0-5, and the <see cref="SintranProtocolId"/>
        /// names only record which traffic produced a given high byte. Setting this leaves the low
        /// half alone, so never set it after the real checksum has been computed.
        /// </remarks>
        public byte ChecksumHigh
        {
            get { return (byte)(Checksum >> 8); }
            set { Checksum = (ushort)((value << 8) | (Checksum & 0x00FF)); }
        }

        /// <summary>
        /// The LOW byte of <see cref="Checksum"/> - wire offset 13.
        /// </summary>
        /// <remarks>
        /// Was called <c>Counter</c>. It is not a counter and it belongs to the SINTRAN header,
        /// not to the XMSG sub-header.
        /// </remarks>
        public byte ChecksumLow
        {
            get { return (byte)(Checksum & 0x00FF); }
            set { Checksum = (ushort)((Checksum & 0xFF00) | value); }
        }

        /// <summary>
        /// Sub-header frame-flags byte (offset 3); <c>0x86</c> for the setup frames.
        /// </summary>
        public byte FrameFlags;

        /// <summary>
        /// Sub-header role byte (offset 4); low nibble 4 = asker, 0 = responder.
        /// </summary>
        public byte Role;

        /// <summary>
        /// XMDSY destination system number (offsets 5-6, big-endian).
        /// </summary>
        public ushort DestinationSystem;

        /// <summary>
        /// XMDPT destination port (offsets 7-8, big-endian).
        /// </summary>
        public ushort DestinationPort;

        /// <summary>
        /// XMSSY source system number (offsets 9-10, big-endian).
        /// </summary>
        public ushort SourceSystem;

        /// <summary>
        /// XMSPT source port (offsets 11-12, big-endian).
        /// </summary>
        public ushort SourcePort;

        /// <summary>
        /// XMCSM plus the first word of the message body, read as one 32-bit value
        /// (sub-header offsets 13-16, i.e. wire offsets 26-29).
        /// </summary>
        /// <remarks>
        /// <para>
        /// CARVED FACT, 2026-07-31 - this field spans a boundary and is misnamed. The XMSG
        /// kernel symbol list (<c>SINTRAN/NPL-SOURCE/SYMBOLS/L07/XMSG-SYMBOL-LIST.SYMB.TXT</c>)
        /// defines the transported header as SEVEN words ending at XMCSM:
        /// </para>
        /// <para>
        /// Word layout, octal symbol values and the wire offset each lands on:
        ///  - <c>XMTHD=134</c> word 0 - wire 14-15.
        ///  - <c>XMSTA=135</c> word 1 - wire 16-17 (low byte = 5M* state, high byte = XF* options).
        ///  - <c>XMDSY=136</c> word 2 - wire 18-19.
        ///  - <c>XMDPT=137</c> word 3 - wire 20-21.
        ///  - <c>XMSSY=140</c> word 4 - wire 22-23.
        ///  - <c>XMSPT=141</c> word 5 - wire 24-25.
        ///  - <c>XMCSM=142</c> word 6 - wire 26-27, and the header ENDS here.
        /// </para>
        /// <para>
        /// <c>XM5HE=7</c> (words) and <c>XM5HL=16</c> octal = 14 bytes confirm the length, and
        /// <c>XMLEN=147</c> sits five words past XMCSM - far outside the transported header, so it
        /// is not the 16-bit length field this layout assumed at wire 30-31 either.
        /// The source carries the warning
        /// <c>"Next block is sent directly over link. Do NOT split up!"</c> against that block.
        /// </para>
        /// <para>
        /// So the true <c>XMCSM</c> is ONE word at wire 26-27, commented in the kernel as
        /// <c>"datagram checksum; if not checksum, then message size"</c>. Wire offsets 28-29 are
        /// the FIRST WORD OF THE MESSAGE BODY, not header.
        /// </para>
        /// <para>
        /// Confirmed on the wire, independently of the symbol file, across the whole capture
        /// corpus: Flags 2 (wire 10-11) equals the 16-bit XMCSM (wire 26-27) on 1449 of 1449
        /// data frames - so the long-documented rule "Flags2 == XMCSM >> 16" was that exact
        /// equality seen through this wrong 32-bit split. And the word at 28-29 takes values that
        /// are plainly application-layer: <c>0x07F0</c>/<c>0x07A2</c>/<c>0x07C0</c>/<c>0x07D2</c>
        /// (the FA message types - see <c>FaMessageType</c>), <c>0x0041</c> XSLET,
        /// <c>0x014B</c> XSGSY, <c>0x0100</c> XRSOK.
        /// </para>
        /// <para>
        /// This is kept as a 32-bit field FOR NOW because splitting it correctly touches 108 call
        /// sites across 34 files and changes what callers pass (a single <c>0x00800141</c> becomes
        /// XMCSM <c>0x0080</c> plus body word <c>0x0141</c>). Treat the value as
        /// <c>(XMCSM shifted left 16) | firstBodyWord</c> until that refactor lands. Consequently
        /// <c>ControlService >> 16</c> IS the real XMCSM, and <c>>> 24</c> is its high
        /// byte - both are genuine quantities despite the misleading name.
        /// </para>
        /// <para>
        /// Doc: <c>SINTRAN/XMSG/DOC/XMSG-SUBHEADER-NAMED-FROM-SYMBOLS-2026-07-29.md</c> section 4
        /// (which raised the conflict) and
        /// <c>SINTRAN/XMSG/DOC/XMSG-CHANNEL-FORMULA-DIVERGES-ON-FILE-SERVER-TRAFFIC-2026-07-31.md</c>
        /// (which resolved it).
        /// </para>
        /// <para>
        /// LANDED 2026-08-04. The stored model is now correct: <see cref="Xmcsm"/> holds the real
        /// one-word XMCSM and <see cref="Body"/> holds the message body from absolute 28.
        /// </para>
        /// <para>
        /// This property is the two of them COMPOSED, and its name says so. It exists because the
        /// <see cref="XmcsmService"/> constants are written in that 32-bit form, so dispatch needs
        /// both halves. It stores nothing itself: the getter reads <see cref="Xmcsm"/> and the
        /// first body word, and the setter writes exactly those - which is what the old name's
        /// deprecation note asked callers to do by hand.
        /// </para>
        /// <para>
        /// It was called <c>ControlService</c>, a name that claimed the wire carries one 32-bit
        /// "control/service" field. It does not, and believing it did hid the 13+19 boundary error.
        /// Use <see cref="Xmcsm"/> alone when only the XMCSM matters.
        /// </para>
        /// </remarks>
        public uint XmcsmWithBodyWord
        {
            get
            {
                uint bodyWord = 0u;
                if (Body != null && Body.Length >= 2)
                {
                    bodyWord = (uint)((Body[0] << 8) | Body[1]);
                }

                return ((uint)Xmcsm << 16) | bodyWord;
            }

            set
            {
                Xmcsm = (ushort)(value >> 16);
                EnsureBody(2);
                Body[0] = (byte)(value >> 8);
                Body[1] = (byte)value;
            }
        }

        /// <summary>
        /// XMCSM (wire 26-27), the LAST word of the XMSG sub-header.
        /// </summary>
        /// <remarks>
        /// Equal to <see cref="Flags2"/> on 1449 of 1449 captured data frames.
        /// </remarks>
        public ushort Xmcsm;

        /// <summary>
        /// The MESSAGE BODY, from absolute wire offset 28 to the end of the datagram.
        /// </summary>
        /// <remarks>
        /// <para>
        /// THE SINGLE SOURCE OF TRUTH for everything past the sub-header. On a letter its first
        /// four bytes are the XROUT header (serial, service, big-endian length) and the parameter
        /// blocks follow; on a file-access message its first word is the
        /// <c>FaMessageType</c>.
        /// </para>
        /// <para>
        /// The legacy views <see cref="XmcsmWithBodyWord"/> (low half), <see cref="Pad"/> and
        /// <see cref="UserDataLength"/> write into bytes 0-3 of this array and
        /// <see cref="XroutPayload"/> into byte 4 onward, each leaving the others alone, so a caller may
        /// set them in ANY order and get the same bytes.
        /// </para>
        /// </remarks>
        public byte[] Body;

        /// <summary>
        /// Body byte 2 (wire 30) under its historical name.
        /// </summary>
        /// <remarks>
        /// COMPATIBILITY VIEW over <see cref="Body"/>. This was never a pad: on a letter it is the
        /// HIGH byte of the XROUT declared length.
        /// </remarks>
        [Obsolete("Body byte 2 (wire 30), not a sub-header pad. Write it through Body.")]
        public byte Pad
        {
            get { return Body != null && Body.Length > 2 ? Body[2] : (byte)0; }
            set { EnsureBody(3); Body[2] = value; }
        }

        /// <summary>
        /// Body byte 3 (wire 31) under its historical name.
        /// </summary>
        /// <remarks>
        /// COMPATIBILITY VIEW over <see cref="Body"/>. XMLEN (<c>0o147</c>) lives five words past
        /// XMCSM in the kernel block and is NOT on the wire here; on a letter this byte is the LOW
        /// byte of the XROUT declared length, which is why it looked like a length.
        /// </remarks>
        [Obsolete("Body byte 3 (wire 31), not XMLEN. Write it through Body.")]
        public byte UserDataLength
        {
            get { return Body != null && Body.Length > 3 ? Body[3] : (byte)0; }
            set { EnsureBody(4); Body[3] = value; }
        }

        /// <summary>
        /// The body bytes from absolute wire offset 32 onward, under the historical name.
        /// </summary>
        /// <remarks>
        /// COMPATIBILITY VIEW over <see cref="Body"/>: this is <c>Body</c> minus its first four
        /// bytes, which the 13+19 model mislabelled as XMCSM's second half, a pad and a length.
        /// </remarks>
        public byte[] XroutPayload
        {
            get
            {
                if (Body == null || Body.Length <= 4)
                {
                    return EmptyBytes;
                }

                byte[] tail = new byte[Body.Length - 4];
                for (int i = 0; i < tail.Length; i++)
                {
                    tail[i] = Body[i + 4];
                }

                return tail;
            }

            set
            {
                byte[] tail = value != null ? value : EmptyBytes;
                EnsureBody(4);
                byte[] joined = new byte[4 + tail.Length];
                for (int i = 0; i < 4; i++)
                {
                    joined[i] = Body[i];
                }

                // Body bytes 2-3 are the big-endian length of everything after them - the XROUT
                // header's length word. The old model called them "Pad" and "XMLEN" and wrote the
                // payload length low byte into the second of them, which is the same value for any
                // body under 256 bytes and is why the mislabel survived. VERIFIED on the captured
                // conn-to-d102 DUMM frame (0000 0002 1800: two bytes of TAD chain) and on the
                // *FA-SERVER connect letter (1B41 0012 ...: 18 bytes of parameter blocks).
                joined[2] = (byte)(tail.Length >> 8);
                joined[3] = (byte)tail.Length;

                for (int i = 0; i < tail.Length; i++)
                {
                    joined[4 + i] = tail[i];
                }

                Body = joined;
            }
        }

        /// <summary>
        /// Grows <see cref="Body"/> with zero bytes until it is at least <paramref name="length"/>
        /// bytes long, preserving whatever is already there.
        /// </summary>
        /// <param name="length">
        /// The minimum length required.
        /// </param>
        /// <remarks>
        /// This is what makes the legacy setters order-independent: each writes only its own bytes
        /// and never truncates the others.
        /// </remarks>
        private void EnsureBody(int length)
        {
            if (Body != null && Body.Length >= length)
            {
                return;
            }

            byte[] grown = new byte[length];
            if (Body != null)
            {
                for (int i = 0; i < Body.Length; i++)
                {
                    grown[i] = Body[i];
                }
            }

            Body = grown;
        }

        /// <summary>
        /// Splits the historical 32-bit control/service word and a payload into the real XMCSM and
        /// the real message body, for the hand-rolled frame builders that have not moved to
        /// <see cref="XmsgPacketBuilder.CreateData"/> yet.
        /// </summary>
        /// <param name="controlService">
        /// The 32-bit value: XMCSM in the high half, the first body word in the low half.
        /// </param>
        /// <param name="payload">
        /// The bytes that used to follow the 19-byte sub-header, i.e. from wire offset 32.
        /// </param>
        /// <param name="xmcsm">
        /// Receives the real one-word XMCSM for wire 26-27.
        /// </param>
        /// <returns>
        /// The message body for wire offset 28 onward.
        /// </returns>
        /// <remarks>
        /// The single place that knows how the composite word and the payload become a body:
        /// XMCSM out of the high half, the body word out of the low half, then the XROUT header and
        /// the payload. Everything that builds a data body goes through here.
        /// </remarks>
        public static byte[] ComposeBody(uint controlService, byte[] payload, out ushort xmcsm)
        {
            XmsgDataFields fields = default(XmsgDataFields);
            fields.XmcsmWithBodyWord = controlService;
            byte[] tail = payload != null ? payload : EmptyBytes;
            // Payload's setter writes the big-endian length into body bytes 2-3 itself, which is
            // exactly what the old Pad + XMLEN pair held.
            fields.XroutPayload = tail;

            xmcsm = fields.Xmcsm;
            // Payload's setter runs EnsureBody, so Body is never null by this point.
            return fields.Body != null ? fields.Body : EmptyBytes;
        }

        /// <summary>
        /// The shared empty array returned when there is no body.
        /// </summary>
        private static readonly byte[] EmptyBytes = new byte[0];
    }
}
