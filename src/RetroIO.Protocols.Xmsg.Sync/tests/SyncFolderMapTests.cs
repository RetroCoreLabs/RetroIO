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
    /// Mapping a Windows folder onto a SINTRAN user's flat directory.
    /// </summary>
    /// <remarks>
    /// The quoting assertions here are the ones worth having: the remote form was corrected four
    /// times in one afternoon, and every correction was a live error message.
    /// </remarks>
    public sealed class SyncFolderMapTests
    {
        private static SyncFolderMap MapWith(SyncFolderMapping mapping)
        {
            SyncFolderMap map = new SyncFolderMap();
            map.Add(mapping);
            return map;
        }

        /// <summary>
        /// A new remote file is quoted, an existing one is not.
        /// </summary>
        [Fact]
        public void OnlyACreatedFileIsQuoted()
        {
            SyncFolderMap map = MapWith(new SyncFolderMapping("E:\\work\\proj", "D102", "SYSTEM"));

            string spec;
            string problem;

            Assert.True(map.TryResolve("E:\\work\\proj\\BLKT7777.DATA", true, out spec, out problem));
            Assert.Equal("D102(SYSTEM).\"BLKT7777:DATA\"", spec);

            Assert.True(map.TryResolve("E:\\work\\proj\\BLKT7777.DATA", false, out spec, out problem));
            Assert.Equal("D102(SYSTEM).BLKT7777:DATA", spec);
        }

        /// <summary>
        /// The quotes never wrap the machine or the user.
        /// </summary>
        /// <remarks>
        /// The wrong forms are asserted against explicitly, because each of them was actually
        /// produced and rejected by a machine at some point.
        /// </remarks>
        [Fact]
        public void TheQuotesWrapTheNameAndTypeOnly()
        {
            string spec = SyncFolderMap.BuildFileSpec("D102", "SYSTEM", "NEW", "DATA", true);

            Assert.Equal("D102(SYSTEM).\"NEW:DATA\"", spec);

            Assert.NotEqual("\"D102(SYSTEM).NEW:DATA\"", spec);   // whole spec quoted
            Assert.NotEqual("D102.(SYSTEM)NEW:DATA", spec);       // the LIST-FILES display form
            Assert.NotEqual("\"D102(SYSTEM)\".NEW:DATA", spec);   // machine and user quoted
        }

        /// <summary>
        /// A file in a sub-folder is refused, with a reason, rather than flattened.
        /// </summary>
        /// <remarks>
        /// The refusal is the point: a flat directory means two same-named files in different
        /// sub-folders become ONE file, and the later transfer destroys the earlier one.
        /// </remarks>
        [Fact]
        public void ASubfolderFileIsRefusedByDefault()
        {
            SyncFolderMap map = MapWith(new SyncFolderMapping("E:\\work\\proj", "D102", "SYSTEM"));

            string spec;
            string problem;
            bool ok = map.TryResolve("E:\\work\\proj\\src\\LEX.SYMB", true, out spec, out problem);

            Assert.False(ok);
            Assert.Equal(string.Empty, spec);
            Assert.Contains("sub-folder", problem);
        }

        /// <summary>
        /// Flattening is available, but only when it is asked for.
        /// </summary>
        [Fact]
        public void FlatteningIsOptIn()
        {
            SyncFolderMapping mapping = new SyncFolderMapping("E:\\work\\proj", "D102", "SYSTEM");
            mapping.Subfolders = SyncSubfolderPolicy.FlattenAll;

            string spec;
            string problem;

            Assert.True(MapWith(mapping).TryResolve(
                "E:\\work\\proj\\src\\LEX.SYMB", false, out spec, out problem));
            Assert.Equal("D102(SYSTEM).LEX:SYMB", spec);
        }

        /// <summary>
        /// A neighbouring folder with a shared prefix is NOT covered by the mapping.
        /// </summary>
        /// <remarks>
        /// Without the separator check, <c>E:\work\proj2</c> would match a mapping for
        /// <c>E:\work\proj</c> and its files would be carried to the wrong user - a silent wrong
        /// destination rather than a visible failure.
        /// </remarks>
        [Fact]
        public void AFolderWithASharedPrefixIsNotCovered()
        {
            SyncFolderMap map = MapWith(new SyncFolderMapping("E:\\work\\proj", "D102", "SYSTEM"));

            Assert.Null(map.FindMapping("E:\\work\\proj2\\X.DATA"));
            Assert.NotNull(map.FindMapping("E:\\work\\proj\\X.DATA"));
        }

        /// <summary>
        /// The longest matching folder wins, so a sub-folder can have its own user.
        /// </summary>
        [Fact]
        public void TheMostSpecificMappingWins()
        {
            SyncFolderMap map = new SyncFolderMap();
            map.Add(new SyncFolderMapping("E:\\work", "D102", "SYSTEM"));
            map.Add(new SyncFolderMapping("E:\\work\\proj", "D102", "RONNY"));

            SyncFolderMapping? found = map.FindMapping("E:\\work\\proj\\X.DATA");

            Assert.NotNull(found);
            Assert.Equal("RONNY", found!.User);
        }

        /// <summary>
        /// Nothing is pulled back until somebody names the types.
        /// </summary>
        /// <remarks>
        /// A user directory holds far more than one project's build output. Pulling everything by
        /// default would drop files into a Windows folder that nobody asked for.
        /// </remarks>
        [Fact]
        public void AMappingPullsNothingUntilTypesAreNamed()
        {
            SyncFolderMapping mapping = new SyncFolderMapping("E:\\work\\proj", "D102", "SYSTEM");

            Assert.Empty(mapping.PullTypes);
            Assert.False(mapping.PullsType("BPUN"));

            mapping.AddPullType("bpun");
            mapping.AddPullType("SYMB");
            mapping.AddPullType("BPUN");          // duplicate, ignored

            Assert.Equal(2, mapping.PullTypes.Count);
            Assert.True(mapping.PullsType("BPUN"));
            Assert.True(mapping.PullsType(":bpun"));   // as the remote may report it
            Assert.False(mapping.PullsType("LIST"));
        }

        /// <summary>
        /// Deleting on the far end is off unless it is switched on.
        /// </summary>
        /// <remarks>
        /// A default of true turns any bad local state - a failed checkout, an unmounted folder, a
        /// wrong mapping - into the destruction of the only copy. Nothing on the SINTRAN side is
        /// under version control.
        /// </remarks>
        [Fact]
        public void RemoteDeleteIsOffByDefault()
        {
            SyncFolderMapping mapping = new SyncFolderMapping("E:\\work\\proj", "D102", "SYSTEM");

            Assert.False(mapping.DeleteRemoteWhenLocalDeleted);
            Assert.Equal(SyncDirection.ToMachine, mapping.Direction);
            Assert.Equal(SyncSubfolderPolicy.TopLevelOnly, mapping.Subfolders);
        }

        /// <summary>
        /// A name SINTRAN cannot hold is refused with the reason, not silently truncated.
        /// </summary>
        [Fact]
        public void AnImpossibleNameIsRefusedWithItsReason()
        {
            SyncFolderMap map = MapWith(new SyncFolderMapping("E:\\work\\proj", "D102", "SYSTEM"));

            string spec;
            string problem;
            bool ok = map.TryResolve(
                "E:\\work\\proj\\PROGRAM-WITH-A-VERY-LONG-NAME.SYMB", true, out spec, out problem);

            Assert.False(ok);
            Assert.NotEqual(string.Empty, problem);
        }

        /// <summary>
        /// A path no mapping covers is refused rather than guessed at.
        /// </summary>
        [Fact]
        public void AnUnmappedPathIsRefused()
        {
            SyncFolderMap map = MapWith(new SyncFolderMapping("E:\\work\\proj", "D102", "SYSTEM"));

            string spec;
            string problem;

            Assert.False(map.TryResolve("E:\\elsewhere\\X.DATA", true, out spec, out problem));
            Assert.Contains("No mapping", problem);
        }

        /// <summary>
        /// Bad arguments are refused at construction.
        /// </summary>
        [Fact]
        public void BadMappingArgumentsAreRefused()
        {
            Assert.Throws<ArgumentNullException>(
                () => new SyncFolderMapping(null!, "D102", "SYSTEM"));
            Assert.Throws<ArgumentException>(
                () => new SyncFolderMapping(string.Empty, "D102", "SYSTEM"));
            Assert.Throws<ArgumentException>(
                () => new SyncFolderMapping("E:\\work", "D102", string.Empty));

            SyncFolderMapping mapping = new SyncFolderMapping("E:\\work", "D102", "SYSTEM");
            Assert.Throws<ArgumentException>(() => mapping.AddPullType("TOOLONG"));
        }

        /// <summary>
        /// A trailing separator on the mapped folder makes no difference.
        /// </summary>
        [Fact]
        public void ATrailingSeparatorIsIgnored()
        {
            SyncFolderMap map = MapWith(new SyncFolderMapping("E:\\work\\proj\\", "D102", "SYSTEM"));

            string spec;
            string problem;

            Assert.True(map.TryResolve("E:\\work\\proj\\X.DATA", false, out spec, out problem));
            Assert.Equal("D102(SYSTEM).X:DATA", spec);
        }
    }
}
