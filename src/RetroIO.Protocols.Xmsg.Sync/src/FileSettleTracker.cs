//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Sync
{
    /// <summary>
    /// Decides when a file has stopped being written and is safe to transfer.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a file event is not enough</b></para>
    /// <para>
    /// <c>FileSystemWatcher</c> reports a change while the editor is still writing, so acting on
    /// the event itself transfers half a file - and half a source file still COMPILES, producing
    /// an error listing that sends you hunting for a bug that is not there. It also misses events
    /// under load and silently drops every pending one when its buffer overflows, so events can
    /// never be the only trigger; a periodic rescan has to feed this too.
    /// </para>
    /// <para>
    /// So an event is only a HINT. A file counts as settled once its size and last-write time have
    /// both stopped changing for a quiet period.
    /// </para>
    /// <para><b>No clock and no disk in here</b></para>
    /// <para>
    /// Every method takes the observation and the current time as arguments. That is what makes
    /// the rule testable: the tests drive time forward by passing numbers, with no sleeping and no
    /// real files, so they are fast and cannot flake. The caller owns the clock and the file
    /// system.
    /// </para>
    /// </remarks>
    public sealed class FileSettleTracker
    {
        /// <summary>
        /// What is known about one file that is still being watched.
        /// </summary>
        private sealed class Pending
        {
            /// <summary>
            /// Size in bytes at the most recent observation.
            /// </summary>
            public long Size;

            /// <summary>
            /// Last-write stamp, in the caller's own units, at the most recent observation.
            /// </summary>
            public long LastWriteTicks;

            /// <summary>
            /// When size and last-write were last seen to CHANGE.
            /// </summary>
            public long LastChangedAtTicks;

            /// <summary>
            /// True once this file has been reported settled and has not changed since.
            /// </summary>
            /// <remarks>
            /// Entries used to be REMOVED when they settled, which read well until a scanning
            /// caller observed the same untouched file a moment later: it came back as a brand new
            /// entry with a fresh clock, so it looked like a file being written all over again.
            /// MEASURED 2026-08-11 - the sync daemon reported "1 file(s) still being written" every
            /// few seconds, for ever, about a file that had long since settled and been carried.
            /// Keeping the entry and marking it is what makes "settled" stick.
            /// </remarks>
            public bool Settled;
        }

        private readonly long _quietPeriodTicks;
        private readonly Dictionary<string, Pending> _pending;

        /// <summary>
        /// Creates a tracker.
        /// </summary>
        /// <param name="quietPeriodTicks">
        /// How long size and last-write must both hold still before a file is called settled, in
        /// the same units the caller passes to <see cref="Observe"/>. Must be positive.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="quietPeriodTicks"/> is zero or negative.
        /// </exception>
        public FileSettleTracker(long quietPeriodTicks)
        {
            if (quietPeriodTicks <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(quietPeriodTicks), "The quiet period must be positive.");
            }

            _quietPeriodTicks = quietPeriodTicks;
            _pending = new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets how many files are currently waiting to settle - being written, and not yet quiet.
        /// </summary>
        /// <remarks>
        /// Counts only the UNSETTLED ones. A settled entry is kept rather than removed, so that
        /// observing the same untouched file again does not present it as freshly written, but
        /// keeping it must not make this number lie: "pending" means still moving.
        /// </remarks>
        public int PendingCount
        {
            get
            {
                int waiting = 0;
                Dictionary<string, Pending>.ValueCollection.Enumerator e = _pending.Values.GetEnumerator();
                while (e.MoveNext())
                {
                    if (!e.Current.Settled)
                    {
                        waiting++;
                    }
                }

                return waiting;
            }
        }

        /// <summary>
        /// Records what a file looks like right now.
        /// </summary>
        /// <param name="path">
        /// The file's path. Compared case-insensitively, because Windows paths are.
        /// </param>
        /// <param name="size">
        /// The file's current size in bytes.
        /// </param>
        /// <param name="lastWriteTicks">
        /// The file's last-write stamp, in the caller's units.
        /// </param>
        /// <param name="nowTicks">
        /// The current time, in the same units.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> is null.
        /// </exception>
        /// <remarks>
        /// Safe to call as often as you like - from a file event, from a rescan, or both for the
        /// same file. An observation that reports the SAME size and stamp does not restart the
        /// quiet period, so a watcher that fires repeatedly on an untouched file will not keep a
        /// settled file waiting for ever.
        /// </remarks>
        public void Observe(string path, long size, long lastWriteTicks, long nowTicks)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            Pending? entry;
            if (!_pending.TryGetValue(path, out entry))
            {
                entry = new Pending();
                entry.Size = size;
                entry.LastWriteTicks = lastWriteTicks;
                entry.LastChangedAtTicks = nowTicks;
                _pending.Add(path, entry);
                return;
            }

            if (entry.Size != size || entry.LastWriteTicks != lastWriteTicks)
            {
                // Really changed: the quiet period starts again from here, and a file that had
                // settled is in play once more.
                entry.Size = size;
                entry.LastWriteTicks = lastWriteTicks;
                entry.LastChangedAtTicks = nowTicks;
                entry.Settled = false;
            }
        }

        /// <summary>
        /// Whether a file is still being written - seen changing, and not yet settled.
        /// </summary>
        /// <param name="path">
        /// The file to ask about.
        /// </param>
        /// <returns>
        /// <see langword="true"/> only while the file is genuinely unsettled. A file never seen,
        /// or one that has already settled, both answer <see langword="false"/>.
        /// </returns>
        /// <remarks>
        /// The distinction a caller needs and could not previously make. "Not in the list this
        /// pass returned" covers both "still being written" and "settled a while ago", and treating
        /// the second as the first is what produced a permanent, false "still being written".
        /// </remarks>
        public bool IsStillBeingWritten(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            Pending? entry;
            return _pending.TryGetValue(path, out entry) && !entry.Settled;
        }

        /// <summary>
        /// Takes the files that have been quiet long enough, removing them from the tracker.
        /// </summary>
        /// <param name="nowTicks">
        /// The current time, in the caller's units.
        /// </param>
        /// <returns>
        /// The settled paths. Empty when nothing is ready. Never null.
        /// </returns>
        /// <remarks>
        /// Each file is returned ONCE per burst of writing: a settled entry is kept and marked
        /// rather than removed, so observing the same untouched file again does not present it as
        /// freshly written. A file that really is edited afterwards clears the mark and settles
        /// again.
        /// </remarks>
        public IReadOnlyList<string> TakeSettled(long nowTicks)
        {
            List<string>? settled = null;

            // Collect first, then remove - a dictionary cannot be modified while it is being
            // enumerated.
            foreach (KeyValuePair<string, Pending> pair in _pending)
            {
                if (!pair.Value.Settled && nowTicks - pair.Value.LastChangedAtTicks >= _quietPeriodTicks)
                {
                    if (settled == null)
                    {
                        settled = new List<string>();
                    }

                    settled.Add(pair.Key);
                }
            }

            if (settled == null)
            {
                return Array.Empty<string>();
            }

            for (int i = 0; i < settled.Count; i++)
            {
                _pending[settled[i]].Settled = true;
            }

            return settled;
        }

        /// <summary>
        /// Drops a file that is being watched, without transferring it.
        /// </summary>
        /// <param name="path">
        /// The path to forget.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the file was being tracked.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> is null.
        /// </exception>
        /// <remarks>
        /// For a file deleted or renamed while it was settling. Without this it would sit in the
        /// tracker until the quiet period elapsed and then be reported as ready to transfer, by
        /// which time it no longer exists.
        /// </remarks>
        public bool Forget(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            return _pending.Remove(path);
        }
    }
}
