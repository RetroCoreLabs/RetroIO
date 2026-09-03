using System;

namespace RetroIO.Protocols.Xmsg.Chat
{
    /// <summary>
    /// Remembers which lines have already been seen, so the same line arriving by a second path is
    /// delivered once.
    /// </summary>
    /// <remarks>
    /// <para><b>What problem this solves, and when it appears</b></para>
    /// <para>
    /// A hop count stops a line travelling for ever. It does NOT stop it arriving TWICE. With
    /// D100/D102/D103 wired as a chain there is only one path between any two machines and this
    /// never happens - but add a single trunk between D102 and D103 and every line typed on any of
    /// them reaches the other two by two routes, and every user sees everything doubled. This is
    /// the piece that makes a mesh safe to wire.
    /// </para>
    /// <para><b>A line is named by (origin, id), and only the origin may name it</b></para>
    /// <para>
    /// The machine the line was typed on stamps it. A relay must never renumber it: a machine with
    /// two neighbours sends one line to both, and if each stamped its own number a node further out
    /// would see two names for one line and deliver it twice - the exact fault being prevented.
    /// </para>
    /// <para><b>Why a ring and not a set</b></para>
    /// <para>
    /// The interesting window is short. A duplicate arrives within a few hops of the original, so
    /// remembering the last few dozen is enough, and a fixed ring costs one allocation for the life
    /// of the server, never grows, and cannot be made to grow by a peer sending traffic. An
    /// unbounded set would be a way for a stranger to exhaust memory.
    /// </para>
    /// <para><b>Wrapping is safe, and this is why</b></para>
    /// <para>
    /// Ids are 16 bits and wrap at 65536. A wrapped id can only be mistaken for a live one if the
    /// same origin sent 65536 lines while an entry was still in the window - and the window holds
    /// far fewer than that, so an id is long gone before it can come round again.
    /// </para>
    /// </remarks>
    public sealed class ChatLineMemory
    {
        /// <summary>
        /// Origin system of each remembered line, parallel to <see cref="_ids"/>.
        /// </summary>
        private readonly ushort[] _origins;

        /// <summary>
        /// Line id of each remembered line, parallel to <see cref="_origins"/>.
        /// </summary>
        /// <remarks>
        /// Two parallel arrays rather than an array of pairs: it keeps the scan over contiguous
        /// memory and costs no allocation per entry.
        /// </remarks>
        private readonly ushort[] _ids;

        /// <summary>
        /// Where the next entry is written. Wraps at the end of the ring.
        /// </summary>
        private int _next;

        /// <summary>
        /// How many slots hold a real entry, up to the ring's length.
        /// </summary>
        /// <remarks>
        /// Tracked separately so a fresh memory does not have to pretend slot zero holds
        /// (origin 0, id 0) - which is a pair a real message could otherwise collide with.
        /// </remarks>
        private int _used;

        /// <summary>
        /// Creates a memory holding the given number of recent lines.
        /// </summary>
        /// <param name="capacity">
        /// How many lines to remember. Must be at least one.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="capacity"/> is less than one.
        /// </exception>
        public ChatLineMemory(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity), "The memory needs room for at least one line.");
            }

            _origins = new ushort[capacity];
            _ids = new ushort[capacity];
            _next = 0;
            _used = 0;
        }

        /// <summary>
        /// Gets how many lines this memory can hold.
        /// </summary>
        public int Capacity
        {
            get { return _origins.Length; }
        }

        /// <summary>
        /// Records a line and says whether it is the first sight of it.
        /// </summary>
        /// <param name="originSystem">
        /// The system the line was typed on, as carried by the message.
        /// </param>
        /// <param name="lineId">
        /// The number that origin stamped on the line.
        /// </param>
        /// <returns>
        /// True when this line has not been seen, in which case it is now remembered. False when it
        /// is a repeat, in which case the memory is left unchanged.
        /// </returns>
        /// <remarks>
        /// Deliberately one call rather than a separate check and record. Two calls invite the bug
        /// where a caller tests, forgets to record, and every copy looks new.
        /// </remarks>
        public bool IsNew(ushort originSystem, ushort lineId)
        {
            for (int i = 0; i < _used; i++)
            {
                if (_origins[i] == originSystem && _ids[i] == lineId)
                {
                    return false;
                }
            }

            _origins[_next] = originSystem;
            _ids[_next] = lineId;

            _next += 1;
            if (_next == _origins.Length)
            {
                _next = 0;
            }

            if (_used < _origins.Length)
            {
                _used += 1;
            }

            return true;
        }

        /// <summary>
        /// Forgets every remembered line.
        /// </summary>
        /// <remarks>
        /// For a server restart or a test. After this every line looks new again, which for a
        /// restart is correct - the copies that were in flight are gone with the old process.
        /// </remarks>
        public void Clear()
        {
            _next = 0;
            _used = 0;
        }
    }
}
