using System;

namespace RetroIO.Protocols.Xmsg.Packet
{
    /// <summary>
    /// Read-only decoded view of an incoming XMSG packet: the SINTRAN header fields, the optional
    /// XMSG sub-header fields, and the trailing payload span - plus the derived envelope values the
    /// universal model (XMSG-PROTOCOL.md section 18.5) is built on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a thin, immutable facade over the proven <see cref="XmsgFrame"/> decoder. It exists
    /// so the codec/layer above it depend on a stable, intention-revealing shape (packet TYPE,
    /// derived <see cref="Base"/>, endpoint accessors) rather than on the wire class. The underlying
    /// frame is retained in <see cref="Frame"/> for the services that still need the full structured
    /// decode (TAD chain, XROUT body) during the migration.
    /// </para>
    /// <para>
    /// <b>The universal envelope model.</b> Across all 209 verified frames the relationship
    /// <c>Base = Flags1 + Counter</c> holds, and from it both the channel and the per-direction
    /// counter are recoverable (<c>Channel = 0xDE - (XMCSM>>24) - (Base>>8)</c>,
    /// <c>Counter = Base0 - Flags1</c>). <see cref="Base"/> exposes the first half of that identity;
    /// the layer derives the rest when it needs to build a reply on a derived channel.
    /// </para>
    /// </remarks>
    public sealed class XmsgPacketInfo
    {
        private readonly XmsgFrame _frame;

        /// <summary>
        /// Initialises the view over an already-decoded frame.
        /// </summary>
        /// <param name="frame">
        /// The decoded frame; must not be null and must have a header.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="frame"/> or its header is null.
        /// </exception>
        public XmsgPacketInfo(XmsgFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (frame.Header == null)
            {
                throw new ArgumentNullException(nameof(frame), "Frame header is null.");
            }

            _frame = frame;
        }

        /// <summary>
        /// Gets the underlying decoded frame (structured TAD/XROUT views, raw bytes).
        /// </summary>
        public XmsgFrame Frame
        {
            get { return _frame; }
        }

        /// <summary>
        /// Gets the transport-level packet type (the SINTRAN subtype at header offset 3), mapped to
        /// the seam classifier. An un-modelled subtype maps to <see cref="XmsgPacketType.Unknown"/>.
        /// </summary>
        public XmsgPacketType Type
        {
            get
            {
                byte subtype = (byte)_frame.Header.Subtype;
                // Only the five modelled subtypes classify; anything else stays Unknown so the
                // layer can log-and-ignore rather than mis-dispatch. (No LINQ - explicit switch.)
                switch (subtype)
                {
                    case (byte)XmsgPacketType.Ack:
                    case (byte)XmsgPacketType.NetworkError:
                    case (byte)XmsgPacketType.Data:
                    case (byte)XmsgPacketType.ReachabilityReply:
                    case (byte)XmsgPacketType.ReachabilityRequest:
                        return (XmsgPacketType)subtype;
                    default:
                        return XmsgPacketType.Unknown;
                }
            }
        }

        /// <summary>
        /// Gets the destination node number (SINTRAN header offsets 4-5).
        /// </summary>
        public ushort DestinationNode
        {
            get { return _frame.Header.DestinationNode; }
        }

        /// <summary>
        /// Gets the source node number (SINTRAN header offsets 6-7).
        /// </summary>
        public ushort SourceNode
        {
            get { return _frame.Header.SourceNode; }
        }

        /// <summary>
        /// Gets Flags 1 (offsets 8-9): the datagram sequence, or 0xFFFF on broadcast.
        /// </summary>
        public ushort Flags1
        {
            get { return _frame.Header.Flags1; }
        }

        /// <summary>
        /// Gets Flags 2 (offsets 10-11): the frame-class word (a negative XE code for a network error).
        /// </summary>
        public ushort Flags2
        {
            get { return _frame.Header.Flags2; }
        }

        /// <summary>
        /// Gets the HIGH byte of <see cref="Checksum"/> - wire offset 12.
        /// </summary>
        /// <remarks>
        /// Was called <c>ProtocolId</c>; the <see cref="SintranProtocolId"/> names are misnomers
        /// for a checksum byte. See <see cref="SintranHeader.ChecksumHigh"/>.
        /// </remarks>
        public byte ChecksumHigh
        {
            get { return _frame.Header.ChecksumHigh; }
        }

        /// <summary>
        /// Gets a value indicating whether this packet carries an XMSG sub-header (true for data
        /// frames, false for short Ack / reachability / network-error frames).
        /// </summary>
        public bool HasSubHeader
        {
            get { return _frame.SubHeader != null; }
        }

        /// <summary>
        /// Gets the XMSG sub-header, or <c>null</c> for a short frame. Prefer the typed accessors
        /// (<see cref="ChecksumLow"/>, <see cref="XmcsmWithBodyWord"/>, ...) which are null-safe.
        /// </summary>
        public XmsgSubHeader? SubHeader
        {
            get { return _frame.SubHeader; }
        }

        /// <summary>
        /// Gets header word 6 (offsets 12-13): the ones-complement checksum over words 0-5.
        /// </summary>
        public ushort Checksum
        {
            get { return _frame.Header.Checksum; }
        }

