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
    /// Carries out a plan: holds the queue, runs one transfer at a time through an
    /// <see cref="ISyncTransferAgent"/>, and records what actually completed in the ledger.
    /// </summary>
    /// <remarks>
    /// <para>This is the half that DOES; <see cref="SyncPlanner"/> is the half that DECIDES</para>
    /// <para>
    /// The planner never opens a file or sends a frame. This class never decides whether a file
    /// ought to move - it is handed actions and carries them out. Keeping the two apart is what
    /// makes both testable, and it is the reason a wrong decision and a failed transfer are
    /// diagnosed in different places instead of in one tangle.
    /// </para>
    /// <para><b>The ledger is written ONLY when a transfer really finished</b></para>
    /// <para>
    /// The ledger's whole job is to answer "have we already carried this exact content". Recording
    /// a transfer that failed would answer YES for bytes the machine never received, and the file
    /// would then be skipped for ever - a silent, permanent hole that no later pass would repair.
    /// So <see cref="SyncLedger.RecordTransfer"/> is called from exactly one place: after an
    /// outcome that says it succeeded.
    /// </para>
    /// <para><b>A failure does not stop the queue</b></para>
    /// <para>
    /// One refused name must not strand the twenty files behind it. Failures are counted and
    /// reported and the queue moves on. What a failure DOES do is leave the ledger untouched, so
    /// the next pass plans that file again - retrying is a consequence of honest bookkeeping
    /// rather than a mechanism of its own.
    /// </para>
    /// <para><b>Nothing here blocks</b></para>
    /// <para>
    /// <see cref="Pump"/> is called from the node's loop tick, which also serves the file server
    /// answering the machine at the other end. Waiting here would stop answering it, and a peer
    /// that is not answered times out the conversation.
    /// </para>
    /// </remarks>
    public sealed class SyncRunner
    {
        private readonly ISyncTransferAgent _agent;
        private readonly SyncLedger _ledger;
        private readonly Queue<SyncTransferRequest> _queue;

        private SyncTransferRequest? _running;

        // Paths already queued or being carried. WHY THIS EXISTS: the ledger is written only when
        // a transfer SUCCEEDS, so between a file being queued and that transfer finishing there is
        // nothing to stop the next pass queueing it again. MEASURED 2026-08-11 against D100: one
        // dropped file was queued on every scan - "1 waiting", "2 waiting", "3 waiting" - and would
        // have been carried once per scan had the link been up. The pass cannot answer this itself:
        // only the queue knows what is in the queue.
        private readonly HashSet<string> _inFlight;

        /// <summary>
        /// Creates a runner.
        /// </summary>
        /// <param name="agent">
        /// The thing that actually moves bytes.
        /// </param>
        /// <param name="ledger">
        /// What has been carried before. Written here, because this is where a transfer completes.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="agent"/> or <paramref name="ledger"/> is null.
        /// </exception>
        public SyncRunner(ISyncTransferAgent agent, SyncLedger ledger)
        {
            _agent = agent ?? throw new ArgumentNullException(nameof(agent));
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _queue = new Queue<SyncTransferRequest>();

            // Paths are compared the way the file system compares them.
            _inFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Raised with a line worth showing a person. Never null-checked at the call site.
        /// </summary>
        public Action<string>? Log { get; set; }

        /// <summary>
        /// Gets how many transfers are waiting, not counting one already running.
        /// </summary>
        public int Queued
        {
            get { return _queue.Count; }
        }

        /// <summary>
        /// Gets whether a transfer is running right now.
        /// </summary>
        public bool Busy
        {
            get { return _running != null; }
        }

        /// <summary>
        /// Gets how many transfers have completed successfully.
        /// </summary>
        public int Completed { get; private set; }

        /// <summary>
        /// Gets how many transfers have failed.
        /// </summary>
        public int Failed { get; private set; }

        /// <summary>
        /// SINTRAN's "File already exists" - 62 decimal, 076 octal.
        /// </summary>
        /// <remarks>
        /// Named here rather than written as a bare 62 at the point of use, because a number in a
        /// comparison is where a reader stops and has to go looking. VERIFIED 2026-08-18 against
        /// D100: a create of a file that was already there came back with it.
        /// </remarks>
        private const int SintranFileAlreadyExists = 62;

        /// <summary>
        /// Adds the transferable parts of a plan to the queue.
        /// </summary>
        /// <param name="actions">
        /// The planner's output for one mapping.
        /// </param>
        /// <param name="machine">
        /// The machine the mapping addresses.
        /// </param>
        /// <returns>
        /// How many actions were queued.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="actions"/> or <paramref name="machine"/> is null.
        /// </exception>
        /// <remarks>
        /// <para>
        /// <see cref="SyncActionKind.Skip"/> is reported and NOT queued - a skip is a decision that
        /// something will not be carried, and the person needs to see it rather than have it
        /// silently vanish between the two halves.
        /// </para>
        /// <para>
        /// <see cref="SyncActionKind.DeleteRemote"/> is reported and not queued either, for a
        /// different and blunter reason: whether the file-access delete works at all has never
        /// been proven against a real machine. Queuing it would mean pretending it had. When it is
        /// proven it becomes an ordinary case here.
        /// </para>
        /// </remarks>
        public int Enqueue(IReadOnlyList<SyncAction> actions, string machine)
        {
            if (actions == null) { throw new ArgumentNullException(nameof(actions)); }
            if (machine == null) { throw new ArgumentNullException(nameof(machine)); }

            int added = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                SyncAction action = actions[i];

                switch (action.Kind)
                {
                    case SyncActionKind.Create:
                    case SyncActionKind.Overwrite:
                    case SyncActionKind.Pull:
                        // Already waiting or already going out: the next pass will see it again
                        // anyway if it still needs carrying, because a transfer only reaches the
                        // ledger once it has actually finished.
                        if (!_inFlight.Add(action.LocalPath))
                        {
                            break;
                        }

                        _queue.Enqueue(new SyncTransferRequest(
                            action.Kind, action.LocalPath, action.FileSpec, machine));
                        added++;
                        break;

                    case SyncActionKind.Skip:
                        Log?.Invoke($"[sync] skipping {action.FileSpec}: {action.Reason}");
                        break;

                    case SyncActionKind.DeleteRemote:
                        // Deliberately not carried out - see the remarks. Saying so is the point.
                        Log?.Invoke(
                            $"[sync] {action.FileSpec} would be DELETED on {machine}, and this"
                            + " daemon does not delete: the file-access delete has never been"
                            + " proven against a real machine. Remove it by hand if that is what"
                            + " you want.");
                        break;

                    default:
                        // None, and anything added later that this does not yet understand.
                        break;
                }
            }

            return added;
        }

        /// <summary>
        /// Moves the queue along by one step. Call once per loop tick.
        /// </summary>
        /// <remarks>
        /// Starts a transfer when the agent is free and ready, and finishes one when the agent
        /// says it is over. Never both in the same tick: a transfer that completed leaves the
        /// agent to settle before the next one takes its session port and its place in the
        /// datagram sequence.
        /// </remarks>
        public void Pump()
        {
            // Something running: the only question is whether it is over.
            if (_running != null)
            {
                SyncTransferResult? result;
                if (!_agent.Poll(out result) || result == null)
                {
                    return;
                }

                Finish(_running, result);
                _running = null;
                return;
            }

            if (_queue.Count == 0 || !_agent.Ready)
            {
                return;
            }

            SyncTransferRequest next = _queue.Dequeue();
            if (!_agent.Begin(next))
            {
                _inFlight.Remove(next.LocalPath);
                // Refused outright. Counted as a failure so the pass reports honestly, and the
                // ledger is left alone so the next pass tries again.
                Failed++;
                Log?.Invoke($"[sync] could not start {Describe(next)}");
                return;
            }

            _running = next;
            Log?.Invoke($"[sync] {Describe(next)} started");
        }

        /// <summary>
        /// Records an outcome and reports it.
        /// </summary>
        /// <param name="request">
        /// What was being carried.
        /// </param>
        /// <param name="result">
        /// How it ended.
        /// </param>
        private void Finish(SyncTransferRequest request, SyncTransferResult result)
        {
            _inFlight.Remove(request.LocalPath);

            if (!result.Succeeded)
            {
                // "FILE ALREADY EXISTS" IS AN ANSWER, NOT A FAULT.
                //
                // The plan said CREATE because nothing knew the file was on the machine. The machine
                // has now said it is - SINTRAN error 62, 076 octal - which is exactly the fact the
                // planner was missing. Remember it, and the next pass plans an OVERWRITE and the
                // file goes across.
                //
                // Without this the daemon retried the same doomed create for ever, once a pass. And
                // before the refusal was read at all, this was recorded as a SUCCESS and the file
                // was silently never sent again - so this one number has now been wrong in both
                // directions.
                //
                // Not counted as a failure, because nothing has failed yet: the transfer is going to
                // be re-planned and re-run. Counting it would make a healthy first pass look broken.
                if (result.SintranError == SintranFileAlreadyExists && request.IsPush)
                {
                    _ledger.RecordRemoteExistence(request.LocalPath);
                    Log?.Invoke(
                        $"[sync] {Describe(request)}: the file is already on the machine -"
                        + " noting that and replacing it on the next pass.");
                    return;
                }

                Failed++;
                Log?.Invoke($"[sync] FAILED {Describe(request)}: {result.Reason}");
                return;
            }

            Completed++;

            // The ledger remembers the CONTENT, so a file that comes back unchanged is not carried
            // again, and our own push arriving back as a listing is not mistaken for an edit.
            SyncDirection direction = request.IsPush ? SyncDirection.ToMachine : SyncDirection.FromMachine;
            _ledger.RecordTransfer(request.LocalPath, result.Hash, direction);

            Log?.Invoke($"[sync] {Describe(request)} done, {result.ByteCount} byte(s)");
        }

        /// <summary>
        /// Describes a request in one readable phrase.
        /// </summary>
        /// <param name="request">
        /// The request.
        /// </param>
        /// <returns>
        /// The description.
        /// </returns>
        private static string Describe(SyncTransferRequest request)
        {
            if (request.Kind == SyncActionKind.Pull)
            {
                return $"pull {request.FileSpec} from {request.Machine} -> {request.LocalPath}";
            }

            string verb = request.Kind == SyncActionKind.Create ? "create" : "overwrite";
            return $"{verb} {request.FileSpec} on {request.Machine} <- {request.LocalPath}";
        }
    }
}
