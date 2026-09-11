//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// CBM DOS write-path tests (Phase 2) for the D64 layout. Each test starts from a blank,
    /// c1541-formatted image (demo/test-images/c64/d64/blank.d64), writes via CbmDosFileSystem, and
    /// validates two ways: a self round-trip (read back with our own reader) and — when a VICE
    /// c1541 binary is available — a real-tool round-trip (c1541 reads what we wrote, byte-identical,
    /// and reports the expected free-block count). The c1541 tests auto-skip if the tool is absent.
    /// </summary>
    [TestFixture]
    [Category("CBM")]
    public class CbmDosWriteTests
    {
        private const int D64FreshFreeBlocks = 664; // a freshly formatted 35-track 1541 disk

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

        private static string? BlankD64Path()
        {
            string? corpus = FindCorpusDir();
            if (corpus == null)
            {
                return null;
            }
            string p = Path.Combine(corpus, "d64", "blank.d64");
            return File.Exists(p) ? p : null;
        }

        // Locates a VICE c1541 binary via the C1541 env var or known install paths.
        private static string? FindC1541()
        {
            string? env = Environment.GetEnvironmentVariable("C1541");
            if (!string.IsNullOrEmpty(env) && File.Exists(env))
            {
                return env;
            }
            string[] candidates =
            {
                @"F:\Prog\GTK3VICE-3.5-win64\bin\c1541.exe"
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i]))
                {
                    return candidates[i];
                }
            }
            return null;
        }

        private static byte[] MakePattern(int length)
        {
            byte[] b = new byte[length];
            for (int i = 0; i < length; i++)
            {
                b[i] = (byte)(i & 0xFF);
            }
            return b;
        }

        // Distinct, seed-dependent content so an overwrite genuinely differs from the prior file.
        private static byte[] Seeded(int seed, int length)
        {
            byte[] b = new byte[length];
            for (int i = 0; i < length; i++)
            {
                b[i] = (byte)((i * 7 + seed * 131) & 0xFF);
            }
            return b;
        }

        private static string CopyBlankToTemp(string blank)
        {
            string temp = Path.Combine(Path.GetTempPath(), "rfs_" + Path.GetRandomFileName() + ".d64");
            File.Copy(blank, temp, overwrite: true);
            return temp;
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
            p.WaitForExit(15000);
            return (p.ExitCode, outText);
        }

        [Test]
        public void D64_Write_Read_Delete_SelfRoundTrip()
        {
            string? blank = BlankD64Path();
            if (blank == null)
            {
                Assert.Ignore("blank.d64 fixture not present");
                return;
            }

            string temp = CopyBlankToTemp(blank);
            try
            {
                byte[] content = MakePattern(300); // spans 2 data blocks (254 + 46)

                using (FileStream stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite))
                {
                    GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.D64_35Track);
                    device.Mount(stream, readOnly: false);
                    CbmDosFileSystem fs = new CbmDosFileSystem(18, 18);
                    fs.Initialize(device);

                    int freeBefore = fs.GetBlocksFree();
                    Assert.That(freeBefore, Is.EqualTo(D64FreshFreeBlocks), "blank disk should be empty");

                    fs.WriteFile("HELLO", content);

                    // Self round-trip: read back exactly what we wrote.
                    byte[] readBack = fs.ReadFile("HELLO");
                    Assert.That(readBack, Is.EqualTo(content), "round-trip read must match written bytes");

                    // 300 bytes => 2 blocks consumed.
                    Assert.That(fs.GetBlocksFree(), Is.EqualTo(freeBefore - 2), "two blocks consumed");

                    DirectoryEntry[] entries = fs.ListDirectory("/");
                    Assert.That(entries.Length, Is.EqualTo(1));
                    Assert.That(entries[0].Name, Is.EqualTo("HELLO"));

                    // Delete restores the free count and empties the directory.
                    fs.DeleteFile("HELLO");
                    Assert.That(fs.GetBlocksFree(), Is.EqualTo(freeBefore), "delete reclaims blocks");
                    Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(0));

                    device.Dispose();
                }
            }
            finally
            {
                File.Delete(temp);
            }
        }

        [Test]
        public void D64_WrittenFile_IsReadableByC1541()
        {
            string? blank = BlankD64Path();
            if (blank == null)
            {
                Assert.Ignore("blank.d64 fixture not present");
                return;
            }
            string? c1541 = FindC1541();
            if (c1541 == null)
            {
                Assert.Ignore("c1541 not found (set the C1541 env var to enable interop tests)");
                return;
            }

            string temp = CopyBlankToTemp(blank);
            string extracted = temp + ".out";
            try
            {
                byte[] content = MakePattern(700); // 3 data blocks (254 + 254 + 192)

                using (FileStream stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite))
                {
                    GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.D64_35Track);
                    device.Mount(stream, readOnly: false);
                    CbmDosFileSystem fs = new CbmDosFileSystem(18, 18);
                    fs.Initialize(device);
                    fs.WriteFile("HELLO", content);
                    device.Dispose();
                } // stream closed -> temp.d64 on disk reflects our writes

                // c1541 must list our file with the expected reduced free count (700 bytes -> 3 blocks).
                (int listCode, string listing) = RunC1541(c1541, $"-attach \"{temp}\" -list");
                Assert.That(listCode, Is.EqualTo(0), "c1541 -list should succeed");
                Assert.That(listing.ToLowerInvariant(), Does.Contain("hello"), "c1541 should see our file");
                Assert.That(listing, Does.Contain("661 blocks free"), "c1541 must agree on free count (664 - 3)");

                // c1541 must extract byte-identical content.
                (int readCode, _) = RunC1541(c1541, $"-attach \"{temp}\" -read HELLO \"{extracted}\"");
                Assert.That(readCode, Is.EqualTo(0), "c1541 -read should succeed");
                byte[] c1541Bytes = File.ReadAllBytes(extracted);
                Assert.That(c1541Bytes, Is.EqualTo(content), "c1541 must read back exactly what we wrote");
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

        // ---- D81 (1581) ----

        private static string? BlankPath(string sub, string name)
        {
            string? corpus = FindCorpusDir();
            if (corpus == null)
            {
                return null;
            }
            string p = Path.Combine(corpus, sub, name);
            return File.Exists(p) ? p : null;
        }

        private static string CopyToTemp(string src, string ext)
        {
            string temp = Path.Combine(Path.GetTempPath(), "rfs_" + Path.GetRandomFileName() + ext);
            File.Copy(src, temp, overwrite: true);
            return temp;
        }

        [Test]
        public void D81_Write_Read_Delete_SelfRoundTrip()
        {
            string? blank = BlankPath("d81", "blank.d81");
            if (blank == null)
            {
                Assert.Ignore("blank.d81 fixture not present");
                return;
            }

            string temp = CopyToTemp(blank, ".d81");
            try
            {
                byte[] content = MakePattern(300);
                using (FileStream stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite))
                {
                    GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.D81);
                    device.Mount(stream, readOnly: false);
                    CbmDosFileSystem fs = new CbmDosFileSystem(40, 40);
                    fs.Initialize(device);

                    int freeBefore = fs.GetBlocksFree();
                    Assert.That(freeBefore, Is.EqualTo(3160), "blank 1581 disk");

                    fs.WriteFile("HELLO", content);
                    Assert.That(fs.ReadFile("HELLO"), Is.EqualTo(content));
                    Assert.That(fs.GetBlocksFree(), Is.EqualTo(freeBefore - 2));

                    DirectoryEntry[] entries = fs.ListDirectory("/");
                    Assert.That(entries.Length, Is.EqualTo(1));
                    Assert.That(entries[0].Name, Is.EqualTo("HELLO"));

                    fs.DeleteFile("HELLO");
                    Assert.That(fs.GetBlocksFree(), Is.EqualTo(freeBefore));
                    Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(0));
                    device.Dispose();
                }
            }
            finally
            {
                File.Delete(temp);
            }
        }

        [Test]
        public void D81_WrittenFile_IsReadableByC1541()
        {
            string? blank = BlankPath("d81", "blank.d81");
            if (blank == null)
            {
                Assert.Ignore("blank.d81 fixture not present");
                return;
            }
            string? c1541 = FindC1541();
            if (c1541 == null)
            {
                Assert.Ignore("c1541 not found");
                return;
            }

            string temp = CopyToTemp(blank, ".d81");
            string extracted = temp + ".out";
            try
            {
                byte[] content = MakePattern(700); // 3 data blocks
                using (FileStream stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite))
                {
                    GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.D81);
                    device.Mount(stream, readOnly: false);
                    CbmDosFileSystem fs = new CbmDosFileSystem(40, 40);
                    fs.Initialize(device);
                    fs.WriteFile("HELLO", content);
                    device.Dispose();
                }

                (int listCode, string listing) = RunC1541(c1541, $"-attach \"{temp}\" -list");
                Assert.That(listCode, Is.EqualTo(0));
                Assert.That(listing.ToLowerInvariant(), Does.Contain("hello"));
                Assert.That(listing, Does.Contain("3157 blocks free"), "3160 - 3");

                (int readCode, _) = RunC1541(c1541, $"-attach \"{temp}\" -read HELLO \"{extracted}\"");
                Assert.That(readCode, Is.EqualTo(0));
                Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(content));
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

        // ---- Rename + driver-level write ----

        [Test]
        public void D64_Rename_PreservesContentAndAllocation()
        {
            string? blank = BlankPath("d64", "blank.d64");
            if (blank == null)
            {
                Assert.Ignore("blank.d64 fixture not present");
                return;
            }

            string temp = CopyToTemp(blank, ".d64");
            try
            {
                byte[] content = MakePattern(300);
                using (FileStream stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite))
                {
                    GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.D64_35Track);
                    device.Mount(stream, readOnly: false);
                    CbmDosFileSystem fs = new CbmDosFileSystem(18, 18);
                    fs.Initialize(device);

                    fs.WriteFile("HELLO", content);
                    int freeAfterWrite = fs.GetBlocksFree();

                    fs.Rename("HELLO", "WORLD");

                    DirectoryEntry[] entries = fs.ListDirectory("/");
                    Assert.That(entries.Length, Is.EqualTo(1));
                    Assert.That(entries[0].Name, Is.EqualTo("WORLD"));
                    Assert.That(fs.ReadFile("WORLD"), Is.EqualTo(content), "content survives rename");
                    Assert.That(fs.GetBlocksFree(), Is.EqualTo(freeAfterWrite), "rename changes no allocation");
                    Assert.Throws<FileNotFoundException>(() => fs.ReadFile("HELLO"), "old name is gone");

                    device.Dispose();
                }
            }
            finally
            {
                File.Delete(temp);
            }
        }

        [Test]
        public void Driver_D64_Write_Read_Delete_RoundTrip()
        {
            string? blank = BlankPath("d64", "blank.d64");
            if (blank == null)
            {
                Assert.Ignore("blank.d64 fixture not present");
                return;
            }

            string temp = CopyToTemp(blank, ".d64");
            try
            {
                byte[] content = MakePattern(500); // 2 blocks
                using (FileStream stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite))
                {
                    using CommodoreDiskDriver driver = new CommodoreDiskDriver();
                    driver.Mount(stream, readOnly: false);

                    Assert.That(driver.CanWrite, Is.True, "D64 read-write mount must report CanWrite");

                    driver.WriteFile("DRVTEST", content);
                    DirectoryEntry[] entries = driver.ListDirectory("/");
                    Assert.That(entries.Length, Is.EqualTo(1));
                    Assert.That(entries[0].Name, Is.EqualTo("DRVTEST"));
                    Assert.That(driver.ReadFile("DRVTEST"), Is.EqualTo(content));

                    driver.DeleteFile("DRVTEST");
                    Assert.That(driver.ListDirectory("/").Length, Is.EqualTo(0));
                }
            }
            finally
            {
                File.Delete(temp);
            }
        }

        // ---- Format (create a blank image in code) ----

        [Test]
        public void D64_Format_ProducesEmptyImage_Writable_ReadableByC1541()
        {
            string temp = Path.Combine(Path.GetTempPath(), "rfs_fmt_" + Path.GetRandomFileName() + ".d64");
            string extracted = temp + ".out";
            try
            {
                File.WriteAllBytes(temp, new byte[174848]); // zeroed, correct 35-track D64 size
                byte[] content = MakePattern(300);

                using (FileStream stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite))
                {
                    GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.D64_35Track);
                    device.Mount(stream, readOnly: false);
                    CbmDosFileSystem fs = new CbmDosFileSystem(18, 18);
                    fs.Initialize(device);

                    fs.Format("TESTDISK", "01");
                    Assert.That(fs.GetBlocksFree(), Is.EqualTo(664), "freshly formatted D64 has 664 free");
                    Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(0), "formatted disk is empty");

                    fs.WriteFile("HELLO", content);
                    Assert.That(fs.ReadFile("HELLO"), Is.EqualTo(content), "write to a code-formatted disk");
                    device.Dispose();
                }

                string? c1541 = FindC1541();
                if (c1541 != null)
                {
                    (int listCode, string listing) = RunC1541(c1541, $"-attach \"{temp}\" -list");
                    Assert.That(listCode, Is.EqualTo(0), "c1541 must accept our code-formatted image");
                    Assert.That(listing.ToLowerInvariant(), Does.Contain("hello"));
                    Assert.That(listing, Does.Contain("662 blocks free"), "664 - 2 (300 bytes = 2 blocks)");

                    (int readCode, _) = RunC1541(c1541, $"-attach \"{temp}\" -read HELLO \"{extracted}\"");
                    Assert.That(readCode, Is.EqualTo(0));
                    Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(content));
                }
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

        // Shared format check: format a zeroed buffer in code, verify empty + free count, write a
        // file, and (when available) confirm c1541 accepts the image and reads the file identically.
        private void RunFormatTest(BlockDeviceDescriptor descriptor, int dirTrack, int bamTrack,
            int sizeBytes, int freshFree, string ext)
        {
            string temp = Path.Combine(Path.GetTempPath(), "rfs_fmt_" + Path.GetRandomFileName() + ext);
            string extracted = temp + ".out";
            try
            {
                File.WriteAllBytes(temp, new byte[sizeBytes]);
                byte[] content = MakePattern(300); // 2 blocks

                using (FileStream stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite))
                {
                    GenericBlockDevice device = new GenericBlockDevice(descriptor);
                    device.Mount(stream, readOnly: false);
                    CbmDosFileSystem fs = new CbmDosFileSystem(dirTrack, bamTrack);
                    fs.Initialize(device);

                    fs.Format("TESTDISK", "01");
                    Assert.That(fs.GetBlocksFree(), Is.EqualTo(freshFree), "freshly formatted free count");
                    Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(0), "formatted disk is empty");

                    fs.WriteFile("HELLO", content);
                    Assert.That(fs.ReadFile("HELLO"), Is.EqualTo(content));
                    device.Dispose();
                }

                string? c1541 = FindC1541();
                if (c1541 != null)
                {
                    (int listCode, string listing) = RunC1541(c1541, $"-attach \"{temp}\" -list");
                    Assert.That(listCode, Is.EqualTo(0), "c1541 must accept our code-formatted image");
                    Assert.That(listing.ToLowerInvariant(), Does.Contain("hello"));
                    Assert.That(listing, Does.Contain($"{freshFree - 2} blocks free"));

                    (int readCode, _) = RunC1541(c1541, $"-attach \"{temp}\" -read HELLO \"{extracted}\"");
                    Assert.That(readCode, Is.EqualTo(0));
                    Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(content));
                }
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

        [Test]
        public void D71_Format_ProducesEmptyImage_Writable_ReadableByC1541()
        {
            RunFormatTest(BlockDeviceDescriptor.D71, 18, 18, 349696, 1328, ".d71");
        }

        [Test]
        public void D81_Format_ProducesEmptyImage_Writable_ReadableByC1541()
        {
            RunFormatTest(BlockDeviceDescriptor.D81, 40, 40, 819200, 3160, ".d81");
        }

        // Overwriting an existing file replaces its content (grow then shrink), keeps a single
        // directory entry, accounts for the freed/allocated blocks, and stays c1541-readable.
        [Test]
        public void D64_Overwrite_ReplacesContent_ReadableByC1541()
        {
            string? blank = BlankPath("d64", "blank.d64");
            if (blank == null)
            {
                Assert.Ignore("blank.d64 fixture not present");
                return;
            }

            string temp = CopyToTemp(blank, ".d64");
            string extracted = temp + ".out";
            try
            {
                byte[] first = Seeded(1, 300);   // 2 blocks
                byte[] grown = Seeded(2, 700);   // 3 blocks, different content
                byte[] shrunk = Seeded(3, 100);  // 1 block, different again

                using (FileStream stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite))
                {
                    GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.D64_35Track);
                    device.Mount(stream, readOnly: false);
                    CbmDosFileSystem fs = new CbmDosFileSystem(18, 18);
                    fs.Initialize(device);

                    fs.WriteFile("HELLO", first);
                    Assert.That(fs.GetBlocksFree(), Is.EqualTo(662));

                    // Overwrite (grow): old 2 blocks freed, new 3 allocated.
                    fs.WriteFile("HELLO", grown);
                    Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(1), "still one entry");
                    Assert.That(fs.GetBlocksFree(), Is.EqualTo(661));
                    Assert.That(fs.ReadFile("HELLO"), Is.EqualTo(grown), "content replaced (grow)");

                    // Overwrite (shrink): old 3 freed, new 1 allocated.
                    fs.WriteFile("HELLO", shrunk);
                    Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(1));
                    Assert.That(fs.GetBlocksFree(), Is.EqualTo(663));
                    Assert.That(fs.ReadFile("HELLO"), Is.EqualTo(shrunk), "content replaced (shrink)");

                    device.Dispose();
                }

                string? c1541 = FindC1541();
                if (c1541 != null)
                {
                    (int listCode, string listing) = RunC1541(c1541, $"-attach \"{temp}\" -list");
                    Assert.That(listCode, Is.EqualTo(0));
                    Assert.That(listing.ToLowerInvariant(), Does.Contain("hello"));
                    Assert.That(listing, Does.Contain("663 blocks free"), "final state = 1 block used");

                    (int readCode, _) = RunC1541(c1541, $"-attach \"{temp}\" -read HELLO \"{extracted}\"");
                    Assert.That(readCode, Is.EqualTo(0));
                    Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(shrunk),
                        "c1541 reads the final overwritten content");
                }
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
    }
}
