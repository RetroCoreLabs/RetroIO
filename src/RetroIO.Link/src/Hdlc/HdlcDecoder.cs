using System;
using System.Buffers;

namespace RetroIO.Link.Hdlc
{
    /// <summary>
    /// The outcome of one <see cref="HdlcDecoder.Decode"/> step.
    /// </summary>
    public enum HdlcDecodeStatus : byte
    {
        /// <summary>The input was consumed without completing a frame. Supply more octets.</summary>
        NeedMoreData = 0,

        /// <summary>A frame arrived and passed the FCS check; it is in <see cref="HdlcDecoder.Frame"/>.</summary>
        FrameComplete = 1,

        /// <summary>
        /// A frame reached its closing flag but the FCS residue was wrong (X.25 clause 2.3.5.3 d,
        /// Q.921 clause 2.9 d). Delivered only when
        /// <see cref="HdlcFramerOptions.DeliverCorruptFrames"/> is set; otherwise dropped.
        /// </summary>
        BadFcs = 2,

        /// <summary>
        /// Fewer octets arrived between the flags than <see cref="HdlcFramerOptions.MinFrameLength"/>.
        /// </summary>
        /// <remarks>
        /// A separate status from <see cref="BadFcs"/> on purpose: RFC 1662 sections 4.3 and 5.3
        /// require a runt to be discarded silently and explicitly NOT counted as an FCS error, and
        /// a monitoring tool that lumps the two together will mislead whoever reads it.
        /// </remarks>
        Runt = 3,

        /// <summary>
        /// More octets arrived without a closing flag than <see cref="HdlcFramerOptions.MaxFrameLength"/>.
        /// A local guard, not a standards condition.
        /// </summary>
        Overlong = 4,

        /// <summary>
        /// An abort: on an octet-stuffed link, a control escape immediately followed by a flag
        /// (X.25 clause 2.3.5.3 f, RFC 1662 section 4.2).
        /// </summary>
        Aborted = 5,
    }

    /// <summary>
    /// Counters a decoder keeps about the stream it has seen.
    /// </summary>
    public struct HdlcStatistics
    {
        /// <summary>Frames delivered with a valid FCS.</summary>
        public long GoodFrames;

        /// <summary>Frames discarded for an FCS error (X.25 clause 2.3.5.3 d).</summary>
        public long FcsErrors;

        /// <summary>Frames discarded for being shorter than the minimum.</summary>
        public long Runts;

        /// <summary>Frames discarded for exceeding the maximum.</summary>
        public long Overlong;

        /// <summary>Aborts received.</summary>
        public long Aborts;

        /// <summary>
        /// Flag octets seen while no frame was in progress - inter-frame time fill
        /// (X.25 clause 2.2.4). A zero-length run between two flags is counted here, never as a runt.
        /// </summary>
        public long IdleFlags;

        /// <summary>Octets consumed from the wire, transparency included.</summary>
        public long OctetsIn;
    }

    /// <summary>
    /// Reassembles HDLC frames from a byte stream that may split a frame across reads and may carry
    /// several frames in one read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not thread-safe, and not re-entrant.</b> An instance holds the state of exactly one
    /// direction of one link. Two threads calling <see cref="Decode"/> concurrently, or a handler
    /// calling back into it, will corrupt the assembly buffer and the running FCS with no
    /// diagnostic. If two threads receive from one link, serialise them yourself - an internal lock
    /// is deliberately not provided, because it would cost an interlocked operation per read on a
    /// type whose correct use is single-threaded, and it would hide the real bug.
    /// </para>
    /// <para>
    /// The assembly buffer is rented from <see cref="ArrayPool{T}"/>, so the decoder must be
    /// disposed. Steady-state decoding allocates nothing per frame.
    /// </para>
    /// <para>
    /// <b>Flag handling</b> follows X.25 clause 2.2.1: a flag while a frame is in progress closes
    /// that frame <em>and opens the next</em>, and a flag while no frame is in progress is time
    /// fill and is ignored. That single rule is what makes shared flags work and makes the length
    /// of a flag run irrelevant - two things the framer this replaces got wrong.
    /// </para>
    /// </remarks>
    public sealed class HdlcDecoder : IDisposable
    {
        private readonly HdlcFramerOptions _options;

