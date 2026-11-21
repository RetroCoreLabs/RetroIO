//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Tests;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// ImageDisk (.IMD) images: the container on its own, and CP/M disks inside it (Osborne 1 and
    /// IBM PC CP/M-86).
    /// </summary>
    [TestFixture]
    public class ImdCpmTests
    {
        private static string Image(string folder, string name) => TestData.FromVariable(TestData.TestImagesVariable, Path.Combine("cpm", folder, name));

        // The CP/M-86 disk comes both as ImageDisk and as a raw dump; the two must be the same bytes.
        [Test]
        public void Cpm86Imd_DecodesToTheSameBytesAsTheRawDump()
        {
            byte[] imd = File.ReadAllBytes(RequireImage(Image("cpm86", "CPM86_10.IMD")));
            byte[] raw = File.ReadAllBytes(RequireImage(Image("cpm86", "CPM86_10.IMG")));
            ImdDiskImage image = ImdDiskImage.Decode(imd);
            Assert.That(image.Flat, Is.EqualTo(raw));
            Assert.That(image.Comment, Does.Contain("CP/m-86"));
        }

        [TestCase("cpm86", "CPM86_10.IMD")]
        [TestCase("osborne", "OCC1-001.IMD")]
        [TestCase("osborne", "OCC1-005.IMD")]
        public void Encode_UnchangedImage_GivesBackTheSameFile(string folder, string name)
        {
            byte[] file = File.ReadAllBytes(RequireImage(Image(folder, name)));
            Assert.That(ImdDiskImage.Decode(file).Encode(), Is.EqualTo(file));
        }

        [Test]
        public void Decode_FileCutShort_SaysWhere()
        {
            byte[] file = File.ReadAllBytes(RequireImage(Image("osborne", "OCC1-001.IMD")));
            byte[] cut = file.AsSpan(0, file.Length - 100).ToArray();
            Assert.That(ImdDiskImage.TryDecode(cut, out string? error), Is.Null);
            Assert.That(error, Does.Contain("ends in the middle"));
        }

        [TestCase("OCC1-001.IMD", "ASM.COM")]
        [TestCase("OCC1-002.IMD", "BASCOM.COM")]
        [TestCase("OCC1-005.IMD", "PCFILE.COM")]
        public void OsborneDisk_IsDetectedAsCpm(string name, string expectedFile)
        {
            string path = RequireImage(Image("osborne", name));
            DriverFactory factory = new DriverFactory();
            factory.Register(new CpmDriverProvider());
            factory.Register(new PcFloppyDriverProvider());
            IFileSystemDriver driver = factory.Detect(path)!;
            Assert.That(driver.FileSystemType, Is.EqualTo("CPM"));
            using FileStream stream = File.OpenRead(path);
            driver.Mount(stream, readOnly: true);
            Assert.That(driver.GetMetadata("/" + expectedFile), Is.Not.Null, expectedFile);
        }

        [Test]
        public void OsborneDisk_Write_IsSavedAsImageDiskAndReadsBack()
        {
            byte[] original = File.ReadAllBytes(RequireImage(Image("osborne", "OCC1-002.IMD")));
            using MemoryStream stream = new MemoryStream();
            stream.Write(original, 0, original.Length);
            CpmDriver writer = new CpmDriver();
            writer.Mount(stream, readOnly: false);
            byte[] text = Encoding.ASCII.GetBytes("WRITTEN BY RETROIO INTO AN IMAGEDISK FILE\r\n");
            writer.WriteFile("/NOTE.TXT", text);

            byte[] saved = stream.ToArray();
            Assert.That(ImdDiskImage.IsImd(saved), Is.True, "still an ImageDisk file");
            CpmDriver reader = new CpmDriver();
            using MemoryStream again = new MemoryStream(saved, writable: false);
            reader.Mount(again, readOnly: true);
            Assert.That(reader.ReadFile("/NOTE.TXT").AsSpan(0, text.Length).SequenceEqual(text), Is.True);
            Assert.That(reader.ReadFile("/BASCOM.COM"), Is.EqualTo(MountRead(original, "/BASCOM.COM")), "the other files are unchanged");
        }

        private static byte[] MountRead(byte[] image, string path)
        {
            CpmDriver driver = new CpmDriver();
            using MemoryStream stream = new MemoryStream(image, writable: false);
            driver.Mount(stream, readOnly: true);
            return driver.ReadFile(path);
        }

        private static string RequireImage(string path)
        {
            if (!File.Exists(path))
            {
                Assert.Ignore("ImageDisk test image not found: " + path + " (see the README, CP/M test images)");
            }

            return path;
        }
    }
}
