//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Writes Dragon disk images through the drivers, plus a manifest of what each file must contain, so that an independent program can read
    /// them back. Not a normal test: it does nothing unless RETROIO_DRAGON_EXPORT_FOLDER is set, and it asserts nothing about the images.
    /// </summary>
    /// <remarks>
    /// The independent reader is the real DragonDOS 1.0 ROM running in the XRoar emulator (and Toolshed for DECB and OS-9 disks). The manifest
    /// is a text file, manifest.txt, with one line per check: image file name, a tab, the file name on the disk, a tab, and either the name of a
    /// file in the expected folder that holds the bytes the file must have, or a minus sign when the file must not be on the disk.
    /// </remarks>
    [TestFixture]
    [Explicit("Writes images for the independent read-back check; set RETROIO_DRAGON_EXPORT_FOLDER")]
    public class DragonIndependentExport
    {
        private string _folder = string.Empty;
        private StringBuilder _manifest = new StringBuilder();
        private int _expectedCount;

        private static string? ImagesRoot()
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable("RETROIO_DRAGON_TEST_IMAGES");
            return !string.IsNullOrEmpty(fromEnvironment)
                ? fromEnvironment
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "RetroFS", "test-images");
        }

        [SetUp]
        public void SetUp()
        {
            string? folder = Environment.GetEnvironmentVariable("RETROIO_DRAGON_EXPORT_FOLDER");
            if (string.IsNullOrEmpty(folder))
            {
                Assert.Ignore("RETROIO_DRAGON_EXPORT_FOLDER is not set");
            }

            _folder = folder!;
            Directory.CreateDirectory(Path.Combine(_folder, "expected"));
            _manifest = new StringBuilder();
            _expectedCount = 0;
        }

        private void Expect(string image, string name, byte[] data)
        {
            string file = "e" + _expectedCount.ToString("D4") + ".bin";
            _expectedCount++;
            File.WriteAllBytes(Path.Combine(_folder, "expected", file), data);
            _manifest.Append(image).Append('\t').Append(name).Append('\t').Append(file).Append('\n');
        }

        private void ExpectAbsent(string image, string name)
        {
            _manifest.Append(image).Append('\t').Append(name).Append('\t').Append('-').Append('\n');
        }

        private string Path_(string name)
        {
            return Path.Combine(_folder, name);
        }

        private static byte[] Join(byte[] a, byte[] b)
        {
            byte[] all = new byte[a.Length + b.Length];
            Array.Copy(a, all, a.Length);
            Array.Copy(b, 0, all, a.Length, b.Length);
            return all;
        }

        private static byte[] Header12(int tracks, int heads)
        {
            return new byte[] { (byte)'d', (byte)'k', 12, 0, 0x10, 0x10, (byte)'X', 0x10, (byte)tracks, (byte)heads, 0, 0 };
        }

        private void WriteVdk(string name, int tracks, int heads, Action<IFileSystemDriver> work)
        {
            File.WriteAllBytes(Path_(name), Join(Header12(tracks, heads), DragonDosTestDisk.Format(tracks, heads)));
            using (FileStream stream = new FileStream(Path_(name), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                DragonVdkDriver driver = new DragonVdkDriver();
                driver.Mount(stream, false);
                work(driver);
                driver.Dispose();
            }
        }

        private void WriteDsk(string name, byte[] header, int tracks, int heads, Action<IFileSystemDriver> work)
        {
            File.WriteAllBytes(Path_(name), Join(header, DragonDosTestDisk.Format(tracks, heads)));
            using (FileStream stream = new FileStream(Path_(name), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                DragonDskDriver driver = new DragonDskDriver();
                driver.Mount(stream, false);
                work(driver);
                driver.Dispose();
            }
        }

        [Test]
        public void Export()
        {
            // basic.vdk: sizes around the sector border, no extension, overwrite, delete, rename.
            WriteVdk("basic.vdk", 40, 1, driver =>
            {
                int[] sizes = { 1, 255, 256, 257, 512, 513, 2000, 10000 };
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte[] data = DragonDosTestDisk.Pattern(sizes[i], i);
                    driver.WriteFile("S" + sizes[i] + ".BIN", data);
                    Expect("basic.vdk", "S" + sizes[i] + ".BIN", data);
                }

                byte[] readme = DragonDosTestDisk.Pattern(700, 40);
                driver.WriteFile("README", readme);
                Expect("basic.vdk", "README", readme);

                driver.WriteFile("OVER.DAT", DragonDosTestDisk.Pattern(5000, 50));
                driver.WriteFile("OVER.DAT", DragonDosTestDisk.Pattern(300, 51));
                byte[] last = DragonDosTestDisk.Pattern(2600, 52);
                driver.WriteFile("OVER.DAT", last);
                Expect("basic.vdk", "OVER.DAT", last);

                driver.WriteFile("GONE.TXT", DragonDosTestDisk.Pattern(900, 60));
                driver.DeleteFile("GONE.TXT");
                ExpectAbsent("basic.vdk", "GONE.TXT");

                byte[] moved = DragonDosTestDisk.Pattern(1234, 61);
                driver.WriteFile("OLDNAME.TXT", moved);
                driver.RenameFile("OLDNAME.TXT", "NEWNAME.TXT");
                Expect("basic.vdk", "NEWNAME.TXT", moved);
                ExpectAbsent("basic.vdk", "OLDNAME.TXT");
            });

            // frag.vdk: a file in 20 extents, so the directory has three continuation entries.
            WriteVdk("frag.vdk", 40, 1, driver =>
            {
                for (int i = 0; i < 40; i++)
                {
                    driver.WriteFile("F" + i.ToString("D2"), DragonDosTestDisk.Pattern(256, i));
                }

                for (int i = 1; i < 40; i += 2)
                {
                    driver.DeleteFile("F" + i.ToString("D2"));
                }

                byte[] big = DragonDosTestDisk.Pattern((19 * 256) + 100, 77);
                driver.WriteFile("BIGFILE.DAT", big);
                Expect("frag.vdk", "BIGFILE.DAT", big);
                Expect("frag.vdk", "F00", DragonDosTestDisk.Pattern(256, 0));
                Expect("frag.vdk", "F38", DragonDosTestDisk.Pattern(256, 38));
                ExpectAbsent("frag.vdk", "F01");
            });

            // frag2.vdk: 9 extents (one continuation entry) and an exact 4 extent file next to them.
            WriteVdk("frag2.vdk", 40, 1, driver =>
            {
                for (int i = 0; i < 40; i++)
                {
                    driver.WriteFile("F" + i.ToString("D2"), DragonDosTestDisk.Pattern(256, i));
                }

                for (int i = 1; i < 40; i += 2)
                {
                    driver.DeleteFile("F" + i.ToString("D2"));
                }

                byte[] nine = DragonDosTestDisk.Pattern(9 * 256, 78);
                driver.WriteFile("NINE.DAT", nine);
                Expect("frag2.vdk", "NINE.DAT", nine);
            });

            // ds.vdk: double sided, 36 sector tracks.
            WriteVdk("ds.vdk", 40, 2, driver =>
            {
                byte[] a = DragonDosTestDisk.Pattern(9000, 1);
                byte[] b = DragonDosTestDisk.Pattern(300, 2);
                driver.WriteFile("A.BIN", a);
                driver.WriteFile("B.BIN", b);
                driver.RenameFile("B.BIN", "C.BIN");
                Expect("ds.vdk", "A.BIN", a);
                Expect("ds.vdk", "C.BIN", b);
                ExpectAbsent("ds.vdk", "B.BIN");
            });

            // ds80.dsk: JVC header, 80 tracks double sided, a file past the first 1440 sectors (the second BAM sector).
            WriteDsk("ds80.dsk", new byte[] { 18, 2, 1, 1, 0 }, 80, 2, driver =>
            {
                driver.WriteFile("FILLER", new byte[1400 * 256]);
                byte[] second = DragonDosTestDisk.Pattern(8000, 3);
                driver.WriteFile("SECOND.BIN", second);
                Expect("ds80.dsk", "SECOND.BIN", second);
            });

            // jvc.dsk: no header, 40 tracks single sided.
            WriteDsk("jvc.dsk", Array.Empty<byte>(), 40, 1, driver =>
            {
                byte[] d = DragonDosTestDisk.Pattern(4321, 4);
                driver.WriteFile("HELLO.BIN", d);
                Expect("jvc.dsk", "HELLO.BIN", d);
            });

            // DMK: write into copies of the real FEdit disks.
            string[] dmks = { "FEdit.dmk", "FEdit_40_2.dmk", "FEdit_80_1.dmk", "FEdit_80_2.dmk" };
            for (int i = 0; i < dmks.Length; i++)
            {
                string source = Path.Combine(ImagesRoot()!, "dmk", dmks[i]);
                if (!File.Exists(source))
                {
                    continue;
                }

                string copy = "w_" + dmks[i];
                File.Copy(source, Path_(copy), true);
                byte[] data = DragonDosTestDisk.Pattern(4000 + i, 9);
                byte[] old;
                using (FileStream stream = new FileStream(Path_(copy), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    DragonDmkDriver driver = new DragonDmkDriver();
                    driver.Mount(stream, false);
                    old = driver.ReadFile("INTRO.DOC");
                    driver.WriteFile("NEWFILE.BIN", data);
                    driver.WriteFile("TEMP.TXT", new byte[] { 1, 2, 3 });
                    driver.DeleteFile("TEMP.TXT");
                    driver.Dispose();
                }

                Expect(copy, "NEWFILE.BIN", data);
                Expect(copy, "INTRO.DOC", old);
                ExpectAbsent(copy, "TEMP.TXT");
            }

            File.WriteAllText(Path_("manifest.txt"), _manifest.ToString());
        }

        /// <summary>
        /// Reads disks that the real DragonDOS ROM wrote (in the XRoar emulator), then changes a copy of one with our code and exports it, so
        /// that the ROM can read the change. Needs RETROIO_DRAGON_XROAR_MADE, the folder with the disks (simple.vdk and frag.vdk).
        /// </summary>
        /// <remarks>
        /// The 6809 program that made the disks calls the DragonDOS jump table: create, write, close, delete, sync directory. Every file it wrote
        /// holds the same pattern, the bytes of the DragonDOS test pattern (seed 0) cut to the size. frag.vdk was filled with 114 files of 6 sectors,
        /// every second one deleted, and then BIG.DAT (20000 bytes) was written into the gaps, so the DOS itself made a file with a continuation
        /// entry.
        /// </remarks>
        // What the 6809 program wrote for a file of this size: it wrote the same 8 KB buffer (the pattern, seed 0) again and again.
        private static byte[] MadeByDos(int size)
        {
            byte[] block = DragonDosTestDisk.Pattern(8192, 0);
            byte[] data = new byte[size];
            for (int i = 0; i < size; i++)
            {
                data[i] = block[i % 8192];
            }

            return data;
        }

        [Test]
        public void ReadAndEditDisksMadeByTheRealDos()
        {
            string? made = Environment.GetEnvironmentVariable("RETROIO_DRAGON_XROAR_MADE");
            if (string.IsNullOrEmpty(made))
            {
                Assert.Ignore("RETROIO_DRAGON_XROAR_MADE is not set");
            }

            // simple.vdk
            using (FileStream stream = File.OpenRead(Path.Combine(made!, "simple.vdk")))
            {
                DragonVdkDriver driver = new DragonVdkDriver();
                driver.Mount(stream, true);
                int[] sizes = { 1000, 5000, 0, 256, 257, 20000 };
                string[] names = { "AAA.BIN", "BBB.BIN", "EMPTY.DAT", "SECT.DAT", "SECT1.DAT", "BIG.DAT" };
                Assert.That(driver.ListDirectory("/").Length, Is.EqualTo(names.Length), "TMP.DAT was deleted by the DOS");
                for (int i = 0; i < names.Length; i++)
                {
                    Assert.That(driver.ReadFile(names[i]), Is.EqualTo(MadeByDos(sizes[i])), names[i]);
                }

                Assert.That(driver.GetMetadata("TMP.DAT"), Is.Null);
                driver.Dispose();
            }

            // frag.vdk: even numbered F files stay, the odd ones were deleted by the DOS, BIG.DAT fills the gaps.
            byte[] frag = File.ReadAllBytes(Path.Combine(made!, "frag.vdk"));
            using (MemoryStream stream = new MemoryStream(frag))
            {
                DragonVdkDriver driver = new DragonVdkDriver();
                driver.Mount(stream, true);
                int count = driver.ListDirectory("/").Length;
                Assert.That(count, Is.EqualTo(57 + 3), "57 even F files, BIG.DAT, EMPTY.DAT and EXACT.DAT");
                for (int i = 0; i < 114; i += 2)
                {
                    Assert.That(driver.ReadFile("F" + i.ToString("D3")), Is.EqualTo(MadeByDos(1536)), "F" + i.ToString("D3"));
                }

                Assert.That(driver.GetMetadata("F001"), Is.Null);
                Assert.That(driver.ReadFile("BIG.DAT"), Is.EqualTo(MadeByDos(20000)), "BIG.DAT is in more than four extents");
                Assert.That(driver.ReadFile("EXACT.DAT"), Is.EqualTo(MadeByDos(2048)));
                Assert.That(driver.ReadFile("EMPTY.DAT"), Is.Empty);
                driver.Dispose();
            }

            // Which entries the DOS used: the number of continuation entries it made, from the raw bytes (attribute 01H, bit 7 clear).
            int continuations = 0;
            for (int n = 0; n < 160; n++)
            {
                int attribute = frag[12 + (((20 * 18) + 2 + (n / 10)) * 256) + ((n % 10) * 25)];
                if ((attribute & 0x81) == 0x01)
                {
                    continuations++;
                }
            }

            Assert.That(continuations, Is.GreaterThanOrEqualTo(1), "the real DOS made a continuation entry");
            TestContext.Out.WriteLine("frag.vdk, written by the real DOS: " + continuations + " continuation entries");

            // Edit a copy with our code: delete the file the DOS fragmented, write two new ones into the gaps, rename one. Then export it for the DOS to read.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RETROIO_DRAGON_EXPORT_FOLDER")))
            {
                return;
            }

            _folder = Environment.GetEnvironmentVariable("RETROIO_DRAGON_EXPORT_FOLDER")!;
            Directory.CreateDirectory(Path.Combine(_folder, "expected"));
            _manifest = new StringBuilder();
            File.WriteAllBytes(Path_("edited_frag.vdk"), frag);
            using (FileStream stream = new FileStream(Path_("edited_frag.vdk"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                DragonVdkDriver driver = new DragonVdkDriver();
                driver.Mount(stream, false);
                driver.DeleteFile("BIG.DAT");
                byte[] bigger = DragonDosTestDisk.Pattern(9000, 9);
                driver.WriteFile("NEWBIG.DAT", bigger);
                byte[] small = DragonDosTestDisk.Pattern(100, 3);
                driver.WriteFile("SMALL.TXT", small);
                driver.RenameFile("F000", "FIRST.TXT");
                Expect("edited_frag.vdk", "NEWBIG.DAT", bigger);
                Expect("edited_frag.vdk", "SMALL.TXT", small);
                Expect("edited_frag.vdk", "FIRST.TXT", MadeByDos(1536));
                Expect("edited_frag.vdk", "F002!", MadeByDos(1536));
                ExpectAbsent("edited_frag.vdk", "BIG.DAT");
                ExpectAbsent("edited_frag.vdk", "F000!");
                driver.Dispose();
            }

            File.WriteAllText(Path_("manifest2.txt"), _manifest.ToString());
        }
    }
}
