//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Codec;

namespace RetroIO.Protocols.Xmsg.Node.Seam
{
    /// <summary>
    /// Adapts an <see cref="ILink"/> into the codec's downward <see cref="IXmsgTransport"/> sink:
    /// each <see cref="IXmsgTransport.Send"/> becomes one <see cref="ILink.SendData"/>.
    /// </summary>
    /// <remarks>
    /// This is the single wire between the pure protocol seam (which knows only
    /// <see cref="IXmsgTransport"/>) and the link seam (which knows LAPB). Keeping it a one-line
    /// adapter means the codec never depends on <c>RetroIO.Protocols.Xmsg.Live</c>, and the whole protocol stack
    /// migrates without touching this class - only the <see cref="ILink"/> implementation swaps.
    /// </remarks>
    public sealed class LinkXmsgTransport : IXmsgTransport
    {
        private readonly ILink _link;

        /// <summary>
        /// Raised when the link REFUSED a frame the stack had already built.
        /// </summary>
        /// <param name="link">
        /// The link that refused.
        /// </param>
        /// <param name="byteCount">
        /// The size of the frame that was dropped.
        /// </param>
        public delegate void XmsgSendRefused(ILink link, int byteCount);

        /// <summary>
        /// Occurs when the link refuses a frame, so a reply that was built but never left the
        /// machine is visible instead of silent.
        /// </summary>
        /// <remarks>
        /// The refusal used to be discarded here. That mattered: an Ethernet link stays
        /// <c>Starting</c> until it has heard from its peer and refuses every send until then, so a
        /// reply built during that window vanished with no trace, and the remote SINTRAN hung
        /// waiting for it.
        /// </remarks>
        public event XmsgSendRefused? SendRefused;

        /// <summary>
        /// Gets the number of frames the link has refused to carry.
        /// </summary>
        public long RefusedFrames { get; private set; }

        /// <summary>
        /// Initialises the transport over a link.
        /// </summary>
        /// <param name="link">
        /// The link to send SINTRAN frames on.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="link"/> is null.
        /// </exception>
        public LinkXmsgTransport(ILink link)
        {
            _link = link ?? throw new ArgumentNullException(nameof(link));
        }

        /// <summary>
        /// Sends one encoded frame as one <see cref="ILink.SendData"/> on the link.
        /// </summary>
        /// <param name="bytes">
        /// The frame bytes. The link copies them, so nothing is kept after the call.
        /// </param>
        /// <remarks>
        /// When the link refuses the frame, <see cref="RefusedFrames"/> goes up by one and
        /// <see cref="SendRefused"/> is raised with the frame size. The frame is not sent again.
        /// </remarks>
        public void Send(ReadOnlySpan<byte> bytes)
        {
            // ILink.SendData takes the span directly and copies it into the LAPB body it enqueues, so
            // this seam allocates nothing per frame. The codec above has no retransmit path of its
            // own (that lives in LAPB), so a refusal cannot be recovered here - but it MUST be
            // reported, because a dropped reply hangs the remote SINTRAN terminal.
            if (!_link.SendData(bytes))
            {
                RefusedFrames++;
                SendRefused?.Invoke(_link, bytes.Length);
            }
        }
    }
}
