using System;
using Microsoft.Extensions.Logging;

using RetroIO.Link.Hdlc;

namespace RetroIO.Link
{
    /// <summary>
    /// Event args for frame events
    /// </summary>
    public class FrameEventArgs : EventArgs
    {
        /// <summary>Frame data (unstuffed, FCS stripped)</summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();
        /// <summary>Frame length</summary>
        public int Length { get; set; }
        /// <summary>Whether FCS was valid</summary>
        public bool FcsValid { get; set; }
        /// <summary>Timestamp</summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Handles HDLC byte-stuffing, frame detection, and FCS calculation/validation.
    /// Used by SerialLink, TcpLapbLink and XotLink for physical layer framing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Since 2026-09-06 this is a thin adapter over the shared engine in
    /// <see cref="RetroIO.Link.Hdlc"/>. It was one of five separate HDLC framers in this
    /// repository; the event-based surface is kept because three links depend on it, but the
    /// framing itself is no longer implemented here.
    /// </para>
    /// <para>
    /// <b>Two long-standing defects went away with the change</b>, both from treating a flag as a
    /// pure toggle rather than as a delimiter:
    /// </para>
    /// <list type="bullet">
    ///   <item>a single flag shared as one frame's closing flag and the next frame's opening flag
    ///   lost the second frame, although ITU-T X.25 clause 2.2.1 permits sharing in as many
    ///   words;</item>
    ///   <item>a RUN of flags alternated the in-frame state, so whether the following frame was
    ///   seen at all depended on whether the run length was odd or even - and inter-frame time
    ///   fill IS a run of flags (clause 2.2.4).</item>
    /// </list>
    /// <para>
    /// Both were pinned by <c>HdlcFramerTests</c> before the merge and those tests were updated
    /// when the fix landed.
    /// </para>
    /// </remarks>
    public class HdlcFramer : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _linkName;

        /// <summary>
        /// The framing policy this framer drives the shared engine with.
        /// </summary>
        /// <remarks>
        /// RFC 1662 transmit escaping, because that is what this framer has always emitted and its
        /// peers expect. Corrupt frames are delivered rather than dropped, because all three
        /// callers read <see cref="FrameEventArgs.FcsValid"/> and count the failures.
        /// </remarks>
        private static readonly HdlcFramerOptions Policy = HdlcFramerOptions.X25 with
        {
            DeliverCorruptFrames = true,
        };

        private readonly HdlcDecoder _decoder = new HdlcDecoder(Policy);

        /// <summary>
        /// Create a new HDLC framer
        /// </summary>
        public HdlcFramer(ILogger logger, string linkName)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _linkName = linkName ?? throw new ArgumentNullException(nameof(linkName));
        }

        /// <summary>
        /// Raised when a complete frame is received (FCS stripped, unstuffed)
        /// </summary>
        public event EventHandler<FrameEventArgs>? FrameReceived;

        /// <summary>
        /// Raised when stuffed bytes should be sent to physical layer
        /// </summary>
        public event EventHandler<byte[]>? BytesToSend;

        /// <summary>
        /// Process received raw bytes (from serial port or TCP)
        /// </summary>
        /// <param name="buffer">Received bytes</param>
        /// <param name="length">Number of bytes</param>
        public void ProcessBytes(byte[] buffer, int length)
        {
            if (buffer == null || length <= 0)
            {
                return;
            }

            ReadOnlySpan<byte> data = new ReadOnlySpan<byte>(buffer, 0, length);

            int offset = 0;
            while (offset < data.Length)
            {
                HdlcDecodeStatus status = _decoder.Decode(data.Slice(offset), out int consumed);
                offset += consumed;

                if (status == HdlcDecodeStatus.NeedMoreData)
                {
                    break;
                }

                if (status == HdlcDecodeStatus.FrameComplete || status == HdlcDecodeStatus.BadFcs)
                {
                    RaiseFrame(status == HdlcDecodeStatus.FrameComplete);
                    continue;
                }

                if (status == HdlcDecodeStatus.Runt)
                {
                    _logger.LogWarning("HDLC[{Link}]: Short frame discarded", _linkName);
                }
                else if (status == HdlcDecodeStatus.Overlong)
                {
                    _logger.LogWarning("HDLC[{Link}]: Oversized frame discarded", _linkName);
                }
                else if (status == HdlcDecodeStatus.Aborted)
                {
                    // An escape immediately followed by a flag (X.25 clause 2.3.5.3 f). Silently
                    // discarded and explicitly NOT counted as an FCS error.
                    _logger.LogDebug("HDLC[{Link}]: Frame aborted", _linkName);
                }
            }
        }

        /// <summary>
        /// Hands the decoder's current frame to subscribers with the FCS stripped.
        /// </summary>
        /// <param name="fcsValid">Whether the frame passed its FCS check.</param>
        /// <remarks>
        /// The shared decoder delivers the frame WITH its FCS, because a framer should not have to
        /// know how wide the checksum is for the layer above. This surface has always stripped it,
        /// so the adapter does that here rather than changing what three links receive.
        /// </remarks>
        private void RaiseFrame(bool fcsValid)
        {
            EventHandler<FrameEventArgs>? handler = FrameReceived;
            if (handler is null)
            {
                return;
            }

            ReadOnlySpan<byte> frame = _decoder.Frame;
            int payloadLength = frame.Length - 2;
            if (payloadLength < 0)
            {
                return;
            }

            byte[] payload = frame.Slice(0, payloadLength).ToArray();

            handler(this, new FrameEventArgs
            {
                Data = payload,
                Length = payloadLength,
                FcsValid = fcsValid,
            });
        }

        /// <summary>
        /// Build and emit a stuffed frame with its FCS.
        /// </summary>
        /// <param name="frameData">Frame data (Address + Control + Information)</param>
        /// <param name="length">Frame length</param>
        public void SendFrame(byte[] frameData, int length)
        {
            if (frameData == null || length <= 0)
            {
                _logger.LogWarning("HDLC[{Link}]: Invalid frame data for transmission", _linkName);
                return;
            }

            byte[] wire = HdlcEncoder.Encode(new ReadOnlySpan<byte>(frameData, 0, length), Policy);

            _logger.LogDebug("HDLC[{Link}]: Sending frame ({Length} bytes, stuffed to {StuffedLength})",
                _linkName, length, wire.Length);

            BytesToSend?.Invoke(this, wire);
        }

        /// <summary>
        /// Discard any partially received frame and hunt for the next opening flag.
        /// </summary>
        public void Reset()
        {
            _decoder.Reset();
            _logger.LogDebug("HDLC[{Link}]: Framer reset", _linkName);
        }

        /// <summary>Returns the decoder's pooled assembly buffer.</summary>
        public void Dispose()
        {
            _decoder.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
