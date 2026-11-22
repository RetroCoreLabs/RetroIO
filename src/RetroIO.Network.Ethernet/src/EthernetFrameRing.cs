//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading;

namespace RetroIO.Network.Ethernet
{
    /// <summary>
    /// A fixed-size queue of received Ethernet frames between ONE writer thread (a backend's
    /// capture thread) and ONE reader thread (the emulation thread). Every slot is a buffer
    /// made once at construction, so carrying a frame allocates nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the receive queue a host backend puts between the host and the guest. It is
    /// bounded on purpose: when the guest does not keep up, a real card drops frames when its
    /// receive ring is full and the sender retransmits, so dropping here is correct behaviour
    /// - but every drop is counted in <see cref="FramesDroppedFull"/>, because a silent drop
    /// looks exactly like a broken link. (The legacy UDP backend learned this the hard way:
    /// Emulated.HW/Common/Network/UdpEthernetBackend.cs, defects fixed 2026-08-03.)
    /// </para>
    /// <para>
    /// Writer side: <see cref="TryReserve"/> hands out the next free slot, the writer fills it
    /// (and may change it in place, for example to repair checksums), then
    /// <see cref="Commit"/> makes it visible to the reader. Reader side: <see cref="TryPeek"/>
    /// gives the oldest frame, and <see cref="Release"/> frees its slot once the frame has been
    /// handed on. Only one thread may write and only one may read; the two indexes are
    /// published with volatile writes so each side sees the other's finished work.
    /// </para>
    /// </remarks>
    public sealed class EthernetFrameRing
    {
        // ── Fields ──

        /// <summary>
        /// The slot buffers, each <see cref="MaxFrameBytes"/> long.
        /// </summary>
        private readonly byte[][] _slots;

        /// <summary>
        /// The frame length held in each slot.
        /// </summary>
        private readonly int[] _lengths;

        /// <summary>
        /// Slot count minus one; the slot count is a power of two.
        /// </summary>
        private readonly int _mask;

        /// <summary>
        /// The largest frame a slot holds.
        /// </summary>
        private readonly int _maxFrameBytes;

        /// <summary>
        /// Count of frames written so far; only the writer changes it.
        /// </summary>
        private int _written;

        /// <summary>
        /// Count of frames read so far; only the reader changes it.
        /// </summary>
        private int _read;

        /// <summary>
        /// True between a successful <see cref="TryReserve"/> and its <see cref="Commit"/>.
        /// </summary>
        private bool _reserved;

        /// <summary>
        /// Frames refused because every slot was full.
        /// </summary>
        private long _droppedFull;

        // ── Constructors ──

        /// <summary>
        /// Initializes a new <see cref="EthernetFrameRing"/>.
        /// </summary>
        /// <param name="slotCount">
        /// How many frames the queue holds; must be a power of two, 2 or more.
        /// </param>
        /// <param name="maxFrameBytes">
        /// The largest frame one slot holds, in bytes (no FCS).
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="slotCount"/> is not a power of two of at least 2, or
        /// <paramref name="maxFrameBytes"/> is not positive.
        /// </exception>
        public EthernetFrameRing(int slotCount, int maxFrameBytes)
        {
            if (slotCount < 2 || (slotCount & (slotCount - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(slotCount), "The slot count must be a power of two, 2 or more.");
            }
            if (maxFrameBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxFrameBytes), "The largest frame must be at least one byte.");
            }

            _slots = new byte[slotCount][];
            for (int i = 0; i < slotCount; i++)
            {
                _slots[i] = new byte[maxFrameBytes];
            }
            _lengths = new int[slotCount];
            _mask = slotCount - 1;
            _maxFrameBytes = maxFrameBytes;
        }

        // ── Properties ──

        /// <summary>
        /// Gets how many frames the queue holds.
        /// </summary>
        public int Capacity => _slots.Length;

        /// <summary>
        /// Gets the largest frame one slot holds, in bytes.
        /// </summary>
        public int MaxFrameBytes => _maxFrameBytes;

        /// <summary>
        /// Gets how many frames are waiting to be read.
        /// </summary>
        public int Count => Volatile.Read(ref _written) - Volatile.Read(ref _read);

        /// <summary>
        /// Gets how many frames were refused because the queue was full.
        /// </summary>
        public long FramesDroppedFull => Interlocked.Read(ref _droppedFull);

        // ── Public methods ──

        /// <summary>
        /// Writer side: gets the next free slot for a frame of <paramref name="length"/> bytes.
        /// </summary>
        /// <remarks>
        /// When every slot is full the frame is refused and counted in
        /// <see cref="FramesDroppedFull"/>. A frame longer than <see cref="MaxFrameBytes"/>, or
        /// of no length, is refused without counting; the caller decides what that means.
        /// </remarks>
        /// <param name="length">
        /// The frame length in bytes.
        /// </param>
        /// <param name="slot">
        /// The slot to fill, exactly <paramref name="length"/> bytes long; empty when refused.
        /// </param>
        /// <returns>
        /// <c>true</c> when a slot was handed out; call <see cref="Commit"/> after filling it.
        /// </returns>
        public bool TryReserve(int length, out Span<byte> slot)
        {
            slot = Span<byte>.Empty;
            if (length <= 0 || length > _maxFrameBytes)
            {
                return false;
            }

            int written = _written;
            if (written - Volatile.Read(ref _read) >= _slots.Length)
            {
                Interlocked.Increment(ref _droppedFull);
                return false;
            }

            int index = written & _mask;
            _lengths[index] = length;
            slot = new Span<byte>(_slots[index], 0, length);
            _reserved = true;
            return true;
        }

        /// <summary>
        /// Writer side: makes the slot from the last <see cref="TryReserve"/> visible to the reader.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no slot is reserved.
        /// </exception>
        public void Commit()
        {
            if (!_reserved)
            {
                throw new InvalidOperationException("EthernetFrameRing.Commit called without a reserved slot.");
            }
            _reserved = false;
            Volatile.Write(ref _written, _written + 1);
        }

        /// <summary>
        /// Reader side: gets the oldest waiting frame without removing it.
        /// </summary>
        /// <param name="buffer">
        /// The slot buffer holding the frame, from index 0; <c>null</c> when empty.
        /// </param>
        /// <param name="length">
        /// The frame length in bytes; 0 when empty.
        /// </param>
        /// <returns>
        /// <c>true</c> when a frame is waiting; call <see cref="Release"/> once it has been handed on.
        /// </returns>
        public bool TryPeek(out byte[]? buffer, out int length)
        {
            int read = _read;
            if (Volatile.Read(ref _written) == read)
            {
                buffer = null;
                length = 0;
                return false;
            }

            int index = read & _mask;
            buffer = _slots[index];
            length = _lengths[index];
            return true;
        }

        /// <summary>
        /// Reader side: frees the slot of the frame <see cref="TryPeek"/> returned.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the queue is empty.
        /// </exception>
        public void Release()
        {
            int read = _read;
            if (Volatile.Read(ref _written) == read)
            {
                throw new InvalidOperationException("EthernetFrameRing.Release called on an empty queue.");
            }
            Volatile.Write(ref _read, read + 1);
        }
    }
}
