//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Regression tests for the V7 partition-offset bug.
    ///
    /// THE BUG: BsdV7FileSystem applied the partition offset by hand at only some call sites.
    /// V7InodeReader and ReadSuperblock added it; the data-block reads behind ReadFile did not.
    /// So a partition at a non-zero offset mounted, reported a plausible superblock, and then read
    /// its file and directory data from the wrong place. On the BSD 2.11 clean_install image,
    /// partition 'c' (/usr, offset 32604 sectors) listed 0 root entries even though its on-disk
    /// root holds 28 subdirectories. Partition 'a' was unaffected because its offset is 0.
    /// V7BlockMapper.PartitionStartBlock is now the single place the offset is applied.
    ///
    /// WHY THESE TESTS LOOK THE WAY THEY DO: asserting "partition c has entries" is NOT sufficient.
    /// With the offset dropped, partition 'c' reads partition 'a's filesystem instead — which has
    /// plenty of entries, so such a test passes while the driver is returning the wrong volume
    /// entirely. Verified by mutation: setting PartitionStartBlock to 0 left the whole suite green.
    /// These tests therefore assert that 'a' and 'c' are DISTINCT filesystems with their own
    /// identifying content, which is the only thing that actually pins the offset down.
    /// </summary>
    [TestFixture]
    public class V7PartitionOffsetRegressionTests
    {
        private const string CleanInstallImage = @"E:\Dev\Ronny\BSD\Boot\1.FromTape\backup\clean_install";

        [Test]
        [Category("V7")]
        [Category("Regression")]
        public void PartitionC_MountsItsOwnFilesystem_NotPartitionA()
        {
            if (!File.Exists(CleanInstallImage))
            {
                Assert.Ignore($"Test image not found: {CleanInstallImage}");
                return;
            }

            string labelA;
            string labelC;

            using (var driverA = new V7Driver())
            {
                driverA.SetPartition('a');
                using var streamA = new FileStream(CleanInstallImage, FileMode.Open, FileAccess.Read, FileShare.Read);
                driverA.Mount(streamA, readOnly: true);
                labelA = driverA.VolumeLabel;
            }

            using (var driverC = new V7Driver())
            {
                driverC.SetPartition('c');
                using var streamC = new FileStream(CleanInstallImage, FileMode.Open, FileAccess.Read, FileShare.Read);
                driverC.Mount(streamC, readOnly: true);
                labelC = driverC.VolumeLabel;
            }

            TestContext.Out.WriteLine($"Partition a label: '{labelA}'");
            TestContext.Out.WriteLine($"Partition c label: '{labelC}'");

            // The superblock label is stored inside each partition, so it is the cheapest proof
            // that we read the right one. Dropping the offset makes both report '/'.
            Assert.That(labelA, Is.EqualTo("/"), "Partition 'a' is the root filesystem");
            Assert.That(labelC, Is.EqualTo("/usr"), "Partition 'c' is /usr - not a second view of 'a'");
            Assert.That(labelC, Is.Not.EqualTo(labelA),
                "The two partitions must be distinct filesystems; identical labels mean the " +
                "partition offset was not applied and 'c' is reading 'a'");
        }

        [Test]
        [Category("V7")]
        [Category("Regression")]
        public void PartitionC_RootDirectory_ContainsUsrSpecificEntries()
        {
            if (!File.Exists(CleanInstallImage))
            {
                Assert.Ignore($"Test image not found: {CleanInstallImage}");
                return;
            }

            using var driver = new V7Driver();
            driver.SetPartition('c');

            using var stream = new FileStream(CleanInstallImage, FileMode.Open, FileAccess.Read, FileShare.Read);
            driver.Mount(stream, readOnly: true);

            DirectoryEntry[] entries = driver.ListDirectory("/");

            TestContext.Out.WriteLine($"Partition c root: {entries.Length} entries");
            for (int i = 0; i < entries.Length; i++)
            {
                TestContext.Out.WriteLine($"  {entries[i].Name}");
            }

            Assert.That(entries.Length, Is.GreaterThan(0),
                "Partition 'c' root must not be empty - this is the original symptom");

            // Directories that exist in /usr but NOT in the root filesystem. Reading partition 'a'
            // by mistake would satisfy a bare count check but cannot produce these.
            Assert.That(HasEntry(entries, "src"), Is.True, "/usr must contain 'src'");
            Assert.That(HasEntry(entries, "include"), Is.True, "/usr must contain 'include'");
            Assert.That(HasEntry(entries, "man"), Is.True, "/usr must contain 'man'");
            Assert.That(HasEntry(entries, "spool"), Is.True, "/usr must contain 'spool'");

            // 'dev' and 'etc' live in the ROOT filesystem, not in /usr. Their presence would mean
            // we are looking at partition 'a'.
            Assert.That(HasEntry(entries, "dev"), Is.False,
                "'dev' belongs to the root filesystem; seeing it here means partition 'a' was read");
        }

        [Test]
        [Category("V7")]
        [Category("Regression")]
        public void PartitionA_RootDirectory_ContainsRootSpecificEntries()
        {
            if (!File.Exists(CleanInstallImage))
            {
                Assert.Ignore($"Test image not found: {CleanInstallImage}");
                return;
            }

            using var driver = new V7Driver();
            driver.SetPartition('a');

            using var stream = new FileStream(CleanInstallImage, FileMode.Open, FileAccess.Read, FileShare.Read);
            driver.Mount(stream, readOnly: true);

            DirectoryEntry[] entries = driver.ListDirectory("/");

            Assert.That(entries.Length, Is.GreaterThan(0), "Root filesystem must list entries");

            // The offset-0 partition must keep working exactly as before the fix.
            Assert.That(HasEntry(entries, "etc"), Is.True, "Root filesystem must contain 'etc'");
            Assert.That(HasEntry(entries, "dev"), Is.True, "Root filesystem must contain 'dev'");
            Assert.That(HasEntry(entries, "bin"), Is.True, "Root filesystem must contain 'bin'");
        }

        [Test]
        [Category("V7")]
        [Category("Regression")]
        public void PartitionC_DeletedEntry_DoesNotTruncateListing()
        {
            if (!File.Exists(CleanInstallImage))
            {
                Assert.Ignore($"Test image not found: {CleanInstallImage}");
                return;
            }

            // /usr's root holds an UNUSED slot: 'lost+found' carries d_ino == 0. A reader that
            // treats ino 0 as end-of-directory would stop after '.' and '..' and return nothing.
            // The 28 real subdirectories all sit AFTER that slot, so a full listing proves the
            // unused entry is skipped rather than treated as a terminator.
            using var driver = new V7Driver();
            driver.SetPartition('c');

            using var stream = new FileStream(CleanInstallImage, FileMode.Open, FileAccess.Read, FileShare.Read);
            driver.Mount(stream, readOnly: true);

            DirectoryEntry[] entries = driver.ListDirectory("/");

            Assert.That(entries.Length, Is.GreaterThanOrEqualTo(20),
                "Entries after the unused 'lost+found' slot must still be listed");

            // 'src' is the very LAST entry in the directory block, so finding it proves the whole
            // block was walked past the unused slot.
            Assert.That(HasEntry(entries, "src"), Is.True,
                "The final entry in the block must be reached");
        }

        /// <summary>
        /// Case-sensitive lookup - V7 is a case-sensitive filesystem.
        /// </summary>
        private static bool HasEntry(DirectoryEntry[] entries, string name)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (string.Equals(entries[i].Name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
