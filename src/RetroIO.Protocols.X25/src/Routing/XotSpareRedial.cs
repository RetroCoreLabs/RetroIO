//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.X25.Transport
{
    /// <summary>
    /// How long a subscriber behind a switch waits before it dials its spare connection again, after the switch closed
    /// it or a dial failed: the first wait, then twice the last one, never more than the most. A dial that works starts
    /// it again at the first wait.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why a subscriber dials again at all: it is a DTE, and X.25 (10/96) 4.6 has the DTE learn of a network failure and
    /// of its end. 4.6.3 (text line 5904-5914): on an out of order condition the DCE clears "with the cause 'Out of
    /// order' for each existing virtual call"; 4.6.5 (line 5938-5941): when it is recovered the DCE "will send a restart
    /// indication packet with the cause 'Network operational'". Over XOT that Restart Indication cannot come: RFC 1613
    /// 6.4 (rfc1613.txt line 410-415) says Restart packets "MUST NOT be sent over the TCP connection". Each circuit has
    /// its own TCP connection (section 5, line 201), so a lost connection is the out of order condition of its circuit,
    /// and the connection being dialled again is the link coming back.
    /// </para>
    /// <para>
    /// The timing is NOT from the standards. X.25 2.4.4.1 (line 3348-3350) ends a link set-up that failed N2 times with
    /// "appropriate higher layer recovery action", unnamed. RFC 1613 6.5 (line 570-571) suggests waiting 5 minutes
    /// between attempts, but only for a PVC set-up. The defaults here (2 s, doubling to 30 s) are our own: 2 s is what
    /// our switch port waits between its own redials (<c>XotSwitchPort.RedialDelay</c>), and the bound keeps a switch
    /// that is down for a long time from being dialled more than twice a minute.
    /// </para>
    /// <para>Not thread safe: the owner calls it under its own lock.</para>
    /// </remarks>
    public sealed class XotSpareRedial
    {
        /// <summary>The default first wait, in milliseconds.</summary>
        public const int DefaultFirstMs = 2000;

        /// <summary>The default longest wait, in milliseconds.</summary>
        public const int DefaultMostMs = 30000;

        private readonly int _firstMs;
        private readonly int _mostMs;
        private int _nextMs;

        /// <summary>
        /// Creates the rule.
        /// </summary>
        /// <param name="firstMs">The first wait in milliseconds; 0 or less takes <see cref="DefaultFirstMs"/>.</param>
        /// <param name="mostMs">The longest wait in milliseconds; 0 or less takes <see cref="DefaultMostMs"/>. Never less
        /// than the first wait.</param>
        public XotSpareRedial(int firstMs, int mostMs)
        {
            _firstMs = firstMs > 0 ? firstMs : DefaultFirstMs;
            _mostMs = Math.Max(mostMs > 0 ? mostMs : DefaultMostMs, _firstMs);
            _nextMs = _firstMs;
        }

        /// <summary>
        /// Gives the wait before the next dial and doubles the one after it, up to the most.
        /// </summary>
        /// <returns>The wait in milliseconds.</returns>
        public int Next()
        {
            int wait = _nextMs;
            _nextMs = _nextMs >= _mostMs / 2 ? _mostMs : _nextMs * 2;
            return wait;
        }

        /// <summary>
        /// A dial worked: the next wait is the first one again.
        /// </summary>
        public void Reset()
        {
            _nextMs = _firstMs;
        }
    }
}
