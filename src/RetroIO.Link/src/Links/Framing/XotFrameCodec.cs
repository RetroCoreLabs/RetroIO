using System;
using System.Buffers;

namespace RetroIO.Link.Framing
{
    /// <summary>
    /// XOT (X.25 over TCP, RFC 1613) length-prefixed framing codec.
    /// </summary>
    /// <remarks>
    /// XOT carries raw X.25 Layer 3 packets directly over TCP with no LAPB and no checksum, since TCP
    /// provides reliable ordered delivery. Used for Cisco router interoperability (default port TCP 1998).
    ///
    /// Wire format (RFC 1613): a 4-byte header followed by the payload:
    ///  - version: 2 bytes, big-endian, always <c>0x0000</c>.
    ///  - length: 2 bytes, big-endian, the payload byte count.
    ///  - payload: the raw X.25 packet.
    ///
    /// Receive is a streaming parser: bytes are accumulated until a full header plus its declared payload
    /// are present, then the payload is delivered and the buffer is compacted. It handles partial reads
    /// (a frame split across several TCP segments) and several frames arriving in one read. A non-zero
    /// version or an over-length declaration discards the buffer and resynchronizes, matching the C
    /// reference (x25emu src/framing/xot.c).
    /// </remarks>
    public sealed class XotFrameCodec : FrameCodecBase
    {
        // RFC 1613 header layout.
        private const int HeaderSize = 4;
        private const int Version = 0x0000;

        /// <summary>
        /// Maximum payload (X.25 packet) size in bytes.
        /// </summary>
        public const int FrameMaxPayload = 4096;

        // Staging buffer holds at most one full frame; partial data carries across calls.
        private readonly byte[] _rx = new byte[HeaderSize + FrameMaxPayload];
        private int _rxLen;

        /// <summary>
        /// Initializes an XOT codec.
        /// </summary>
        /// <param name="linkId">
        /// Identifier of the owning link.
        /// </param>
        /// <param name="transport">
        /// The transport sink that framed bytes are written to.
        /// </param>
        public XotFrameCodec(string linkId, IFrameTransport transport)
            : base(linkId, transport)
        {
        }

        /// <inheritdoc/>
        public override void ProcessBytes(ReadOnlySpan<byte> data)
        {
            int i = 0;
            while (i < data.Length)
            {
                int space = _rx.Length - _rxLen;
                if (space == 0)
                {
                    // Staging full with no complete frame parsed -- malformed, discard and resync.
                    _rxLen = 0;
                    space = _rx.Length;
                }

                int take = Math.Min(space, data.Length - i);
                data.Slice(i, take).CopyTo(_rx.AsSpan(_rxLen));
                _rxLen += take;
                i += take;
                DrainFrames();
            }
        }

        /// <inheritdoc/>
        public override void SendFrame(ReadOnlySpan<byte> frame)
        {
            if (frame.Length > FrameMaxPayload)
            {
                throw new ArgumentException($"XOT payload {frame.Length} exceeds maximum {FrameMaxPayload}.", nameof(frame));
            }

            int total = HeaderSize + frame.Length;
            byte[] rented = ArrayPool<byte>.Shared.Rent(total);
            try
            {
                rented[0] = 0x00;                          // version high
                rented[1] = 0x00;                          // version low
                rented[2] = (byte)(frame.Length >> 8);     // length high
                rented[3] = (byte)(frame.Length & 0xFF);   // length low
                frame.CopyTo(rented.AsSpan(HeaderSize));
                Transport.Send(rented.AsSpan(0, total));
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
        }

        // Parses and delivers every complete frame currently in the staging buffer, then compacts.
        private void DrainFrames()
        {
            int offset = 0;
            while (_rxLen - offset >= HeaderSize)
            {
                int version = (_rx[offset] << 8) | _rx[offset + 1];
                int length = (_rx[offset + 2] << 8) | _rx[offset + 3];

                if (version != Version || length > FrameMaxPayload)
                {
                    // Protocol violation -- discard everything and resynchronize.
                    _rxLen = 0;
                    return;
                }

                int frameSize = HeaderSize + length;
                if (_rxLen - offset < frameSize)
                {
                    // Header present but payload incomplete -- wait for more bytes.
                    break;
                }

                RaiseFrameReceived(_rx.AsSpan(offset + HeaderSize, length));
                offset += frameSize;
            }

            if (offset > 0)
            {
                int remain = _rxLen - offset;
                if (remain > 0)
                {
                    _rx.AsSpan(offset, remain).CopyTo(_rx.AsSpan(0));
                }
                _rxLen = remain;
            }
        }
    }
}
