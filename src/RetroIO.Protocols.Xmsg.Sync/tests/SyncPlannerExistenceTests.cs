//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Sync;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Sync.Tests
{
    /// <summary>
    /// Creating a file versus replacing one, when there is no directory listing to consult.
    /// </summary>
    /// <remarks>
    /// The daemon has no listing today, so an empty array reaches the planner and is
    /// indistinguishable from a genuinely empty directory. Everything then read as new, and every
    /// push after a restart went out as a CREATE of a name that already existed - which SINTRAN
    /// refuses. The ledger settles it: it holds an entry only for a file actually carried.
    /// </remarks>
    public sealed class SyncPlannerExistenceTests
    {
        private static SyncFolderMapping Mapping(SyncLedger ledger, out SyncPlanner planner)
        {
            SyncFolderMapping mapping = new SyncFolderMapping("c:\\work", "D100", "SYSTEM");
            mapping.Direction = SyncDirection.ToMachine;

            SyncFolderMap map = new SyncFolderMap();
            map.Add(mapping);

            planner = new SyncPlanner(map, ledger);
            return mapping;
        }

        private static LocalFileState[] One(string path, params byte[] hash)
        {
            return new LocalFileState[] { new LocalFileState(path, hash) };
        }

        /// <summary>
        /// A file never carried, with no listing, is CREATED.
        /// </summary>
        /// <remarks>
        /// The first run against a machine. Nothing says the file is there, so making it is right.
        /// </remarks>
        [Fact]
        public void WithNoListingAFileNeverCarriedIsCreated()
        {
            SyncLedger ledger = new SyncLedger();
            SyncPlanner planner;
            SyncFolderMapping mapping = Mapping(ledger, out planner);

            SyncAction[] plan = planner.Plan(
                mapping, One("c:\\work\\A.SYMB", 1), Array.Empty<RemoteFileState>());

            Assert.Single(plan);
            Assert.Equal(SyncActionKind.Create, plan[0].Kind);
        }

        /// <summary>
        /// A file we HAVE carried, with no listing, is OVERWRITTEN rather than created again.
        /// </summary>
        /// <remarks>
        /// The case that was broken. After a restart the ledger still says this file was sent, so
        /// the copy on the machine exists and asking to create it would be refused.
        /// </remarks>
        [Fact]
        public void WithNoListingAFileAlreadyCarriedIsOverwritten()
        {
            SyncLedger ledger = new SyncLedger();
            ledger.RecordTransfer("c:\\work\\A.SYMB", new byte[] { 1 }, SyncDirection.ToMachine);

            SyncPlanner planner;
            SyncFolderMapping mapping = Mapping(ledger, out planner);

            // Edited since - a different hash, so it does need carrying.
            SyncAction[] plan = planner.Plan(
                mapping, One("c:\\work\\A.SYMB", 2), Array.Empty<RemoteFileState>());

            Assert.Single(plan);
            Assert.Equal(SyncActionKind.Overwrite, plan[0].Kind);
        }

        /// <summary>
        /// A file PULLED from the machine is also known to exist there.
        /// </summary>
        /// <remarks>
        /// Direction does not matter to the question being asked. If we read it off the machine,
        /// it was on the machine.
        /// </remarks>
        [Fact]
        public void AFilePulledFromTheMachineCountsAsExisting()
        {
            SyncLedger ledger = new SyncLedger();
            ledger.RecordTransfer("c:\\work\\A.SYMB", new byte[] { 1 }, SyncDirection.FromMachine);

            SyncPlanner planner;
            SyncFolderMapping mapping = Mapping(ledger, out planner);

            SyncAction[] plan = planner.Plan(
                mapping, One("c:\\work\\A.SYMB", 2), Array.Empty<RemoteFileState>());

            Assert.Single(plan);
            Assert.Equal(SyncActionKind.Overwrite, plan[0].Kind);
        }

        /// <summary>
        /// A REAL listing overrules the ledger: a file the machine does not have is created.
        /// </summary>
        /// <remarks>
        /// The guard on the whole idea. The ledger is a fallback for when nothing better is known;
        /// it must never overrule what the machine actually said. Somebody deleting the file there
        /// is exactly the case a listing reports and the ledger cannot.
        /// </remarks>
        [Fact]
        public void ARealListingOverrulesTheLedger()
        {
            SyncLedger ledger = new SyncLedger();
            ledger.RecordTransfer("c:\\work\\A.SYMB", new byte[] { 1 }, SyncDirection.ToMachine);

            SyncPlanner planner;
            SyncFolderMapping mapping = Mapping(ledger, out planner);

            // The machine answered, and it holds a DIFFERENT file - so A:SYMB is gone from it.
            RemoteFileState[] listing = new RemoteFileState[]
            {
                new RemoteFileState("SOMETHINGELSE", "SYMB"),
            };

            SyncAction[] plan = planner.Plan(mapping, One("c:\\work\\A.SYMB", 2), listing);

            Assert.Single(plan);
            Assert.Equal(SyncActionKind.Create, plan[0].Kind);
        }

        /// <summary>
        /// A listing that DOES hold the file still means overwrite, ledger or no ledger.
        /// </summary>
        [Fact]
        public void AListingHoldingTheFileMeansOverwrite()
        {
            SyncLedger ledger = new SyncLedger();

            SyncPlanner planner;
            SyncFolderMapping mapping = Mapping(ledger, out planner);

            RemoteFileState[] listing = new RemoteFileState[]
            {
                new RemoteFileState("A", "SYMB"),
            };

            SyncAction[] plan = planner.Plan(mapping, One("c:\\work\\A.SYMB", 2), listing);

            Assert.Single(plan);
            Assert.Equal(SyncActionKind.Overwrite, plan[0].Kind);
        }

        /// <summary>
        /// Unchanged content is still nothing to do, whatever the ledger says about existence.
        /// </summary>
        [Fact]
        public void UnchangedContentIsStillNotCarried()
        {
            SyncLedger ledger = new SyncLedger();
            ledger.RecordTransfer("c:\\work\\A.SYMB", new byte[] { 1 }, SyncDirection.ToMachine);

            SyncPlanner planner;
            SyncFolderMapping mapping = Mapping(ledger, out planner);

            SyncAction[] plan = planner.Plan(
                mapping, One("c:\\work\\A.SYMB", 1), Array.Empty<RemoteFileState>());

            Assert.Empty(plan);
        }
    }
}
