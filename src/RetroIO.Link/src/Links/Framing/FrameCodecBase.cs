//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Link.Framing
{
    /// <summary>
    /// Abstract base for framing codecs (SLIP, HDLC/PPP, XOT).
    /// </summary>
    /// <remarks>
    /// Holds the shared seam surface: the down-facing transport reference (an <see cref="IFrameTransport"/>)
    /// and the up-facing <see cref="FrameReceived"/> event. Concrete codecs implement the framing rules in
    /// <see cref="IFrameCodec.SendFrame"/> and <see cref="IFrameCodec.ProcessBytes"/> and raise a complete
    /// de-framed frame via <see cref="RaiseFrameReceived"/>.
    ///
    /// The payload is opaque at this boundary — a codec never inspects it — which is what lets one codec
    /// carry X.25, SINTRAN, or LAPB frames interchangeably.
    /// </remarks>
    public abstract class FrameCodecBase : IFrameCodec
    {
        // Identifies the owning link, forwarded to subscribers so a single handler can serve many links.
        private readonly string _linkId;

        // Down-facing sink: framed bytes are written here (interface = the "down" call).
        private readonly IFrameTransport _transport;

        /// <summary>
        /// Handler for a fully de-framed frame delivered upward.
        /// </summary>
        /// <param name="linkId">
        /// Identifier of the link that produced the frame.
        /// </param>
        /// <param name="frame">
        /// The de-framed payload. Only valid for the duration of the callback; copy to retain it.
        /// </param>
        public delegate void FrameReceivedHandler(string linkId, ReadOnlySpan<byte> frame);

        /// <summary>
        /// Raised when a complete frame has been de-framed from the incoming byte stream (the "up" seam).
        /// </summary>
        public event FrameReceivedHandler? FrameReceived;

        /// <summary>
        /// Raised when the byte stream cannot be framed any further and the connection has to go.
        /// </summary>
        /// <param name="linkId">
        /// Identifier of the owning link.
        /// </param>
        /// <param name="reason">
        /// What was wrong, for the log.
        /// </param>
        public delegate void FramingFailedHandler(string linkId, string reason);

        /// <summary>
        /// Raised when framing has failed beyond recovery, so the caller must close the connection.
        /// </summary>
        /// <remarks>
        /// Some framings can resynchronize: HDLC has a flag byte, so a decoder that loses its place
        /// finds the next frame boundary by looking for one. A length-prefixed framing has no such
        /// marker. Once a header is wrong, every byte after it is at an unknown offset, and going on
        /// means reading whatever comes next as a header and delivering rubbish as X.25 packets.
        /// The only honest answer is to drop the connection and let it be made again.
        /// </remarks>
        public event FramingFailedHandler? FramingFailed;

        /// <summary>
        /// Reports that framing has failed beyond recovery.
        /// </summary>
        /// <param name="reason">
        /// What was wrong, for the log.
        /// </param>
        protected void RaiseFramingFailed(string reason)
        {
            FramingFailed?.Invoke(_linkId, reason);
        }

        /// <summary>
        /// Initializes the base codec.
        /// </summary>
        /// <param name="linkId">
        /// Identifier of the owning link, forwarded on every <see cref="FrameReceived"/> event.
        /// </param>
        /// <param name="transport">
        /// The transport sink that framed bytes are written to.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="linkId"/> or <paramref name="transport"/> is null.
        /// </exception>
        protected FrameCodecBase(string linkId, IFrameTransport transport)
        {
            _linkId = linkId ?? throw new ArgumentNullException(nameof(linkId));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        /// <summary>
        /// Identifier of the owning link.
        /// </summary>
        protected string LinkId => _linkId;

        /// <summary>
        /// The transport sink that framed bytes are written to.
        /// </summary>
        protected IFrameTransport Transport => _transport;

        /// <inheritdoc/>
        public abstract void SendFrame(ReadOnlySpan<byte> frame);

        /// <inheritdoc/>
        public abstract void ProcessBytes(ReadOnlySpan<byte> data);

        /// <inheritdoc/>
        public virtual void Reset()
        {
        }

        /// <summary>
        /// Raises <see cref="FrameReceived"/> for a completed frame.
        /// </summary>
        /// <param name="frame">
        /// The de-framed payload to deliver upward.
        /// </param>
        protected void RaiseFrameReceived(ReadOnlySpan<byte> frame)
        {
            FrameReceived?.Invoke(_linkId, frame);
        }
    }
}
