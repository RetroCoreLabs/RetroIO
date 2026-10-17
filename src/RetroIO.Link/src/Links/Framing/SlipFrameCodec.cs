//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers;

namespace RetroIO.Link.Framing
{
    /// <summary>
    /// SLIP (RFC 1055) byte-stuffed framing codec.
    /// </summary>
    /// <remarks>
    /// Self-synchronizing frame boundaries over any byte stream (serial or TCP). This codec performs
    /// delimiting and escaping ONLY — it carries no checksum. When it transports LAPB frames, the LAPB
    /// layer supplies the frame check sequence.
    ///
    /// Wire format (RFC 1055):
    ///  - Frames are delimited by END (<c>0xC0</c>). A leading END is emitted to flush line noise.
    ///  - A literal END inside the payload is escaped as ESC (<c>0xDB</c>) then ESC_END (<c>0xDC</c>).
    ///  - A literal ESC inside the payload is escaped as ESC (<c>0xDB</c>) then ESC_ESC (<c>0xDD</c>).
    ///
    /// Receive is an incremental byte-at-a-time state machine: an END with nothing buffered is treated as
    /// inter-frame fill and skipped; an unknown byte following ESC is accepted as-is (lenient, per the
    /// original SLIP spec); a frame that overflows the buffer is discarded and the codec resynchronizes.
    /// Behavior follows RFC 1055 for interoperability.
    /// </remarks>
    public sealed class SlipFrameCodec : FrameCodecBase
    {
        // RFC 1055 control octets.
        private const byte End = 0xC0;
        private const byte Esc = 0xDB;
        private const byte EscEnd = 0xDC;
        private const byte EscEsc = 0xDD;

        /// <summary>
        /// Maximum de-framed payload size in bytes (X.25 max packet plus LAPB overhead).
        /// </summary>
        public const int FrameMaxPayload = 4096;

        // Receive accumulation buffer and its current fill; _inEscape tracks a pending ESC.
        private readonly byte[] _rxBuffer = new byte[FrameMaxPayload];
        private int _rxLen;
        private bool _inEscape;

        /// <summary>
        /// Initializes a SLIP codec.
        /// </summary>
        /// <param name="linkId">
        /// Identifier of the owning link.
        /// </param>
        /// <param name="transport">
        /// The transport sink that framed bytes are written to.
        /// </param>
        public SlipFrameCodec(string linkId, IFrameTransport transport)
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
                    case End:
                        // An END always delimits, regardless of a dangling escape.
                        if (_rxLen == 0)
                        {
                            // Empty frame (inter-frame fill or leading END) -- skip.
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
                            if (b == EscEnd)
                            {
                                value = End;
                            }
                            else if (b == EscEsc)
                            {
                                value = Esc;
                            }
                            // Any other byte after ESC is a protocol violation -- accept it as-is.
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
            // Worst case: every byte escaped (2x) plus leading and trailing END.
            int maxLen = (frame.Length * 2) + 2;
            byte[] rented = ArrayPool<byte>.Shared.Rent(maxLen);
            try
            {
                int pos = 0;
                rented[pos++] = End; // leading END flushes accumulated line noise
                for (int i = 0; i < frame.Length; i++)
                {
                    byte b = frame[i];
                    if (b == End)
                    {
                        rented[pos++] = Esc;
                        rented[pos++] = EscEnd;
                    }
                    else if (b == Esc)
                    {
                        rented[pos++] = Esc;
                        rented[pos++] = EscEsc;
                    }
                    else
                    {
                        rented[pos++] = b;
                    }
                }

                rented[pos++] = End; // trailing END marks the frame boundary
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
