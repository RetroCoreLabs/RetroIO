//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Comprehensive CBM write integrity tests. The goal is to PROVE that writing does not corrupt
    /// existing data: after every single write, ALL previously written files are re-read and must be
    /// byte-identical. Deletes are checked to reclaim exactly the right number of blocks while leaving
    /// survivors intact, the lifecycle ends with delete-all + a fresh re-open (persistence), and a
    /// large D71 file is forced to spill onto the second side and verified with c1541.
    /// </summary>
    [TestFixture]
    [Category("CBM")]
    public class CbmDosIntegrityTests
    {
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

        private static string? CorpusFile(string relative)
        {
            string? corpus = FindCorpusDir();
            if (corpus == null)
            {
                return null;
            }
            string p = Path.Combine(corpus, relative.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(p) ? p : null;
        }

        private static string CopyToTemp(string src, string ext)
        {
            string temp = Path.Combine(Path.GetTempPath(), "rfs_" + Path.GetRandomFileName() + ext);
            File.Copy(src, temp, overwrite: true);
            return temp;
        }

        private static string? FindC1541()
        {
            string? env = Environment.GetEnvironmentVariable("C1541");
            if (!string.IsNullOrEmpty(env) && File.Exists(env))
            {
                return env;
            }
            string[] candidates = { @"F:\Prog\GTK3VICE-3.5-win64\bin\c1541.exe" };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i]))
                {
                    return candidates[i];
                }
            }
            return null;
        }

        private static (int exitCode, string stdout) RunC1541(string exe, string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using Process p = Process.Start(psi)!;
            string outText = p.StandardOutput.ReadToEnd();
            p.WaitForExit(20000);
            return (p.ExitCode, outText);
        }

        // Distinct, position-dependent content per file so cross-file corruption or offset bugs show.
        private static byte[] Pattern(int seed, int length)
        {
            byte[] b = new byte[length];
            for (int i = 0; i < length; i++)
            {
                b[i] = (byte)((seed * 131 + i * 7 + (i >> 8) * 13) & 0xFF);
            }
            return b;
        }

        private static int BlocksFor(int length)
        {
            return length <= 0 ? 1 : (length + 253) / 254;
        }

        // ----- The shared integrity lifecycle, run per format -----
        private void RunIntegrity(BlockDeviceDescriptor descriptor, int dirTrack, int bamTrack,
            int freshFree, string blankRelative, string ext)
        {
            string? blank = CorpusFile(blankRelative);
            if (blank == null)
            {
                Assert.Ignore($"fixture {blankRelative} not present");
                return;
            }

            string temp = CopyToTemp(blank, ext);
            try
            {
                int[] sizes = { 0, 1, 100, 254, 255, 300, 700, 1500, 3000, 5000 };
                int n = sizes.Length;
                string[] names = new string[n];
                byte[][] datas = new byte[n][];
                bool[] alive = new bool[n];

                using (FileStream stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite))
                {
                    GenericBlockDevice device = new GenericBlockDevice(descriptor);
                    device.Mount(stream, readOnly: false);
                    CbmDosFileSystem fs = new CbmDosFileSystem(dirTrack, bamTrack);
                    fs.Initialize(device);

                    int free0 = fs.GetBlocksFree();
                    Assert.That(free0, Is.EqualTo(freshFree), "blank disk free-block count");

                    int used = 0;

                    // ---- write each file; after EACH write verify all prior files are intact ----
                    for (int i = 0; i < n; i++)
                    {
                        names[i] = "FILE" + i;
                        datas[i] = Pattern(i, sizes[i]);
                        fs.WriteFile(names[i], datas[i]);
                        alive[i] = true;
                        used += BlocksFor(sizes[i]);

                        for (int j = 0; j <= i; j++)
                        {
                            if (alive[j])
                            {
                                Assert.That(fs.ReadFile(names[j]), Is.EqualTo(datas[j]),
                                    $"{names[j]} was corrupted by writing {names[i]}");
                            }
                        }
                        Assert.That(fs.GetBlocksFree(), Is.EqualTo(free0 - used),
                            $"free-block accounting after writing {names[i]}");
                    }

                    Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(n), "all files listed");

                    // ---- rename one; content preserved, others intact ----
                    fs.Rename(names[3], "RENAMED");
                    names[3] = "RENAMED";
                    for (int j = 0; j < n; j++)
                    {
                        if (alive[j])
                        {
                            Assert.That(fs.ReadFile(names[j]), Is.EqualTo(datas[j]),
                                $"{names[j]} changed after rename");
                        }
                    }

                    // ---- delete every other file; survivors intact + blocks reclaimed ----
                    for (int i = 0; i < n; i += 2)
                    {
                        fs.DeleteFile(names[i]);
                        alive[i] = false;
                        used -= BlocksFor(sizes[i]);
                    }
                    Assert.That(fs.GetBlocksFree(), Is.EqualTo(free0 - used), "free reclaimed after partial delete");
                    for (int j = 0; j < n; j++)
                    {
                        if (alive[j])
                        {
                            Assert.That(fs.ReadFile(names[j]), Is.EqualTo(datas[j]),
                                $"{names[j]} corrupted by deleting other files");
                        }
                    }

                    // ---- delete the rest; empty + fully reclaimed ----
                    for (int i = 0; i < n; i++)
                    {
                        if (alive[i])
                        {
                            fs.DeleteFile(names[i]);
                            alive[i] = false;
                        }
                    }
                    Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(0), "directory empty after delete-all");
                    Assert.That(fs.GetBlocksFree(), Is.EqualTo(free0), "all blocks reclaimed");

                    device.Dispose();
                }

                // ---- re-open from disk: the persisted state must be empty (flush correctness) ----
                using (FileStream stream2 = new FileStream(temp, FileMode.Open, FileAccess.Read))
                {
                    GenericBlockDevice device2 = new GenericBlockDevice(descriptor);
                    device2.Mount(stream2, readOnly: true);
                    CbmDosFileSystem fs2 = new CbmDosFileSystem(dirTrack, bamTrack);
                    fs2.Initialize(device2);
                    Assert.That(fs2.ListDirectory("/").Length, Is.EqualTo(0), "persisted directory empty");
                    Assert.That(fs2.GetBlocksFree(), Is.EqualTo(freshFree), "persisted free count restored");
                    device2.Dispose();
                }
            }
            finally
            {
                File.Delete(temp);
            }
        }

        [Test]
        public void D64_MultiFile_Integrity_NoCorruption()
        {
            RunIntegrity(BlockDeviceDescriptor.D64_35Track, 18, 18, 664, "d64/blank.d64", ".d64");
        }

        [Test]
        public void D71_MultiFile_Integrity_NoCorruption()
        {
            RunIntegrity(BlockDeviceDescriptor.D71, 18, 18, 1328, "d71/blank.d71", ".d71");
        }

        [Test]
        public void D81_MultiFile_Integrity_NoCorruption()
        {
            RunIntegrity(BlockDeviceDescriptor.D81, 40, 40, 3160, "d81/blank.d81", ".d81");
        }

        // A single large file must spill from side 1 (tracks 1-35) onto side 2 (tracks 36-70) of a
        // D71, exercising the split second-side BAM. c1541 must read it back byte-identical and agree
        // on the free count, proving the side-2 allocation is valid against the real tool.
        [Test]
        public void D71_LargeFile_SpillsToSide2_ReadableByC1541()
        {
            string? blank = CorpusFile("d71/blank.d71");
            if (blank == null)
            {
                Assert.Ignore("blank.d71 fixture not present");
                return;
            }
            string? c1541 = FindC1541();
            if (c1541 == null)
            {
                Assert.Ignore("c1541 not found");
                return;
            }

            string temp = CopyToTemp(blank, ".d71");
            string extracted = temp + ".out";
            try
            {
                // 200000 bytes => 788 blocks (> the ~664 usable on side 1), so it must use side 2.
                byte[] content = Pattern(99, 200000);
                int blocks = BlocksFor(content.Length); // 788
                int expectedFree = 1328 - blocks;       // 540

                using (FileStream stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite))
                {
                    GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.D71);
                    device.Mount(stream, readOnly: false);
                    CbmDosFileSystem fs = new CbmDosFileSystem(18, 18);
                    fs.Initialize(device);
                    fs.WriteFile("BIG", content);
                    Assert.That(fs.GetBlocksFree(), Is.EqualTo(expectedFree), "our free count after big write");
                    Assert.That(fs.ReadFile("BIG"), Is.EqualTo(content), "self round-trip of the spilled file");
                    device.Dispose();
                }

                (int listCode, string listing) = RunC1541(c1541, $"-attach \"{temp}\" -list");
                Assert.That(listCode, Is.EqualTo(0));
                Assert.That(listing.ToLowerInvariant(), Does.Contain("big"));
                Assert.That(listing, Does.Contain($"{expectedFree} blocks free"),
                    "c1541 must agree on the free count after a side-2 spill");

                (int readCode, _) = RunC1541(c1541, $"-attach \"{temp}\" -read BIG \"{extracted}\"");
                Assert.That(readCode, Is.EqualTo(0));
                Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(content),
                    "c1541 must read the side-2-spilled file byte-identical");
            }
            finally
            {
                File.Delete(temp);
                if (File.Exists(extracted))
                {
                    File.Delete(extracted);
                }
            }
        }

        // Mounts a fresh blank D64 copy for the focused write-edge tests below.
        private static string CopyBlankD64()
        {
            string? blank = CorpusFile("d64/blank.d64");
            if (blank == null)
            {
                return string.Empty;
            }
            return CopyToTemp(blank, ".d64");
        }

        // GAP #1: a write that runs out of space must throw, fully roll back (free count restored),
        // and leave the already-present file completely intact (no partial/garbage allocation).
        [Test]
        public void D64_FailedWrite_RollsBack_DoesNotCorruptExistingData()
        {
            string temp = CopyBlankD64();
            if (temp.Length == 0)
            {
                Assert.Ignore("blank.d64 fixture not present");
                return;
            }
            try
            {
                byte[] big = Pattern(1, 600 * 254);   // 600 blocks (of 664 usable)
                byte[] tooBig = Pattern(2, 100 * 254); // 100 blocks; only 64 remain -> must fail

                using FileStream stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite);
                GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.D64_35Track);
                device.Mount(stream, readOnly: false);
                CbmDosFileSystem fs = new CbmDosFileSystem(18, 18);
                fs.Initialize(device);

                fs.WriteFile("BIG1", big);
                int freeAfterBig = fs.GetBlocksFree();
                Assert.That(freeAfterBig, Is.EqualTo(64));

                Assert.Throws<IOException>(() => fs.WriteFile("BIG2", tooBig), "disk-full must throw");

                // The aborted write must not have changed anything.
                Assert.That(fs.GetBlocksFree(), Is.EqualTo(freeAfterBig), "rollback restores the free count");
                Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(1), "no directory entry for the failed file");
                Assert.That(fs.ReadFile("BIG1"), Is.EqualTo(big), "existing file intact after a failed write");

                device.Dispose();
            }
            finally
            {
                File.Delete(temp);
            }
        }

        // GAP #2: after deleting a file, writing a new file must reuse the freed blocks without
        // corrupting the surviving files, with correct accounting throughout.
        [Test]
        public void D64_DeleteThenWrite_ReusesFreedSpace_NoCorruption()
        {
            string temp = CopyBlankD64();
            if (temp.Length == 0)
            {
                Assert.Ignore("blank.d64 fixture not present");
                return;
            }
            try
            {
                byte[] a = Pattern(10, 2 * 254);
                byte[] b = Pattern(11, 3 * 254);
                byte[] c = Pattern(12, 2 * 254);
                byte[] d = Pattern(13, 4 * 254); // written after B is deleted

                using FileStream stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite);
                GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.D64_35Track);
                device.Mount(stream, readOnly: false);
                CbmDosFileSystem fs = new CbmDosFileSystem(18, 18);
                fs.Initialize(device);

                fs.WriteFile("AAA", a);
                fs.WriteFile("BBB", b);
                fs.WriteFile("CCC", c);
                Assert.That(fs.GetBlocksFree(), Is.EqualTo(664 - 7));

                fs.DeleteFile("BBB");
                Assert.That(fs.GetBlocksFree(), Is.EqualTo(664 - 4), "3 blocks reclaimed");

                // Reuse the freed space (and then some).
                fs.WriteFile("DDD", d);
                Assert.That(fs.GetBlocksFree(), Is.EqualTo(664 - 8));

                // Survivors + the new file must all be byte-correct; the deleted one is gone.
                Assert.That(fs.ReadFile("AAA"), Is.EqualTo(a));
                Assert.That(fs.ReadFile("CCC"), Is.EqualTo(c));
                Assert.That(fs.ReadFile("DDD"), Is.EqualTo(d));
                Assert.Throws<FileNotFoundException>(() => fs.ReadFile("BBB"));

                device.Dispose();
            }
            finally
            {
                File.Delete(temp);
            }
        }

        // GAP #3: written file CONTENT must persist across a close + reopen (flush correctness),
        // read back with our own reader (not just c1541).
        [Test]
        public void D64_WrittenFiles_Content_PersistsAcrossReopen()
        {
            string temp = CopyBlankD64();
            if (temp.Length == 0)
            {
                Assert.Ignore("blank.d64 fixture not present");
                return;
            }
            try
            {
                byte[] one = Pattern(20, 300);
                byte[] two = Pattern(21, 1500);
                byte[] three = Pattern(22, 9);

                using (FileStream stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite))
                {
                    GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.D64_35Track);
                    device.Mount(stream, readOnly: false);
                    CbmDosFileSystem fs = new CbmDosFileSystem(18, 18);
                    fs.Initialize(device);
                    fs.WriteFile("ONE", one);
                    fs.WriteFile("TWO", two);
                    fs.WriteFile("THREE", three);
                    device.Dispose();
                }

                // Reopen fresh from disk and read the content back.
                using (FileStream stream2 = new FileStream(temp, FileMode.Open, FileAccess.Read))
                {
                    GenericBlockDevice device2 = new GenericBlockDevice(BlockDeviceDescriptor.D64_35Track);
                    device2.Mount(stream2, readOnly: true);
                    CbmDosFileSystem fs2 = new CbmDosFileSystem(18, 18);
                    fs2.Initialize(device2);

                    Assert.That(fs2.ListDirectory("/").Length, Is.EqualTo(3));
                    Assert.That(fs2.ReadFile("ONE"), Is.EqualTo(one));
                    Assert.That(fs2.ReadFile("TWO"), Is.EqualTo(two));
                    Assert.That(fs2.ReadFile("THREE"), Is.EqualTo(three));
                    device2.Dispose();
                }
            }
            finally
            {
                File.Delete(temp);
            }
        }
    }
}
