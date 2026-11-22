//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Threading;

namespace RetroIO.Network.DialUp
{
    /// <summary>
    /// The answer of one login check that may finish later (<see cref="DialUpLoginStart"/>).
    /// </summary>
    /// <remarks>
    /// Safe from any thread. Only the first answer counts; later ones are ignored. A caller who
    /// hangs up while the check runs makes the answer go nowhere.
    /// </remarks>
    public sealed class DialUpLoginAnswer
    {
        // ── Fields ──

        private const int Waiting = 0;
        private const int Accepted = 1;
        private const int Refused = 2;

        private int _state;

        // ── Constructors ──

        internal DialUpLoginAnswer()
        {
        }

        // ── Properties ──

        /// <summary>
        /// Gets a value indicating whether the check has answered.
        /// </summary>
        public bool Answered => Volatile.Read(ref _state) != Waiting;

        /// <summary>
        /// Gets a value indicating whether the answer was yes.
        /// </summary>
        public bool IsAccepted => Volatile.Read(ref _state) == Accepted;

        // ── Public methods ──

        /// <summary>
        /// Lets the caller on.
        /// </summary>
        public void Accept()
        {
            Interlocked.CompareExchange(ref _state, Accepted, Waiting);
        }

        /// <summary>
        /// Refuses the caller.
        /// </summary>
        public void Refuse()
        {
            Interlocked.CompareExchange(ref _state, Refused, Waiting);
        }
    }
}
