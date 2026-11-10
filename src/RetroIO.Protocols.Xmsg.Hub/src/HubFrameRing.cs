//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;

namespace RetroIO.Protocols.Xmsg.Hub
{
    /// <summary>
    /// A fixed-size buffer of the most recent frames the hub carried.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written by every member's receive thread, read by whoever wants to look (the management
    /// API). One lock guards it; holding it while a record goes in or comes out costs nothing next
    /// to the socket work.
    /// </para>
    /// <para>
    /// When full, the oldest frame is overwritten. Records are numbered from 1 and the numbers
    /// never repeat, so a reader that asks for frames after the last number it saw gets exactly
    /// the new ones, and can tell from a gap that the buffer wrapped and some were lost.
    /// </para>
    /// <para>
    /// This is a VIEW for a person, not a capture. For a complete record of a run, turn on the
    /// pcap capture file, which keeps every frame.
    /// </para>
    /// </remarks>
    public sealed class HubFrameRing
    {
        private readonly object _lock = new object();
        private readonly HubFrameRecord?[] _slots;
        private long _nextSequence = 1;
        private int _head;
        private int _count;

        /// <summary>
        /// Initialises an empty buffer.
        /// </summary>
        /// <param name="capacity">
        /// How many frames it holds. At least 1.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="capacity"/> is less than 1.
        /// </exception>
        public HubFrameRing(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The buffer must hold at least one frame.");
            }

            _slots = new HubFrameRecord?[capacity];
        }

        /// <summary>
        /// Gets how many frames the buffer can hold.
        /// </summary>
        public int Capacity
        {
            get { return _slots.Length; }
        }

        /// <summary>
        /// Gets how many frames it holds now.
        /// </summary>
        public int Count
        {
            get { lock (_lock) { return _count; } }
        }

        /// <summary>
        /// Gets the number of the most recent frame, or 0 when none has been seen.
        /// </summary>
        public long LastSequence
        {
            get { lock (_lock) { return _nextSequence - 1; } }
        }

        /// <summary>
        /// Stores one frame, overwriting the oldest when full.
        /// </summary>
        /// <param name="timeUtc">
        /// When it arrived.
        /// </param>
        /// <param name="member">
        /// The member it came from.
        /// </param>
        /// <param name="frame">
        /// The frame bytes; a copy is kept.
        /// </param>
        /// <param name="outcome">
        /// What the hub did with it.
        /// </param>
        /// <param name="copiesSent">
        /// How many members it was queued to.
        /// </param>
        /// <param name="copiesDropped">
        /// How many members lost it.
        /// </param>
        /// <returns>
        /// The stored record.
        /// </returns>
        public HubFrameRecord Add(
            DateTime timeUtc,
            string member,
            ReadOnlySpan<byte> frame,
            HubFrameOutcome outcome,
            int copiesSent,
            int copiesDropped)
        {
            lock (_lock)
            {
                // The record is built under the lock so its number and its place in the buffer
                // agree; two receive threads must not hand out the same number.
                HubFrameRecord record = new HubFrameRecord(
                    _nextSequence++, timeUtc, member, frame, outcome, copiesSent, copiesDropped);
                _slots[_head] = record;
                _head = (_head + 1) % _slots.Length;
                if (_count < _slots.Length)
                {
                    _count++;
                }

                return record;
            }
        }

        /// <summary>
        /// Returns the frames numbered after <paramref name="afterSequence"/>, oldest first.
        /// </summary>
        /// <param name="afterSequence">
        /// The last number the caller already has; 0 for everything still in the buffer.
        /// </param>
        /// <param name="limit">
        /// The most to return. The OLDEST matching frames come first, so a caller paging through
        /// with the last number it got never skips any.
        /// </param>
        /// <returns>
        /// The frames, oldest first.
        /// </returns>
        public HubFrameRecord[] GetAfter(long afterSequence, int limit)
        {
            if (limit <= 0)
            {
                return Array.Empty<HubFrameRecord>();
            }

            lock (_lock)
            {
                List<HubFrameRecord> result = new List<HubFrameRecord>(Math.Min(limit, _count));
                int oldest = (_head - _count + _slots.Length) % _slots.Length;
                for (int i = 0; i < _count && result.Count < limit; i++)
                {
                    HubFrameRecord record = _slots[(oldest + i) % _slots.Length]!;
                    if (record.Sequence > afterSequence)
                    {
                        result.Add(record);
                    }
                }

                return result.ToArray();
            }
        }

        /// <summary>
        /// Empties the buffer. The numbering carries on, so a reader is never handed a number twice.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_slots, 0, _slots.Length);
                _head = 0;
                _count = 0;
            }
        }

        /// <summary>
        /// Writes frames as a classic pcap file (link type Ethernet) to a stream.
        /// </summary>
        /// <param name="records">
        /// The frames, in the order to write them.
        /// </param>
        /// <param name="destination">
        /// The stream. It is left open.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when an argument is null.
        /// </exception>
        /// <remarks>
        /// Each frame keeps the time it ARRIVED at the hub, so the file lines up with machine and
        /// runner logs from the same moment.
        /// </remarks>
        public static void WritePcap(HubFrameRecord[] records, Stream destination)
        {
            ArgumentNullException.ThrowIfNull(records);
            ArgumentNullException.ThrowIfNull(destination);

            using PcapWriter writer = new PcapWriter(destination);
            for (int i = 0; i < records.Length; i++)
            {
                HubFrameRecord record = records[i];
                writer.Write(record.Frame, record.Frame.Length, record.TimeUtc);
            }
        }
    }
}