        /// <summary>
        /// Gets the LOW byte of <see cref="Checksum"/> - wire offset 13.
        /// </summary>
        /// <remarks>
        /// Was called <c>Counter</c>. It is part of the SINTRAN header, not the XMSG sub-header,
        /// and it is not a counter - see <see cref="SintranHeader.ChecksumLow"/>.
        /// </remarks>
        public byte ChecksumLow
        {
            get { return _frame.Header.ChecksumLow; }
        }

        /// <summary>
        /// Gets the sub-header frame-flags byte (offset 3), or 0 for a short frame.
        /// </summary>
        public byte FrameFlags
        {
            get { return _frame.SubHeader != null ? _frame.SubHeader.FrameFlags : (byte)0; }
        }

        /// <summary>
        /// Gets the sub-header role byte (offset 4; low nibble 4 = asker, 0 = responder), or 0.
        /// </summary>
        public byte Role
        {
            get { return _frame.SubHeader != null ? _frame.SubHeader.Role : (byte)0; }
        }

        /// <summary>
        /// Gets the XMDSY destination system (offsets 5-6), or 0 for a short frame.
        /// </summary>
        public ushort DestinationSystem
        {
            get { return _frame.SubHeader != null ? _frame.SubHeader.DestinationSystem : (ushort)0; }
        }

        /// <summary>
        /// Gets the XMDPT destination port (offsets 7-8), or 0 for a short frame.
        /// </summary>
        public ushort DestinationPort
        {
            get { return _frame.SubHeader != null ? _frame.SubHeader.DestinationPort : (ushort)0; }
        }

        /// <summary>
        /// Gets the XMSSY source system (offsets 9-10), or 0 for a short frame.
        /// </summary>
        public ushort SourceSystem
        {
            get { return _frame.SubHeader != null ? _frame.SubHeader.SourceSystem : (ushort)0; }
        }

        /// <summary>
        /// Gets the XMSPT source port (offsets 11-12), or 0 for a short frame.
        /// </summary>
        public ushort SourcePort
        {
            get { return _frame.SubHeader != null ? _frame.SubHeader.SourcePort : (ushort)0; }
        }

        /// <summary>
        /// Gets XMCSM (wire 26-27), the last word of the sub-header, or 0 for a short frame.
        /// </summary>
        public ushort Xmcsm
        {
            get { return _frame.SubHeader != null ? _frame.SubHeader.Xmcsm : (ushort)0; }
        }

        /// <summary>
        /// Gets the message body, from absolute wire offset 28 onward.
        /// </summary>
        public byte[] Body
        {
            get { return _frame.GetBodyBytes(); }
        }

        /// <summary>
        /// Gets the XMCSM word and the first body word composed into one 32-bit value:
        /// <c>(XMCSM shifted left 16) | firstBodyWord</c>, or 0 for a short frame.
        /// </summary>
        /// <remarks>
        /// Two fields from opposite sides of the sub-header/body boundary, joined because the
        /// <see cref="XmcsmService"/> constants are written that way. See
        /// <see cref="XmsgFrame.XmcsmWithBodyWord"/> for the full account.
        /// </remarks>
        public uint XmcsmWithBodyWord
        {
            get { return _frame.XmcsmWithBodyWord; }
        }

        /// <summary>
        /// Gets body byte 3 (wire 31) under its historical name, or 0 when there is none.
        /// </summary>
        /// <remarks>
        /// COMPATIBILITY VIEW. XMLEN is not on the wire here; on a letter this is the LOW byte of
        /// the XROUT declared length.
        /// </remarks>
        [Obsolete("Body byte 3 (wire 31), not XMLEN. Read it from Body.")]
        public byte UserDataLength
        {
            get
            {
                byte[] body = _frame.GetBodyBytes();
                return body.Length > 3 ? body[3] : (byte)0;
            }
        }

        /// <summary>
        /// Gets the trailing control byte of a short frame (Ack / reachability / network error), or
        /// 0 when the frame carries no trailing byte.
        /// </summary>
        /// <remarks>
        /// For an Ack this is the decrementing per-direction counter; for a reachability reply the
        /// reply command byte; for a network error the reject code byte. Data frames carry their
        /// content in the sub-header + trailing payload of the underlying <see cref="Frame"/> instead.
        /// </remarks>
        public byte TrailingByte
        {
            get
            {
                byte[]? trailing = _frame.TrailingBytes;
                return (trailing != null && trailing.Length > 0) ? trailing[0] : (byte)0;
            }
        }

        /// <summary>
        /// Gets the derived envelope base <c>Flags1 + Counter</c> - the first half of the universal
        /// envelope identity. For a short frame (no sub-header) this equals Flags1.
        /// </summary>
        public ushort Base
        {
            get { return (ushort)(_frame.Header.Flags1 + (byte)(_frame.Header.Checksum & 0x00FF)); }
        }

        /// <summary>
        /// Gets the exact original information-field bytes this packet decoded from, when available
        /// (a packet parsed from the wire always has them).
        /// </summary>
        public ReadOnlySpan<byte> RawBytes
        {
            get { return _frame.RawBytes != null ? _frame.RawBytes : ReadOnlySpan<byte>.Empty; }
        }
    }
}
