//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.BSD.Partitions;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.Sun.Partitions;
using RetroIO.Storage.Local.V7.FileSystems;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Comprehensive directory traversal tests for Sun, BSD, and FFS/UFS disk images.
    /// Verifies that all filesystem types can be fully traversed without errors.
    ///
    /// NOT [Explicit]: these run in the normal suite. Every test checks File.Exists on its image
    /// and calls Assert.Ignore when it is absent, so a machine without the images reports skips
    /// rather than failures, while a machine that HAS them gets real coverage.
    ///
    /// This matters because these walks are the only tests that exercise whole real filesystems
    /// end to end. While the fixture was [Explicit] it never ran, and it silently stopped being a
    /// regression guard: two genuine read bugs (the FFS tail-fragment overrun and the V7
    /// PDP-endian indirect-pointer decode) sat here undetected because nothing executed them.
    /// Synthetic equivalents now live in RetroIO.Tests/FfsRegressionTests.cs, but these
    /// full-image walks are what caught them first.
    /// </summary>
    [TestFixture]
    public class DirectoryTraversalTests
    {
        // Test image paths
        private const string BsdCleanInstall = @"E:\Dev\Ronny\BSD\Boot\1.FromTape\backup\clean_install";
        private const string Bsd211Image = @"D:\211bsd.img";
        private const string SunS20DiskImage = @"D:\sun2-s2.0-disk.img";
        private const string SunS32DiskImage = @"D:\ND\HDD\sun2-sunos-3.2.img";
        private const string SunS35DiskImage = @"D:\ND\HDD\SUN2.img";

        /// <summary>
        /// Recursively traverses all directories in a filesystem and counts total files/directories.
        /// </summary>
        private TraversalStats TraverseAllDirectories(IFileSystemDriver driver, string path = "/", int depth = 0, int maxDepth = 50)
        {
            var stats = new TraversalStats();

            if (depth > maxDepth)
            {
                TestContext.Out.WriteLine($"{"".PadLeft(depth * 2)}⚠️ Max depth {maxDepth} reached at: {path}");
                return stats;
            }

            try
            {
                DirectoryEntry[] entries = driver.ListDirectory(path);
                stats.DirsVisited++;

                string indent = "".PadLeft(depth * 2);
                TestContext.Out.WriteLine($"{indent}📁 {path} ({entries.Length} entries)");

                for (int i = 0; i < entries.Length; i++)
                {
                    var entry = entries[i];

                    // Skip . and ..
                    if (entry.Name == "." || entry.Name == "..")
                        continue;

                    string childPath = path == "/" ? "/" + entry.Name : path + "/" + entry.Name;

                    // Check if this is a device file (has IsDevice property)
                    bool isDevice = entry.Metadata.GetProperty<bool>("IsDevice");
                    bool isCharDevice = entry.Metadata.GetProperty<bool>("IsCharDevice");
                    bool isBlockDevice = entry.Metadata.GetProperty<bool>("IsBlockDevice");

                    if (isDevice || isCharDevice || isBlockDevice)
                    {
                        // Skip device files - they don't have regular data blocks
                        string deviceType = isCharDevice ? "char" : (isBlockDevice ? "block" : "device");
                        string indent2 = "".PadLeft(depth * 2);
                        TestContext.Out.WriteLine($"{indent2}  [DEVICE:{deviceType}] {entry.Name}");
                        continue;
                    }

                    if (entry.Metadata.IsDirectory)
                    {
                        stats.DirsFound++;

                        // Recursively traverse subdirectory
                        var childStats = TraverseAllDirectories(driver, childPath, depth + 1, maxDepth);
                        stats.Merge(childStats);
                    }
                    else
                    {
                        stats.FilesFound++;
                        stats.TotalBytes += entry.Metadata.Size;
                    }
                }
            }
            catch (Exception ex)
            {
                stats.ErrorsEncountered++;
                TestContext.Out.WriteLine($"{"".PadLeft(depth * 2)}❌ Error at {path}: {ex.Message}");
            }

            return stats;
        }

        [Test]
        [Description("Traverses all directories in BSD clean_install image - partition 'a' (root)")]
        public void BsdCleanInstall_PartitionA_TraverseAllDirectories()
        {
            // Arrange
            if (!File.Exists(BsdCleanInstall))
            {
                Assert.Ignore($"Test image not found: {BsdCleanInstall}");
                return;
            }

            TestContext.Out.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            TestContext.Out.WriteLine("║  BSD 2.11 Clean Install - Full Directory Traversal       ║");
            TestContext.Out.WriteLine("║  Partition 'a' (root filesystem)                          ║");
            TestContext.Out.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            TestContext.Out.WriteLine("");

            var driver = new V7Driver();
            driver.SetPartition('a');

            using var stream = new FileStream(BsdCleanInstall, FileMode.Open, FileAccess.Read, FileShare.Read);
            driver.Mount(stream, readOnly: true);

            // Act: Traverse all directories
            var stats = TraverseAllDirectories(driver);

            // Assert
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            TestContext.Out.WriteLine("║  TRAVERSAL STATISTICS                                     ║");
            TestContext.Out.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            stats.PrintSummary(TestContext.Out);

            Assert.That(stats.DirsVisited, Is.GreaterThan(0), "Should visit at least root directory");
            Assert.That(stats.ErrorsEncountered, Is.EqualTo(0), "Should traverse without errors");
            Assert.That(stats.FilesFound, Is.GreaterThan(0), "Should find at least some files");

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("✓ Full traversal completed successfully!");
        }

        [Test]
        [Description("Traverses all directories in BSD clean_install image - partition 'c' (usr)")]
        public void BsdCleanInstall_PartitionC_TraverseAllDirectories()
        {
            // Arrange
            if (!File.Exists(BsdCleanInstall))
            {
                Assert.Ignore($"Test image not found: {BsdCleanInstall}");
                return;
            }

            TestContext.Out.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            TestContext.Out.WriteLine("║  BSD 2.11 Clean Install - Full Directory Traversal       ║");
            TestContext.Out.WriteLine("║  Partition 'c' (whole disk / usr)                         ║");
            TestContext.Out.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            TestContext.Out.WriteLine("");

            var driver = new V7Driver();
            driver.SetPartition('c');

            using var stream = new FileStream(BsdCleanInstall, FileMode.Open, FileAccess.Read, FileShare.Read);
            driver.Mount(stream, readOnly: true);

            // Act: Traverse all directories
            var stats = TraverseAllDirectories(driver);

            // Assert
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            TestContext.Out.WriteLine("║  TRAVERSAL STATISTICS                                     ║");
            TestContext.Out.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            stats.PrintSummary(TestContext.Out);

            Assert.That(stats.DirsVisited, Is.GreaterThan(0), "Should visit at least root directory");
            Assert.That(stats.ErrorsEncountered, Is.EqualTo(0), "Should traverse without errors");

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("✓ Full traversal completed successfully!");
        }

        [Test]
        [Description("Traverses all directories in BSD 211bsd.img - all partitions")]
        public void Bsd211_AllPartitions_TraverseAllDirectories()
        {
            // Arrange
            if (!File.Exists(Bsd211Image))
            {
                Assert.Ignore($"Test image not found: {Bsd211Image}");
                return;
            }

            TestContext.Out.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            TestContext.Out.WriteLine("║  BSD 2.11 - Full Directory Traversal (All Partitions)    ║");
            TestContext.Out.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            TestContext.Out.WriteLine("");

            // First, detect all partitions
            var descriptor = new BlockDeviceDescriptor
            {
                Name = "BSD 2.11",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(new FileInfo(Bsd211Image).Length / 512)
            };

            var device = new GenericBlockDevice(descriptor);
            using var previewStream = new FileStream(Bsd211Image, FileMode.Open, FileAccess.Read, FileShare.Read);
            device.Mount(previewStream, readOnly: true);

            var partMgr = new BsdPartitionManager(device);
            var partitions = partMgr.ListPartitions();

            TestContext.Out.WriteLine($"Found {partitions.Count} partitions:");
            for (int i = 0; i < partitions.Count; i++)
            {
                var (letter, part) = partitions[i];
                TestContext.Out.WriteLine($"  [{letter}] {part.FileSystemTypeName} - {part.Size * 512.0 / (1024.0 * 1024.0):F1} MB");
            }
            TestContext.Out.WriteLine("");

            int totalPartitionsTested = 0;
            var overallStats = new TraversalStats();

            // Traverse each V7 filesystem partition
            for (int i = 0; i < partitions.Count; i++)
            {
                var (letter, part) = partitions[i];

                if (!part.IsV7Filesystem)
                {
                    TestContext.Out.WriteLine($"Skipping [{letter}] - {part.FileSystemTypeName}");
                    continue;
                }

                TestContext.Out.WriteLine("");
                TestContext.Out.WriteLine($"═══ PARTITION [{letter}] - {part.FileSystemTypeName} ═══");
                TestContext.Out.WriteLine("");

                var driver = new V7Driver();
                driver.SetPartition(letter);

                using var stream = new FileStream(Bsd211Image, FileMode.Open, FileAccess.Read, FileShare.Read);
                driver.Mount(stream, readOnly: true);

                // Traverse this partition
                var stats = TraverseAllDirectories(driver);
                overallStats.Merge(stats);

                TestContext.Out.WriteLine("");
                stats.PrintSummary(TestContext.Out);

                Assert.That(stats.DirsVisited, Is.GreaterThan(0),
                    $"Partition [{letter}] should visit at least root directory");
                Assert.That(stats.ErrorsEncountered, Is.EqualTo(0),
                    $"Partition [{letter}] should traverse without errors");

                totalPartitionsTested++;
            }

            // Assert
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            TestContext.Out.WriteLine("║  OVERALL STATISTICS (ALL PARTITIONS)                      ║");
            TestContext.Out.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            TestContext.Out.WriteLine($"Partitions tested: {totalPartitionsTested}");
            overallStats.PrintSummary(TestContext.Out);

            Assert.That(totalPartitionsTested, Is.GreaterThan(0),
                "Should test at least one filesystem partition");
            Assert.That(overallStats.ErrorsEncountered, Is.EqualTo(0),
                "Should traverse all partitions without errors");

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("✓ All partitions traversed successfully!");
        }

        [Test]
        [Description("Traverses all directories in Sun 2.0 disk image - all partitions")]
        public void SunS20_AllPartitions_TraverseAllDirectories()
        {
            // Arrange
            if (!File.Exists(SunS20DiskImage))
            {
                Assert.Ignore($"Test image not found: {SunS20DiskImage}");
                return;
            }

            TestContext.Out.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            TestContext.Out.WriteLine("║  Sun 2.0 - Full Directory Traversal (All Partitions)     ║");
            TestContext.Out.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            TestContext.Out.WriteLine("");

            // Detect Sun partitions
            var descriptor = new BlockDeviceDescriptor
            {
                Name = "Sun 2.0",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(new FileInfo(SunS20DiskImage).Length / 512)
            };

            var device = new GenericBlockDevice(descriptor);
            using var previewStream = new FileStream(SunS20DiskImage, FileMode.Open, FileAccess.Read, FileShare.Read);
            device.Mount(previewStream, readOnly: true);

            var partMgr = new SunPartitionManager(device);
            var partitions = partMgr.ListPartitions();

            TestContext.Out.WriteLine($"Found {partitions.Count} partitions:");
            for (int i = 0; i < partitions.Count; i++)
            {
                var (letter, part) = partitions[i];
                TestContext.Out.WriteLine($"  [{letter}] {part.GetDisplayName(letter)} - {part.NumSectors * 512.0 / (1024.0 * 1024.0):F1} MB");
            }
            TestContext.Out.WriteLine("");

            int totalPartitionsTested = 0;
            var overallStats = new TraversalStats();
            // Partitions that couldn't be mounted with either driver — previously this was only
            // logged and silently `continue`d, so a totally-unmountable partition contributed
            // nothing to overallStats and the final assertion never saw it: "TraverseAllDirectories"
            // could report zero errors even if every single partition failed to mount. Tracked
            // explicitly now so that case is a real test failure, not silent.
            var mountFailures = new List<string>();

            // Try to mount each partition
            for (int i = 0; i < partitions.Count; i++)
            {
                var (letter, part) = partitions[i];

                // Skip partitions that are too small or likely swap/boot
                if (part.NumSectors < 1000)
                {
                    TestContext.Out.WriteLine($"Skipping [{letter}] - too small ({part.NumSectors} sectors)");
                    continue;
                }

                TestContext.Out.WriteLine("");
                TestContext.Out.WriteLine($"═══ PARTITION [{letter}] - {part.GetDisplayName(letter)} ═══");
                TestContext.Out.WriteLine("");

                try
                {
                    IFileSystemDriver? driver = null;
                    bool mountSuccess = false;

                    // Try FFS driver first
                    try
                    {
                        var ffsDriver = new FfsDriver();
                        ffsDriver.SetPartition(letter);

                        using var stream = new FileStream(SunS20DiskImage, FileMode.Open, FileAccess.Read, FileShare.Read);
                        ffsDriver.Mount(stream, readOnly: true);

                        driver = ffsDriver;
                        mountSuccess = true;
                        TestContext.Out.WriteLine("Mounted with FFS driver");
                    }
                    catch (Exception ex)
                    {
                        TestContext.Out.WriteLine($"FFS mount failed: {ex.Message}");
                        TestContext.Out.WriteLine("Trying V7 driver...");

                        // Try V7 driver as fallback
                        try
                        {
                            var v7driver = new V7Driver();
                            v7driver.SetPartition(letter);

                            using var v7stream = new FileStream(SunS20DiskImage, FileMode.Open, FileAccess.Read, FileShare.Read);
                            v7driver.Mount(v7stream, readOnly: true);

                            driver = v7driver;
                            mountSuccess = true;
                            TestContext.Out.WriteLine("Mounted with V7 driver");
                        }
                        catch (Exception v7ex)
                        {
                            TestContext.Out.WriteLine($"V7 mount also failed: {v7ex.Message}");
                        }
                    }

                    if (!mountSuccess || driver == null)
                    {
                        TestContext.Out.WriteLine($"⚠️ Could not mount partition [{letter}] with any driver");
                        mountFailures.Add(letter.ToString());
                        continue;
                    }

                    // Traverse this partition
                    var stats = TraverseAllDirectories(driver);
                    overallStats.Merge(stats);

                    TestContext.Out.WriteLine("");
                    stats.PrintSummary(TestContext.Out);

                    Assert.That(stats.DirsVisited, Is.GreaterThan(0),
                        $"Partition [{letter}] should visit at least root directory");
                    Assert.That(stats.ErrorsEncountered, Is.EqualTo(0),
                        $"Partition [{letter}] should traverse without errors");

                    totalPartitionsTested++;
                }
                catch (Exception ex)
                {
                    TestContext.Out.WriteLine($"❌ Error processing partition [{letter}]: {ex.Message}");
                    mountFailures.Add(letter.ToString());
                }
            }

            // Assert
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            TestContext.Out.WriteLine("║  OVERALL STATISTICS (ALL PARTITIONS)                      ║");
            TestContext.Out.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            TestContext.Out.WriteLine($"Partitions tested: {totalPartitionsTested}");
            TestContext.Out.WriteLine($"Partitions that failed to mount: {mountFailures.Count} [{string.Join(", ", mountFailures)}]");
            overallStats.PrintSummary(TestContext.Out);

            Assert.That(mountFailures, Is.Empty,
                $"Partition(s) [{string.Join(", ", mountFailures)}] could not be mounted with FFS or V7 — " +
                "a partition that can't be mounted can't be traversed, so this must fail the test, not be silently skipped.");

            if (totalPartitionsTested > 0)
            {
                Assert.That(overallStats.ErrorsEncountered, Is.EqualTo(0),
                    "Should traverse all mounted partitions without errors");
                TestContext.Out.WriteLine("");
                TestContext.Out.WriteLine("✓ All mounted partitions traversed successfully!");
            }
            else
            {
                Assert.Warn("No partitions could be mounted and traversed");
            }
        }

        [Test]
        [Description("Traverses all directories in Sun 3.2 disk image - all partitions")]
        public void SunS32_AllPartitions_TraverseAllDirectories()
        {
            // Arrange
            if (!File.Exists(SunS32DiskImage))
            {
                Assert.Ignore($"Test image not found: {SunS32DiskImage}");
                return;
            }

            TestContext.Out.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            TestContext.Out.WriteLine("║  Sun 3.2 - Full Directory Traversal (All Partitions)     ║");
            TestContext.Out.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            TestContext.Out.WriteLine("");

            // Detect Sun partitions
            var descriptor = new BlockDeviceDescriptor
            {
                Name = "Sun 3.2",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(new FileInfo(SunS32DiskImage).Length / 512)
            };

            var device = new GenericBlockDevice(descriptor);
            using var previewStream = new FileStream(SunS32DiskImage, FileMode.Open, FileAccess.Read, FileShare.Read);
            device.Mount(previewStream, readOnly: true);

            var partMgr = new SunPartitionManager(device);
            var partitions = partMgr.ListPartitions();

            TestContext.Out.WriteLine($"Found {partitions.Count} partitions:");
            for (int i = 0; i < partitions.Count; i++)
            {
                var (letter, part) = partitions[i];
                TestContext.Out.WriteLine($"  [{letter}] {part.GetDisplayName(letter)} - {part.NumSectors * 512.0 / (1024.0 * 1024.0):F1} MB");
            }
            TestContext.Out.WriteLine("");

            // Act: Traverse all FFS/V7 partitions
            var overallStats = new TraversalStats();
            int totalPartitionsTested = 0;
            // See SunS20_AllPartitions_TraverseAllDirectories for why this is tracked explicitly.
            // This method had a second, worse copy of the same bug: totalPartitionsTested was
            // incremented UNCONDITIONALLY (outside the mountSuccess branch below), so a partition
            // that failed to mount was still counted as "tested" — the summary line looked
            // legitimate even if every partition failed to mount, since overallStats itself was
            // never merged for a failed partition and so never picked up an error either.
            var mountFailures = new List<string>();

            for (int i = 0; i < partitions.Count; i++)
            {
                var (letter, part) = partitions[i];

                // Skip partitions that are too small
                if (part.NumSectors < 1000)
                {
                    TestContext.Out.WriteLine($"Skipping partition '{letter}' (too small: {part.NumSectors} sectors)");
                    TestContext.Out.WriteLine("");
                    continue;
                }

                // Swap carries no filesystem, so failing to mount it is the correct outcome,
                // not a defect. Counting it as a mount failure made this test permanently red
                // on every real Sun pack.
                if (part.IsSwap(letter))
                {
                    TestContext.Out.WriteLine($"Skipping partition '{letter}' (swap - no filesystem)");
                    TestContext.Out.WriteLine("");
                    continue;
                }
                TestContext.Out.WriteLine($"═══════════════════════════════════════════════════════════");
                TestContext.Out.WriteLine($"Testing partition '{letter}'");
                TestContext.Out.WriteLine($"═══════════════════════════════════════════════════════════");

                IFileSystemDriver? driver = null;
                bool mountSuccess = false;

                // Try FFS driver first
                try
                {
                    var ffsDriver = new FfsDriver();
                    ffsDriver.SetPartition(letter);
                    ffsDriver.SetImagePath(SunS32DiskImage);  // Enable quirk detection

                    using var stream = new FileStream(SunS32DiskImage, FileMode.Open, FileAccess.Read, FileShare.Read);
                    ffsDriver.Mount(stream, readOnly: true);

                    driver = ffsDriver;
                    mountSuccess = true;
                    TestContext.Out.WriteLine($"✓ Mounted partition '{letter}' as FFS");
                }
                catch (Exception ffsEx)
                {
                    TestContext.Out.WriteLine($"✗ FFS mount failed: {ffsEx.Message}");

                    // Try V7 as fallback
                    try
                    {
                        var v7driver = new V7Driver();
                        v7driver.SetPartition(letter);

                        using var v7stream = new FileStream(SunS32DiskImage, FileMode.Open, FileAccess.Read, FileShare.Read);
                        v7driver.Mount(v7stream, readOnly: true);

                        driver = v7driver;
                        mountSuccess = true;
                        TestContext.Out.WriteLine($"✓ Mounted partition '{letter}' as V7 (fallback)");
                    }
                    catch (Exception v7ex)
                    {
                        TestContext.Out.WriteLine($"✗ V7 mount also failed: {v7ex.Message}");
                    }
                }

                if (mountSuccess && driver != null)
                {
                    var stats = TraverseAllDirectories(driver);
                    overallStats.Merge(stats);

                    TestContext.Out.WriteLine("");
                    TestContext.Out.WriteLine($"─── Partition '{letter}' Statistics ───");
                    stats.PrintSummary(TestContext.Out);
                    TestContext.Out.WriteLine("");

                    driver.Dispose();
                    totalPartitionsTested++;
                }
                else
                {
                    TestContext.Out.WriteLine($"⚠️ Could not mount partition '{letter}'");
                    TestContext.Out.WriteLine("");
                    mountFailures.Add(letter.ToString());
                }
            }

            // Assert
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            TestContext.Out.WriteLine("║  OVERALL STATISTICS (ALL PARTITIONS)                      ║");
            TestContext.Out.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            TestContext.Out.WriteLine($"Partitions tested: {totalPartitionsTested}");
            TestContext.Out.WriteLine($"Partitions that failed to mount: {mountFailures.Count} [{string.Join(", ", mountFailures)}]");
            overallStats.PrintSummary(TestContext.Out);

            Assert.That(mountFailures, Is.Empty,
                $"Partition(s) [{string.Join(", ", mountFailures)}] could not be mounted with FFS or V7 — " +
                "a partition that can't be mounted can't be traversed, so this must fail the test, not be silently skipped.");
            Assert.That(totalPartitionsTested, Is.GreaterThan(0),
                "Should test at least one filesystem partition");
            Assert.That(overallStats.ErrorsEncountered, Is.EqualTo(0),
                "Should traverse all partitions without errors");

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("✓ All partitions traversed successfully!");

            if (totalPartitionsTested == 0)
            {
                Assert.Warn("No partitions could be mounted and traversed");
            }
        }

        [Test]
        [Description("Traverses all directories in Sun S3.5 disk image - all partitions")]
        public void SunS35_AllPartitions_TraverseAllDirectories()
        {
            // Arrange
            if (!File.Exists(SunS35DiskImage))
            {
                Assert.Ignore($"Test image not found: {SunS35DiskImage}");
                return;
            }

            TestContext.Out.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            TestContext.Out.WriteLine("║  Sun S3.5 Disk - Full Directory Traversal (All Parts)    ║");
            TestContext.Out.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            TestContext.Out.WriteLine("");

            // Detect Sun partitions
            var descriptor = new BlockDeviceDescriptor
            {
                Name = "Sun S3.5 Disk",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(new FileInfo(SunS35DiskImage).Length / 512)
            };

            var device = new GenericBlockDevice(descriptor);
            using var previewStream = new FileStream(SunS35DiskImage, FileMode.Open, FileAccess.Read, FileShare.Read);
            device.Mount(previewStream, readOnly: true);

            var partMgr = new SunPartitionManager(device);
            var partitions = partMgr.ListPartitions();

            TestContext.Out.WriteLine($"Found {partitions.Count} partitions:");
            for (int i = 0; i < partitions.Count; i++)
            {
                var (letter, part) = partitions[i];
                TestContext.Out.WriteLine($"  [{letter}] {part.GetDisplayName(letter)} - {part.NumSectors * 512.0 / (1024.0 * 1024.0):F1} MB");
            }
            TestContext.Out.WriteLine("");

            int totalPartitionsTested = 0;
            var overallStats = new TraversalStats();
            // See SunS20_AllPartitions_TraverseAllDirectories for why this is tracked explicitly:
            // without it, a partition that fails to mount with either driver silently vanishes
            // from the statistics instead of failing the test.
            var mountFailures = new List<string>();

            for (int i = 0; i < partitions.Count; i++)
            {
                var (letter, part) = partitions[i];

                // Skip partitions that are too small
                if (part.NumSectors < 1000)
                {
                    TestContext.Out.WriteLine($"Skipping [{letter}] - too small ({part.NumSectors} sectors)");
                    continue;
                }

                TestContext.Out.WriteLine("");
                TestContext.Out.WriteLine($"═══ PARTITION [{letter}] - {part.GetDisplayName(letter)} ═══");
                TestContext.Out.WriteLine("");

                try
                {
                    IFileSystemDriver? driver = null;
                    bool mountSuccess = false;

                    // Try FFS driver first
                    try
                    {
                        var ffsDriver = new FfsDriver();
                        ffsDriver.SetPartition(letter);

                        using var stream = new FileStream(SunS35DiskImage, FileMode.Open, FileAccess.Read, FileShare.Read);
                        ffsDriver.Mount(stream, readOnly: true);

                        driver = ffsDriver;
                        mountSuccess = true;
                        TestContext.Out.WriteLine("Mounted with FFS driver");
                    }
                    catch (Exception ex)
                    {
                        TestContext.Out.WriteLine($"FFS mount failed: {ex.Message}");
                        TestContext.Out.WriteLine("Trying V7 driver...");

                        // Try V7 driver as fallback
                        try
                        {
                            var v7driver = new V7Driver();
                            v7driver.SetPartition(letter);

                            using var v7stream = new FileStream(SunS35DiskImage, FileMode.Open, FileAccess.Read, FileShare.Read);
                            v7driver.Mount(v7stream, readOnly: true);

                            driver = v7driver;
                            mountSuccess = true;
                            TestContext.Out.WriteLine("Mounted with V7 driver");
                        }
                        catch (Exception v7ex)
                        {
                            TestContext.Out.WriteLine($"V7 mount also failed: {v7ex.Message}");
                        }
                    }

                    if (!mountSuccess || driver == null)
                    {
                        TestContext.Out.WriteLine($"⚠️ Could not mount partition [{letter}] with any driver");
                        mountFailures.Add(letter.ToString());
                        continue;
                    }

                    // Traverse this partition
                    var stats = TraverseAllDirectories(driver);
                    overallStats.Merge(stats);

                    TestContext.Out.WriteLine("");
                    stats.PrintSummary(TestContext.Out);

                    Assert.That(stats.DirsVisited, Is.GreaterThan(0),
                        $"Partition [{letter}] should visit at least root directory");
                    Assert.That(stats.ErrorsEncountered, Is.EqualTo(0),
                        $"Partition [{letter}] should traverse without errors");

                    totalPartitionsTested++;
                }
                catch (Exception ex)
                {
                    TestContext.Out.WriteLine($"❌ Error processing partition [{letter}]: {ex.Message}");
                    mountFailures.Add(letter.ToString());
                }
            }

            // Assert
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            TestContext.Out.WriteLine("║  OVERALL STATISTICS (ALL PARTITIONS)                      ║");
            TestContext.Out.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            TestContext.Out.WriteLine($"Partitions tested: {totalPartitionsTested}");
            TestContext.Out.WriteLine($"Partitions that failed to mount: {mountFailures.Count} [{string.Join(", ", mountFailures)}]");
            overallStats.PrintSummary(TestContext.Out);

            Assert.That(mountFailures, Is.Empty,
                $"Partition(s) [{string.Join(", ", mountFailures)}] could not be mounted with FFS or V7 — " +
                "a partition that can't be mounted can't be traversed, so this must fail the test, not be silently skipped.");

            if (totalPartitionsTested > 0)
            {
                Assert.That(overallStats.ErrorsEncountered, Is.EqualTo(0),
                    "Should traverse all mounted partitions without errors");
                TestContext.Out.WriteLine("");
                TestContext.Out.WriteLine("✓ All mounted partitions traversed successfully!");
            }
            else
            {
                Assert.Warn("No partitions could be mounted and traversed");
            }
        }

        /// <summary>
        /// Statistics collected during directory traversal.
        /// </summary>
        private class TraversalStats
        {
            public int DirsVisited { get; set; }
            public int DirsFound { get; set; }
            public int FilesFound { get; set; }
            public long TotalBytes { get; set; }
            public int ErrorsEncountered { get; set; }

            public void Merge(TraversalStats other)
            {
                DirsVisited += other.DirsVisited;
                DirsFound += other.DirsFound;
                FilesFound += other.FilesFound;
                TotalBytes += other.TotalBytes;
                ErrorsEncountered += other.ErrorsEncountered;
            }

            public void PrintSummary(TextWriter output)
            {
                output.WriteLine($"Directories visited: {DirsVisited:N0}");
                output.WriteLine($"Subdirectories found: {DirsFound:N0}");
                output.WriteLine($"Files found: {FilesFound:N0}");
                output.WriteLine($"Total size: {FormatBytes(TotalBytes)}");
                output.WriteLine($"Errors encountered: {ErrorsEncountered}");
            }

            private string FormatBytes(long bytes)
            {
                if (bytes < 1024)
                    return $"{bytes} bytes";
                else if (bytes < 1024 * 1024)
                    return $"{bytes / 1024.0:F2} KB";
                else if (bytes < 1024 * 1024 * 1024)
                    return $"{bytes / (1024.0 * 1024.0):F2} MB";
                else
                    return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
            }
        }
    }
}
