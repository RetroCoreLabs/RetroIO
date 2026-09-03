using System;
using System.Buffers;

namespace RetroIO.Link.Framing
{
    /// <summary>
    /// HDLC/PPP (RFC 1662) byte-stuffed framing codec.
    /// </summary>
    /// <remarks>
    /// RFC 1662 "PPP in HDLC-like Framing" — the standard way to carry HDLC/LAPB frames over
    /// byte-oriented links (async serial, TCP). This is NOT full PPP: there is no LCP/NCP negotiation,
    /// no authentication and no PPP Protocol field — only the framing. Like <see cref="SlipFrameCodec"/>,
    /// it performs delimiting and escaping ONLY and carries no checksum; LAPB supplies the FCS.
    ///
    /// Wire format (RFC 1662):
    ///  - Frames are delimited by FLAG (<c>0x7E</c>). A leading FLAG is emitted to flush line noise.
    ///  - FLAG and ESC (<c>0x7D</c>) inside the payload are escaped as ESC then (byte XOR <c>0x20</c>).
    ///  - On transmit, control octets below <c>0x20</c> are also escaped (RFC 1662 async transparency /
    ///    ACCM). This is a superset of minimal FLAG/ESC escaping; it remains interoperable because the
    ///    receiver decodes every escaped byte by XOR <c>0x20</c> regardless.
    ///
    /// Receive mirrors <see cref="SlipFrameCodec"/>: a FLAG always delimits; an empty frame between flags
    /// is skipped; buffer overflow discards and resynchronizes. Behavior follows RFC 1662.
    /// </remarks>
    public sealed class HdlcFrameCodec : FrameCodecBase
    {
        // RFC 1662 control octets.
        private const byte Flag = 0x7E;
        private const byte Esc = 0x7D;
        private const byte Xor = 0x20;

        // Control octets at or above this value are not subject to ACCM transmit escaping.
        private const byte ControlCharLimit = 0x20;

        /// <summary>
        /// Maximum de-framed payload size in bytes (X.25 max packet plus LAPB overhead).
        /// </summary>
        public const int FrameMaxPayload = 4096;

        // Receive accumulation buffer and its current fill; _inEscape tracks a pending ESC.
        private readonly byte[] _rxBuffer = new byte[FrameMaxPayload];
        private int _rxLen;
        private bool _inEscape;

        /// <summary>
        /// Initializes an HDLC/PPP codec.
        /// </summary>
        /// <param name="linkId">
        /// Identifier of the owning link.
        /// </param>
        /// <param name="transport">
        /// The transport sink that framed bytes are written to.
        /// </param>
        public HdlcFrameCodec(string linkId, IFrameTransport transport)
            : base(linkId, transport)
        {
        }

        /// <inheritdoc/>
        public override void ProcessBytes(ReadOnlySpan<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                switch (b)
                {
                    case Flag:
                        // A FLAG always delimits, regardless of a dangling escape.
                        if (_rxLen == 0)
                        {
                            // Empty frame (inter-frame fill or leading FLAG) -- skip.
                            _inEscape = false;
                            break;
                        }
                        RaiseFrameReceived(_rxBuffer.AsSpan(0, _rxLen));
                        _rxLen = 0;
                        _inEscape = false;
                        break;

                    case Esc:
                        _inEscape = true;
                        break;

                    default:
                        byte value = b;
                        if (_inEscape)
                        {
                            _inEscape = false;
                            value = (byte)(b ^ Xor); // undo the transmit XOR encoding
                        }

                        if (_rxLen >= FrameMaxPayload)
                        {
                            // Frame too large -- discard and resync.
                            _rxLen = 0;
                            _inEscape = false;
                            break;
                        }

                        _rxBuffer[_rxLen++] = value;
                        break;
                }
            }
        }

        /// <inheritdoc/>
        public override void SendFrame(ReadOnlySpan<byte> frame)
        {
            // Worst case: every byte escaped (2x) plus leading and trailing FLAG.
            int maxLen = (frame.Length * 2) + 2;
            byte[] rented = ArrayPool<byte>.Shared.Rent(maxLen);
            try
            {
                int pos = 0;
                rented[pos++] = Flag; // leading FLAG flushes accumulated line noise
                for (int i = 0; i < frame.Length; i++)
                {
                    byte b = frame[i];
                    if (b == Flag || b == Esc || b < ControlCharLimit)
                    {
                        rented[pos++] = Esc;
                        rented[pos++] = (byte)(b ^ Xor);
                    }
                    else
                    {
                        rented[pos++] = b;
                    }
                }

                rented[pos++] = Flag; // trailing FLAG marks the frame boundary
                Transport.Send(rented.AsSpan(0, pos));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        /// <inheritdoc/>
        public override void Reset()
        {
            _rxLen = 0;
            _inEscape = false;
        }
    }
}
