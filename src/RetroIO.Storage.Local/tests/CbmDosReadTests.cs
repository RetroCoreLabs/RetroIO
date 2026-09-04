//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// CBM DOS read-path tests (Phase 1) exercised directly on the filesystem layer using the
    /// committed corpus under demo/test-images/c64. Validates the 8-entries-per-directory-sector
    /// fix and BAM-based free-block counting against real D64 images.
    /// </summary>
    [TestFixture]
    [Category("CBM")]
    public class CbmDosReadTests
    {
        // Standard 35-track 1541 image; the directory track is 18 (BAM at 18/0).
        private const int D64DirectoryTrack = 18;
        private const int D64BamTrack = 18;
        private const int D64MaxFreeBlocks = 664; // 683 total - 19 reserved on track 18

        // Walks up from the test bin directory to find the repo's demo/test-images/c64 corpus.
        private static string? FindCorpusDir()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "demo", "test-images", "c64");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            return null;
        }

        // All .d64 images in the corpus (case-insensitive). Empty if the corpus is absent.
        private static IEnumerable<string> D64Images()
        {
            string? corpus = FindCorpusDir();
            if (corpus == null)
            {
                yield break;
            }
            string d64Dir = Path.Combine(corpus, "d64");
            if (!Directory.Exists(d64Dir))
            {
                yield break;
            }
            string[] files = Directory.GetFiles(d64Dir, "*.d64");
            for (int i = 0; i < files.Length; i++)
            {
                // blank.d64 is the (intentionally empty) write-test base, not a read fixture.
                if (string.Equals(Path.GetFileName(files[i]), "blank.d64", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                yield return files[i];
            }
        }

        // Mounts a standard 35-track D64 image and returns the wired-up CBM filesystem + device.
        private static (GenericBlockDevice device, CbmDosFileSystem fs) MountD64(Stream stream)
        {
            GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.D64_35Track);
            device.Mount(stream, readOnly: true);

            CbmDosFileSystem fs = new CbmDosFileSystem(D64DirectoryTrack, D64BamTrack);
            fs.Initialize(device);
            return (device, fs);
        }

        [TestCaseSource(nameof(D64Images))]
        public void D64_DirectoryListing_IsNonEmpty(string imagePath)
        {
            using FileStream stream = File.OpenRead(imagePath);
            (GenericBlockDevice device, CbmDosFileSystem fs) = MountD64(stream);

            DirectoryEntry[] entries = fs.ListDirectory("/");
            TestContext.Out.WriteLine($"{Path.GetFileName(imagePath)}: {entries.Length} directory entries");

            Assert.That(entries.Length, Is.GreaterThan(0), "A real D64 should list at least one file");
            device.Dispose();
        }

        [TestCaseSource(nameof(D64Images))]
        public void D64_FreeBlocks_WithinValidRange(string imagePath)
        {
            using FileStream stream = File.OpenRead(imagePath);
            (GenericBlockDevice device, CbmDosFileSystem fs) = MountD64(stream);

            int free = fs.GetBlocksFree();
            TestContext.Out.WriteLine($"{Path.GetFileName(imagePath)}: {free} blocks free");

            // BAM free-count must be physically possible for a 35-track 1541 disk.
            Assert.That(free, Is.InRange(0, D64MaxFreeBlocks),
                "Free-block count must be between 0 and 664 for a 35-track D64");
            device.Dispose();
        }

        [TestCaseSource(nameof(D64Images))]
        public void D64_FirstFile_ReadsNonEmptyData(string imagePath)
        {
            using FileStream stream = File.OpenRead(imagePath);
            (GenericBlockDevice device, CbmDosFileSystem fs) = MountD64(stream);

            DirectoryEntry[] entries = fs.ListDirectory("/");
            if (entries.Length == 0)
            {
                Assert.Ignore("No directory entries to read");
                device.Dispose();
                return;
            }

            byte[] data = fs.ReadFile(entries[0].Name);
            TestContext.Out.WriteLine($"{Path.GetFileName(imagePath)}: first file '{entries[0].Name}' = {data.Length} bytes");
            Assert.That(data.Length, Is.GreaterThan(0), "Reading the first file should return data");
            device.Dispose();
        }

        // Regression guard for the 8-entries-per-directory-sector fix: an independent 8-slot scan of
        // the directory chain must agree with ListDirectory(). If the code had reverted to 7 slots,
        // any disk whose directory fills a sector would disagree.
        [TestCaseSource(nameof(D64Images))]
        public void D64_DirectoryCount_MatchesIndependentEightSlotScan(string imagePath)
        {
            using FileStream stream = File.OpenRead(imagePath);
            (GenericBlockDevice device, CbmDosFileSystem fs) = MountD64(stream);

            int expected = CountDirectoryEntriesEightSlots(device);
            int actual = fs.ListDirectory("/").Length;
            TestContext.Out.WriteLine($"{Path.GetFileName(imagePath)}: independent={expected} listed={actual}");

            Assert.That(actual, Is.EqualTo(expected),
                "ListDirectory must return every live entry across all 8 slots of each dir sector");
            device.Dispose();
        }

        // Independent oracle: counts live directory entries using 8 slots/sector, walking the chain.
        private static int CountDirectoryEntriesEightSlots(GenericBlockDevice device)
        {
            byte[] sector = new byte[device.BlockSize];
            byte track = D64DirectoryTrack;
            byte sec = 1; // D64/D71 first directory sector
            int count = 0;
            int guard = 0;

            while (track != 0 && guard < 4096)
            {
                guard++;
                device.ReadBlock(BlockAddress.FromTrackSector(track, sec), sector);

                for (int slot = 0; slot < 8; slot++)
                {
                    int typeByte = sector[slot * 32 + 2];
                    if ((typeByte & 0x80) != 0) // bit7 set = live (closed) entry
                    {
                        count++;
                    }
                }

                track = sector[0];
                sec = sector[1];
            }
            return count;
        }

        // ---- D71 / D81: golden values produced by VICE c1541 3.5 ----
        // Both images were formatted by c1541 with 2 PRG files written. c1541 -list reports:
        //   test.d71 -> 1320 blocks free ; test.d81 -> 3152 blocks free.
        // These tests validate the D71/D81 BAM free-count paths against that real-tool oracle.

        private static string? CorpusFile(string sub, string name)
        {
            string? corpus = FindCorpusDir();
            if (corpus == null)
            {
                return null;
            }
            string p = Path.Combine(corpus, sub, name);
            return File.Exists(p) ? p : null;
        }

        [Test]
        public void D71_Read_DirectoryAndFreeBlocks_MatchC1541()
        {
            string? path = CorpusFile("d71", "test.d71");
            if (path == null)
            {
                Assert.Ignore("D71 fixture not present (generate with c1541)");
                return;
            }

            using FileStream stream = File.OpenRead(path);
            GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.D71);
            device.Mount(stream, readOnly: true);
            CbmDosFileSystem fs = new CbmDosFileSystem(18, 18);
            fs.Initialize(device);

            DirectoryEntry[] entries = fs.ListDirectory("/");
            int free = fs.GetBlocksFree();
            TestContext.Out.WriteLine($"D71 test.d71: {entries.Length} entries, {free} blocks free");

            Assert.That(entries.Length, Is.EqualTo(2), "c1541 wrote 2 files");
            Assert.That(free, Is.EqualTo(1320), "must match c1541's reported free count");
            device.Dispose();
        }

        [Test]
        public void D81_Read_DirectoryAndFreeBlocks_MatchC1541()
        {
            string? path = CorpusFile("d81", "test.d81");
            if (path == null)
            {
                Assert.Ignore("D81 fixture not present (generate with c1541)");
                return;
            }

            using FileStream stream = File.OpenRead(path);
            GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.D81);
            device.Mount(stream, readOnly: true);
            CbmDosFileSystem fs = new CbmDosFileSystem(40, 40);
            fs.Initialize(device);

            DirectoryEntry[] entries = fs.ListDirectory("/");
            int free = fs.GetBlocksFree();
            TestContext.Out.WriteLine($"D81 test.d81: {entries.Length} entries, {free} blocks free");

            Assert.That(entries.Length, Is.EqualTo(2), "c1541 wrote 2 files");
            Assert.That(free, Is.EqualTo(3152), "must match c1541's reported free count");
            device.Dispose();
        }
    }
}
