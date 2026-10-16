//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Local;
using PartitionTableType = RetroIO.Storage.Core.PartitionTableType;

namespace RetroIO.Tests
{
    [TestFixture]
    public class BsdSunDiskDetectionTests
    {
        private const string SunDiskImagePath = @"D:\ND\HDD\sun2-sunos-3.2.img";

        [Test]
        [Category("SunOS")]
        public void DetectSunDisk_ValidSunOSImage_ReturnsHighConfidence()
        {
            // Arrange
            if (!File.Exists(SunDiskImagePath))
            {
                Assert.Ignore($"Test skipped: Sun disk image not found at {SunDiskImagePath}");
                return;
            }

            var driverFactory = new DriverFactory();
            driverFactory.Register(new RetroIO.Storage.Local.Drivers.FfsDriverProvider());  // Register FFS driver

            // Act
            var driver = driverFactory.Detect(SunDiskImagePath);

            // Assert
            Assert.That(driver, Is.Not.Null, "DriverFactory should detect the Sun disk image");
            Assert.That(driver.FileSystemType, Is.EqualTo("FFS-a"),
                "Sun disk should be detected as FFS filesystem (partition 'a')");

            // Clean up
            driver.Dispose();
        }

        [Test]
        [Category("SunOS")]
        public void DetectSunDisk_ValidSunOSImage_ConfidenceAbove90Percent()
        {
            // Arrange
            if (!File.Exists(SunDiskImagePath))
            {
                Assert.Ignore($"Test skipped: Sun disk image not found at {SunDiskImagePath}");
                return;
            }

            var driverFactory = new DriverFactory();
            driverFactory.Register(new RetroIO.Storage.Local.Drivers.FfsDriverProvider());  // Register FFS driver

            // Act
            var confidences = driverFactory.GetDetectionConfidences(
                SunDiskImagePath,
                "FFS",
                "NDFS",
                "CBM-DOS",
                "FAT12",
                "FAT16"
            );

            // Assert
            Assert.That(confidences.ContainsKey("FFS"), Is.True,
                "Confidence scores should include FFS");

            float ffsConfidence = confidences["FFS"];
            Assert.That(ffsConfidence, Is.GreaterThan(0.9f),
                $"FFS detection confidence should be above 90% for Sun disk. Actual: {ffsConfidence:P1}");

            // Log all confidence scores for debugging
            TestContext.Out.WriteLine("Detection confidence scores:");
            foreach (var kvp in confidences)
            {
                TestContext.Out.WriteLine($"  {kvp.Key}: {kvp.Value:P1}");
            }
        }

        [Test]
        [Category("SunOS")]
        public void MountSunDisk_ValidSunOSImage_SuccessfullyMounts()
        {
            // Arrange
            if (!File.Exists(SunDiskImagePath))
            {
                Assert.Ignore($"Test skipped: Sun disk image not found at {SunDiskImagePath}");
                return;
            }

            var driverFactory = new DriverFactory();
            driverFactory.Register(new RetroIO.Storage.Local.Drivers.FfsDriverProvider());  // Register FFS driver
            var driver = driverFactory.Detect(SunDiskImagePath);

            if (driver == null)
            {
                Assert.Fail("Could not detect Sun disk filesystem");
                return;
            }

            // Act & Assert
            try
            {
                using var stream = File.OpenRead(SunDiskImagePath);

                // Attempt to mount the filesystem
                Assert.DoesNotThrow(() => driver.Mount(stream, readOnly: true),
                    "Sun disk should mount without throwing exceptions");

                // Verify filesystem is accessible
                Assert.That(driver.FileSystemType, Is.EqualTo("FFS-a"),
                    "Sun disk partition 'a' contains FFS filesystem");

                TestContext.Out.WriteLine($"Successfully mounted Sun disk:");
                TestContext.Out.WriteLine($"  Filesystem type: {driver.FileSystemType}");
                TestContext.Out.WriteLine($"  Volume label: {driver.VolumeLabel}");
                TestContext.Out.WriteLine($"  Can read: {driver.CanRead}");
                TestContext.Out.WriteLine($"  Can write: {driver.CanWrite}");
            }
            finally
            {
                driver.Dispose();
            }
        }

        [Test]
        [Category("SunOS")]
        [Category("Partitions")]
        public void DetectSunDiskPartitions_ValidSunOSImage_DetectsPartitions()
        {
            // Arrange
            if (!File.Exists(SunDiskImagePath))
            {
                Assert.Ignore($"Test skipped: Sun disk image not found at {SunDiskImagePath}");
                return;
            }

            // Act
            var partitions = RetroIO.Storage.Local.Partitions.PartitionDetectionService.DetectPartitions(SunDiskImagePath);

            // Assert
            Assert.That(partitions, Is.Not.Null, "Partition detection should return a result");

            if (partitions.Count > 0)
            {
                TestContext.Out.WriteLine($"Found {partitions.Count} partition(s) in Sun disk:");

                for (int i = 0; i < partitions.Count; i++)
                {
                    var p = partitions[i];
                    double sizeMB = (p.SizeSectors * 512.0) / (1024.0 * 1024.0);

                    TestContext.Out.WriteLine($"\nPartition {i + 1}:");
                    TestContext.Out.WriteLine($"  ID: {p.Id}");
                    TestContext.Out.WriteLine($"  Display: {p.DisplayText}");
                    TestContext.Out.WriteLine($"  Offset: {p.OffsetSectors} sectors");
                    TestContext.Out.WriteLine($"  Size: {p.SizeSectors} sectors ({sizeMB:F1} MB)");
                    TestContext.Out.WriteLine($"  Type Code: 0x{p.FileSystemTypeCode:X2}");
                    TestContext.Out.WriteLine($"  Table Type: {p.TableType}");
                    TestContext.Out.WriteLine($"  Recommended URN: {p.RecommendedUrn}");

                    if (p.IsBootable)
                    {
                        TestContext.Out.WriteLine($"  *** BOOTABLE ***");
                    }
                }

                // Verify partition table type
                bool isSunLabel = partitions[0].TableType.ToString() == "SunLabel";
                Assert.That(isSunLabel, Is.True,
                    "Sun disk should use Sun disk label partition table");
            }
            else
            {
                TestContext.Out.WriteLine("No partition table detected in Sun disk image.");
                TestContext.Out.WriteLine("This may be a raw filesystem without partitions.");
            }
        }

