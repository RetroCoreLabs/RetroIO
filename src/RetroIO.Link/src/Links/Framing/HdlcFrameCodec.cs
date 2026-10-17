//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers;

using RetroIO.Link.Hdlc;

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
    /// <para>
    /// Since 2026-09-06 this is a thin adapter over the shared engine in
    /// <see cref="RetroIO.Link.Hdlc"/> rather than its own hand-rolled loop - one of five separate
    /// HDLC framers that were merged. The wire format is unchanged and
    /// <c>FramingCodecTests</c> asserts the exact on-the-wire bytes either way. What the shared
    /// engine adds here is per-condition counters and an overlong frame that is discarded up to its
    /// closing flag rather than silently restarting mid-stream.
    /// </para>
    /// </remarks>
    public sealed class HdlcFrameCodec : FrameCodecBase, IDisposable
    {
        /// <summary>
        /// Maximum de-framed payload size in bytes (X.25 max packet plus LAPB overhead).
        /// </summary>
        public const int FrameMaxPayload = 4096;

        /// <summary>
        /// The framing policy this codec drives the shared engine with: RFC 1662 transmit escaping,
        /// and no FCS, because <c>CodecLinkBridge</c> owns the checksum at the layer above.
        /// </summary>
        private static readonly HdlcFramerOptions Policy = HdlcFramerOptions.FramingOnly with
        {
            MaxFrameLength = FrameMaxPayload,
        };

        private readonly HdlcDecoder _decoder = new HdlcDecoder(Policy);

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
            int offset = 0;
            while (offset < data.Length)
            {
                HdlcDecodeStatus status = _decoder.Decode(data.Slice(offset), out int consumed);
                offset += consumed;

                if (status == HdlcDecodeStatus.NeedMoreData)
                {
                    break;
                }

                if (status == HdlcDecodeStatus.FrameComplete)
                {
                    RaiseFrameReceived(_decoder.Frame);
                }

                // Runt, Overlong and Aborted are discarded here, as they were before. The decoder
                // has already counted each of them separately, which the hand-rolled loop this
                // replaces could not do - it lumped an oversized frame in with everything else.
            }
        }

        /// <inheritdoc/>
        public override void SendFrame(ReadOnlySpan<byte> frame)
        {
            int max = HdlcEncoder.GetMaxEncodedLength(frame.Length);
            byte[] rented = ArrayPool<byte>.Shared.Rent(max);
            try
            {
                int written = HdlcEncoder.Encode(frame, rented, Policy);
                Transport.Send(rented.AsSpan(0, written));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        /// <inheritdoc/>
        public override void Reset()
        {
            _decoder.Reset();
        }

        /// <summary>
        /// Returns the decoder's pooled assembly buffer.
        /// </summary>
        public void Dispose()
        {
            _decoder.Dispose();
        }
    }
}
