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
    /// One cycle of the daemon for one mapping: read the folder, hold back anything still being
    /// written, plan, and hand the work to a <see cref="SyncRunner"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>The settle tracker is why a half-written file is not sent</b></para>
    /// <para>
    /// A scan can catch a file in the middle of being saved, and a compiler on the machine would
    /// then be handed half a source file - which fails in a way that looks like a compiler bug
    /// rather than a race. So a file is only offered once its size and write time have stopped
    /// moving for the quiet period. Everything a pass sees is reported to the tracker; only what
    /// the tracker calls settled reaches the planner.
    /// </para>
    /// <para><b>What it does with the remote side</b></para>
    /// <para>
    /// A listing has to come from the machine, and getting one is a conversation, not a function
    /// call. So the caller supplies whatever listing it last obtained, and an empty list is a
    /// perfectly good answer meaning "we have not listed it yet". With an empty listing the
    /// planner treats every local file as needing creating, which is right for a first run and
    /// harmless afterwards because the ledger stops the ones already carried.
    /// </para>
    /// </remarks>
    public sealed class SyncPass
    {
        private readonly SyncPlanner _planner;
        private readonly FileSettleTracker _settle;

        /// <summary>
        /// Creates a pass.
        /// </summary>
        /// <param name="planner">
        /// The decision half.
        /// </param>
        /// <param name="settle">
        /// The tracker that decides when a file has stopped changing.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="planner"/> or <paramref name="settle"/> is null.
        /// </exception>
        public SyncPass(SyncPlanner planner, FileSettleTracker settle)
        {
            _planner = planner ?? throw new ArgumentNullException(nameof(planner));
            _settle = settle ?? throw new ArgumentNullException(nameof(settle));
        }

        /// <summary>
        /// Raised with a line worth showing a person.
        /// </summary>
        public Action<string>? Log { get; set; }

        /// <summary>
        /// Runs one cycle and queues whatever it decided.
        /// </summary>
        /// <param name="mapping">
        /// The mapping to run.
        /// </param>
        /// <param name="observed">
        /// Every file now in the folder, already hashed - normally from
        /// <see cref="LocalFolderScanner.Scan"/>.
        /// </param>
        /// <param name="sizesAndTimes">
        /// Size and last-write ticks per path, for the settle tracker. A path missing from this is
        /// treated as settled, which is what a test that does not care about settling wants.
        /// </param>
        /// <param name="remote">
        /// The machine's listing as last obtained, or an empty list when it has not been listed.
        /// </param>
        /// <param name="runner">
        /// Where the work goes.
        /// </param>
        /// <param name="nowTicks">
        /// The current time, in the same units the tracker was built with.
        /// </param>
        /// <returns>
        /// How many transfers were queued.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any argument is null.
        /// </exception>
        public int Run(
            SyncFolderMapping mapping,
            IReadOnlyList<LocalFileState> observed,
            IReadOnlyDictionary<string, FileSizeAndTime> sizesAndTimes,
            IReadOnlyList<RemoteFileState> remote,
            SyncRunner runner,
            long nowTicks)
        {
            if (mapping == null) { throw new ArgumentNullException(nameof(mapping)); }
            if (observed == null) { throw new ArgumentNullException(nameof(observed)); }
            if (sizesAndTimes == null) { throw new ArgumentNullException(nameof(sizesAndTimes)); }
            if (remote == null) { throw new ArgumentNullException(nameof(remote)); }
            if (runner == null) { throw new ArgumentNullException(nameof(runner)); }

            // Tell the tracker about everything we can see, so a file that is STILL moving keeps
            // its clock running rather than being forgotten between passes.
            for (int i = 0; i < observed.Count; i++)
            {
                string path = observed[i].Path;
                FileSizeAndTime stamp;
                if (sizesAndTimes.TryGetValue(path, out stamp))
                {
                    _settle.Observe(path, stamp.Size, stamp.LastWriteTicks, nowTicks);
                }
            }

            _settle.TakeSettled(nowTicks);

            // A file is held back ONLY while it is genuinely still being written. Anything else is
            // offered, and the ledger decides whether it actually moves.
            //
            // The old rule was "offer it if it is in the list TakeSettled just returned", which
            // conflated two different things: a file being written right now, and a file that
            // settled several passes ago. The second is the normal state of every file in the
            // folder, so the daemon reported "1 file(s) still being written" for ever about a file
            // it had already carried. MEASURED 2026-08-11.
            List<LocalFileState> ready = new List<LocalFileState>(observed.Count);
            for (int i = 0; i < observed.Count; i++)
            {
                LocalFileState state = observed[i];
                if (!_settle.IsStillBeingWritten(state.Path))
                {
                    ready.Add(state);
                }
            }

            int held = observed.Count - ready.Count;
            if (held > 0)
            {
                Log?.Invoke($"[sync] {held} file(s) still being written; they wait for the next pass");
            }

            // The planner takes arrays. Copying here rather than changing its signature keeps the
            // decision half's shape settled - it has tests written against exactly that shape.
            RemoteFileState[] remoteArray = new RemoteFileState[remote.Count];
            for (int i = 0; i < remote.Count; i++)
            {
                remoteArray[i] = remote[i];
            }

            IReadOnlyList<SyncAction> actions = _planner.Plan(mapping, ready.ToArray(), remoteArray);
            return runner.Enqueue(actions, mapping.Machine);
        }

    }
}
