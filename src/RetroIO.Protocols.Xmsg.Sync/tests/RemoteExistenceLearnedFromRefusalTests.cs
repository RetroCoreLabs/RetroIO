using System;
using RetroIO.Protocols.Xmsg.Sync;
using Xunit;

namespace RetroIO.Protocols.Xmsg.Sync.Tests
{
    /// <summary>
    /// Pins how the daemon learns that a file is already on the far machine.
    /// </summary>
    /// <remarks>
    /// <para><b>The hole these close</b></para>
    /// The planner must choose between CREATE and OVERWRITE, and SINTRAN refuses a create of a file
    /// that already exists. Its only evidence used to be "did WE carry this", which is never true
    /// for a file somebody else made or one that outlived a deleted ledger - so it chose create and
    /// the machine refused it, once a pass, for ever.
    /// <para><b>Measured, not invented</b></para>
    /// The refusal is SINTRAN error 62 = 076 octal, "File already exists", read off D100 on
    /// 2026-08-18.
    /// </remarks>
    public sealed class RemoteExistenceLearnedFromRefusalTests
    {
        /// <summary>
        /// A path nobody has mentioned is not assumed to be on the machine.
        /// </summary>
        /// <remarks>
        /// The control. Without it every test below would pass against a ledger that simply says
        /// yes to everything.
        /// </remarks>
        [Fact]
        public void AnUnknownPathIsNotBelievedToExist()
        {
            SyncLedger ledger = new SyncLedger();

            Assert.False(ledger.KnownToExistRemotely(@"C:\watch\THING.TXT"));
        }

        /// <summary>
        /// A refusal teaches the ledger that the file is there.
        /// </summary>
        [Fact]
        public void ARefusalTeachesTheLedgerTheFileIsThere()
        {
            SyncLedger ledger = new SyncLedger();

            ledger.RecordRemoteExistence(@"C:\watch\THING.TXT");

            Assert.True(ledger.KnownToExistRemotely(@"C:\watch\THING.TXT"));
        }

        /// <summary>
        /// Learning that a file exists does NOT mark it as carried.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The distinction the whole fix rests on. Recording existence by writing an entry into the
        /// ledger proper - the obvious shortcut - would tell <c>NeedsTransfer</c> the file is
        /// already up to date, and the file would never be sent. That is the same silent drop this
        /// change exists to remove, reintroduced by the cure.
        /// </para>
        /// <para>
        /// So: we know it is over there. We know nothing about what is in it.
        /// </para>
        /// </remarks>
        [Fact]
        public void ExistenceIsNotTheSameAsHavingBeenCarried()
        {
            SyncLedger ledger = new SyncLedger();

            ledger.RecordRemoteExistence(@"C:\watch\THING.TXT");

            Assert.False(ledger.HasCarried(@"C:\watch\THING.TXT"));

            // And the content question is still open, so the file is still due to be sent.
            byte[] hash = new byte[] { 1, 2, 3, 4 };
            Assert.True(ledger.NeedsTransfer(@"C:\watch\THING.TXT", hash));
        }

        /// <summary>
        /// A file we carried ourselves is known to exist without any refusal.
        /// </summary>
        /// <remarks>
        /// The old behaviour has to keep working: a recorded transfer is still evidence that the
        /// file is on the machine.
        /// </remarks>
        [Fact]
        public void ACarriedFileIsStillKnownToExist()
        {
            SyncLedger ledger = new SyncLedger();
            byte[] hash = new byte[] { 9, 9, 9, 9 };

            ledger.RecordTransfer(@"C:\watch\THING.TXT", hash, SyncDirection.ToMachine);

            Assert.True(ledger.KnownToExistRemotely(@"C:\watch\THING.TXT"));
        }

        /// <summary>
        /// A null path is refused rather than silently ignored.
        /// </summary>
        [Fact]
        public void ANullPathIsRefused()
        {
            SyncLedger ledger = new SyncLedger();

            Assert.Throws<ArgumentNullException>(
                delegate () { ledger.RecordRemoteExistence(null!); });
            Assert.Throws<ArgumentNullException>(
                delegate () { ledger.KnownToExistRemotely(null!); });
        }

        /// <summary>
        /// A refusal carries its SINTRAN number, not just its words.
        /// </summary>
        /// <remarks>
        /// The runner decides on the number. Deciding on <see cref="SyncTransferResult.Reason"/>
        /// would work until somebody reworded it, and then fail quietly.
        /// </remarks>
        [Fact]
        public void ARefusedResultCarriesTheNumber()
        {
            SyncTransferResult refused = SyncTransferResult.Refused("File already exists", 62);

            Assert.False(refused.Succeeded);
            Assert.Equal(62, refused.SintranError);

            // A failure with no number from the machine reads as zero, which is "no number" and
            // must not be mistaken for success.
            SyncTransferResult plain = SyncTransferResult.Failed("the link went away");
            Assert.False(plain.Succeeded);
            Assert.Equal(0, plain.SintranError);
        }
    }
}
