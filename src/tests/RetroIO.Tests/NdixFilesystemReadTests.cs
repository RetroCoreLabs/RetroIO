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
using RetroIO.Storage.Local.Drivers;

namespace RetroIO.Tests
{
    /// <summary>
    /// End-to-end read tests against real NDIX (Norsk Data UNIX) root filesystems.
    ///
    /// NDIX uses a 4.3BSD-Tahoe FFS, big-endian, written by an ND-100. Two things make these
    /// images different from a stock BSD/SunOS pack, and both are handled by the FFS driver:
    ///
    ///  1. The filesystem does NOT start at byte 0 and there is no partition table saying so.
    ///     The NDIX disk driver (kernel/MASTER/io/di.c) sets blkzero = nspc = nsect * ntrak
    ///     = 18 * 5 = 90 for a di70 and adds it to every block address, because physical
    ///     cylinder 0 belongs to SINTRAN (label / boot / bad-track area). With DEV_BSIZE 1024
    ///     that puts the filesystem base at byte 92160.
    ///
    ///  2. di_size lives in ic_size.val[0] (NDIX h/inode.h:98), the opposite half from
    ///     SunOS/mc68000. Reading it as a big-endian 64-bit quad yields size shifted left by 32.
    ///
    /// These tests walk each available image completely and read file contents, so a
    /// regression in either area fails here rather than silently returning short data.
    /// </summary>
    [TestFixture]
    public class NdixFilesystemReadTests
    {
        private const string NdixDirectory = @"E:\Dev\Ronny\NDIX-C";

        /// <summary>
        /// Filesystem base: 90 sectors of 1024 bytes (one di70 cylinder).
        /// </summary>
        private const long NdixFilesystemBase = 90L * 1024L;

        /// <summary>
        /// Every NDIX root image we know about. Missing files are skipped, not failed, so the
        /// suite still runs on a machine that does not have the NDIX tree checked out.
        /// </summary>
        private static readonly string[] ImageNames =
        {
            "rootfs.img",
            "rootfs_mkproto.img",
            "rootfs_init.img",
            "rootfs_hello.img",
            "rootfs_shell.img",
            "rootfs_full.img"
        };

        [Test]
        [Category("FFS")]
        [Category("NDIX")]
        [TestCaseSource(nameof(ImageNames))]
        public void NdixImage_MountsAndTraversesCompletely(string imageName)
        {
            string path = Path.Combine(NdixDirectory, imageName);
            if (!File.Exists(path))
            {
                Assert.Ignore($"Test skipped: NDIX image not found at {path}");
                return;
            }

            using var driver = new FfsDriver();
            driver.SetRawMode();

            using var stream = File.OpenRead(path);
            driver.Mount(stream, readOnly: true);

            Assert.That(driver.FilesystemOffsetBytes, Is.EqualTo(NdixFilesystemBase),
                "Filesystem base should be discovered at one di70 cylinder in");

            var stats = new TraversalStats();
            Traverse(driver, "/", 0, stats);

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine($"{imageName}: {stats.Directories} dirs, {stats.Files} files, " +
                                      $"{stats.BytesRead:N0} bytes read, {stats.Errors} errors");

            Assert.That(stats.Errors, Is.EqualTo(0), "Traversal should complete without errors");
            Assert.That(stats.Directories, Is.GreaterThan(0), "Should visit at least the root directory");
        }

        [Test]
        [Category("FFS")]
        [Category("NDIX")]
        public void NdixImage_ReadsEtcInit_AsAOutBinary()
        {
            // /etc/init is an a.out. Reading its header proves the driver returns real file
            // content through the indirect/fragment machinery, not just directory entries.
            string? path = FindFirstImageContaining("/etc/init");
            if (path == null)
            {
                Assert.Ignore("Test skipped: no NDIX image with /etc/init found");
                return;
            }

            using var driver = new FfsDriver();
            driver.SetRawMode();

            using var stream = File.OpenRead(path);
            driver.Mount(stream, readOnly: true);

            byte[] data = driver.ReadFile("/etc/init");

            Assert.That(data.Length, Is.GreaterThan(32), "a.out must be at least a header long");

            // a_magic is a big-endian 32-bit value; 0413 octal = 0x10B = ZMAGIC.
            uint magic = ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];
            uint textSize = ((uint)data[4] << 24) | ((uint)data[5] << 16) | ((uint)data[6] << 8) | data[7];

