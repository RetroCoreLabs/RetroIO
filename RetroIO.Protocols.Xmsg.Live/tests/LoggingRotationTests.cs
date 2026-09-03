using System;
using System.IO;

using RetroIO.Protocols.Xmsg.Live.Logging;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Live.Tests
{
    /// <summary>
    /// Proves the Linux-syslog-style log rotation: numbered shifting with oldest-deleted, a fresh empty
    /// file on startup (a restart immediately rotates), and a size-triggered roll-over while running.
    /// </summary>
    public sealed class LoggingRotationTests : IDisposable
    {
        // A private temp directory per test-class instance so the file operations never touch real logs.
        private readonly string _dir;
        private readonly string _log;

        /// <summary>
        /// Creates the isolated temp directory used by every test in this class.
        /// </summary>
        public LoggingRotationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "xmsg-log-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _log = Path.Combine(_dir, "runner.log");
        }

        /// <summary>
        /// Removes the temp directory and all rotated files after each test.
        /// </summary>
        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a still-open handle must not fail the test run.
            }
        }

        /// <summary>
        /// Rotation shifts the live file to .1, .1 to .2, and deletes the version beyond the keep count.
        /// </summary>
        [Fact]
        public void Rotate_ShiftsNumbersAndDropsOldest()
        {
            // Seed a live file plus two existing archives; keep = 2 (so .3 must never appear).
            File.WriteAllText(_log, "live");
            File.WriteAllText(_log + ".1", "one");
            File.WriteAllText(_log + ".2", "two");

            LogRotator.Rotate(_log, 2);

            // Live is gone (caller reopens it); .1 = former live, .2 = former .1; former .2 deleted.
            Assert.False(File.Exists(_log));
            Assert.Equal("live", File.ReadAllText(_log + ".1"));
            Assert.Equal("one", File.ReadAllText(_log + ".2"));
            Assert.False(File.Exists(_log + ".3"));
        }

        /// <summary>
        /// With keep = 0 no archive is created; the live file is simply removed.
        /// </summary>
        [Fact]
        public void Rotate_KeepZero_DeletesLiveNoArchive()
        {
            File.WriteAllText(_log, "live");

            LogRotator.Rotate(_log, 0);

            Assert.False(File.Exists(_log));
            Assert.False(File.Exists(_log + ".1"));
        }

        /// <summary>
        /// Opening the writer rotates any existing log out to .1 and starts a fresh empty live file
        /// (a restart immediately renames and starts empty).
        /// </summary>
        [Fact]
        public void Ctor_RotatesExistingAndStartsEmpty()
        {
            File.WriteAllText(_log, "previous run content");

            using (RotatingFileWriter writer = new RotatingFileWriter(_log, 1024 * 1024, 3))
            {
                // The old content moved to .1; the live file exists and is empty.
                Assert.Equal("previous run content", File.ReadAllText(_log + ".1"));
                Assert.True(File.Exists(_log));
                writer.Flush();
                Assert.Equal(0, new FileInfo(_log).Length);
            }
        }

        /// <summary>
        /// Writing past the size limit rolls the live file over: the crossing content lands in .1 and the
        /// live file resumes from empty.
        /// </summary>
        [Fact]
        public void Write_PastLimit_RollsOver()
        {
            // Tiny 64-byte limit so a couple of lines trip it.
            using (RotatingFileWriter writer = new RotatingFileWriter(_log, 64, 3))
            {
                string line = new string('x', 50) + "\n"; // 51 bytes, flushes on the newline
                writer.Write(line);                        // 51 bytes: under the limit, no roll yet
                Assert.False(File.Exists(_log + ".1"));
                writer.Write(line);                        // 102 bytes total: crosses 64, rolls over
                writer.Flush();

                // The first two lines were archived to .1; the live file is fresh (empty until more writes).
                Assert.True(File.Exists(_log + ".1"));
                Assert.Equal(0, new FileInfo(_log).Length);
            }
        }
    }
}
