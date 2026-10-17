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

namespace RetroIO.Tests.VDK
{
    [TestFixture]
    public class DragonDskDriverTests
    {
        [Test]
        public void Mount_NoHeader_ShouldDetectGeometry()
        {
            // Create 40-track single-sided DSK with no header (exact multiple of 256)
            byte[] dskData = CreateDskImage(40, 1, 0);

            using (MemoryStream stream = new MemoryStream(dskData))
            {
                DragonDskDriver driver = new DragonDskDriver();
                driver.Mount(stream, readOnly: true);

                Assert.That(driver.FileSystemType, Is.EqualTo("DragonDOS"));
            }
        }

        [Test]
        public void Mount_WithHeader_ShouldParseGeometry()
        {
            // Create 40-track single-sided DSK with 5-byte header
            byte[] header = new byte[] { 18, 1, 1, 1, 0 };  // SPT=18, Sides=1, SizeCode=1, FirstSector=1, Attr=0
            byte[] dskData = CreateDskImage(40, 1, header.Length);
            Array.Copy(header, 0, dskData, 0, header.Length);

            using (MemoryStream stream = new MemoryStream(dskData))
            {
                DragonDskDriver driver = new DragonDskDriver();
                driver.Mount(stream, readOnly: true);

                Assert.That(driver.FileSystemType, Is.EqualTo("DragonDOS"));
            }
        }

        [Test]
        public void Mount_DoubleSided_ShouldSucceed()
        {
            // Create 80-track double-sided DSK with 2-byte header
            byte[] header = new byte[] { 18, 2 };  // SPT=18, Sides=2
            byte[] dskData = CreateDskImage(80, 2, header.Length);
            Array.Copy(header, 0, dskData, 0, header.Length);

            using (MemoryStream stream = new MemoryStream(dskData))
            {
                DragonDskDriver driver = new DragonDskDriver();
                driver.Mount(stream, readOnly: true);

                Assert.That(driver.FileSystemType, Is.EqualTo("DragonDOS"));
            }
        }

        [Test]
        public void Mount_CoCo35Track_ShouldSucceed()
        {
            // Create 35-track single-sided DSK (CoCo standard) with no header
            // 35-track disks are CoCo (Color Computer) format, which uses DECB, not DragonDOS
            byte[] dskData = CreateDskImage(35, 1, 0);

            using (MemoryStream stream = new MemoryStream(dskData))
            {
                DragonDskDriver driver = new DragonDskDriver();
                driver.Mount(stream, readOnly: true);

                // CoCo 35-track disks use DECB (Disk Extended Color BASIC), not DragonDOS
                Assert.That(driver.FileSystemType, Is.EqualTo("DECB"));
            }
        }

        [Test]
        public void DetectConfidence_DskExtension_ShouldReturnHigh()
        {
            DragonDskDriverProvider provider = new DragonDskDriverProvider();

            DetectionHints hints = new DetectionHints
            {
                FileExtension = ".dsk",
                FileSize = 184320  // 40T × 18SPT × 256 bytes (no header)
            };

            float confidence = provider.DetectConfidence(hints);
            Assert.That(confidence, Is.GreaterThan(0.4f));
        }

        [Test]
        public void DetectConfidence_JvcExtension_ShouldReturnHigh()
        {
            DragonDskDriverProvider provider = new DragonDskDriverProvider();

            DetectionHints hints = new DetectionHints
            {
                FileExtension = ".jvc",
                FileSize = 184325  // 40T × 18SPT × 256 bytes + 5-byte header
            };

            float confidence = provider.DetectConfidence(hints);
            Assert.That(confidence, Is.GreaterThan(0.4f));
        }

        [Test]
        public void DetectConfidence_VdkSignature_ShouldReturnZero()
        {
            // Should not detect VDK files (those are handled by DragonVdkDriver)
            DragonDskDriverProvider provider = new DragonDskDriverProvider();

            DetectionHints hints = new DetectionHints
            {
                FileExtension = ".vdk",
                FileSize = 184332,
                HeaderBytes = new byte[] { 0x64, 0x6B, 0x0C, 0x00 }  // "dk" signature
            };

            float confidence = provider.DetectConfidence(hints);
            Assert.That(confidence, Is.EqualTo(0.0f));
        }

        // Helper method to create test DSK image
        private byte[] CreateDskImage(int tracks, byte sides, int headerSize)
        {
            int sectorsPerTrack = 18;
            int sectorSize = 256;
            int dataSize = tracks * sides * sectorsPerTrack * sectorSize;
            int totalSize = headerSize + dataSize;

            byte[] image = new byte[totalSize];

            // Fill with test data (sector pattern)
            for (int i = headerSize; i < totalSize; i += sectorSize)
            {
                int sectorNum = (i - headerSize) / sectorSize;
                byte fillByte = (byte)(sectorNum % 256);
                for (int j = 0; j < sectorSize && (i + j) < totalSize; j++)
                {
                    image[i + j] = fillByte;
                }
            }

            return image;
        }
    }
}
