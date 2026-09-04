using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Sync;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Sync.Tests
{
    /// <summary>
    /// One daemon cycle: what it offers the planner, and - the point of the whole class - what it
    /// holds back because somebody is still writing it.
    /// </summary>
    public sealed class SyncPassTests
    {
        /// <summary>
        /// An agent that accepts everything, so a pass can be tested without a machine.
        /// </summary>
        private sealed class AcceptingAgent : ISyncTransferAgent
        {
            public bool Ready
            {
                get { return true; }
            }

            public bool Begin(SyncTransferRequest request)
            {
                return true;
            }

            public bool Poll(out SyncTransferResult? result)
            {
                result = null;
                return false;
            }
        }

        private const long Quiet = 1000;

        private static SyncFolderMapping Mapping()
        {
            SyncFolderMapping mapping = new SyncFolderMapping("c:\\work", "D100", "SYSTEM");
            mapping.Direction = SyncDirection.ToMachine;
            return mapping;
        }

        /// <summary>
        /// Builds a pass, a runner and the ledger they share, with the mapping REGISTERED.
        /// </summary>
        /// <remarks>
        /// The mapping has to be in the map, not merely passed to Plan: the planner checks that the
        /// file's folder resolves back to the SAME mapping instance, so that a file cannot be
        /// carried to the wrong user's flat directory. Building the two separately is a test bug,
        /// and it produced an empty plan with no error - which is the planner behaving correctly.
        /// </remarks>
        private static SyncPass BuildPass(
            SyncFolderMapping mapping, out SyncRunner runner, out SyncLedger ledger)
        {
            ledger = new SyncLedger();
            runner = new SyncRunner(new AcceptingAgent(), ledger);

            SyncFolderMap map = new SyncFolderMap();
            map.Add(mapping);

            return new SyncPass(new SyncPlanner(map, ledger), new FileSettleTracker(Quiet));
        }

        /// <summary>
        /// A file that is still being written is NOT offered, and is offered once it stops.
        /// </summary>
        /// <remarks>
        /// The case the settle tracker exists for. Catch a save halfway and the machine gets half a
        /// source file, which fails on the far side looking like a compiler fault rather than a
        /// race here.
        /// </remarks>
        [Fact]
        public void AFileStillBeingWrittenIsHeldBackUntilItStops()
        {
            SyncRunner runner;
            SyncLedger ledger;
            SyncFolderMapping mapping = Mapping();
            SyncPass pass = BuildPass(mapping, out runner, out ledger);

            List<LocalFileState> files = new List<LocalFileState>();
            files.Add(new LocalFileState("c:\\work\\A.SYMB", new byte[] { 1 }));

            Dictionary<string, FileSizeAndTime> stamps = new Dictionary<string, FileSizeAndTime>();

            // First sight of the file: the clock starts, nothing is settled yet.
            stamps["c:\\work\\A.SYMB"] = new FileSizeAndTime(100, 5);
            int queued = pass.Run(mapping, files, stamps, Array.Empty<RemoteFileState>(), runner, 0);
            Assert.Equal(0, queued);

            // It grew, so the clock restarts even though time has passed.
            stamps["c:\\work\\A.SYMB"] = new FileSizeAndTime(400, 900);
            queued = pass.Run(mapping, files, stamps, Array.Empty<RemoteFileState>(), runner, 900);
            Assert.Equal(0, queued);

            // Unchanged for longer than the quiet period: now it may go.
            queued = pass.Run(mapping, files, stamps, Array.Empty<RemoteFileState>(), runner, 2500);
            Assert.Equal(1, queued);
        }

        /// <summary>
        /// A file with no size stamp is treated as settled.
        /// </summary>
        /// <remarks>
        /// Lets a caller that does not care about settling - a one-shot push, a test - use the same
        /// pass without inventing timestamps.
        /// </remarks>
        [Fact]
        public void AFileWithNoStampIsOfferedStraightAway()
        {
            SyncRunner runner;
            SyncLedger ledger;
            SyncFolderMapping mapping = Mapping();
            SyncPass pass = BuildPass(mapping, out runner, out ledger);

            List<LocalFileState> files = new List<LocalFileState>();
            files.Add(new LocalFileState("c:\\work\\B.SYMB", new byte[] { 2 }));

            int queued = pass.Run(
                mapping, files, new Dictionary<string, FileSizeAndTime>(),
                Array.Empty<RemoteFileState>(), runner, 0);

            Assert.Equal(1, queued);
        }

        /// <summary>
        /// A file already carried at this exact content is not queued again.
        /// </summary>
        /// <remarks>
        /// The ledger doing its job through the pass: saving a file without editing it is a normal
        /// habit and must not cost a transfer.
        /// </remarks>
        [Fact]
        public void ContentAlreadyCarriedIsNotQueuedAgain()
        {
            SyncRunner runner;
            SyncLedger ledger;
            SyncFolderMapping mapping = Mapping();
            SyncPass pass = BuildPass(mapping, out runner, out ledger);

            byte[] hash = new byte[] { 3, 3, 3 };
            ledger.RecordTransfer("c:\\work\\C.SYMB", hash, SyncDirection.ToMachine);

            List<LocalFileState> files = new List<LocalFileState>();
            files.Add(new LocalFileState("c:\\work\\C.SYMB", hash));

            int queued = pass.Run(
                mapping, files, new Dictionary<string, FileSizeAndTime>(),
                Array.Empty<RemoteFileState>(), runner, 0);

            Assert.Equal(0, queued);
        }

        /// <summary>
        /// An empty listing still lets the first pass run.
        /// </summary>
        /// <remarks>
        /// Getting a listing off the machine is a conversation, not a function call, so the first
        /// pass usually has none. Everything local then reads as needing creating, which is right.
        /// </remarks>
        [Fact]
        public void AnEmptyRemoteListingMeansEverythingLocalIsNew()
        {
            SyncRunner runner;
            SyncLedger ledger;
            SyncFolderMapping mapping = Mapping();
            SyncPass pass = BuildPass(mapping, out runner, out ledger);

            List<LocalFileState> files = new List<LocalFileState>();
            files.Add(new LocalFileState("c:\\work\\D.SYMB", new byte[] { 4 }));
            files.Add(new LocalFileState("c:\\work\\E.SYMB", new byte[] { 5 }));

            int queued = pass.Run(
                mapping, files, new Dictionary<string, FileSizeAndTime>(),
                Array.Empty<RemoteFileState>(), runner, 0);

            Assert.Equal(2, queued);
        }
    }
}
