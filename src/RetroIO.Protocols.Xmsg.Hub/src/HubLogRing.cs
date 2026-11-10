//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Hub
{
    /// <summary>
    /// One line the hub logged.
    /// </summary>
    /// <param name="Sequence">
    /// The line's number, counting from 1; never repeats.
    /// </param>
    /// <param name="TimeUtc">
    /// When it was logged.
    /// </param>
    /// <param name="Message">
    /// The text.
    /// </param>
    public sealed record HubLogLine(long Sequence, DateTime TimeUtc, string Message);

    /// <summary>
    /// A fixed-size buffer of the hub's most recent log lines, for the admin log view.
    /// </summary>
    /// <remarks>
    /// The lines also go to the host's normal logger. This buffer exists so the admin UI can show
    /// the HUB's lines on their own, the way the console of the standalone xmsghub did, without
    /// digging them out of everything else the server logs.
    /// </remarks>
    public sealed class HubLogRing
    {
        private readonly object _lock = new object();
        private readonly HubLogLine?[] _slots;
        private long _nextSequence = 1;
        private int _head;
        private int _count;

        /// <summary>
        /// Initialises an empty buffer.
        /// </summary>
        /// <param name="capacity">
        /// How many lines it holds. At least 1.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="capacity"/> is less than 1.
        /// </exception>
        public HubLogRing(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The buffer must hold at least one line.");
            }

            _slots = new HubLogLine?[capacity];
        }

        /// <summary>
        /// Gets the number of the most recent line, or 0 when none has been logged.
        /// </summary>
        public long LastSequence
        {
            get { lock (_lock) { return _nextSequence - 1; } }
        }

        /// <summary>
        /// Stores one line, overwriting the oldest when full.
        /// </summary>
        /// <param name="timeUtc">
        /// When it was logged.
        /// </param>
        /// <param name="message">
        /// The text.
        /// </param>
        /// <returns>
        /// The stored line.
        /// </returns>
        public HubLogLine Add(DateTime timeUtc, string message)
        {
            lock (_lock)
            {
                HubLogLine line = new HubLogLine(_nextSequence++, timeUtc, message ?? string.Empty);
                _slots[_head] = line;
                _head = (_head + 1) % _slots.Length;
                if (_count < _slots.Length)
                {
                    _count++;
                }

                return line;
            }
        }

        /// <summary>
        /// Returns the lines numbered after <paramref name="afterSequence"/>, oldest first.
        /// </summary>
        /// <param name="afterSequence">
        /// The last number the caller already has; 0 for everything still held.
        /// </param>
        /// <param name="limit">
        /// The most to return, oldest first.
        /// </param>
        /// <returns>
        /// The lines.
        /// </returns>
        public HubLogLine[] GetAfter(long afterSequence, int limit)
        {
            if (limit <= 0)
            {
                return Array.Empty<HubLogLine>();
            }

            lock (_lock)
            {
                List<HubLogLine> result = new List<HubLogLine>(Math.Min(limit, _count));
                int oldest = (_head - _count + _slots.Length) % _slots.Length;
                for (int i = 0; i < _count && result.Count < limit; i++)
                {
                    HubLogLine line = _slots[(oldest + i) % _slots.Length]!;
                    if (line.Sequence > afterSequence)
                    {
                        result.Add(line);
                    }
                }

                return result.ToArray();
            }
        }
    }
}
