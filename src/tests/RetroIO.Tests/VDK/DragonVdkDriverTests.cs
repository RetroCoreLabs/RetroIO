//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Tests;

namespace RetroIO.Tests.VDK
{
    [TestFixture]
    public class DragonVdkDriverTests
    {
        [Test]
        public void Mount_ValidVdkImage_ShouldSucceed()
        {
            string testImagePath = TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\vdk\PLANETFALL.VDK");

            if (!File.Exists(testImagePath))
            {
                Assert.Ignore($"Test image not found: {testImagePath}");
            }

            IFileSystemDriver driver = new DragonVdkDriver();

            using (FileStream stream = File.OpenRead(testImagePath))
            {
                Assert.DoesNotThrow(() => driver.Mount(stream, readOnly: true),
                    "VDK mount should not throw exception");

                // PLANETFALL.VDK is a CoCo DECB disk of 68 granules in a 40 track image (see DragonDosNonDosDiskTests).
                Assert.That(driver.FileSystemType, Is.EqualTo("DECB"));
                Assert.That(driver.VolumeLabel, Is.Not.Null);
            }

            driver.Dispose();
        }

        [Test]
        public void Mount_ValidVdkImage_ShouldListFiles()
        {
            // 6809TUT1.VDK is a DragonDOS disk. PLANETFALL.VDK is not: its sector 0 holds 6809 boot code and it is a DECB disk with one file (see DragonDosNonDosDiskTests).
            string testImagePath = TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\vdk\6809TUT1.VDK");

            if (!File.Exists(testImagePath))
            {
                Assert.Ignore($"Test image not found: {testImagePath}");
            }

            IFileSystemDriver driver = new DragonVdkDriver();

            using (FileStream stream = File.OpenRead(testImagePath))
            {
                driver.Mount(stream, readOnly: true);

                DirectoryEntry[] entries = driver.ListDirectory("/");

                Assert.That(entries, Is.Not.Null, "Directory listing should not be null");
                Assert.That(entries.Length, Is.GreaterThan(0), "VDK image should contain files");

                Console.WriteLine($"Found {entries.Length} files:");
                for (int i = 0; i < Math.Min(5, entries.Length); i++)
                {
                    DirectoryEntry entry = entries[i];
                    Console.WriteLine($"  - {entry.Name} ({entry.Metadata.Size} bytes)");
                }
            }

            driver.Dispose();
        }

        [Test]
        public void Mount_InvalidSignature_ShouldThrowException()
        {
            byte[] invalidData = new byte[200000];
            for (int i = 0; i < invalidData.Length; i++)
            {
                invalidData[i] = 0xFF;
            }

            invalidData[0] = 0x00;
            invalidData[1] = 0x00;

            IFileSystemDriver driver = new DragonVdkDriver();

            using (MemoryStream stream = new MemoryStream(invalidData))
            {
                InvalidDataException ex = Assert.Throws<InvalidDataException>(
                    () => driver.Mount(stream, readOnly: true));

                Assert.That(ex.Message, Does.Contain("VDK"));
            }

            driver.Dispose();
        }

        [Test]
        public void Mount_ExtendedHeader_ShouldReadCorrectly()
        {
            byte[] vdkData = CreateVdkImage(headerSize: 256, tracks: 40, sides: 1);

            IFileSystemDriver driver = new DragonVdkDriver();

            using (MemoryStream stream = new MemoryStream(vdkData))
            {
                Assert.DoesNotThrow(() => driver.Mount(stream, readOnly: true),
                    "VDK with 256-byte header should mount successfully");

                Assert.That(driver.FileSystemType, Is.EqualTo("DragonDOS"));
            }

            driver.Dispose();
        }

        [Test]
        public void DetectConfidence_VdkExtension_ShouldReturnHigh()
        {
            DragonVdkDriverProvider provider = new DragonVdkDriverProvider();

            DetectionHints hints = new DetectionHints
            {
                FileExtension = ".vdk",
                FileSize = 184320
            };

            float confidence = provider.DetectConfidence(hints);

            Assert.That(confidence, Is.GreaterThan(0f),
                "VDK extension with valid size should have positive confidence");
        }

        [Test]
        public void DetectConfidence_WrongExtension_ShouldReturnLow()
        {
            DragonVdkDriverProvider provider = new DragonVdkDriverProvider();

            DetectionHints hints = new DetectionHints
            {
                FileExtension = ".d64",
                FileSize = 184320
            };

            float confidence = provider.DetectConfidence(hints);

            Assert.That(confidence, Is.LessThan(50f),
                "Wrong extension should have low confidence");
        }

        private byte[] CreateVdkImage(ushort headerSize, byte tracks, byte sides)
        {
            long dataSize = tracks * sides * 18 * 256;
            long totalSize = headerSize + dataSize;

            byte[] image = new byte[totalSize];

            image[0] = 0x64;
            image[1] = 0x6B;
            image[2] = (byte)(headerSize & 0xFF);
            image[3] = (byte)((headerSize >> 8) & 0xFF);
            image[4] = 0x10;
            image[5] = 0x10;
            image[6] = 0x58;
            image[7] = 0x00;
            image[8] = tracks;
            image[9] = sides;
            image[10] = 0x00;
            image[11] = 0x00;

            int bamOffset = headerSize + (20 * (int)sides * 18 * 256);
            if (bamOffset + 512 <= totalSize)
            {
                // DragonDOS geometry bytes (dosdefs.asm DirTracks): tracks, sectors per track,
                // complement of tracks, complement of sectors per track, at $FC to $FF.
                // DragonDOS has no volume name, so none is written.
                image[bamOffset + 0xFC] = tracks;
                image[bamOffset + 0xFD] = (byte)(sides * 18);
                image[bamOffset + 0xFE] = (byte)(~tracks & 0xFF);
                image[bamOffset + 0xFF] = (byte)(~(sides * 18) & 0xFF);
            }

            return image;
        }
    }
}
