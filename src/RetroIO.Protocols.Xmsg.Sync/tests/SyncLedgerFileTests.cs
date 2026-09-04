using System;
using System.IO;

using RetroIO.Protocols.Xmsg.Sync;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Sync.Tests
{
    /// <summary>
    /// Writing the ledger down, so a restart does not offer the whole folder again.
    /// </summary>
    /// <remarks>
    /// Held only in memory, the ledger answers "have we carried this content" with NO for
    /// everything after every restart. Against a machine that already has those files each one is
    /// a CREATE of a name that exists, which SINTRAN refuses - a burst of failures that mean
    /// nothing.
    /// </remarks>
    public sealed class SyncLedgerFileTests
    {
        private static string TempPath()
        {
            return Path.Combine(Path.GetTempPath(), "xmsg-ledger-" + Guid.NewGuid().ToString("N") + ".state");
        }

        /// <summary>
        /// What was carried is still known after a save and load.
        /// </summary>
        [Fact]
        public void ACarriedFileIsStillKnownAfterReloading()
        {
            string path = TempPath();
            try
            {
                byte[] hash = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

                SyncLedger saved = new SyncLedger();
                saved.RecordTransfer("c:\\work\\a.symb", hash, SyncDirection.ToMachine);
                SyncLedgerFile.Save(saved, path);

                SyncLedger loaded = SyncLedgerFile.Load(path);

                Assert.False(loaded.NeedsTransfer("c:\\work\\a.symb", hash));
            }
            finally
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
        }

        /// <summary>
        /// Different content for a known path still needs carrying after a reload.
        /// </summary>
        /// <remarks>
        /// The ledger promises something about CONTENT, not about the path having been seen. A
        /// saved form that forgot the hash would answer "already carried" for an edited file and
        /// the edit would never leave.
        /// </remarks>
        [Fact]
        public void EditedContentStillNeedsCarryingAfterReloading()
        {
            string path = TempPath();
            try
            {
                SyncLedger saved = new SyncLedger();
                saved.RecordTransfer("c:\\work\\a.symb", new byte[] { 1, 1 }, SyncDirection.ToMachine);
                SyncLedgerFile.Save(saved, path);

                SyncLedger loaded = SyncLedgerFile.Load(path);

                Assert.True(loaded.NeedsTransfer("c:\\work\\a.symb", new byte[] { 2, 2 }));
            }
            finally
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
        }

        /// <summary>
        /// The direction survives, so a pulled file is not pushed straight back.
        /// </summary>
        [Fact]
        public void TheDirectionSurvivesReloading()
        {
            string path = TempPath();
            try
            {
                SyncLedger saved = new SyncLedger();
                saved.RecordTransfer("c:\\work\\out.lst", new byte[] { 7 }, SyncDirection.FromMachine);
                SyncLedgerFile.Save(saved, path);

                SyncLedger loaded = SyncLedgerFile.Load(path);

                SyncDirection direction;
                Assert.True(loaded.TryGetDirection("c:\\work\\out.lst", out direction));
                Assert.Equal(SyncDirection.FromMachine, direction);
            }
            finally
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
        }

        /// <summary>
        /// A path containing the separator survives, because the path is written last.
        /// </summary>
        /// <remarks>
        /// A Windows path can hold almost anything. Putting it last means nothing after it needs
        /// escaping, and this is the test that keeps that true.
        /// </remarks>
        [Fact]
        public void APathContainingTheSeparatorSurvives()
        {
            string path = TempPath();
            try
            {
                string awkward = "c:\\work\\odd|name.symb";

                SyncLedger saved = new SyncLedger();
                saved.RecordTransfer(awkward, new byte[] { 5 }, SyncDirection.ToMachine);
                SyncLedgerFile.Save(saved, path);

                SyncLedger loaded = SyncLedgerFile.Load(path);

                Assert.False(loaded.NeedsTransfer(awkward, new byte[] { 5 }));
            }
            finally
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
        }

        /// <summary>
        /// A missing file is an empty ledger, not a crash - that is the first run.
        /// </summary>
        [Fact]
        public void AMissingFileLoadsAsEmpty()
        {
            SyncLedger loaded = SyncLedgerFile.Load(TempPath());

            Assert.Equal(0, loaded.Count);
        }

        /// <summary>
        /// A garbled line is skipped and the rest of the ledger still loads.
        /// </summary>
        /// <remarks>
        /// A half-written file must not stop the daemon starting. The cost of skipping a line is
        /// that its file gets offered once more, which is the safe direction to fail in.
        /// </remarks>
        [Fact]
        public void AGarbledLineIsSkippedAndTheRestSurvives()
        {
            string path = TempPath();
            try
            {
                File.WriteAllLines(path, new string[]
                {
                    "1|DEAD|c:\\work\\good.symb",
                    "this line is nonsense",
                    "1|NOTHEX|c:\\work\\bad.symb",
                });

                SyncLedger loaded = SyncLedgerFile.Load(path);

                Assert.Equal(1, loaded.Count);
                Assert.False(loaded.NeedsTransfer("c:\\work\\good.symb", new byte[] { 0xDE, 0xAD }));
            }
            finally
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
        }
    }
}
