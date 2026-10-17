//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Sync;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Sync.Tests
{
    /// <summary>
    /// The half that carries a plan out: the queue, one transfer at a time, and the ledger rule
    /// that decides whether a file is ever offered again.
    /// </summary>
    public sealed class SyncRunnerTests
    {
        /// <summary>
        /// A transfer agent that does nothing, so the QUEUE can be tested without a machine.
        /// </summary>
        /// <remarks>
        /// The whole reason <see cref="ISyncTransferAgent"/> exists. Every case below - a refused
        /// start, a failure halfway, two transfers not overlapping - is a thing that happens
        /// against a real ND and is miserable to provoke there on purpose.
        /// </remarks>
        private sealed class FakeAgent : ISyncTransferAgent
        {
            private SyncTransferResult? _pending;

            public bool Ready { get; set; } = true;

            public bool AcceptBegin { get; set; } = true;

            public int BeginCount { get; private set; }

            public SyncTransferRequest? LastRequest { get; private set; }

            public bool Running { get; private set; }

            public bool Begin(SyncTransferRequest request)
            {
                if (!AcceptBegin)
                {
                    return false;
                }

                // Catches an overlap directly rather than by counting frames afterwards.
                Assert.False(Running, "the runner started a transfer while one was still running");

                BeginCount++;
                LastRequest = request;
                Running = true;
                return true;
            }

            public bool Poll(out SyncTransferResult? result)
            {
                if (_pending == null)
                {
                    result = null;
                    return false;
                }

                result = _pending;
                _pending = null;
                Running = false;
                return true;
            }

            /// <summary>
            /// Makes the running transfer report the given outcome on the next poll.
            /// </summary>
            /// <param name="result">
            /// The outcome to report.
            /// </param>
            public void CompleteWith(SyncTransferResult result)
            {
                _pending = result;
            }
        }

        private static List<SyncAction> One(SyncActionKind kind, string path, string spec)
        {
            List<SyncAction> actions = new List<SyncAction>();
            actions.Add(new SyncAction(kind, path, spec, string.Empty));
            return actions;
        }

        /// <summary>
        /// A push that completes is written to the ledger, so the same content is never sent twice.
        /// </summary>
        [Fact]
        public void ACompletedPushIsRecordedInTheLedger()
        {
            SyncLedger ledger = new SyncLedger();
            FakeAgent agent = new FakeAgent();
            SyncRunner runner = new SyncRunner(agent, ledger);

            byte[] hash = new byte[] { 1, 2, 3 };
            runner.Enqueue(One(SyncActionKind.Create, "c:\\work\\a.txt", "A:TXT"), "D100");

            runner.Pump();                                  // starts it
            agent.CompleteWith(SyncTransferResult.Ok(hash, 20400));
            runner.Pump();                                  // finishes it

            Assert.Equal(1, runner.Completed);
            Assert.Equal(0, runner.Failed);
            Assert.False(ledger.NeedsTransfer("c:\\work\\a.txt", hash));
        }

        /// <summary>
        /// A FAILED transfer must NOT be written to the ledger.
        /// </summary>
        /// <remarks>
        /// The one that matters most. The ledger answers "have we already carried this content";
        /// recording a failure answers YES for bytes the machine never received, and that file is
        /// then skipped for ever - a silent hole no later pass repairs. Leaving the ledger alone
        /// is also what makes the next pass retry, with no retry mechanism of its own.
        /// </remarks>
        [Fact]
        public void AFailedTransferIsNotRecordedSoTheNextPassRetriesIt()
        {
            SyncLedger ledger = new SyncLedger();
            FakeAgent agent = new FakeAgent();
            SyncRunner runner = new SyncRunner(agent, ledger);

            byte[] hash = new byte[] { 9, 9 };
            runner.Enqueue(One(SyncActionKind.Create, "c:\\work\\b.txt", "B:TXT"), "D100");

            runner.Pump();
            agent.CompleteWith(SyncTransferResult.Failed("the peer refused the name"));
            runner.Pump();

            Assert.Equal(0, runner.Completed);
            Assert.Equal(1, runner.Failed);
            Assert.True(ledger.NeedsTransfer("c:\\work\\b.txt", hash));
        }

        /// <summary>
        /// One failure does not strand the files queued behind it.
        /// </summary>
        [Fact]
        public void TheQueueCarriesOnAfterAFailure()
        {
            SyncLedger ledger = new SyncLedger();
            FakeAgent agent = new FakeAgent();
            SyncRunner runner = new SyncRunner(agent, ledger);

            List<SyncAction> actions = new List<SyncAction>();
            actions.Add(new SyncAction(SyncActionKind.Create, "c:\\w\\1.txt", "ONE:TXT", string.Empty));
            actions.Add(new SyncAction(SyncActionKind.Create, "c:\\w\\2.txt", "TWO:TXT", string.Empty));
            runner.Enqueue(actions, "D100");

            runner.Pump();
            agent.CompleteWith(SyncTransferResult.Failed("nope"));
            runner.Pump();

            runner.Pump();
            agent.CompleteWith(SyncTransferResult.Ok(new byte[] { 7 }, 10));
            runner.Pump();

            Assert.Equal(2, agent.BeginCount);
            Assert.Equal(1, runner.Completed);
            Assert.Equal(1, runner.Failed);
        }

        /// <summary>
        /// Nothing starts while the agent is not ready, and nothing is lost either.
        /// </summary>
        /// <remarks>
        /// A link that has not come up yet is the normal state for the first seconds of a run. The
        /// work must wait rather than fail, or every start would burn the queue.
        /// </remarks>
        [Fact]
        public void WorkWaitsWhileTheAgentIsNotReady()
        {
            SyncLedger ledger = new SyncLedger();
            FakeAgent agent = new FakeAgent();
            agent.Ready = false;
            SyncRunner runner = new SyncRunner(agent, ledger);

            runner.Enqueue(One(SyncActionKind.Create, "c:\\w\\a.txt", "A:TXT"), "D100");

            runner.Pump();
            runner.Pump();

            Assert.Equal(0, agent.BeginCount);
            Assert.Equal(1, runner.Queued);
            Assert.Equal(0, runner.Failed);

            agent.Ready = true;
            runner.Pump();

            Assert.Equal(1, agent.BeginCount);
        }

        /// <summary>
        /// A start the agent refuses counts as a failure and does not hold the queue.
        /// </summary>
        [Fact]
        public void ARefusedStartIsAFailureAndTheQueueMovesOn()
        {
            SyncLedger ledger = new SyncLedger();
            FakeAgent agent = new FakeAgent();
            agent.AcceptBegin = false;
            SyncRunner runner = new SyncRunner(agent, ledger);

            runner.Enqueue(One(SyncActionKind.Create, "c:\\w\\a.txt", "A:TXT"), "D100");
            runner.Pump();

            Assert.Equal(1, runner.Failed);
            Assert.Equal(0, runner.Queued);
            Assert.False(runner.Busy);
        }

        /// <summary>
        /// A skip is reported and never queued.
        /// </summary>
        [Fact]
        public void ASkipIsReportedAndNotQueued()
        {
            SyncLedger ledger = new SyncLedger();
            FakeAgent agent = new FakeAgent();
            SyncRunner runner = new SyncRunner(agent, ledger);

            List<string> lines = new List<string>();
            runner.Log = line => lines.Add(line);

            List<SyncAction> actions = new List<SyncAction>();
            actions.Add(new SyncAction(
                SyncActionKind.Skip, "c:\\w\\toolonganame.txt", "TOOLONG:TXT", "name will not fit"));

            int queued = runner.Enqueue(actions, "D100");

            Assert.Equal(0, queued);
            Assert.Equal(0, runner.Queued);
            Assert.Contains(lines, line => line.Contains("name will not fit"));
        }

        /// <summary>
        /// A wanted remote delete is announced loudly and NOT carried out.
        /// </summary>
        /// <remarks>
        /// Deleting on the machine has never been proven to work over file access. Queuing it would
        /// be pretending it had, and the failure mode of pretending is somebody's file.
        /// </remarks>
        [Fact]
        public void ARemoteDeleteIsAnnouncedButNotCarriedOut()
        {
            SyncLedger ledger = new SyncLedger();
            FakeAgent agent = new FakeAgent();
            SyncRunner runner = new SyncRunner(agent, ledger);

            List<string> lines = new List<string>();
            runner.Log = line => lines.Add(line);

            List<SyncAction> actions = new List<SyncAction>();
            actions.Add(new SyncAction(
                SyncActionKind.DeleteRemote, "c:\\w\\gone.txt", "GONE:TXT", "local file removed"));

            int queued = runner.Enqueue(actions, "D100");

            Assert.Equal(0, queued);
            Assert.Equal(0, agent.BeginCount);
            Assert.Contains(lines, line => line.Contains("does not delete"));
        }

        /// <summary>
        /// A pull is recorded against the PULL direction, so the next pass does not push it back.
        /// </summary>
        /// <remarks>
        /// Without the direction the file would look locally new on the next pass and be sent
        /// straight back to the machine it came from - the two halves fighting over one file,
        /// for ever.
        /// </remarks>
        [Fact]
        public void ACompletedPullIsRecordedAsAPull()
        {
            SyncLedger ledger = new SyncLedger();
            FakeAgent agent = new FakeAgent();
            SyncRunner runner = new SyncRunner(agent, ledger);

            runner.Enqueue(One(SyncActionKind.Pull, "c:\\w\\r.txt", "R:TXT"), "D100");

            runner.Pump();
            agent.CompleteWith(SyncTransferResult.Ok(new byte[] { 4, 5 }, 99));
            runner.Pump();

            SyncDirection direction;
            Assert.True(ledger.TryGetDirection("c:\\w\\r.txt", out direction));
            Assert.Equal(SyncDirection.FromMachine, direction);
        }

        /// <summary>
        /// The request tells the agent whether the remote file is being made or replaced.
        /// </summary>
        /// <remarks>
        /// It decides the quoting of the filespec on the wire, so losing it here would produce a
        /// request the machine refuses.
        /// </remarks>
        [Fact]
        public void TheCreateOrOverwriteDistinctionReachesTheAgent()
        {
            SyncLedger ledger = new SyncLedger();
            FakeAgent agent = new FakeAgent();
            SyncRunner runner = new SyncRunner(agent, ledger);

            runner.Enqueue(One(SyncActionKind.Overwrite, "c:\\w\\a.txt", "A:TXT"), "D100");
            runner.Pump();

            Assert.NotNull(agent.LastRequest);
            Assert.Equal(SyncActionKind.Overwrite, agent.LastRequest!.Kind);
            Assert.True(agent.LastRequest.IsPush);
            Assert.Equal("D100", agent.LastRequest.Machine);
        }

        /// <summary>
        /// The same file offered twice before it has been carried is queued ONCE.
        /// </summary>
        /// <remarks>
        /// MEASURED against D100 on 2026-08-11 and it is why this exists. The daemon rescans every
        /// few seconds; the ledger is only written when a transfer SUCCEEDS; so between queueing a
        /// file and finishing it there was nothing to stop the next scan queueing it again. The log
        /// read "1 waiting", "2 waiting", "3 waiting" for one dropped file, and each of those would
        /// have become a separate transfer of the same bytes.
        /// </remarks>
        [Fact]
        public void AFileAlreadyQueuedIsNotQueuedAgain()
        {
            SyncLedger ledger = new SyncLedger();
            FakeAgent agent = new FakeAgent();
            agent.Ready = false;                       // nothing drains, so both offers pile up
            SyncRunner runner = new SyncRunner(agent, ledger);

            runner.Enqueue(One(SyncActionKind.Create, "c:\\w\\a.txt", "A:TXT"), "D100");
            runner.Enqueue(One(SyncActionKind.Create, "c:\\w\\a.txt", "A:TXT"), "D100");

            Assert.Equal(1, runner.Queued);
        }

        /// <summary>
        /// A file being carried right now is not queued a second time.
        /// </summary>
        [Fact]
        public void AFileBeingCarriedIsNotQueuedAgain()
        {
            SyncLedger ledger = new SyncLedger();
            FakeAgent agent = new FakeAgent();
            SyncRunner runner = new SyncRunner(agent, ledger);

            runner.Enqueue(One(SyncActionKind.Create, "c:\\w\\a.txt", "A:TXT"), "D100");
            runner.Pump();                              // now running, queue empty
            Assert.True(runner.Busy);

            runner.Enqueue(One(SyncActionKind.Create, "c:\\w\\a.txt", "A:TXT"), "D100");

            Assert.Equal(0, runner.Queued);
        }

        /// <summary>
        /// Once a transfer has FAILED, the same file may be queued again.
        /// </summary>
        /// <remarks>
        /// The other half of the rule. Blocking duplicates for ever would turn one failure into a
        /// file that is never carried again - the same permanent hole that recording a failed
        /// transfer in the ledger would open.
        /// </remarks>
        [Fact]
        public void AFileMayBeQueuedAgainAfterItFailed()
        {
            SyncLedger ledger = new SyncLedger();
            FakeAgent agent = new FakeAgent();
            SyncRunner runner = new SyncRunner(agent, ledger);

            runner.Enqueue(One(SyncActionKind.Create, "c:\\w\\a.txt", "A:TXT"), "D100");
            runner.Pump();
            agent.CompleteWith(SyncTransferResult.Failed("link went down"));
            runner.Pump();

            runner.Enqueue(One(SyncActionKind.Create, "c:\\w\\a.txt", "A:TXT"), "D100");

            Assert.Equal(1, runner.Queued);
        }
    }
}
