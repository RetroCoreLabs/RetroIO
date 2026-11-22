//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Network.Ethernet
{
    /// <summary>
    /// The receive side every link backend shares: socket threads put frames in, the emulation
    /// thread takes them out in <see cref="Deliver"/>, so the machine never sees a second thread.
    /// </summary>
    /// <remarks>
    /// The queue is bounded (<see cref="EthernetFrameRing"/>): when the guest does not keep up a
    /// frame is dropped and counted, as a real card drops when its receive ring is full. Several
    /// socket threads may put frames in (a hub has one per member), so the writer side is locked.
    /// </remarks>
    public sealed class EthernetLinkReceiveQueue
    {
        // ── Constants ──

        /// <summary>
        /// The most frames handed to the guest per poll.
        /// </summary>
        public const int MaxFramesPerPoll = 64;

        private const int Slots = 512;

        // ── Fields ──

        private readonly EthernetFrameRing _ring;
        private readonly object _writeLock = new object();

        // ── Constructors ──

        /// <summary>
        /// Initializes a new <see cref="EthernetLinkReceiveQueue"/>.
        /// </summary>
        /// <param name="maxFrameBytes">
        /// The largest frame it holds.
        /// </param>
        public EthernetLinkReceiveQueue(int maxFrameBytes)
        {
            _ring = new EthernetFrameRing(Slots, maxFrameBytes);
        }

        // ── Properties ──

        /// <summary>
        /// Gets how many frames were dropped because the queue was full.
        /// </summary>
        public long FramesDroppedFull => _ring.FramesDroppedFull;

        // ── Public methods ──

        /// <summary>
        /// Puts one frame in; any thread.
        /// </summary>
        /// <param name="frame">
        /// The frame.
        /// </param>
        /// <returns>
        /// <c>false</c> when it was dropped (queue full or too long).
        /// </returns>
        public bool Put(ReadOnlySpan<byte> frame)
        {
            lock (_writeLock)
            {
                if (!_ring.TryReserve(frame.Length, out Span<byte> slot))
                {
                    return false;
                }
                frame.CopyTo(slot);
                _ring.Commit();
                return true;
            }
        }

        /// <summary>
        /// Hands waiting frames to <paramref name="handler"/>; the emulation thread only.
        /// </summary>
        /// <param name="handler">
        /// The backend's receive event; null drops the frames.
        /// </param>
        /// <returns>
        /// How many frames were handed on.
        /// </returns>
        public int Deliver(EthernetFrameHandler? handler)
        {
            int count = 0;
            while (count < MaxFramesPerPoll && _ring.TryPeek(out byte[]? buffer, out int length))
            {
                handler?.Invoke(buffer!, 0, length);
                _ring.Release();
                count++;
            }
            return count;
        }
    }
}