        private byte[] _buffer;
        private int _length;

        // Length of the frame the last Decode call reported, so that Frame stays readable after
        // the assembly state has been reset for the next one. Zero means "no frame".
        private int _frameLength;
        private ushort _fcs;
        private bool _escaped;

        // False until the first flag has been seen, and again after a Reset. While false every
        // octet is discarded: X.25 clause 2.3.5.3 a makes a frame invalid unless it is "properly
        // bounded by two flags", so content that never had an opening flag is not a frame and must
        // not be handed up. Two of the engines this replaces got this right by ignoring anything
        // received while out of frame; one did not, and would turn the tail of a discarded frame
        // into a frame of its own.
        private bool _seenFlag;
        private bool _overflowed;
        private bool _disposed;

        private HdlcStatistics _statistics;

        /// <summary>Creates a decoder for a link with the given framing policy.</summary>
        /// <param name="options">Framing policy; may be shared with other decoders and encoders.</param>
        public HdlcDecoder(HdlcFramerOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();

            int initial = _options.MaxFrameLength < 512 ? _options.MaxFrameLength : 512;
            _buffer = ArrayPool<byte>.Shared.Rent(initial);
            _fcs = HdlcFcs.Init;
        }

        /// <summary>The framing policy this decoder was created with.</summary>
        public HdlcFramerOptions Options => _options;

        /// <summary>
        /// The frame the last <see cref="Decode"/> call reported: transparency removed, FCS
        /// <b>included</b>.
        /// </summary>
        /// <value>
        /// A view into the decoder's own buffer, valid only until the next call to
        /// <see cref="Decode"/>, <see cref="Reset"/> or <see cref="Dispose"/>. Copy it if you need
        /// to keep it, and never hold it across an <c>await</c>.
        /// </value>
        /// <remarks>
        /// The FCS is left on deliberately. Three of the five framers this class replaces delivered
        /// it and one stripped it, and keeping it means the framer never has to know how wide the
        /// FCS is for the layer above. Callers strip the last two octets.
        /// </remarks>
        public ReadOnlySpan<byte> Frame =>
            _frameLength == 0 ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(_buffer, 0, _frameLength);

        /// <summary>Counters since construction or the last <see cref="ResetStatistics"/>.</summary>
        public HdlcStatistics Statistics => _statistics;

        /// <summary>
        /// Consumes octets until one frame completes, one error is detected, or the input runs out.
        /// </summary>
        /// <param name="input">Newly received octets.</param>
        /// <param name="consumed">
        /// How many octets were taken. On any status other than
        /// <see cref="HdlcDecodeStatus.NeedMoreData"/> this stops just past the flag that ended the
        /// frame, so the rest of the buffer is still available to the next call. On
        /// <see cref="HdlcDecodeStatus.NeedMoreData"/> it always equals the input length.
        /// </param>
        /// <returns>What happened. Nothing here throws for a framing error - they are values.</returns>
        /// <example>
        /// Draining a read that may hold several frames:
        /// <code>
        /// int offset = 0;
        /// while (offset &lt; received.Length)
        /// {
        ///     HdlcDecodeStatus status = decoder.Decode(received.AsSpan(offset), out int consumed);
        ///     offset += consumed;
        ///     if (status == HdlcDecodeStatus.NeedMoreData) break;
        ///     if (status == HdlcDecodeStatus.FrameComplete) Handle(decoder.Frame);
        /// }
        /// </code>
        /// </example>
        public HdlcDecodeStatus Decode(ReadOnlySpan<byte> input, out int consumed)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _frameLength = 0;

