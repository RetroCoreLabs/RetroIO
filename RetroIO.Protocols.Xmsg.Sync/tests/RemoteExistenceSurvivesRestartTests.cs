using System;
using System.IO;
using RetroIO.Protocols.Xmsg.Sync;
using Xunit;

namespace RetroIO.Protocols.Xmsg.Sync.Tests
{
    /// <summary>
    /// Pins that "the machine already has this file" survives a daemon restart.
    /// </summary>
    /// <remarks>
    /// <para><b>What it costs when it does not</b></para>
    /// The planner chooses CREATE when nothing knows the file is on the machine, and SINTRAN refuses
    /// a create of a file that already exists. The daemon learns that fact from the refusal - once
    /// per file. Held only in memory, every restart pays that refusal again for every file it has
    /// not carried itself.
    /// <para><b>And the older format still has to load</b></para>
    /// The new lines carry a non-numeric first field so a reader that predates them skips them, and
    /// a ledger written by that older reader must still load here. Both directions are checked.
    /// </remarks>
    public sealed class RemoteExistenceSurvivesRestartTests : IDisposable
    {
        private readonly string _path;

        /// <summary>
        /// Creates the fixture, with a ledger file of its own.
        /// </summary>
        public RemoteExistenceSurvivesRestartTests()
        {
            _path = Path.Combine(
                Path.GetTempPath(),
                "xmsg-ledger-test-" + Guid.NewGuid().ToString("N") + ".state");
        }

        /// <summary>
        /// Removes the ledger file.
        /// </summary>
        public void Dispose()
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }

        /// <summary>
        /// A learned existence survives save and load.
        /// </summary>
        [Fact]
        public void ExistenceLearnedFromARefusalSurvivesARestart()
        {
            SyncLedger before = new SyncLedger();
            before.RecordRemoteExistence(@"C:\watch\THING.TXT");

            SyncLedgerFile.Save(before, _path);
            SyncLedger after = SyncLedgerFile.Load(_path);

            Assert.True(after.KnownToExistRemotely(@"C:\watch\THING.TXT"));

            // Still not a transfer: the content question stays open, so the file is still due to
            // be sent. Recording it as carried would drop it for good.
            Assert.False(after.HasCarried(@"C:\watch\THING.TXT"));
            Assert.True(after.NeedsTransfer(@"C:\watch\THING.TXT", new byte[] { 1, 2, 3 }));
        }

        /// <summary>
        /// A carried file is written once, not twice.
        /// </summary>
        /// <remarks>
        /// A transfer entry already implies the file is over there. Writing an existence line for
        /// it as well would be a second copy of the same fact that could disagree with itself.
        /// </remarks>
        [Fact]
        public void ACarriedFileIsNotAlsoWrittenAsExistenceOnly()
        {
            SyncLedger before = new SyncLedger();
            before.RecordTransfer(@"C:\watch\A.TXT", new byte[] { 9 }, SyncDirection.ToMachine);
            before.RecordRemoteExistence(@"C:\watch\A.TXT");
            before.RecordRemoteExistence(@"C:\watch\B.TXT");

            SyncLedgerFile.Save(before, _path);

            string[] lines = File.ReadAllLines(_path);
            int existenceLines = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("X|", StringComparison.Ordinal))
                {
                    existenceLines++;
                }
            }

            // B only. A is covered by its transfer entry.
            Assert.Equal(1, existenceLines);

            SyncLedger after = SyncLedgerFile.Load(_path);
            Assert.True(after.KnownToExistRemotely(@"C:\watch\A.TXT"));
            Assert.True(after.KnownToExistRemotely(@"C:\watch\B.TXT"));
        }

        /// <summary>
        /// A ledger with no existence lines still loads.
        /// </summary>
        /// <remarks>
        /// The format that existed before this change. Written by hand rather than by the current
        /// writer, so the test cannot pass by both sides agreeing on something new.
        /// </remarks>
        [Fact]
        public void ALedgerWrittenBeforeThisChangeStillLoads()
        {
            File.WriteAllLines(_path, new string[]
            {
                "0|0102|C:\\watch\\OLD.TXT",
            });

            SyncLedger loaded = SyncLedgerFile.Load(_path);

            Assert.True(loaded.HasCarried(@"C:\watch\OLD.TXT"));
            Assert.True(loaded.KnownToExistRemotely(@"C:\watch\OLD.TXT"));
        }

        /// <summary>
        /// A path containing a bar still round-trips.
        /// </summary>
        /// <remarks>
        /// The path is written LAST precisely so it needs no escaping. Worth a test, because the
        /// existence line has the same shape and would break the same way.
        /// </remarks>
        [Fact]
        public void APathContainingTheSeparatorRoundTrips()
        {
            string awkward = @"C:\watch\odd|name.TXT";

            SyncLedger before = new SyncLedger();
            before.RecordRemoteExistence(awkward);

            SyncLedgerFile.Save(before, _path);
            SyncLedger after = SyncLedgerFile.Load(_path);

            Assert.True(after.KnownToExistRemotely(awkward));
        }
    }
}
