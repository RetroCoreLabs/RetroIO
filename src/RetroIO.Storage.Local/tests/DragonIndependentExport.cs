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
    }
}