            for (int i = 0; i < input.Length; i++)
            {
                byte b = input[i];
                _statistics.OctetsIn++;

                if (b == HdlcFramerOptions.Flag)
                {
                    if (!_seenFlag)
                    {
                        // The opening flag we were hunting for. Nothing before it was a frame.
                        _seenFlag = true;
                        _statistics.IdleFlags++;
                        continue;
                    }

                    // An escape immediately followed by a flag is an abort (X.25 clause 2.3.5.3 f,
                    // RFC 1662 section 4.2). Discard silently and go back to hunting - and note it
                    // is NOT an FCS error.
                    if (_escaped)
                    {
                        _statistics.Aborts++;
                        StartNewFrame();
                        consumed = i + 1;
                        return HdlcDecodeStatus.Aborted;
                    }

                    // A flag with nothing accumulated is inter-frame time fill (clause 2.2.4).
                    // This is the branch that makes flag runs harmless and their parity irrelevant.
                    if (_length == 0 && !_overflowed)
                    {
                        _statistics.IdleFlags++;
                        continue;
                    }

                    HdlcDecodeStatus status = CompleteFrame();
                    consumed = i + 1;
                    return status;
                }

                if (!_seenFlag)
                {
                    // Still hunting: discard inter-frame junk rather than accumulating it.
                    continue;
                }

                if (_escaped)
                {
                    Append((byte)(b ^ HdlcFramerOptions.EscapeMask));
                    _escaped = false;
                    continue;
                }

                if (b == HdlcFramerOptions.ControlEscape)
                {
                    _escaped = true;
                    continue;
                }

                Append(b);
            }

            consumed = input.Length;
            return HdlcDecodeStatus.NeedMoreData;
        }

        /// <summary>
        /// Discards any partially assembled frame and returns to hunting for a flag, as after a
        /// line drop. Counters are kept.
        /// </summary>
        public void Reset()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StartNewFrame();
            _seenFlag = false;
            _frameLength = 0;
        }

        /// <summary>Zeroes the counters.</summary>
        public void ResetStatistics()
        {
            _statistics = default;
        }

        /// <summary>Returns the assembly buffer to the pool.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _frameLength = 0;
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = Array.Empty<byte>();
        }

        /// <summary>
        /// Decides what a completed run of octets between flags actually was.
        /// </summary>
        private HdlcDecodeStatus CompleteFrame()
        {
            if (_overflowed)
            {
                _statistics.Overlong++;
                StartNewFrame();
                return HdlcDecodeStatus.Overlong;
            }

            int length = _length;

            // With UseFcs false this framer is delimiting and transparency only - the layer above
            // owns the checksum - so there is no residue to test and every run that clears the
            // minimum length is a frame.
            bool goodFcs = !_options.UseFcs || HdlcFcs.IsGoodResidue(_fcs);

            if (length < _options.MinFrameLength)
            {
                _statistics.Runts++;
                StartNewFrame();
                return HdlcDecodeStatus.Runt;
            }

            if (!goodFcs)
            {
                _statistics.FcsErrors++;
                if (_options.DeliverCorruptFrames)
                {
                    _frameLength = length;
                }
                PrepareForNextFrame();
                return HdlcDecodeStatus.BadFcs;
            }

            _statistics.GoodFrames++;
            _frameLength = length;
            PrepareForNextFrame();
            return HdlcDecodeStatus.FrameComplete;
        }

        /// <summary>
        /// Resets the assembly state while leaving the buffer contents alone, so that
        /// <see cref="Frame"/> stays valid until the caller's next <see cref="Decode"/>.
        /// </summary>
        private void PrepareForNextFrame()
        {
            _length = 0;
            _fcs = HdlcFcs.Init;
            _escaped = false;
            _overflowed = false;
        }

        private void StartNewFrame()
        {
            PrepareForNextFrame();
        }

        private void Append(byte value)
        {
            // Already past the limit: keep discarding until the closing flag, but do not grow.
            if (_overflowed)
            {
                return;
            }

            if (_length >= _options.MaxFrameLength)
            {
                _overflowed = true;
                return;
            }

            if (_length == _buffer.Length)
            {
                Grow();
            }

            _buffer[_length] = value;
            _length++;
            _fcs = HdlcFcs.Update(_fcs, value);
        }

        private void Grow()
        {
            int wanted = _buffer.Length * 2;
            if (wanted > _options.MaxFrameLength)
            {
                wanted = _options.MaxFrameLength;
            }

            byte[] bigger = ArrayPool<byte>.Shared.Rent(wanted);
            Array.Copy(_buffer, bigger, _length);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = bigger;
        }
    }
}
