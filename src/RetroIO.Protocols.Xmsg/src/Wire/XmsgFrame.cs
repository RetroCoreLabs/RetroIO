using System;

using RetroIO.Protocols.Xmsg.SubProtocol;

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// A complete XMSG wire frame: the SINTRAN header, an optional XMSG sub-header,
    /// and either an XROUT message body (data frames) or trailing control bytes
    /// (short/control frames).
    /// </summary>
    /// <remarks>
    /// This models the HDLC information field only; HDLC framing, byte-stuffing and
    /// the FCS are out of scope (see XMSG-PROTOCOL.md sections 2-3). A data frame
    /// (subtype <see cref="SintranPacketSubtype.Data"/>) carries a
    /// <see cref="XmsgSubHeader"/>; short frames (Ack / reachability) carry a single
    /// trailing counter/command byte in <see cref="TrailingBytes"/>.
    /// </remarks>
    public sealed class XmsgFrame
    {
        private static readonly byte[] EmptyTrailing = new byte[0];

        /// <summary>
        /// Initialises an empty frame with a default normal-frame header.
        /// </summary>
        public XmsgFrame()
        {
            Header = new SintranHeader();
            TrailingBytes = EmptyTrailing;
        }

        /// <summary>
        /// Gets or sets the SINTRAN header.
        /// </summary>
        public SintranHeader Header { get; set; }

        /// <summary>
        /// Gets or sets the XMSG sub-header, or <c>null</c> when the frame carries none.
        /// </summary>
        public XmsgSubHeader? SubHeader { get; set; }

        /// <summary>
        /// Gets or sets the XROUT message body, or <c>null</c> when the frame carries none.
        /// </summary>
        public XroutMessage? Body { get; set; }

        /// <summary>
        /// Gets or sets the trailing control bytes for short frames (for example the
        /// single counter/command byte of an Ack or reachability frame).
        /// </summary>
        public byte[] TrailingBytes { get; set; }

        /// <summary>
        /// Gets or sets an exact copy of the original information-field bytes this
        /// frame was decoded from, or <c>null</c> for a frame built from scratch.
        /// </summary>
        /// <remarks>
        /// When non-<c>null</c> this is authoritative: <see cref="ToArray"/> reproduces
        /// these bytes verbatim so that decode/re-encode is guaranteed byte-identical
        /// for ANY captured frame, including sub-protocols this model does not fully
        /// decode structurally (TAD, PAD, DB, and any un-modeled XROUT tail). The
        /// structured properties (<see cref="Header"/>, <see cref="SubHeader"/>,
        /// <see cref="Body"/>, <see cref="TrailingBytes"/>) remain populated for
        /// human-readable inspection. Set this to <c>null</c> (call
        /// <see cref="ClearRawBytes"/>) to force re-serialisation from the model.
        /// </remarks>
        public byte[]? RawBytes { get; set; }

        /// <summary>
        /// Gets or sets the decoded TAD message chain for a TAD frame (protocol
        /// <see cref="SintranProtocolId.Tad"/>), or <c>null</c> for other protocols.
        /// </summary>
        /// <remarks>
        /// TAD frames carry a chain of terminal messages directly after the SINTRAN
        /// header rather than an XMSG sub-header, so a TAD frame populates this property
        /// instead of <see cref="SubHeader"/> and <see cref="Body"/>. Purely informational;
        /// serialisation is driven by <see cref="RawBytes"/>.
        /// </remarks>
        public TadChain? Tad { get; set; }

        /// <summary>
        /// Gets or sets the decoded ROUTING short-command view for a routing control frame
        /// (protocol <see cref="SintranProtocolId.Routing"/> that is not a data letter), or
        /// <c>null</c> otherwise.
        /// </summary>
        /// <remarks>
        /// Full XSGSY routing-table replies arrive as XROUT letters and appear in
        /// <see cref="Body"/>; this property covers only the one-byte routing control
        /// commands. Purely informational; serialisation is driven by
        /// <see cref="RawBytes"/>.
        /// </remarks>
        public RoutingCommandInfo? Routing { get; set; }

        /// <summary>
        /// Gets the message body as it appears on the wire from absolute offset 28 onward.
        /// </summary>
        /// <returns>
        /// The body bytes, empty when the frame carries none.
        /// </returns>
        /// <remarks>
        /// The one place that decides what the body IS: the XROUT letter when <see cref="Body"/> is
        /// set (serial / service / length header included - it IS on the wire, see
        /// <see cref="XroutMessageFraming"/>), otherwise the raw <see cref="TrailingBytes"/>. Any
        /// bytes past a letter's declared length are appended, because the captured FA connect
        /// letter really does carry eight of them.
        /// </remarks>
        public byte[] GetBodyBytes()
        {
            if (Body == null)
            {
                return TrailingBytes != null ? TrailingBytes : EmptyTrailing;
            }

            byte[] letter = Body.ToArray(XroutMessageFraming.WithHeader);
            byte[]? extra = TrailingBytes;
            if (extra == null || extra.Length == 0)
            {
                return letter;
            }

            byte[] joined = new byte[letter.Length + extra.Length];
            for (int i = 0; i < letter.Length; i++)
            {
                joined[i] = letter[i];
            }

            for (int i = 0; i < extra.Length; i++)
            {
                joined[letter.Length + i] = extra[i];
            }

            return joined;
        }

        /// <summary>
        /// Gets the historical 32-bit "XMCSM control/service word":
        /// <c>(XMCSM shifted left 16) | firstBodyWord</c>.
        /// </summary>
        /// <remarks>
        /// COMPATIBILITY VIEW, computed - nothing stores it. The high half is the real XMCSM at
        /// absolute 26-27; the low half is the FIRST WORD OF THE MESSAGE BODY at absolute 28-29,
        /// which on a letter is the XROUT serial byte and service byte. Reading them as one 32-bit
        /// field is what hid the header/sub-header boundary error for months. Zero when the frame
        /// carries no sub-header.
        /// </remarks>
        [Obsolete("Reads across the sub-header/body boundary. Use SubHeader.Xmcsm plus the body word from GetBodyBytes().")]
        public uint ControlService
        {
            get
            {
                if (SubHeader == null)
                {
                    return 0u;
                }

                // The captured bytes are authoritative when present: a decoded frame's structured
                // body may be a TAD chain that does not re-serialise identically.
                if (RawBytes != null && RawBytes.Length >= 30)
                {
                    return ((uint)SubHeader.Xmcsm << 16)
                        | (uint)((RawBytes[28] << 8) | RawBytes[29]);
                }

                byte[] body = GetBodyBytes();
                uint bodyWord = 0u;
                if (body.Length >= 2)
                {
                    bodyWord = (uint)((body[0] << 8) | body[1]);
                }
                else if (body.Length == 1)
                {
                    bodyWord = (uint)(body[0] << 8);
                }

                return ((uint)SubHeader.Xmcsm << 16) | bodyWord;
            }
        }

        /// <summary>
        /// Decodes a full XMSG frame from an HDLC information-field span.
        /// </summary>
        /// <param name="frame">
        /// The information-field bytes starting at the SINTRAN header (Marker 1).
        /// </param>
        /// <returns>
        /// The decoded <see cref="XmsgFrame"/>.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="frame"/> is shorter than a SINTRAN header.
        /// </exception>
        public static XmsgFrame Parse(ReadOnlySpan<byte> frame)
        {
            XmsgFrame result = new XmsgFrame();

            // Retain an exact copy of the input so ToArray can reproduce it byte-for-byte
            // regardless of how far the structured decode below gets. This is what makes
            // decode/re-encode lossless for every captured sub-protocol variant.
            result.RawBytes = frame.ToArray();

            result.Header = SintranHeader.Parse(frame);

            ReadOnlySpan<byte> rest = frame.Slice(SintranHeader.Size);

            // A FIRST FRAGMENT carries the sub-header too - CORRECTED 2026-08-06.
            //
            // It is the opening piece of an ordinary data message, so it has the same 14-byte
            // sub-header a whole one does; only the CONTINUATION goes without. Our own splitter has
            // always said so - SintranMessageFragment.FirstFragmentBodyOffset is header + sub-header
            // (28) while ContinuationBodyOffset is header alone (14) - but this gate tested for Data
            // only, so a received first fragment fell through to the short/control branch and its
            // sub-header was counted as 14 bytes of body.
            //
            // What that cost: GetBodyBytes returned 608 where the continuation's resume offset said
            // 594, SintranFragmentReassembler refused the join as a mismatch, and a file written to
            // us was silently dropped - D100 retransmitting the same pair 39 seconds later. The
            // reassembler's own unit tests could not see it because they BUILD frames rather than
            // parsing them. Pinned by SintranFragmentCaptureTests against a real captured pair.
            if ((result.Header.Subtype == SintranPacketSubtype.Data
                    || result.Header.Subtype == SintranPacketSubtype.MessageFirstFragment)
                && rest.Length >= XmsgSubHeader.Size)
            {
                // Every data (0x0E) frame carries the SAME XMSG sub-header, for ALL
                // Protocol IDs (0xD8..0xDE). Verified across all 13 captures: the
                // Protocol ID byte is a channel/tag, NOT a distinct frame layout. So there
                // is ONE data-frame decode path - sub-header then a content-dispatched
                // trailer - regardless of whether the channel is DC, TAD, PAD or ROUTING.
                result.SubHeader = XmsgSubHeader.Parse(rest.Slice(0, XmsgSubHeader.Size));
                ReadOnlySpan<byte> trailer = rest.Slice(XmsgSubHeader.Size);

                if (trailer.Length > 0)
                {
                    DecodeDataTrailer(result, trailer);
                }
            }
            else if (rest.Length > 0)
            {
                // Short/control frame (Ack, reachability request/reply) - keep the
                // trailing counter/command byte(s) verbatim.
                result.TrailingBytes = rest.ToArray();

                // A ROUTING (0xDE) short frame's trailing byte is a routing control
                // command; expose it as a typed view (informational, RawBytes remains
                // authoritative for serialisation).
                // The checksum high byte 0xDE is the value the old model called "Routing". It is a
                // MISNOMER (see SintranProtocolId) but it is kept as the gate here because the
                // typed routing view is purely informational and no better discriminator has been
                // carved for short frames.
                if ((byte)(result.Header.Checksum >> 8) == (byte)SintranProtocolId.Routing)
                {
                    result.Routing = RoutingCommandInfo.Parse(rest);
                }
            }

            return result;
        }

        /// <summary>
        /// Decodes the trailer of a data frame (the bytes after the XMSG sub-header) by
        /// its content, choosing between an XROUT letter, a TAD message chain, or raw bytes.
        /// </summary>
        /// <param name="result">
        /// The frame being populated; its <see cref="SubHeader"/> is already set.
        /// </param>
        /// <param name="trailer">
        /// The message body: the bytes from ABSOLUTE offset 28, i.e. after the 14-byte SINTRAN
        /// header and the 14-byte XMSG sub-header.
        /// </param>
        /// <remarks>
        /// Dispatch is by the XMCSM control/service word (XMSG-PROTOCOL.md; the reference
        /// dissector's LI ROUTING / XSLET handling):
        ///  - XMCSM <c>0x0100014B</c> or <c>0x01000100</c> - LI ROUTING (XSGSY) reply, an
        ///    XROUT letter of 4-byte parameter blocks.
        ///  - XMCSM low byte <c>0x41</c> - XSLET (send letter), an XROUT letter.
        /// Letters are parsed with <see cref="XroutMessageFraming.BodyOnly"/>: on this transport
        /// the XROUT header is not on the wire.
        ///  - otherwise - a TAD message chain (terminal session data / control).
        /// Any part that cannot be decoded is retained verbatim (letter tail in
        /// <see cref="TrailingBytes"/>, TAD tail in <see cref="SubProtocol.TadChain.Remainder"/>),
        /// and <see cref="RawBytes"/> still guarantees a byte-identical re-serialisation.
        /// </remarks>
        private static void DecodeDataTrailer(XmsgFrame result, ReadOnlySpan<byte> trailer)
        {
            // Dispatch still uses the historical 32-bit composite because the CONSTANTS in
            // XmcsmService are written in that form. It is composed here explicitly so the
            // boundary is visible: XMCSM is the high half, the body's first word the low half.
            ushort xmcsm16 = result.SubHeader != null ? result.SubHeader.Xmcsm : (ushort)0;
            uint bodyWord = trailer.Length >= 2 ? (uint)((trailer[0] << 8) | trailer[1]) : 0u;
            uint xmcsm = ((uint)xmcsm16 << 16) | bodyWord;

            bool isLetter = xmcsm == (uint)XmcsmService.XsgsyRequest
                || xmcsm == (uint)XmcsmService.XsgsyReply
                || (xmcsm & 0xFFu) == XmcsmMask.XsletServiceLowByte;

            if (isLetter && trailer.Length >= XroutMessage.HeaderSize)
            {
                // CORRECTED 2026-08-04: the XROUT 4-byte serial / service / length header IS on the
                // wire, at absolute 28-31. The old "body-only" reading was an artefact of the
                // 13+19 boundary error, which swallowed those four bytes into ControlService, Pad
                // and UserDataLength. Worked example, the captured *FA-SERVER connect letter:
                // body = 1B41 0012 FF0A 2A46412D534552564552 ... - serial 0x1B, service 0x41
                // (XSLET), declared length 0x12 = 18, then the parameter blocks. The eight bytes
                // past the declared length (07E2 0000 0002 6400) are kept in TrailingBytes.
                try
                {
                    result.Body = XroutMessage.Parse(trailer, XroutMessageFraming.WithHeader);

                    int consumed = XroutMessage.HeaderSize + result.Body.Length;
                    if (consumed < trailer.Length)
                    {
                        result.TrailingBytes = trailer.Slice(consumed).ToArray();
                    }
                }
                catch (Exception)
                {
                    result.Body = null;
                    result.TrailingBytes = trailer.ToArray();
                }

                return;
            }

            // Non-letter channels (TAD session, DC/PAD terminal data, unknown XMCSM):
            // decode as a TAD message chain. TadChain never throws and keeps any odd
            // trailing byte in its Remainder, so nothing is lost.
            //
            // VERIFIED 2026-08-04 on the captured conn-to-d102 DUMM frame:
            //   2113 000E 0064 0066 0131 0108 DBDB | 2100 92 00 0064 02AB 0066 04C2 0108 | 0000 0002 1800
            // The body at absolute 28 opens with the SAME four bytes a letter does - serial 0x00,
            // service 0x00, then the big-endian length of what follows (0x0002) - and the TAD
            // chain itself (1800) begins at absolute 32. So the chain is taken from body offset 4.
            // File-access bodies do NOT have this header (theirs opens with the FaMessageType, e.g.
            // 0x07D2), but an FA frame's TAD view is never read - FaServer takes its body straight
            // off the serialised datagram at 28.
            //
            // The whole body is ALSO kept verbatim in TrailingBytes. The TAD view is a decode, not
            // a representation: without this a parsed FA or terminal frame would have no body at
            // all once RawBytes is cleared, and GetBodyBytes could not re-serialise it.
            result.TrailingBytes = trailer.ToArray();

            int chainOffset = trailer.Length >= XroutMessage.HeaderSize ? XroutMessage.HeaderSize : trailer.Length;
            result.Tad = TadChain.Parse(trailer.Slice(chainOffset));
        }

        /// <summary>
        /// Serialises this frame into a new array.
        /// </summary>
        /// <returns>
        /// A new array containing the serialised information field.
        /// </returns>
        public byte[] ToArray()
        {
            // When decoded from a capture, the retained raw bytes are authoritative so
            // that re-encoding is byte-identical even for un-modeled sub-protocol tails.
            if (RawBytes != null)
            {
                byte[] copy = new byte[RawBytes.Length];
                Array.Copy(RawBytes, copy, RawBytes.Length);
                return copy;
            }

            byte[] subBytes = EmptyTrailing;
            byte[] bodyBytes = EmptyTrailing;

            if (SubHeader != null)
            {
                subBytes = new byte[XmsgSubHeader.Size];
                SubHeader.Serialize(subBytes);
                // GetBodyBytes is the single definition of what sits at absolute 28 onward, so
                // decode and re-encode cannot drift apart.
                bodyBytes = GetBodyBytes();
            }
            else
            {
                // No sub-header: any payload is the raw trailing control bytes.
                bodyBytes = TrailingBytes;
            }

            int total = SintranHeader.Size + subBytes.Length + bodyBytes.Length;
            byte[] buffer = new byte[total];
            Header.Serialize(buffer);

            int cursor = SintranHeader.Size;
            for (int i = 0; i < subBytes.Length; i++)
            {
                buffer[cursor++] = subBytes[i];
            }

            for (int i = 0; i < bodyBytes.Length; i++)
            {
                buffer[cursor++] = bodyBytes[i];
            }

            return buffer;
        }

        /// <summary>
        /// Discards the retained raw bytes so that <see cref="ToArray"/> re-serialises
        /// this frame from its structured properties instead of the captured copy.
        /// </summary>
        public void ClearRawBytes()
        {
            RawBytes = null;
        }
    }
}