        [Test]
        [Category("SunOS")]
        [Category("Partitions")]
        public void MountSunDiskPartition_ValidPartition_SuccessfullyMounts()
        {
            // Arrange
            if (!File.Exists(SunDiskImagePath))
            {
                Assert.Ignore($"Test skipped: Sun disk image not found at {SunDiskImagePath}");
                return;
            }

            var partitions = RetroIO.Storage.Local.Partitions.PartitionDetectionService.DetectPartitions(SunDiskImagePath);

            if (partitions.Count == 0)
            {
                Assert.Ignore("No partitions detected in Sun disk image - skipping partition mount test");
                return;
            }

            var driverFactory = new DriverFactory();
            driverFactory.Register(new RetroIO.Storage.Local.Drivers.FfsDriverProvider());  // Register FFS driver for Sun partitions
            var volumeProvider = new DiskImageVolumeProvider(driverFactory);

            // Act
            var firstPartition = partitions[0];
            string volumeId = Guid.NewGuid().ToString();

            try
            {
                // Try mounting first partition using recommended URN
                var mountInfo = volumeProvider.Mount(
                    firstPartition.RecommendedUrn,
                    volumeId,
                    readOnly: true
                );

                // Assert
                Assert.That(mountInfo, Is.Not.Null, "Mount should succeed");
                Assert.That(mountInfo.VolumeId, Is.EqualTo(volumeId));
                Assert.That(mountInfo.Type, Is.EqualTo("diskimage"));

                TestContext.Out.WriteLine($"Successfully mounted Sun disk partition:");
                TestContext.Out.WriteLine($"  Partition ID: {firstPartition.Id}");
                TestContext.Out.WriteLine($"  Filesystem: {mountInfo.FileSystemType}");
                TestContext.Out.WriteLine($"  Volume label: {mountInfo.VolumeLabel}");
                TestContext.Out.WriteLine($"  Read-only: {mountInfo.IsReadOnly}");
                TestContext.Out.WriteLine($"  URN used: {firstPartition.RecommendedUrn}");

                // Cleanup
                volumeProvider.Unmount(volumeId);
            }
            catch (Exception ex)
            {
                TestContext.Out.WriteLine($"Partition mount failed: {ex.Message}");
                TestContext.Out.WriteLine("This may indicate partition support needs additional work for Sun disks.");
                throw;
            }
            finally
            {
                volumeProvider.Dispose();
            }
        }

        [Test]
        [Category("SunOS")]
        public void GetSunDiskFileInfo_ValidSunOSImage_ReturnsImageDetails()
        {
            // Arrange
            if (!File.Exists(SunDiskImagePath))
            {
                Assert.Ignore($"Test skipped: Sun disk image not found at {SunDiskImagePath}");
                return;
            }

            var fileInfo = new FileInfo(SunDiskImagePath);

            // Act & Assert
            TestContext.Out.WriteLine("Sun disk image details:");
            TestContext.Out.WriteLine($"  Path: {SunDiskImagePath}");
            TestContext.Out.WriteLine($"  Size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");
            TestContext.Out.WriteLine($"  Created: {fileInfo.CreationTime}");
            TestContext.Out.WriteLine($"  Modified: {fileInfo.LastWriteTime}");

            // Verify file is large enough to contain a BSD filesystem
            Assert.That(fileInfo.Length, Is.GreaterThan(512 * 1024),
                "Sun disk image should be at least 512 KB");
        }

        [Test]
        [Category("SunOS")]
        public void CompareSunDiskWithOtherFormats_ValidSunOSImage_BsdHasHighestConfidence()
        {
            // Arrange
            if (!File.Exists(SunDiskImagePath))
            {
                Assert.Ignore($"Test skipped: Sun disk image not found at {SunDiskImagePath}");
                return;
            }

            var driverFactory = new DriverFactory();
            driverFactory.Register(new RetroIO.Storage.Local.Drivers.FfsDriverProvider());  // Register FFS driver

            // Act - Get confidence for all major filesystem types
            var confidences = driverFactory.GetDetectionConfidences(
                SunDiskImagePath,
                "FFS",
                "NDFS",
                "CBM-DOS",
                "FAT12",
                "FAT16",
                "FAT32",
                "DragonDOS",
                "DECB"
            );

            // Assert - FFS should have highest confidence
            float maxConfidence = 0f;
            string bestMatch = "";

            foreach (var kvp in confidences)
            {
                if (kvp.Value > maxConfidence)
                {
                    maxConfidence = kvp.Value;
                    bestMatch = kvp.Key;
                }
            }

            TestContext.Out.WriteLine("Confidence comparison for Sun disk:");
            foreach (var kvp in confidences)
            {
                string marker = (kvp.Key == bestMatch) ? " <-- BEST MATCH" : "";
                TestContext.Out.WriteLine($"  {kvp.Key}: {kvp.Value:P1}{marker}");
            }

            Assert.That(bestMatch, Is.EqualTo("FFS"),
                $"FFS should have highest confidence for Sun disk. Best match: {bestMatch} ({maxConfidence:P1})");
        }
    }
}
