//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// Joins a received <see cref="SintranPacketSubtype.MessageFirstFragment"/> and its
    /// <see cref="SintranPacketSubtype.MessageContinuation"/> back into one data frame.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this has to exist before a file can be written to us</b></para>
    /// A file-content message is 1032 bytes and travels as two frames. Everything above the frame
    /// layer - the server dispatch in <c>XmsgNode</c>, <c>XmsgServerHost.Route</c>, the file-access
    /// server - is gated on <see cref="SintranPacketSubtype.Data"/> with a sub-header present, so a
    /// fragment reaches none of them. Sending was the easy half; this is the other one.
    /// <para><b>The pairing rule, measured over a whole capture</b></para>
    /// The two frames of a message share ONE Flags 1 and nothing else identifies them - see
    /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-read.txt</c>, where all 36 pairs share
    /// theirs. A first fragment carries the full 28-byte addressing head and declares the TOTAL
    /// message length in Flags 2; a continuation carries no sub-header at all and declares the byte
    /// OFFSET it resumes at.
    /// <para><b>What the joined frame looks like</b></para>
    /// An ordinary data frame: the first fragment's header and sub-header, its subtype changed to
    /// <see cref="SintranPacketSubtype.Data"/>, and the two payloads concatenated. Flags 2 is left
    /// as the TOTAL length, which is what a data frame carries anyway, so everything above this
    /// layer sees exactly what it would have seen if the message had arrived whole.
    /// <para><b>What it does NOT do</b></para>
    /// It does not acknowledge, re-order or time out. A continuation whose first fragment was never
    /// seen is dropped rather than guessed at, and a first fragment that is never completed is held
    /// until the cap below evicts it. No capture shows a lost or interleaved fragment, so there is
    /// nothing to model recovery on and inventing it would be fiction.
    /// </remarks>
    public sealed class SintranFragmentReassembler
    {
        /// <summary>
        /// The most incomplete messages held at once, across all peers.
        /// </summary>
        /// <remarks>
        /// A first fragment whose continuation never arrives would otherwise be held forever. The
        /// transfer is stop-and-wait - the peak number of outstanding messages measured over a whole
        /// captured transfer is exactly ONE - so anything above a handful already means something is
        /// wrong, and the oldest is evicted rather than letting the table grow.
        /// </remarks>
        public const int MaxPendingMessages = 8;

        /// <summary>
        /// First fragments waiting for their continuation, keyed by source node and Flags 1.
        /// </summary>
        private readonly Dictionary<uint, XmsgFrame> _pending;

        /// <summary>
        /// The keys in arrival order, so the oldest can be evicted when the cap is reached.
        /// </summary>
        private readonly List<uint> _order;

        /// <summary>
        /// Initialises an empty reassembler.
        /// </summary>
        public SintranFragmentReassembler()
        {
            _pending = new Dictionary<uint, XmsgFrame>();
            _order = new List<uint>();
        }

        /// <summary>
        /// Occurs when a fragment cannot be used: a continuation with no first fragment, or a first
        /// fragment evicted before its continuation arrived.
        /// </summary>
        /// <remarks>
        /// Worth surfacing rather than dropping quietly - a silent drop here looks exactly like a
        /// peer that has gone mute, and that is expensive to diagnose from the outside.
        /// </remarks>
        public event XmsgLogHandler? Log;

        /// <summary>
        /// Gets the number of first fragments currently waiting for a continuation.
        /// </summary>
        public int PendingCount
        {
            get { return _pending.Count; }
        }

        /// <summary>
        /// Offers a received frame to the reassembler.
        /// </summary>
        /// <param name="incoming">
        /// The frame as it arrived.
        /// </param>
        /// <returns>
        /// The frame to go on handling: <paramref name="incoming"/> unchanged when it is not a
        /// fragment, the joined data frame when a pair has just completed, or
        /// <c>null</c> when the frame was consumed and there is nothing to handle yet.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="incoming"/> is null.
        /// </exception>
        public XmsgFrame? Accept(XmsgFrame incoming)
        {
            if (incoming == null)
            {
                throw new ArgumentNullException(nameof(incoming));
            }

            if (incoming.Header == null)
            {
                return incoming;
            }

            if (incoming.Header.Subtype == SintranPacketSubtype.MessageFirstFragment)
            {
                Hold(incoming);
                return null;
            }

            if (incoming.Header.Subtype == SintranPacketSubtype.MessageContinuation)
            {
                return Complete(incoming);
            }

            return incoming;
        }

        /// <summary>
        /// Parks a first fragment until its continuation arrives.
        /// </summary>
        /// <param name="first">
        /// The first fragment.
        /// </param>
        private void Hold(XmsgFrame first)
        {
            uint key = KeyOf(first);

            if (!_pending.ContainsKey(key))
            {
                if (_order.Count >= MaxPendingMessages)
                {
                    uint oldest = _order[0];
                    _order.RemoveAt(0);
                    _pending.Remove(oldest);
                    Log?.Invoke(
                        $"[frag] holding {MaxPendingMessages} incomplete message(s); dropping the oldest "
                        + $"(node {(ushort)(oldest >> 16)} Flags1 0x{(ushort)oldest:X4}) to take a new one");
                }

                _order.Add(key);
            }

            _pending[key] = first;
        }

        /// <summary>
        /// Joins a continuation to the first fragment it belongs to.
        /// </summary>
        /// <param name="continuation">
        /// The continuation.
        /// </param>
        /// <returns>
        /// The joined data frame, or <c>null</c> when no first fragment is held for it.
        /// </returns>
        private XmsgFrame? Complete(XmsgFrame continuation)
        {
            uint key = KeyOf(continuation);

            XmsgFrame? first;
            if (!_pending.TryGetValue(key, out first) || first == null)
            {
                Log?.Invoke(
                    $"[frag] a continuation arrived from node {continuation.Header.SourceNode} at Flags1 "
                    + $"0x{continuation.Header.Flags1:X4} with no first fragment held for it; dropped");
                return null;
            }

            _pending.Remove(key);
            _order.Remove(key);

            byte[] head = first.GetBodyBytes();
            byte[] tail = continuation.GetBodyBytes();

            byte[] whole = new byte[head.Length + tail.Length];
            for (int i = 0; i < head.Length; i++)
            {
                whole[i] = head[i];
            }

            for (int i = 0; i < tail.Length; i++)
            {
                whole[head.Length + i] = tail[i];
            }

            // The continuation's Flags 2 says where it resumes, which must be exactly what the first
            // fragment carried. A mismatch means the two do not belong together however well their
            // Flags 1 agreed, and joining them would hand the layer above a corrupt message.
            if (continuation.Header.Flags2 != head.Length)
            {
                Log?.Invoke(
                    $"[frag] continuation from node {continuation.Header.SourceNode} resumes at "
                    + $"{continuation.Header.Flags2} but its first fragment carried {head.Length} byte(s); dropped");
                return null;
            }

            XmsgFrame joined = new XmsgFrame();
            joined.Header = first.Header;
            joined.Header.Subtype = SintranPacketSubtype.Data;
            joined.SubHeader = first.SubHeader;
            joined.TrailingBytes = whole;
            joined.ClearRawBytes();

            return joined;
        }

        /// <summary>
        /// Builds the key that pairs two fragments: the source node and the shared Flags 1.
        /// </summary>
        /// <param name="frame">
        /// The fragment.
        /// </param>
        /// <returns>
        /// The packed key.
        /// </returns>
        /// <remarks>
        /// The node is part of the key because Flags 1 is per LINK - two peers can be mid-message
        /// with the same number at the same time, and pairing on Flags 1 alone would splice one
        /// peer's continuation onto the other's first fragment.
        /// </remarks>
        private static uint KeyOf(XmsgFrame frame)
        {
            return ((uint)frame.Header.SourceNode << 16) | frame.Header.Flags1;
        }
    }
}
