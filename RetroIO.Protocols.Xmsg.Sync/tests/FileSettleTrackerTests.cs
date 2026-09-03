using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Sync;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Sync.Tests
{
    /// <summary>
    /// The rule that decides when a file has stopped being written.
    /// </summary>
    /// <remarks>
    /// Time is a number these tests pass in, so they never sleep and cannot flake. That is the
    /// reason <see cref="FileSettleTracker"/> takes the clock as an argument rather than reading
    /// one - a settle rule tested with real delays is either slow or unreliable, usually both.
    /// </remarks>
    public sealed class FileSettleTrackerTests
    {
        private const long Quiet = 100;

        /// <summary>
        /// A file still growing is never handed over, however many times it is observed.
        /// </summary>
        /// <remarks>
        /// This is the case that matters most. An editor writes in chunks, and transferring a
        /// half-written source file is worse than transferring nothing: half a file still
        /// compiles, so you get an error listing for a bug that does not exist.
        /// </remarks>
        [Fact]
        public void AFileStillBeingWrittenIsNeverSettled()
        {
            FileSettleTracker tracker = new FileSettleTracker(Quiet);

            // Growing steadily, each observation well inside the quiet period.
            tracker.Observe("a.symb", 100, 1, 0);
            Assert.Empty(tracker.TakeSettled(50));

            tracker.Observe("a.symb", 200, 2, 60);
            Assert.Empty(tracker.TakeSettled(120));

            tracker.Observe("a.symb", 300, 3, 130);
            Assert.Empty(tracker.TakeSettled(200));

            // Only once it stops does it settle.
            Assert.Equal(new string[] { "a.symb" }, tracker.TakeSettled(230));
        }

        /// <summary>
        /// A file holding still for the quiet period is handed over exactly once.
        /// </summary>
        [Fact]
        public void ASettledFileIsReturnedOnceAndThenForgotten()
        {
            FileSettleTracker tracker = new FileSettleTracker(Quiet);
            tracker.Observe("a.symb", 100, 1, 0);

            Assert.Equal(new string[] { "a.symb" }, tracker.TakeSettled(Quiet));

            // Taken means taken - it must not come back on the next sweep, or every settled file
            // would be transferred over and over.
            Assert.Empty(tracker.TakeSettled(Quiet + 1000));
            Assert.Equal(0, tracker.PendingCount);
        }

        /// <summary>
        /// The boundary is inclusive: exactly the quiet period counts as settled.
        /// </summary>
        [Fact]
        public void TheQuietPeriodBoundaryCounts()
        {
            FileSettleTracker tracker = new FileSettleTracker(Quiet);
            tracker.Observe("a.symb", 1, 1, 0);

            Assert.Empty(tracker.TakeSettled(Quiet - 1));
            Assert.Single(tracker.TakeSettled(Quiet));
        }

        /// <summary>
        /// Re-observing a file that has NOT changed does not restart its quiet period.
        /// </summary>
        /// <remarks>
        /// A watcher can fire repeatedly for an untouched file, and a rescan re-observes
        /// everything by design. If either restarted the clock, a file could sit unsent for as
        /// long as the daemon kept looking at it - the failure would look like "the sync just
        /// stops sometimes".
        /// </remarks>
        [Fact]
        public void RepeatedIdenticalObservationsDoNotDelaySettling()
        {
            FileSettleTracker tracker = new FileSettleTracker(Quiet);
            tracker.Observe("a.symb", 100, 7, 0);

            for (int at = 10; at <= 90; at += 10)
            {
                tracker.Observe("a.symb", 100, 7, at);
            }

            Assert.Equal(new string[] { "a.symb" }, tracker.TakeSettled(Quiet));
        }

        /// <summary>
        /// A last-write change with no size change still restarts the quiet period.
        /// </summary>
        /// <remarks>
        /// Editing a byte in place, or saving a file whose length happens not to change, moves the
        /// stamp and not the size. Watching size alone would call that file settled while it was
        /// still being written.
        /// </remarks>
        [Fact]
        public void ATouchThatDoesNotChangeSizeStillRestartsTheWait()
        {
            FileSettleTracker tracker = new FileSettleTracker(Quiet);
            tracker.Observe("a.symb", 100, 1, 0);

            tracker.Observe("a.symb", 100, 2, 90);

            Assert.Empty(tracker.TakeSettled(Quiet));
            Assert.Equal(new string[] { "a.symb" }, tracker.TakeSettled(190));
        }

        /// <summary>
        /// Files settle independently of each other.
        /// </summary>
        [Fact]
        public void FilesAreTrackedSeparately()
        {
            FileSettleTracker tracker = new FileSettleTracker(Quiet);
            tracker.Observe("quiet.symb", 10, 1, 0);
            tracker.Observe("busy.symb", 10, 1, 0);
            tracker.Observe("busy.symb", 20, 2, 80);

            IReadOnlyList<string> settled = tracker.TakeSettled(Quiet);

            Assert.Equal(new string[] { "quiet.symb" }, settled);
            Assert.Equal(1, tracker.PendingCount);
        }

        /// <summary>
        /// A file deleted while it was settling is dropped rather than transferred.
        /// </summary>
        /// <remarks>
        /// Without this the tracker would report a path that no longer exists, and the transfer
        /// would fail on a file the user deliberately removed.
        /// </remarks>
        [Fact]
        public void AForgottenFileIsNotHandedOver()
        {
            FileSettleTracker tracker = new FileSettleTracker(Quiet);
            tracker.Observe("gone.symb", 10, 1, 0);

            Assert.True(tracker.Forget("gone.symb"));
            Assert.Empty(tracker.TakeSettled(Quiet));
            Assert.False(tracker.Forget("gone.symb"));
        }

        /// <summary>
        /// Paths are matched case-insensitively, because Windows paths are.
        /// </summary>
        /// <remarks>
        /// A watcher event and a rescan can report the same file with different casing. Treating
        /// those as two files would transfer it twice and settle neither on time.
        /// </remarks>
        [Fact]
        public void PathsAreMatchedWithoutRegardToCase()
        {
            FileSettleTracker tracker = new FileSettleTracker(Quiet);
            tracker.Observe(@"src\Main.SYMB", 10, 1, 0);
            tracker.Observe(@"SRC\MAIN.symb", 20, 2, 50);

            Assert.Equal(1, tracker.PendingCount);
            Assert.Empty(tracker.TakeSettled(Quiet));
            Assert.Single(tracker.TakeSettled(150));
        }

        /// <summary>
        /// A quiet period must be positive - zero would settle a file the instant it was seen.
        /// </summary>
        [Fact]
        public void AQuietPeriodOfZeroIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new FileSettleTracker(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FileSettleTracker(-1));
        }

        /// <summary>
        /// A null path is rejected rather than silently tracked.
        /// </summary>
        [Fact]
        public void NullPathsAreRejected()
        {
            FileSettleTracker tracker = new FileSettleTracker(Quiet);

            Assert.Throws<ArgumentNullException>(() => tracker.Observe(null!, 1, 1, 0));
            Assert.Throws<ArgumentNullException>(() => tracker.Forget(null!));
        }

        /// <summary>
        /// A settled file that is observed again, unchanged, is NOT presented as being written.
        /// </summary>
        /// <remarks>
        /// MEASURED 2026-08-11 and it is why the settled entry is kept rather than removed. A
        /// scanning caller re-observes every file in the folder every few seconds; when a settled
        /// entry had been removed, the next observation created a brand new one with a fresh clock,
        /// so the daemon reported "1 file(s) still being written" for ever about a file it had
        /// already carried.
        /// </remarks>
        [Fact]
        public void ASettledFileObservedAgainUnchangedIsNotBeingWritten()
        {
            FileSettleTracker tracker = new FileSettleTracker(Quiet);
            tracker.Observe("a.symb", 100, 1, 0);

            Assert.Single(tracker.TakeSettled(Quiet));
            Assert.False(tracker.IsStillBeingWritten("a.symb"));

            // The scanner comes round again and sees the same untouched file.
            tracker.Observe("a.symb", 100, 1, Quiet + 500);

            Assert.False(tracker.IsStillBeingWritten("a.symb"));
            Assert.Equal(0, tracker.PendingCount);
            Assert.Empty(tracker.TakeSettled(Quiet + 5000));
        }

        /// <summary>
        /// A settled file that is really edited settles again, and is reported again.
        /// </summary>
        /// <remarks>
        /// The other half: marking a file settled must not mean it is never carried again. An edit
        /// puts it back in play.
        /// </remarks>
        [Fact]
        public void AnEditedFileSettlesAgain()
        {
            FileSettleTracker tracker = new FileSettleTracker(Quiet);
            tracker.Observe("a.symb", 100, 1, 0);
            Assert.Single(tracker.TakeSettled(Quiet));

            // Edited: different size and stamp.
            tracker.Observe("a.symb", 250, 2, Quiet + 100);

            Assert.True(tracker.IsStillBeingWritten("a.symb"));
            Assert.Equal(1, tracker.PendingCount);
            Assert.Equal(new string[] { "a.symb" }, tracker.TakeSettled(Quiet + 100 + Quiet));
        }

        /// <summary>
        /// A file the tracker has never seen is not "being written".
        /// </summary>
        [Fact]
        public void AnUnknownFileIsNotBeingWritten()
        {
            FileSettleTracker tracker = new FileSettleTracker(Quiet);

            Assert.False(tracker.IsStillBeingWritten("never-seen.symb"));
        }
    }
}
