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
    /// The ledger that stops unchanged files being resent and stops the two directions fighting.
    /// </summary>
    public sealed class SyncLedgerTests
    {
        private static byte[] Hash(params byte[] bytes)
        {
            return bytes;
        }

        /// <summary>
        /// A path never seen before needs transferring.
        /// </summary>
        [Fact]
        public void AnUnknownPathNeedsTransfer()
        {
            SyncLedger ledger = new SyncLedger();

            Assert.True(ledger.NeedsTransfer("a.symb", Hash(1, 2, 3)));
        }

        /// <summary>
        /// Saving a file without editing it costs nothing.
        /// </summary>
        /// <remarks>
        /// Ctrl-S on an untouched buffer is a normal habit, and in an edit-compile loop it would
        /// otherwise mean a transfer and a rebuild for no reason.
        /// </remarks>
        [Fact]
        public void IdenticalContentIsNotResent()
        {
            SyncLedger ledger = new SyncLedger();
            ledger.RecordTransfer("a.symb", Hash(1, 2, 3), SyncDirection.ToMachine);

            Assert.False(ledger.NeedsTransfer("a.symb", Hash(1, 2, 3)));
        }

        /// <summary>
        /// A real edit is transferred.
        /// </summary>
        [Fact]
        public void ChangedContentIsSent()
        {
            SyncLedger ledger = new SyncLedger();
            ledger.RecordTransfer("a.symb", Hash(1, 2, 3), SyncDirection.ToMachine);

            Assert.True(ledger.NeedsTransfer("a.symb", Hash(1, 2, 4)));
        }

        /// <summary>
        /// The file we just pushed does not get pushed again when it comes back.
        /// </summary>
        /// <remarks>
        /// <para>
        /// THE loop bug. We push a source file; the return leg sees it on the machine and pulls it;
        /// the pull looks like a local change; we push it again. Left alone that never stops, and
        /// it saturates the link with a file nobody edited.
        /// </para>
        /// <para>
        /// The guard is content, not direction - see SyncLedger's remarks for why direction alone
        /// is the wrong test.
        /// </para>
        /// </remarks>
        [Fact]
        public void AFileComingBackUnchangedDoesNotStartALoop()
        {
            SyncLedger ledger = new SyncLedger();
            byte[] content = Hash(9, 9, 9);

            // We push it out...
            ledger.RecordTransfer("a.symb", content, SyncDirection.ToMachine);

            // ...and the return leg offers the very same bytes back.
            Assert.False(ledger.NeedsTransfer("a.symb", content));
        }

        /// <summary>
        /// A file the machine really did change IS brought back, even though we pushed it.
        /// </summary>
        /// <remarks>
        /// The case that rules out suppressing by direction: a compiler rewrites a file we also
        /// edit. Ignoring inbound changes for anything we pushed would lose exactly the output the
        /// whole loop exists to deliver.
        /// </remarks>
        [Fact]
        public void ContentTheMachineChangedIsStillBroughtBack()
        {
            SyncLedger ledger = new SyncLedger();
            ledger.RecordTransfer("a.symb", Hash(1, 1, 1), SyncDirection.ToMachine);

            Assert.True(ledger.NeedsTransfer("a.symb", Hash(2, 2, 2)));
        }

        /// <summary>
        /// Hashes of different lengths are different content.
        /// </summary>
        [Fact]
        public void DifferentLengthHashesDoNotMatch()
        {
            SyncLedger ledger = new SyncLedger();
            ledger.RecordTransfer("a.symb", Hash(1, 2, 3), SyncDirection.ToMachine);

            Assert.True(ledger.NeedsTransfer("a.symb", Hash(1, 2)));
            Assert.True(ledger.NeedsTransfer("a.symb", Hash(1, 2, 3, 4)));
        }

        /// <summary>
        /// The ledger copies the hash, so a caller reusing its buffer cannot corrupt it.
        /// </summary>
        /// <remarks>
        /// A hashing loop that reuses one scratch array is the obvious way to avoid allocating per
        /// file. If the ledger kept the reference, every entry would end up pointing at the same
        /// array and comparisons would silently stop meaning anything.
        /// </remarks>
        [Fact]
        public void TheLedgerDoesNotKeepTheCallersBuffer()
        {
            SyncLedger ledger = new SyncLedger();
            byte[] scratch = new byte[] { 1, 2, 3 };

            ledger.RecordTransfer("a.symb", scratch, SyncDirection.ToMachine);

            // The caller reuses its buffer for the next file.
            scratch[0] = 99;

            Assert.False(ledger.NeedsTransfer("a.symb", new byte[] { 1, 2, 3 }));
            Assert.True(ledger.NeedsTransfer("a.symb", new byte[] { 99, 2, 3 }));
        }

        /// <summary>
        /// The recorded direction is readable, for reporting.
        /// </summary>
        [Fact]
        public void TheDirectionIsRemembered()
        {
            SyncLedger ledger = new SyncLedger();
            ledger.RecordTransfer("out.symb", Hash(1), SyncDirection.ToMachine);
            ledger.RecordTransfer("in.list", Hash(2), SyncDirection.FromMachine);

            SyncDirection direction;

            Assert.True(ledger.TryGetDirection("out.symb", out direction));
            Assert.Equal(SyncDirection.ToMachine, direction);

            Assert.True(ledger.TryGetDirection("in.list", out direction));
            Assert.Equal(SyncDirection.FromMachine, direction);

            Assert.False(ledger.TryGetDirection("never-seen", out direction));
            Assert.Equal(SyncDirection.None, direction);
        }

        /// <summary>
        /// Forgetting a path makes the next transfer look new.
        /// </summary>
        [Fact]
        public void ForgettingAPathClearsIt()
        {
            SyncLedger ledger = new SyncLedger();
            ledger.RecordTransfer("a.symb", Hash(1, 2, 3), SyncDirection.ToMachine);

            Assert.True(ledger.Forget("a.symb"));
            Assert.True(ledger.NeedsTransfer("a.symb", Hash(1, 2, 3)));
            Assert.False(ledger.Forget("a.symb"));
        }

        /// <summary>
        /// Paths are matched without regard to case, as on Windows.
        /// </summary>
        [Fact]
        public void PathsAreMatchedWithoutRegardToCase()
        {
            SyncLedger ledger = new SyncLedger();
            ledger.RecordTransfer(@"src\Main.SYMB", Hash(1, 2, 3), SyncDirection.ToMachine);

            Assert.False(ledger.NeedsTransfer(@"SRC\MAIN.symb", Hash(1, 2, 3)));
            Assert.Equal(1, ledger.Count);
        }

        /// <summary>
        /// Nulls are rejected rather than recorded.
        /// </summary>
        [Fact]
        public void NullsAreRejected()
        {
            SyncLedger ledger = new SyncLedger();

            Assert.Throws<ArgumentNullException>(() => ledger.NeedsTransfer(null!, Hash(1)));
            Assert.Throws<ArgumentNullException>(() => ledger.NeedsTransfer("a", null!));
            Assert.Throws<ArgumentNullException>(
                () => ledger.RecordTransfer(null!, Hash(1), SyncDirection.ToMachine));
            Assert.Throws<ArgumentNullException>(
                () => ledger.RecordTransfer("a", null!, SyncDirection.ToMachine));
        }
    }
}
