//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading;

namespace RetroIO.Protocols.X25.Transport
{
    /// <summary>
    /// The connection slots of one XOT listening entry: one slot per X.25 channel of the line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Copied from x25emu's XOT port (tools/x25switch.c). There a port has conn_max connections,
    /// conn_max is the link's channel count (vc_count), and every connection - one carrying a call or
    /// an idle one a caller keeps open for arriving calls - holds one. The listening socket is only
    /// polled while conn_slot_free finds a free slot, so when all are taken the next connection is not
    /// accepted: it waits in the TCP backlog until a connection closes. RFC 1613 section 5 gives each
    /// circuit its own TCP connection, so a line cannot use more connections than it has channels.
    /// </para>
    /// <para>
    /// A connection is never accepted and then closed for want of room. The far end sees that as its
    /// call lost (cleared with cause 0x09, out of order), which is what this class replaced.
    /// </para>
    /// </remarks>
    public sealed class XotConnectionSlots
    {
        private readonly object _lock = new object();
        private readonly int _limit;
        private int _used;

        /// <summary>
        /// Creates the slots.
        /// </summary>
        /// <param name="limit">How many slots: the line's channel count.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="limit"/> is below 1.
        /// </exception>
        public XotConnectionSlots(int limit)
        {
            if (limit < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), limit, "An XOT line needs at least one connection slot (one channel).");
            }

            _limit = limit;
        }

        /// <summary>
        /// Raised (outside the lock) each time a slot is given back.
        /// </summary>
        public event Action? Freed;

        /// <summary>Gets how many slots there are.</summary>
        public int Limit => _limit;

        /// <summary>Gets how many slots are taken now.</summary>
        public int Used
        {
            get
            {
                lock (_lock)
                {
                    return _used;
                }
            }
        }

        /// <summary>Gets whether a slot is free now.</summary>
        public bool HasFree
        {
            get
            {
                lock (_lock)
                {
                    return _used < _limit;
                }
            }
        }

        /// <summary>
        /// Takes a slot if one is free.
        /// </summary>
        /// <returns>True when a slot was taken.</returns>
        public bool TryTake()
        {
            lock (_lock)
            {
                if (_used >= _limit)
                {
                    return false;
                }

                _used++;
                return true;
            }
        }

        /// <summary>
        /// Waits until a slot is free and takes it.
        /// </summary>
        /// <param name="cancellationToken">Ends the wait (the entry is stopping).</param>
        /// <returns>True when a slot was taken; false when the wait was cancelled.</returns>
        public bool WaitAndTake(CancellationToken cancellationToken)
        {
            using CancellationTokenRegistration wake = cancellationToken.Register(() =>
            {
                lock (_lock)
                {
                    Monitor.PulseAll(_lock);
                }
            });

            lock (_lock)
            {
                while (_used >= _limit)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return false;
                    }

                    Monitor.Wait(_lock);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                _used++;
                return true;
            }
        }

        /// <summary>
        /// Gives a slot back (its connection has closed) and wakes anything waiting for one.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no slot is taken.
        /// </exception>
        public void Release()
        {
            lock (_lock)
            {
                if (_used == 0)
                {
                    // A slot given back twice is a bookkeeping fault in the caller; hiding it would let the
                    // line hold more connections than it has channels.
                    throw new InvalidOperationException("XOT connection slot released with none taken.");
                }

                _used--;

                Monitor.PulseAll(_lock);
            }

            Freed?.Invoke();
        }
    }
}