            TestContext.Out.WriteLine($"Image      : {path}");
            TestContext.Out.WriteLine($"/etc/init  : {data.Length:N0} bytes");
            TestContext.Out.WriteLine($"a_magic    : 0x{magic:X} (0{Convert.ToString(magic, 8)} octal)");
            TestContext.Out.WriteLine($"a_text     : {textSize:N0}");

            Assert.That(magic, Is.EqualTo(0x10Bu),
                "a_magic should be 0413 octal (ZMAGIC) for an NDIX a.out");
        }

        [Test]
        [Category("FFS")]
        [Category("NDIX")]
        public void NdixImage_ReadsEtcMotd_AsText()
        {
            // A small text file is the clearest end-to-end proof: exact byte count and readable
            // content, with no header format to hide behind.
            string? path = FindFirstImageContaining("/etc/motd");
            if (path == null)
            {
                Assert.Ignore("Test skipped: no NDIX image with /etc/motd found");
                return;
            }

            using var driver = new FfsDriver();
            driver.SetRawMode();

            using var stream = File.OpenRead(path);
            driver.Mount(stream, readOnly: true);

            byte[] data = driver.ReadFile("/etc/motd");
            string text = System.Text.Encoding.ASCII.GetString(data);

            TestContext.Out.WriteLine($"Image: {path}");
            TestContext.Out.WriteLine($"/etc/motd ({data.Length} bytes):");
            TestContext.Out.WriteLine(text);

            Assert.That(data.Length, Is.GreaterThan(0), "motd should not be empty");

            // Every byte must be printable ASCII, tab, CR or LF. Anything else means we read the
            // wrong blocks or ran past the end of the file.
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                bool printable = (b >= 0x20 && b < 0x7F) || b == 0x09 || b == 0x0A || b == 0x0D;
                Assert.That(printable, Is.True,
                    $"/etc/motd byte {i} = 0x{b:X2} is not printable text");
            }
        }

        [Test]
        [Category("FFS")]
        [Category("NDIX")]
        public void NdixImage_FileSizesMatchBytesActuallyRead()
        {
            // The di_size val[0] fix is what makes this hold. If the wrong half were read, the
            // reported size would be size << 32 and the read would overflow rather than match.
            string? path = FindRichestImage();
            if (path == null)
            {
                Assert.Ignore("Test skipped: no NDIX image found");
                return;
            }

            using var driver = new FfsDriver();
            driver.SetRawMode();

            using var stream = File.OpenRead(path);
            driver.Mount(stream, readOnly: true);

            var checkedFiles = new List<string>();
            VerifySizes(driver, "/", 0, checkedFiles);

            TestContext.Out.WriteLine($"Image: {path}");
            TestContext.Out.WriteLine($"Verified {checkedFiles.Count} file(s) read exactly to their declared size");

            Assert.That(checkedFiles.Count, Is.GreaterThan(0), "Should have verified at least one file");
        }

        // =================================================================
        // HELPERS
        // =================================================================

        private sealed class TraversalStats
        {
            public int Directories;
            public int Files;
            public int Errors;
            public long BytesRead;
        }

        /// <summary>
        /// Walks the whole tree, reading every regular file, and records what it found.
        /// </summary>
        private static void Traverse(FfsDriver driver, string path, int depth, TraversalStats stats)
        {
            if (depth > 32)
            {
                return;   // guard against a cyclic or corrupt tree
            }

            DirectoryEntry[] entries;
            try
            {
                entries = driver.ListDirectory(path);
                stats.Directories++;
            }
            catch (Exception ex)
            {
                stats.Errors++;
                TestContext.Out.WriteLine($"{new string(' ', depth * 2)}ERROR listing {path}: {ex.Message}");
                return;
            }

            TestContext.Out.WriteLine($"{new string(' ', depth * 2)}{path}  ({entries.Length} entries)");

            for (int i = 0; i < entries.Length; i++)
            {
                DirectoryEntry entry = entries[i];

                if (entry.Name == "." || entry.Name == "..")
                {
                    continue;
                }

                string child = path == "/" ? "/" + entry.Name : path + "/" + entry.Name;

                if (entry.Metadata.IsDirectory)
                {
                    Traverse(driver, child, depth + 1, stats);
                    continue;
                }

                stats.Files++;

                // Device nodes have no data blocks - their inode holds a device number instead,
                // so reading them is meaningless rather than an error.
                if (entry.Metadata.GetProperty<bool>("IsDevice")
                    || entry.Metadata.GetProperty<bool>("IsCharDevice")
                    || entry.Metadata.GetProperty<bool>("IsBlockDevice"))
                {
                    TestContext.Out.WriteLine($"{new string(' ', (depth + 1) * 2)}{entry.Name}  [device]");
                    continue;
                }

                try
                {
                    byte[] data = driver.ReadFile(child);
                    stats.BytesRead += data.Length;
                    TestContext.Out.WriteLine(
                        $"{new string(' ', (depth + 1) * 2)}{entry.Name}  ({data.Length:N0} bytes)");
                }
                catch (Exception ex)
                {
                    stats.Errors++;
                    TestContext.Out.WriteLine(
                        $"{new string(' ', (depth + 1) * 2)}ERROR reading {child}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Asserts that every regular file reads back exactly as many bytes as its metadata says.
        /// </summary>
        private static void VerifySizes(FfsDriver driver, string path, int depth, List<string> checkedFiles)
        {
            if (depth > 32)
            {
                return;
            }

            DirectoryEntry[] entries = driver.ListDirectory(path);

            for (int i = 0; i < entries.Length; i++)
            {
                DirectoryEntry entry = entries[i];

                if (entry.Name == "." || entry.Name == "..")
                {
                    continue;
                }

                string child = path == "/" ? "/" + entry.Name : path + "/" + entry.Name;

                if (entry.Metadata.IsDirectory)
                {
                    VerifySizes(driver, child, depth + 1, checkedFiles);
                    continue;
                }

                if (entry.Metadata.GetProperty<bool>("IsDevice")
                    || entry.Metadata.GetProperty<bool>("IsCharDevice")
                    || entry.Metadata.GetProperty<bool>("IsBlockDevice"))
                {
                    continue;
                }

                byte[] data = driver.ReadFile(child);

                Assert.That((long)data.Length, Is.EqualTo(entry.Metadata.Size),
                    $"{child}: bytes read should equal the declared di_size");

                checkedFiles.Add(child);
            }
        }

        /// <summary>
        /// Returns the first available image whose tree contains the given path, or null when no
        /// NDIX image is present on this machine at all.
        ///
        /// IMPORTANT: an image that EXISTS but cannot be mounted or walked is a hard failure, not
        /// a reason to skip. Swallowing those exceptions once made a reverted di_size fix show up
        /// as a skipped test instead of a red one - i.e. the regression guard silently disarmed
        /// itself. "No image on disk" and "image is present but broken" must not share a path.
        /// </summary>
        private static string? FindFirstImageContaining(string filePath)
        {
            bool anyImagePresent = false;

            for (int i = 0; i < ImageNames.Length; i++)
            {
                string path = Path.Combine(NdixDirectory, ImageNames[i]);
                if (!File.Exists(path))
                {
                    continue;
                }

                anyImagePresent = true;

                using var driver = new FfsDriver();
                driver.SetRawMode();
                using var stream = File.OpenRead(path);

                // Deliberately NOT wrapped in try/catch: a present-but-unmountable NDIX image
                // means the driver regressed, and the exception must reach the test runner.
                driver.Mount(stream, readOnly: true);

                if (driver.GetMetadata(filePath) != null)
                {
                    return path;
                }
            }

            if (!anyImagePresent)
            {
                return null;   // genuinely nothing to test against -> caller may Ignore
            }

            // Images exist and all mounted cleanly, but none contains the requested path. That is
            // a legitimate "nothing to test here" for content-specific tests.
            return null;
        }

        /// <summary>
        /// Returns the available image with the most root entries, or null when none exist.
        /// As above, a present-but-unmountable image throws rather than being skipped over.
        /// </summary>
        private static string? FindRichestImage()
        {
            string? best = null;
            int bestCount = -1;

            for (int i = 0; i < ImageNames.Length; i++)
            {
                string path = Path.Combine(NdixDirectory, ImageNames[i]);
                if (!File.Exists(path))
                {
                    continue;
                }

                using var driver = new FfsDriver();
                driver.SetRawMode();
                using var stream = File.OpenRead(path);
                driver.Mount(stream, readOnly: true);

                int count = driver.ListDirectory("/").Length;
                if (count > bestCount)
                {
                    bestCount = count;
                    best = path;
                }
            }

            return best;
        }
    }
}
