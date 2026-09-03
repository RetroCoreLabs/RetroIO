//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Local;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.FFS.Structures;

namespace RetroIO.Tests
{
    [TestFixture]
    public class FfsDetectionTests
    {
        private const string SunDiskImagePath = @"D:\ND\HDD\sun2-sunos-3.2.img";

        [Test]
        [Category("FFS")]
        [Category("Detection")]
        public void DetectFfs_ValidSunOSImage_ReturnsHighConfidence()
        {
            // Arrange
            if (!File.Exists(SunDiskImagePath))
            {
                Assert.Ignore($"Test skipped: Sun disk image not found at {SunDiskImagePath}");
                return;
            }

            var driverFactory = new DriverFactory();
            driverFactory.Register(new FfsDriverProvider());
            driverFactory.Register(new V7DriverProvider());  // Also register BSD to compare

            // Act
            var confidences = driverFactory.GetDetectionConfidences(SunDiskImagePath, "FFS", "V7");

            // Assert
            TestContext.Out.WriteLine($"FFS confidence: {confidences["FFS"]:P0}");
            TestContext.Out.WriteLine($"V7 confidence: {confidences["V7"]:P0}");

            Assert.That(confidences["FFS"], Is.GreaterThan(0.5f),
                "FFS confidence should be > 50% for SunOS disk image");
        }

        [Test]
        [Category("FFS")]
        [Category("Detection")]
        public void DetectFfs_ValidSunOSImage_CreatesDriver()
        {
            // Arrange
            if (!File.Exists(SunDiskImagePath))
            {
                Assert.Ignore($"Test skipped: Sun disk image not found at {SunDiskImagePath}");
                return;
            }

            var driverFactory = new DriverFactory();
            driverFactory.Register(new FfsDriverProvider());

            // Act
            var driver = driverFactory.Detect(SunDiskImagePath);

            // Assert
            Assert.That(driver, Is.Not.Null, "FFS driver should be created for SunOS disk image");
            Assert.That(driver.FileSystemType, Does.StartWith("FFS"));

            TestContext.Out.WriteLine("SUCCESS: FFS/UFS detected and driver created");
        }

        [Test]
        [Category("FFS")]
        public void FfsSuperblock_ParseMagic_ValidFfsBigEndian()
        {
            // Arrange - Create a buffer with FFS magic (big-endian 0x011954) at offset 1372
            byte[] buffer = new byte[FfsConstants.SBSIZE];
            Array.Fill(buffer, (byte)0);

            // Write FFS magic in big-endian at offset 1372
            buffer[1372] = 0x00;
            buffer[1373] = 0x01;
            buffer[1374] = 0x19;
            buffer[1375] = 0x54;

            // Set minimum required fields for validation
            // fs_bsize = 8192 (big-endian)
            buffer[48] = 0x00;
            buffer[49] = 0x00;
            buffer[50] = 0x20;
            buffer[51] = 0x00;

            // fs_fsize = 1024 (big-endian)
            buffer[52] = 0x00;
            buffer[53] = 0x00;
            buffer[54] = 0x04;
            buffer[55] = 0x00;

            // fs_frag = 8 (big-endian)
            buffer[56] = 0x00;
            buffer[57] = 0x00;
            buffer[58] = 0x00;
            buffer[59] = 0x08;

            // fs_ncg = 1 (number of cylinder groups, offset 44, big-endian)
            buffer[44] = 0x00;
            buffer[45] = 0x00;
            buffer[46] = 0x00;
            buffer[47] = 0x01;

            // fs_ipg = 320 (inodes per group, offset 184, big-endian)
            buffer[184] = 0x00;
            buffer[185] = 0x00;
            buffer[186] = 0x01;
            buffer[187] = 0x40;

            // Act
            var sb = FfsSuperblock.Parse(buffer);

            // Assert
            Assert.That(sb, Is.Not.Null, "Should parse valid FFS magic");
            Assert.That(sb.fs_magic, Is.EqualTo(FfsConstants.FS_MAGIC));
            Assert.That(sb.Validate(), Is.True);
        }

        [Test]
        [Category("FFS")]
        public void FfsSuperblock_ParseMagic_InvalidMagic_FailsValidation()
        {
            // Arrange - Create a buffer with invalid magic
            byte[] buffer = new byte[FfsConstants.SBSIZE];
            Array.Fill(buffer, (byte)0xFF);

            // Act
            var sb = FfsSuperblock.Parse(buffer);

            // Assert
            Assert.That(sb, Is.Not.Null, "Parse should not return null");
            Assert.That(sb.Validate(), Is.False, "Validation should fail for invalid magic");
        }

        [Test]
        [Category("FFS")]
        public void FfsSuperblock_ParseMagic_BufferTooSmall_ThrowsException()
        {
            // Arrange - Create a buffer that's too small
            byte[] buffer = new byte[100];

            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
            {
                var sb = FfsSuperblock.Parse(buffer);
            }, "Should throw for buffer too small");
        }
    }
}
