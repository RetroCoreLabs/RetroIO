using System;

using RetroIO.Protocols.Xmsg.Sync;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Sync.Tests
{
    /// <summary>
    /// The decisions the sync daemon makes, with no disk and no machine involved.
    /// </summary>
    /// <remarks>
    /// <para><b>These are the cases Ronny named</b></para>
    /// <para>
    /// The remote file does not exist and must be created; it exists and must be overwritten; the
    /// local file has gone and the remote one should follow, but only when that has been asked
    /// for. Plus the return leg for build output, and the name rules that stop a file being
    /// carried under a name SINTRAN cannot hold.
    /// </para>
    /// </remarks>
    public sealed class SyncPlannerTests
    {
        /// <summary>
        /// The folder the tests map. Never touched on disk - the planner only compares strings.
        /// </summary>
        private const string LocalFolder = "E:\\work\\proj";

        /// <summary>
        /// A file that is not on the machine is created, with the name QUOTED.
        /// </summary>
        /// <remarks>
        /// The quotes are the whole point of telling create and overwrite apart: without them
        /// SINTRAN complains about a file that is not there.
        /// </remarks>
        [Fact]
        public void AFileTheMachineDoesNotHaveIsCreatedWithQuotes()
        {
            SyncFolderMap map = new SyncFolderMap();
            SyncFolderMapping mapping = new SyncFolderMapping(LocalFolder, "D102", "SYSTEM");
            map.Add(mapping);

            SyncPlanner planner = new SyncPlanner(map, new SyncLedger());

            SyncAction[] plan = planner.Plan(
                mapping,
                new LocalFileState[] { File("HELLO.SYMB", 1) },
                new RemoteFileState[0]);

            Assert.Single(plan);
            Assert.Equal(SyncActionKind.Create, plan[0].Kind);
            Assert.Equal("D102(SYSTEM).\"HELLO:SYMB\"", plan[0].FileSpec);
        }

        /// <summary>
        /// A file the machine already has is overwritten, with the name BARE.
        /// </summary>
        [Fact]
        public void AFileTheMachineAlreadyHasIsOverwrittenWithoutQuotes()
        {
            SyncFolderMap map = new SyncFolderMap();
            SyncFolderMapping mapping = new SyncFolderMapping(LocalFolder, "D102", "SYSTEM");
            map.Add(mapping);

            SyncPlanner planner = new SyncPlanner(map, new SyncLedger());

            SyncAction[] plan = planner.Plan(
                mapping,
                new LocalFileState[] { File("HELLO.SYMB", 1) },
                new RemoteFileState[] { new RemoteFileState("HELLO", "SYMB") });

            Assert.Single(plan);
            Assert.Equal(SyncActionKind.Overwrite, plan[0].Kind);
            Assert.Equal("D102(SYSTEM).HELLO:SYMB", plan[0].FileSpec);
        }

        /// <summary>
        /// The type is part of matching, so two types of one name are two files.
        /// </summary>
        /// <remarks>
        /// Matching on the name alone would see <c>HELLO:BPUN</c> on the machine and call a push of
        /// <c>HELLO.SYMB</c> an overwrite - of a different file.
        /// </remarks>
        [Fact]
        public void TheTypeIsPartOfDecidingWhetherTheFileIsThere()
        {
            SyncFolderMap map = new SyncFolderMap();
            SyncFolderMapping mapping = new SyncFolderMapping(LocalFolder, "D102", "SYSTEM");
            map.Add(mapping);

            SyncPlanner planner = new SyncPlanner(map, new SyncLedger());

            SyncAction[] plan = planner.Plan(
                mapping,
                new LocalFileState[] { File("HELLO.SYMB", 1) },
                new RemoteFileState[] { new RemoteFileState("HELLO", "BPUN") });

            Assert.Single(plan);
            Assert.Equal(SyncActionKind.Create, plan[0].Kind);
        }

        /// <summary>
        /// An unchanged file produces nothing at all.
        /// </summary>
        [Fact]
        public void AnUnchangedFileProducesNoAction()
        {
            SyncFolderMap map = new SyncFolderMap();
            SyncFolderMapping mapping = new SyncFolderMapping(LocalFolder, "D102", "SYSTEM");
            map.Add(mapping);

            SyncLedger ledger = new SyncLedger();
            LocalFileState file = File("HELLO.SYMB", 1);
            ledger.RecordTransfer(file.Path, file.Hash, SyncDirection.ToMachine);

            SyncPlanner planner = new SyncPlanner(map, ledger);

            SyncAction[] plan = planner.Plan(
                mapping,
                new LocalFileState[] { file },
                new RemoteFileState[] { new RemoteFileState("HELLO", "SYMB") });

            Assert.Empty(plan);

            // The same path with different content is an edit again.
            SyncAction[] afterEdit = planner.Plan(
                mapping,
                new LocalFileState[] { File("HELLO.SYMB", 2) },
                new RemoteFileState[] { new RemoteFileState("HELLO", "SYMB") });

            Assert.Single(afterEdit);
            Assert.Equal(SyncActionKind.Overwrite, afterEdit[0].Kind);
        }

        /// <summary>
        /// A locally deleted file is left alone unless the mapping was told to delete.
        /// </summary>
        /// <remarks>
        /// The refusal is REPORTED rather than silent, because a person who deleted a file and
        /// expected it to go is entitled to be told why it did not.
        /// </remarks>
        [Fact]
        public void ADeletedFileIsOnlyDeletedRemotelyWhenTheSettingSaysSo()
        {
            SyncFolderMap map = new SyncFolderMap();
            SyncFolderMapping mapping = new SyncFolderMapping(LocalFolder, "D102", "SYSTEM");
            map.Add(mapping);

            SyncLedger ledger = new SyncLedger();
            LocalFileState carried = File("HELLO.SYMB", 1);
            ledger.RecordTransfer(carried.Path, carried.Hash, SyncDirection.ToMachine);

            SyncPlanner planner = new SyncPlanner(map, ledger);

            RemoteFileState[] remote = new RemoteFileState[]
            {
                new RemoteFileState("HELLO", "SYMB"),
            };

            // The file is gone locally, and the mapping's default is to leave the machine alone.
            SyncAction[] refused = planner.Plan(mapping, new LocalFileState[0], remote);

            Assert.Single(refused);
            Assert.Equal(SyncActionKind.Skip, refused[0].Kind);
            Assert.Contains("DeleteRemoteWhenLocalDeleted", refused[0].Reason);

            // Asked for, and now it happens - with a BARE name, because the file exists.
            mapping.DeleteRemoteWhenLocalDeleted = true;
            SyncAction[] allowed = planner.Plan(mapping, new LocalFileState[0], remote);

            Assert.Single(allowed);
            Assert.Equal(SyncActionKind.DeleteRemote, allowed[0].Kind);
            Assert.Equal("D102(SYSTEM).HELLO:SYMB", allowed[0].FileSpec);
        }

        /// <summary>
        /// A file gone from both sides is not a delete and not a warning.
        /// </summary>
        [Fact]
        public void AFileGoneFromBothSidesProducesNothing()
        {
            SyncFolderMap map = new SyncFolderMap();
            SyncFolderMapping mapping = new SyncFolderMapping(LocalFolder, "D102", "SYSTEM");
            mapping.DeleteRemoteWhenLocalDeleted = true;
            map.Add(mapping);

            SyncLedger ledger = new SyncLedger();
            LocalFileState carried = File("HELLO.SYMB", 1);
            ledger.RecordTransfer(carried.Path, carried.Hash, SyncDirection.ToMachine);

            SyncPlanner planner = new SyncPlanner(map, ledger);

            SyncAction[] plan = planner.Plan(
                mapping, new LocalFileState[0], new RemoteFileState[0]);

            Assert.Empty(plan);
        }

        /// <summary>
        /// A name SINTRAN cannot hold is refused, with the reason kept.
        /// </summary>
        /// <remarks>
        /// Ronny's requirement: do not transfer a file whose name or type will not fit. The plan
        /// carries the sentence so a person sees WHICH file and WHY, not just a count.
        /// </remarks>
        [Fact]
        public void ANameThatWillNotFitIsRefusedWithItsReason()
        {
            SyncFolderMap map = new SyncFolderMap();
            SyncFolderMapping mapping = new SyncFolderMapping(LocalFolder, "D102", "SYSTEM");
            map.Add(mapping);

            SyncPlanner planner = new SyncPlanner(map, new SyncLedger());

            SyncAction[] plan = planner.Plan(
                mapping,
                new LocalFileState[]
                {
                    File("PROGRAM.SYMBOL", 1),               // Five-character type.
                    File("ABCDEFGHIJKLMNOPQ.SYMB", 1),       // Seventeen-character name.
                },
                new RemoteFileState[0]);

            Assert.Equal(2, plan.Length);
            Assert.Equal(SyncActionKind.Skip, plan[0].Kind);
            Assert.Contains("4", plan[0].Reason);
            Assert.Equal(SyncActionKind.Skip, plan[1].Kind);
            Assert.Contains("16", plan[1].Reason);
        }

        /// <summary>
        /// A file in a sub-folder is refused while the mapping is top-level only.
        /// </summary>
        [Fact]
        public void ASubFolderFileIsRefusedUnlessTheMappingFlattens()
        {
            SyncFolderMap map = new SyncFolderMap();
            SyncFolderMapping mapping = new SyncFolderMapping(LocalFolder, "D102", "SYSTEM");
            map.Add(mapping);

            SyncPlanner planner = new SyncPlanner(map, new SyncLedger());

            LocalFileState deep = new LocalFileState(
                LocalFolder + "\\sub\\HELLO.SYMB", new byte[] { 1 });

            SyncAction[] refused = planner.Plan(
                mapping, new LocalFileState[] { deep }, new RemoteFileState[0]);

            Assert.Single(refused);
            Assert.Equal(SyncActionKind.Skip, refused[0].Kind);
            Assert.Contains("sub-folder", refused[0].Reason);

            mapping.Subfolders = SyncSubfolderPolicy.FlattenAll;
            SyncAction[] flattened = planner.Plan(
                mapping, new LocalFileState[] { deep }, new RemoteFileState[0]);

            Assert.Single(flattened);
            Assert.Equal(SyncActionKind.Create, flattened[0].Kind);
            Assert.Equal("D102(SYSTEM).\"HELLO:SYMB\"", flattened[0].FileSpec);
        }

        /// <summary>
        /// A pulling mapping brings back the named types and nothing else.
        /// </summary>
        [Fact]
        public void OnlyTheNamedTypesComeBack()
        {
            SyncFolderMap map = new SyncFolderMap();
            SyncFolderMapping mapping = new SyncFolderMapping(LocalFolder, "D102", "SYSTEM");
            mapping.Direction = SyncDirection.FromMachine;
            mapping.AddPullType("BPUN");
            mapping.AddPullType("list");
            map.Add(mapping);

            SyncPlanner planner = new SyncPlanner(map, new SyncLedger());

            SyncAction[] plan = planner.Plan(
                mapping,
                new LocalFileState[0],
                new RemoteFileState[]
                {
                    new RemoteFileState("HELLO", "BPUN"),
                    new RemoteFileState("HELLO", "SYMB"),   // Not asked for.
                    new RemoteFileState("HELLO", "LIST"),
                });

            Assert.Equal(2, plan.Length);
            Assert.Equal(SyncActionKind.Pull, plan[0].Kind);
            Assert.Equal(LocalFolder + "\\HELLO.BPUN", plan[0].LocalPath);
            Assert.Equal("D102(SYSTEM).HELLO:BPUN", plan[0].FileSpec);
            Assert.Equal(LocalFolder + "\\HELLO.LIST", plan[1].LocalPath);
        }

        /// <summary>
        /// A mapping that names no types brings back nothing.
        /// </summary>
        /// <remarks>
        /// This is what stops a first run from dragging a whole user directory onto the disk.
        /// </remarks>
        [Fact]
        public void AMappingWithNoTypesNamedPullsNothing()
        {
            SyncFolderMap map = new SyncFolderMap();
            SyncFolderMapping mapping = new SyncFolderMapping(LocalFolder, "D102", "SYSTEM");
            mapping.Direction = SyncDirection.FromMachine;
            map.Add(mapping);

            SyncPlanner planner = new SyncPlanner(map, new SyncLedger());

            SyncAction[] plan = planner.Plan(
                mapping,
                new LocalFileState[0],
                new RemoteFileState[]
                {
                    new RemoteFileState("HELLO", "BPUN"),
                    new RemoteFileState("HELLO", "SYMB"),
                });

            Assert.Empty(plan);
        }

        /// <summary>
        /// A pushing mapping does not pull, and a pulling mapping does not push.
        /// </summary>
        /// <remarks>
        /// A transfer has ONE direction. A folder that needs both is two mappings, and this is the
        /// check that keeps a caller from getting the return leg by accident.
        /// </remarks>
        [Fact]
        public void EachMappingCarriesOneWayOnly()
        {
            SyncFolderMap map = new SyncFolderMap();
            SyncFolderMapping pushing = new SyncFolderMapping(LocalFolder, "D102", "SYSTEM");
            pushing.AddPullType("BPUN");   // Named, but the direction is out - so it means nothing.
            map.Add(pushing);

            SyncPlanner planner = new SyncPlanner(map, new SyncLedger());

            SyncAction[] plan = planner.Plan(
                pushing,
                new LocalFileState[0],
                new RemoteFileState[] { new RemoteFileState("HELLO", "BPUN") });

            Assert.Empty(plan);
        }

        /// <summary>
        /// A file belonging to another mapping is refused rather than sent to the wrong user.
        /// </summary>
        [Fact]
        public void AFileFromAnotherMappingIsRefused()
        {
            SyncFolderMap map = new SyncFolderMap();
            SyncFolderMapping mine = new SyncFolderMapping(LocalFolder, "D102", "SYSTEM");
            SyncFolderMapping other = new SyncFolderMapping("E:\\work\\other", "D102", "GUEST");
            map.Add(mine);
            map.Add(other);

            SyncPlanner planner = new SyncPlanner(map, new SyncLedger());

            LocalFileState stray = new LocalFileState(
                "E:\\work\\other\\HELLO.SYMB", new byte[] { 1 });

            SyncAction[] plan = planner.Plan(
                mine, new LocalFileState[] { stray }, new RemoteFileState[0]);

            Assert.Single(plan);
            Assert.Equal(SyncActionKind.Skip, plan[0].Kind);
            Assert.Contains("not covered", plan[0].Reason);
        }

        /// <summary>
        /// The planner never changes the ledger.
        /// </summary>
        /// <remarks>
        /// A plan that is looked at and thrown away must leave the daemon believing nothing
        /// happened, or the next pass would skip a file it never actually sent.
        /// </remarks>
        [Fact]
        public void PlanningLeavesTheLedgerAlone()
        {
            SyncFolderMap map = new SyncFolderMap();
            SyncFolderMapping mapping = new SyncFolderMapping(LocalFolder, "D102", "SYSTEM");
            map.Add(mapping);

            SyncLedger ledger = new SyncLedger();
            SyncPlanner planner = new SyncPlanner(map, ledger);

            planner.Plan(
                mapping,
                new LocalFileState[] { File("HELLO.SYMB", 1) },
                new RemoteFileState[0]);

            Assert.Equal(0, ledger.Count);
        }

        /// <summary>
        /// Nulls are refused rather than half-planned.
        /// </summary>
        [Fact]
        public void NullsAreRefused()
        {
            SyncFolderMap map = new SyncFolderMap();
            SyncFolderMapping mapping = new SyncFolderMapping(LocalFolder, "D102", "SYSTEM");
            map.Add(mapping);

            SyncPlanner planner = new SyncPlanner(map, new SyncLedger());

            Assert.Throws<ArgumentNullException>(
                () => planner.Plan(null!, new LocalFileState[0], new RemoteFileState[0]));
            Assert.Throws<ArgumentNullException>(
                () => planner.Plan(mapping, null!, new RemoteFileState[0]));
            Assert.Throws<ArgumentNullException>(
                () => planner.Plan(mapping, new LocalFileState[0], null!));
            Assert.Throws<ArgumentNullException>(
                () => new SyncPlanner(null!, new SyncLedger()));
            Assert.Throws<ArgumentNullException>(
                () => new SyncPlanner(map, null!));
        }

        /// <summary>
        /// Builds a local file state in the mapped folder.
        /// </summary>
        /// <param name="name">
        /// The Windows file name.
        /// </param>
        /// <param name="content">
        /// A byte standing in for the file's content, so two calls with different values look like
        /// an edit.
        /// </param>
        /// <returns>
        /// The state.
        /// </returns>
        private static LocalFileState File(string name, byte content)
        {
            return new LocalFileState(LocalFolder + "\\" + name, new byte[] { content });
        }
    }
}
