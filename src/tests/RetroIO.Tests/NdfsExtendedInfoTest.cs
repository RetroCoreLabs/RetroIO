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
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.FileSystems;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Tests
{
    [TestFixture]
    public class NdfsExtendedInfoTest
    {
        private static string SMD_100_IMAGE => TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\ndfs\SMD-ID-100.IMG");
        private static string SMD_102_IMAGE => TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\ndfs\SMD-ID-102.IMG");
        private static string WD0_IMAGE => TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\ndfs\WD0.img");
        private static string FLOPPY_360_IMAGE => TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\ndfs\FLOPPY-360KB.img");

        [Test]
        [Category("NDFS")]
        [Category("ExtendedInfo")]
        public void SMD100_HasValidExtendedInfo()
        {
            if (!File.Exists(SMD_100_IMAGE))
            {
                Assert.Ignore($"Test image not found: {SMD_100_IMAGE}");
                return;
            }

            using (var stream = new FileStream(SMD_100_IMAGE, FileMode.Open, FileAccess.Read))
            {
                var descriptor = NdfsBlockDeviceDescriptor.Create(stream.Length);
                var blockDevice = new GenericBlockDevice(descriptor);
                blockDevice.Mount(stream, readOnly: true);

                var ndfs = new NdfsFileSystem();
                ndfs.Initialize(blockDevice);

                // Act
                bool hasExtInfo = ndfs.HasValidExtendedInfo();
                var info = ndfs.GetExtendedMasterBlockInfo();

                // Assert
                Assert.That(hasExtInfo, Is.True, "SMD-ID-100 should have valid Extended Info");
                Assert.That(info, Is.Not.Null, "Extended Info should be returned");

                // Verify SMD-ID-100 specific values (from verification data)
                // The checksum is the kernel's additive sum of words 1751B-1757B (WXDIR / CHDSI); the
                // formula is asserted against the words on the image in ChecksumCalculation_MatchesExpectedValues.
                Assert.That(info["SystemNumber"], Is.EqualTo(100), "System number should be 100");
                Assert.That(info["Reserved1"], Is.EqualTo(0), "Reserved1 should be 0 (hard disk)");
                Assert.That(info["Reserved2"], Is.EqualTo(0), "Reserved2 should be 0 (hard disk)");
                Assert.That(info["Reserved3"], Is.EqualTo(0), "Reserved3 should be 0 (hard disk)");
                Assert.That(info["IsHardDisk"], Is.True, "Should be identified as hard disk");
                Assert.That(info["IsFloppy"], Is.False, "Should not be floppy");

                Console.WriteLine("=== SMD-ID-100 Extended Info ===");
                Console.WriteLine($"System Number: {info["SystemNumber"]}");
                Console.WriteLine($"Pages Available: {info["PagesAvailable"]}");
                Console.WriteLine($"Checksum (Stored): 0x{info["Checksum"]:X4}");
                Console.WriteLine($"Checksum (Calculated): 0x{info["ChecksumCalculated"]:X4}");
                Console.WriteLine($"Checksum Valid: {info["ChecksumValid"]}");
                Console.WriteLine($"Flag Word: 0x{info["FlagWord"]:X4}");
            }
        }

        [Test]
        [Category("NDFS")]
        [Category("ExtendedInfo")]
        public void SMD102_HasValidExtendedInfo()
        {
            if (!File.Exists(SMD_102_IMAGE))
            {
                Assert.Ignore($"Test image not found: {SMD_102_IMAGE}");
                return;
            }

            using (var stream = new FileStream(SMD_102_IMAGE, FileMode.Open, FileAccess.Read))
            {
                var descriptor = NdfsBlockDeviceDescriptor.Create(stream.Length);
                var blockDevice = new GenericBlockDevice(descriptor);
                blockDevice.Mount(stream, readOnly: true);

                var ndfs = new NdfsFileSystem();
                ndfs.Initialize(blockDevice);

                // Act
                var info = ndfs.GetExtendedMasterBlockInfo();

                // Assert
                Assert.That(info, Is.Not.Null);
                Assert.That(info["SystemNumber"], Is.EqualTo(102), "System number should be 102");
                Assert.That(info["IsHardDisk"], Is.True);

                Console.WriteLine("=== SMD-ID-102 Extended Info ===");
                Console.WriteLine($"System Number: {info["SystemNumber"]}");
                Console.WriteLine($"Checksum (Stored): 0x{info["Checksum"]:X4}");
                Console.WriteLine($"Checksum (Calculated): 0x{info["ChecksumCalculated"]:X4}");
                Console.WriteLine($"Checksum Valid: {info["ChecksumValid"]}");
            }
        }

        [Test]
        [Category("NDFS")]
        [Category("ExtendedInfo")]
        public void WD0_HasValidExtendedInfo()
        {
            if (!File.Exists(WD0_IMAGE))
            {
                Assert.Ignore($"Test image not found: {WD0_IMAGE}");
                return;
            }

            using (var stream = new FileStream(WD0_IMAGE, FileMode.Open, FileAccess.Read))
            {
                var descriptor = NdfsBlockDeviceDescriptor.Create(stream.Length);
                var blockDevice = new GenericBlockDevice(descriptor);
                blockDevice.Mount(stream, readOnly: true);

                var ndfs = new NdfsFileSystem();
                ndfs.Initialize(blockDevice);

                // Act
                var info = ndfs.GetExtendedMasterBlockInfo();

                // Assert
                Assert.That(info, Is.Not.Null);
                Assert.That(info["SystemNumber"], Is.EqualTo(0), "WD0 has system number 0");
                Assert.That(info["IsHardDisk"], Is.True);
                Assert.That(info["ChecksumValid"], Is.True, "Checksum should validate");

                Console.WriteLine("=== WD0 Extended Info ===");
                Console.WriteLine($"System Number: {info["SystemNumber"]}");
                Console.WriteLine($"Checksum (Stored): 0x{info["Checksum"]:X4}");
                Console.WriteLine($"Checksum (Calculated): 0x{info["ChecksumCalculated"]:X4}");
                Console.WriteLine($"Checksum Valid: {info["ChecksumValid"]}");
            }
        }

        [Test]
        [Category("NDFS")]
        [Category("ExtendedInfo")]
        public void Floppy360_DoesNotHaveValidExtendedInfo()
        {
            if (!File.Exists(FLOPPY_360_IMAGE))
            {
                Assert.Ignore($"Test image not found: {FLOPPY_360_IMAGE}");
                return;
            }

            using (var stream = new FileStream(FLOPPY_360_IMAGE, FileMode.Open, FileAccess.Read))
            {
                var descriptor = NdfsBlockDeviceDescriptor.Create(stream.Length);
                var blockDevice = new GenericBlockDevice(descriptor);
                blockDevice.Mount(stream, readOnly: true);

                var ndfs = new NdfsFileSystem();
                ndfs.Initialize(blockDevice);

                // Act
                bool hasExtInfo = ndfs.HasValidExtendedInfo();
                var info = ndfs.GetExtendedMasterBlockInfo();

                // Assert - Floppies have non-zero Reserved fields (FLOMON format)
                Assert.That(hasExtInfo, Is.False, "Floppy should NOT have valid Extended Info");
                Assert.That(info, Is.Null, "Extended Info should be null for floppy");

                Console.WriteLine("=== FLOPPY-360KB Extended Info ===");
                Console.WriteLine("Extended Info: Not available (FLOMON floppy disk)");
            }
        }

        [Test]
        [Category("NDFS")]
        [Category("ExtendedInfo")]
        public void ChecksumCalculation_MatchesExpectedValues()
        {
            // This test verifies the checksum formula against known good values
            if (!File.Exists(SMD_100_IMAGE))
            {
                Assert.Ignore($"Test image not found: {SMD_100_IMAGE}");
                return;
            }

            using (var stream = new FileStream(SMD_100_IMAGE, FileMode.Open, FileAccess.Read))
            {
                var descriptor = NdfsBlockDeviceDescriptor.Create(stream.Length);
                var blockDevice = new GenericBlockDevice(descriptor);
                blockDevice.Mount(stream, readOnly: true);

                var ndfs = new NdfsFileSystem();
                ndfs.Initialize(blockDevice);

                var info = ndfs.GetExtendedMasterBlockInfo();
                Assert.That(info, Is.Not.Null);

                // SMD-ID-100 verification:
                // PagesAvailable = 0x0051 (81 decimal, low word only)
                // System Number = 100
                // Reserved fields = 0
                // Flag Word = 0
                // The checksum is the kernel's plain 16-bit additive sum of words 1751B-1757B (WXDIR 37702B
                // writes it, CHDSI 37763B checks it); the words themselves are read from the image below.

                ushort stored = (ushort)info["Checksum"];
                ushort calculated = (ushort)info["ChecksumCalculated"];
                bool valid = (bool)info["ChecksumValid"];

                Console.WriteLine($"Stored Checksum: 0x{stored:X4} ({stored})");
                Console.WriteLine($"Calculated Checksum: 0x{calculated:X4} ({calculated})");
                Console.WriteLine($"Match: {valid}");

                Assert.That(valid, Is.True, "Checksum should validate");

                // The stored word must be the plain additive sum of the seven words as they are ON THIS
                // IMAGE (read back here, not assumed), which is the kernel formula; an XOR of the same
                // words would differ as soon as two of them have overlapping bits.
                uint pages = (uint)info["PagesAvailable"];
                ushort additive = MasterBlock.ComputeExtChecksum(
                    (ushort)info["Reserved3"], (ushort)info["Reserved2"], (ushort)info["Reserved1"],
                    (ushort)(uint)info["FlagWord"], (ushort)(uint)info["SystemNumber"],
                    (ushort)(pages >> 16), (ushort)(pages & 0xFFFF));
                Assert.That(stored, Is.EqualTo(additive), "the stored checksum is the additive sum of the words on the image");
            }
        }
    }
}
