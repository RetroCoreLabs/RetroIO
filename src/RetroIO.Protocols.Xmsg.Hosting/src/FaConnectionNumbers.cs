//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//


using System;

namespace RetroIO.Protocols.Xmsg.Hosting
{
    /// <summary>
    /// Hands out the COSMOS file server's connection numbers in blocks, persisted in a state file,
    /// so a restarted process never reuses one the peer has already seen.
    /// </summary>
    /// <remarks>
    /// Moved here from the runner (2026-11-09) so a long-running service and the runner share
    /// one implementation. The only change is that the caller names the state file; the runner
    /// used to put it beside its own executable.
    /// </remarks>
    public static class FaConnectionNumbers
    {
        /// <summary>
        /// How many FA connection numbers each run reserves up front.
        /// </summary>
        /// <remarks>
        /// Reserved in BLOCKS rather than saved per connection, so a runner that is killed - which is
        /// how it usually ends - still never hands out a number twice.
        /// </remarks>
        public const ushort ConnectionNumberBlock = 64;

        /// <summary>
        /// The lowest connection number handed out, and where the counter wraps back to.
        /// </summary>
        /// <remarks>
        /// Mirrors <c>FaServer</c>'s own range. Kept here too because this method decides what the
        /// NEXT process starts from, and a stale file written before the wrap existed can still hold a
        /// number from the climbing days.
        /// </remarks>
        private const ushort FirstConnectionNumber = 0x0042;

        /// <summary>
        /// The highest connection number handed out before wrapping.
        /// </summary>
        /// <remarks>
        /// Every connection number a real machine has sent us is small - 0x0004 through 0x0046 - and a
        /// climbed 0x0E02 was silently ignored by a live D100. Where the true limit sits is NOT known;
        /// this keeps us in the captures' order of magnitude. See <c>FaServer.LastConnectionNumber</c>.
        /// </remarks>
        private const ushort LastConnectionNumber = 0x00FF;

        /// <summary>
        /// Takes the next block of FA connection numbers, so a restart never reuses one.
        /// </summary>
        /// <param name="path">
        /// The state file that records where the next process starts (<c>fa-connection.state</c> in
        /// the state folder; see <see cref="XmsgStateFiles.FaConnection"/>).
        /// </param>
        /// <param name="log">
        /// Where the reserved range, and any trouble with the file, is reported. Null discards it.
        /// </param>
        /// <returns>
        /// The number the file server should answer its next connect letter with.
        /// </returns>
        /// <remarks>
        /// <para><b>Why this exists</b></para>
        /// The connection number counts up per CONNECTION and carries across conversations - its scope
        /// is the server's lifetime, unlike the two per-conversation counters, which reset with the
        /// conversation. A real server stays up; ours restarts constantly and began at <c>0x0042</c>
        /// every time, handing back numbers the peer had already seen. Persisting it is what the
        /// observed semantics ask for. See <c>FaServer.NextConnectionNumber</c>.
        /// <para><b>It does NOT fix the first-connect stall - that theory was DISPROVED</b></para>
        /// The stall was the reason this was written: after a runner restart the first connect letter
        /// gets its confirmation and then nothing follows, and a retry minutes later works. Reusing a
        /// connection number looked like the cause, because the retry that worked had used the NEXT
        /// number and because restarting XMSG on D100 also cured it.
        /// <para>
        /// Tested 2026-08-06: with the state file in place a restart answered the first connect with a
        /// fresh <c>0x0082</c> and it stalled anyway. So the cause is something else that resets when
        /// this process does - the session wire port, which also restarts at <c>0x0211</c> every run, is
        /// the next candidate and is UNTESTED. Do not repeat the connection-number theory.
        /// </para>
        /// <para><b>Block reservation, not save-on-use</b></para>
        /// The file records where the NEXT run must start. Reserving
        /// <see cref="ConnectionNumberBlock"/> up front lets a run hand out that many without touching
        /// the file again, and being killed loses nothing - the worst case is skipping some numbers,
        /// which costs nothing.
        /// </remarks>
        public static ushort Reserve(string path, XmsgLogHandler? log)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            ushort start = FirstConnectionNumber;
            try
            {
                if (System.IO.File.Exists(path))
                {
                    string text = System.IO.File.ReadAllText(path).Trim();
                    ushort saved;
                    if (ushort.TryParse(text, out saved) && saved != 0)
                    {
                        start = saved;
                    }
                }

                // WRAP, do not climb. This used to be a plain add, so every process start pushed the
                // number 64 higher for ever and it reached 0x0E02 - far outside anything a real machine
                // sends. A live D100 then ignored our connect confirmation entirely: no error, no
                // reject, it just re-sent its letter until the terminal timed out. Measured 2026-08-09.
                ushort next = (ushort)(start + ConnectionNumberBlock);
                if (start < FirstConnectionNumber || start > LastConnectionNumber
                    || next > LastConnectionNumber)
                {
                    // Either the file is stale from the climbing days, or this block runs off the end.
                    // Either way the next run starts over rather than leaving the range.
                    if (start < FirstConnectionNumber || start > LastConnectionNumber)
                    {
                        start = FirstConnectionNumber;
                    }

                    next = FirstConnectionNumber;
                }

                System.IO.File.WriteAllText(path, next.ToString());
            }
            catch (System.IO.IOException error)
            {
                // A state file we cannot read or write is not worth failing the run for - the cost is
                // one stalled connect, which the client retries past.
                log?.Invoke($"[fa] connection-number state file unusable ({error.Message}); starting at 0x{start:X4}");
            }

            log?.Invoke(
                $"[fa] connection numbers 0x{start:X4}..0x{(ushort)(start + ConnectionNumberBlock - 1):X4} (state: {path})");
            return start;
        }
    }
}
