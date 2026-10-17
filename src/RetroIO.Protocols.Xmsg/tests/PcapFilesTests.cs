//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Checks the policy that decides what happens when the recorded <c>.pcapng</c> files are not on
    /// the machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a small test guarding a rule that is easy to get backwards, and getting it backwards is
    /// expensive: for a long time every one of the seventeen test files that read those recordings
    /// printed "skipping" and PASSED when they were absent. They are the tests that check our decoder
    /// and our frame builder against bytes taken off real ND-100s, so the whole set could report green
    /// while checking nothing.
    /// </para>
    /// <para>
    /// The rule is now: missing means fail, unless somebody deliberately sets
    /// <c>XMSG_PCAP_OPTIONAL</c>. These tests pin both directions, because a policy that only ever
    /// gets exercised one way is the same trap again.
    /// </para>
    /// </remarks>
    public sealed class PcapFilesTests
    {
        /// <summary>
        /// A found path is returned unchanged, whether or not the opt-out is set.
        /// </summary>
        /// <param name="optional">
        /// The opt-out flag, which must make no difference when the item was found.
        /// </param>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void WhenTheItemIsFound_ItIsReturnedUnchanged(bool optional)
        {
            string found = @"E:\somewhere\pcap";

            Assert.Equal(found, PcapFiles.Apply(found, optional, "anything"));
        }

        /// <summary>
        /// A missing item throws when the opt-out is not set. This is the whole point of the change.
        /// </summary>
        [Fact]
        public void WhenTheItemIsMissingAndNotOptedOut_ItThrows()
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => PcapFiles.Apply(null, false, "the folder holding the recorded .pcapng files"));

            // The message has to tell whoever hits this how to proceed, or they will just delete the
            // test. Both escape routes must be named.
            Assert.Contains("the folder holding the recorded .pcapng files", error.Message);
            Assert.Contains(PcapFiles.DirectoryVariable, error.Message);
            Assert.Contains(PcapFiles.OptionalVariable, error.Message);
        }

        /// <summary>
        /// A missing item returns null when the opt-out is set, so a test can return early.
        /// </summary>
        [Fact]
        public void WhenTheItemIsMissingAndOptedOut_ItReturnsNull()
        {
            Assert.Null(PcapFiles.Apply(null, true, "anything"));
        }

        /// <summary>
        /// An explicitly named folder that does not exist is an error rather than a silent fallback.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Found while trying to test the policy: pointing <c>XMSG_PCAP_DIR</c> at a nonexistent path
        /// used to be ignored, because the search quietly went on to walk up the tree and found the
        /// real folder. A mistyped path therefore looked like it worked, which would send someone
        /// hunting the wrong problem.
        /// </para>
        /// <para>
        /// <b>This test used to set the environment variable and restore it afterwards. That was a
        /// bug.</b> xUnit runs test classes in parallel, so during that window any other test reading
        /// the variable saw a folder that does not exist and threw. It caused exactly one
        /// intermittent failure across the suite before it was caught. The check is now a pure
        /// function and no process-wide state is touched.
        /// </para>
        /// </remarks>
        [Fact]
        public void WhenTheNamedFolderDoesNotExist_ItThrowsRatherThanFallingBack()
        {
            string missing = @"E:\this-path-does-not-exist-" + nameof(PcapFilesTests);

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => PcapFiles.CheckNamedFolder(missing, false));

            Assert.Contains(missing, error.Message);
            Assert.Contains("does not exist", error.Message);
        }

        /// <summary>
        /// A named folder that does exist, and the case where none was named, both pass silently.
        /// </summary>
        [Fact]
        public void WhenTheNamedFolderExistsOrIsUnset_ItDoesNotThrow()
        {
            PcapFiles.CheckNamedFolder(@"E:\somewhere", true);
            PcapFiles.CheckNamedFolder(null, false);
            PcapFiles.CheckNamedFolder(string.Empty, false);
        }

        /// <summary>
        /// An empty file name is rejected outright.
        /// </summary>
        [Fact]
        public void AnEmptyFileName_IsRejected()
        {
            Assert.Throws<ArgumentException>(() => PcapFiles.File(string.Empty));
        }
    }
}
