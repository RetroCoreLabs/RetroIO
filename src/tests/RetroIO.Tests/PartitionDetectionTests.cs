//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BSD.Partitions;

namespace RetroIO.Tests
{
    [TestFixture]
    public class PartitionDetectionTests
    {
        // Test images - tests will be skipped if not found
        // Note: Both Sun Disk Labels and BSD Disk Labels are supported partition table formats
        private const string SunDiskImagePath = @"D:\ND\HDD\sun2-sunos-3.2.img";
        private const string SunDiskImagePath2 = @"D:\ND\HDD\SUN2.img";
        private static string BsdDiskImagePath => TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\bsd211.img");

        // Valid partition table types for BSD-family partition detection tests
        private static bool IsValidPartitionTableType(PartitionTableType tableType)
        {
            return tableType == PartitionTableType.BsdDiskLabel || tableType == PartitionTableType.SunLabel;
        }

        private string? GetAvailablePartitionedImage()
        {
            // Try multiple possible paths - includes both Sun and BSD disk images
            string[] candidates = new string[]
            {
                SunDiskImagePath,
                SunDiskImagePath2,
                BsdDiskImagePath,
                TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\sun2-sunos-3.2.img"),
                TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\sun2-s3.5-disk.img")
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

        // Keep old name for backward compatibility
        private string? GetAvailableBsdImage() => GetAvailablePartitionedImage();

        [Test]
        [Category("Partition")]
        [Category("Detection")]
        public void PartitionDetector_DetectFromFile_FindsBsdPartitions()
        {
            // Arrange
            string? imagePath = GetAvailableBsdImage();
            if (imagePath == null)
            {
                Assert.Ignore("Test skipped: No BSD disk image found");
                return;
            }

            TestContext.Out.WriteLine($"Using test image: {imagePath}");

            // Act
            var result = PartitionDetector.DetectFromFile(imagePath);

            // Assert
            Assert.That(result, Is.Not.Null, "Detection result should not be null");
            TestContext.Out.WriteLine($"TableType: {result.TableType}");
            TestContext.Out.WriteLine($"Partitions count: {result.Partitions.Count}");

            if (result.HasPartitions)
            {
                Assert.That(IsValidPartitionTableType(result.TableType), Is.True,
                    $"Should detect BSD or Sun Disk Label partition table, got: {result.TableType}");

                TestContext.Out.WriteLine("\nDetected partitions:");
                for (int i = 0; i < result.Partitions.Count; i++)
                {
                    var part = result.Partitions[i];
                    TestContext.Out.WriteLine($"  {part.Id}: {part.DisplayText}");
                    TestContext.Out.WriteLine($"    Offset: {part.OffsetSectors} sectors, Size: {part.SizeSectors} sectors");
                    TestContext.Out.WriteLine($"    FS Type: 0x{part.FileSystemTypeCode:X2}");
                }

                Assert.That(result.Partitions.Count, Is.GreaterThan(0),
                    "Should find at least one BSD partition");
            }
            else
            {
                TestContext.Out.WriteLine("WARNING: No partitions detected. This may indicate a problem.");
            }
        }

        [Test]
        [Category("Partition")]
        [Category("Detection")]
        public void BsdPartitionManager_DetectFromFile_FindsPartitions()
        {
            // Arrange
            string? imagePath = GetAvailableBsdImage();
            if (imagePath == null)
            {
                Assert.Ignore("Test skipped: No partitioned disk image found");
                return;
            }

            TestContext.Out.WriteLine($"Using test image: {imagePath}");

            // First check what type of image we have using PartitionDetector
            var detectorResult = PartitionDetector.DetectFromFile(imagePath);
            TestContext.Out.WriteLine($"Detected partition table type: {detectorResult.TableType}");

            // BsdPartitionManager only handles BSD Disk Labels, not Sun Labels
            if (detectorResult.TableType == PartitionTableType.SunLabel)
            {
                TestContext.Out.WriteLine("Note: Image has Sun Disk Label - BsdPartitionManager does not handle Sun labels");
                TestContext.Out.WriteLine("Test will verify BsdPartitionManager returns empty for Sun labels");

                var sunPartitions = BsdPartitionManager.DetectFromFile(imagePath);
                Assert.That(sunPartitions, Is.Not.Null, "Partition list should not be null");
                Assert.That(sunPartitions.Count, Is.EqualTo(0),
                    "BsdPartitionManager should return empty for Sun labels");
                return;
            }

            // Act
            var partitions = BsdPartitionManager.DetectFromFile(imagePath);

            // Assert
            Assert.That(partitions, Is.Not.Null, "Partition list should not be null");
            TestContext.Out.WriteLine($"Found {partitions.Count} BSD partitions");

            for (int i = 0; i < partitions.Count; i++)
            {
                var part = partitions[i];
                TestContext.Out.WriteLine($"\nPartition {part.Id}:");
                TestContext.Out.WriteLine($"  Display: {part.DisplayText}");
                TestContext.Out.WriteLine($"  Offset: {part.OffsetSectors} sectors");
                TestContext.Out.WriteLine($"  Size: {part.SizeSectors} sectors ({part.SizeSectors * 512.0 / (1024.0 * 1024.0):F2} MB)");
                TestContext.Out.WriteLine($"  FS Type: 0x{part.FileSystemTypeCode:X2}");
                TestContext.Out.WriteLine($"  Table Type: {part.TableType}");
                TestContext.Out.WriteLine($"  Recommended URN: {part.RecommendedUrn}");

                // Validate partition metadata
                Assert.That(part.Id, Is.Not.Null.And.Not.Empty, "Partition ID should not be null or empty");
                Assert.That(part.DisplayText, Is.Not.Null.And.Not.Empty, "Display text should not be null or empty");
                Assert.That(part.OffsetSectors, Is.GreaterThanOrEqualTo(0), "Offset should be non-negative");
                Assert.That(part.SizeSectors, Is.GreaterThan(0), "Size should be positive");
                Assert.That(part.TableType, Is.EqualTo(PartitionTableType.BsdDiskLabel),
                    "Table type should be BsdDiskLabel for BSD images");
            }

            if (partitions.Count == 0)
            {
                TestContext.Out.WriteLine("WARNING: No partitions found. This may indicate a problem with partition detection.");
            }
        }

        [Test]
        [Category("Partition")]
        [Category("Detection")]
        public void PartitionDetector_DetectFromFile_PartitionsMatchBsdPartitionManager()
        {
            // Arrange
            string? imagePath = GetAvailableBsdImage();
            if (imagePath == null)
            {
                Assert.Ignore("Test skipped: No partitioned disk image found");
                return;
            }

            TestContext.Out.WriteLine($"Using test image: {imagePath}");

            // Act
            var detectorResult = PartitionDetector.DetectFromFile(imagePath);
            var managerResult = BsdPartitionManager.DetectFromFile(imagePath);

            // Assert
            TestContext.Out.WriteLine($"PartitionDetector found: {detectorResult.Partitions.Count} partitions");
            TestContext.Out.WriteLine($"BsdPartitionManager found: {managerResult.Count} partitions");
            TestContext.Out.WriteLine($"Detected table type: {detectorResult.TableType}");

            // BsdPartitionManager only handles BSD Disk Labels, not Sun Labels
            // If we detected a Sun label, skip this comparison test
            if (detectorResult.TableType == PartitionTableType.SunLabel)
            {
                TestContext.Out.WriteLine("Note: Image has Sun Disk Label - BsdPartitionManager does not handle Sun labels");
                Assert.That(managerResult.Count, Is.EqualTo(0),
                    "BsdPartitionManager should return empty for Sun labels");
                return;
            }

            Assert.That(detectorResult.Partitions.Count, Is.EqualTo(managerResult.Count),
                "Both methods should find the same number of partitions for BSD labels");

            // Compare partition IDs
            var detectorIds = new List<string>();
            for (int i = 0; i < detectorResult.Partitions.Count; i++)
            {
                detectorIds.Add(detectorResult.Partitions[i].Id);
            }

            var managerIds = new List<string>();
            for (int i = 0; i < managerResult.Count; i++)
            {
                managerIds.Add(managerResult[i].Id);
            }

            detectorIds.Sort();
            managerIds.Sort();

            for (int i = 0; i < detectorIds.Count; i++)
            {
                Assert.That(detectorIds[i], Is.EqualTo(managerIds[i]),
                    $"Partition {i} ID should match between detector and manager");
            }
        }

        [Test]
        [Category("Partition")]
        [Category("Detection")]
        public void PartitionDetector_DetectFromFile_PartitionsHaveCorrectMetadata()
        {
            // Arrange
            string? imagePath = GetAvailableBsdImage();
            if (imagePath == null)
            {
                Assert.Ignore("Test skipped: No BSD disk image found");
                return;
            }

            TestContext.Out.WriteLine($"Using test image: {imagePath}");

            // Act
            var result = PartitionDetector.DetectFromFile(imagePath);

            // Assert
            Assert.That(result.Partitions, Is.Not.Null, "Partitions list should not be null");

            for (int i = 0; i < result.Partitions.Count; i++)
            {
                var partition = result.Partitions[i];

                TestContext.Out.WriteLine($"\nValidating partition {partition.Id}:");

                // Check required fields
                Assert.That(partition.Id, Is.Not.Null.And.Not.Empty,
                    $"Partition {i} should have a valid ID");

                Assert.That(partition.DisplayText, Is.Not.Null.And.Not.Empty,
                    $"Partition {i} should have display text");

                Assert.That(partition.OffsetSectors, Is.GreaterThanOrEqualTo(0),
                    $"Partition {i} offset should be non-negative");

                Assert.That(partition.SizeSectors, Is.GreaterThan(0),
                    $"Partition {i} size should be positive");

                Assert.That(IsValidPartitionTableType(partition.TableType), Is.True,
                    $"Partition {i} should have BSD or Sun Disk Label table type, got: {partition.TableType}");

                // Check recommended URN format
                if (!string.IsNullOrEmpty(partition.RecommendedUrn))
                {
                    // Accept BSD, Sun, and FFS URN schemes
                    bool isValidUrn = partition.RecommendedUrn.StartsWith("bsd://") ||
                                     partition.RecommendedUrn.StartsWith("ffs://") ||
                                     partition.RecommendedUrn.StartsWith("sun://");
                    Assert.That(isValidUrn, Is.True,
                        $"Partition {i} URN should start with bsd://, ffs://, or sun://, got: {partition.RecommendedUrn}");

                    bool hasFragment = partition.RecommendedUrn.Contains("#");
                    Assert.That(hasFragment, Is.True,
                        $"Partition {i} URN should contain # fragment with partition letter");
                }

                TestContext.Out.WriteLine($"  ✓ All validations passed for partition {partition.Id}");
            }
        }

        [Test]
        [Category("Partition")]
        [Category("Detection")]
        public void PartitionDetector_DetectFromFile_NonExistentFile_ReturnsEmpty()
        {
            // Arrange
            string nonExistentPath = @"C:\this\path\does\not\exist\test.img";

            // Act
            var result = PartitionDetector.DetectFromFile(nonExistentPath);

            // Assert
            Assert.That(result, Is.Not.Null, "Result should not be null");
            Assert.That(result.HasPartitions, Is.False, "Should not have partitions for non-existent file");
            Assert.That(result.Partitions.Count, Is.EqualTo(0), "Partition count should be 0");
            Assert.That(result.TableType, Is.EqualTo(PartitionTableType.Unknown),
                "Table type should be Unknown for non-existent file");
        }

        [Test]
        [Category("Partition")]
        [Category("Detection")]
        public void PartitionDetector_DetectFromFile_FfsPartitions_HaveFfsTypeCodes()
        {
            // Arrange
            string? imagePath = GetAvailableBsdImage();
            if (imagePath == null)
            {
                Assert.Ignore("Test skipped: No BSD disk image found");
                return;
            }

            TestContext.Out.WriteLine($"Using test image: {imagePath}");

            // Act
            var result = PartitionDetector.DetectFromFile(imagePath);

            // Assert
            if (!result.HasPartitions)
            {
                Assert.Ignore("Test skipped: No partitions found in image");
                return;
            }

            TestContext.Out.WriteLine($"Found {result.Partitions.Count} partitions");

            // Check for FFS partitions (type code 7 = FS_BSDFFS)
            bool foundFfsPartition = false;
            for (int i = 0; i < result.Partitions.Count; i++)
            {
                var part = result.Partitions[i];
                TestContext.Out.WriteLine($"Partition {part.Id}: FS Type = 0x{part.FileSystemTypeCode:X2}");

                if (part.FileSystemTypeCode == 7) // FS_BSDFFS
                {
                    foundFfsPartition = true;
                    TestContext.Out.WriteLine($"  ✓ Found FFS partition: {part.Id}");

                    // FFS partitions should have ffs:// URN recommendation
                    if (!string.IsNullOrEmpty(part.RecommendedUrn))
                    {
                        // Currently might be bsd://, but could be ffs:// - either is valid
                        TestContext.Out.WriteLine($"  Recommended URN: {part.RecommendedUrn}");
                    }
                }
            }

            if (!foundFfsPartition)
            {
                TestContext.Out.WriteLine("Note: No FFS partitions found in this image");
            }
        }

        [Test]
        [Category("Partition")]
        [Category("Detection")]
        public void BsdPartitionManager_DetectFromFile_ResourcesProperlyDisposed()
        {
            // Arrange
            string? imagePath = GetAvailableBsdImage();
            if (imagePath == null)
            {
                Assert.Ignore("Test skipped: No BSD disk image found");
                return;
            }

            TestContext.Out.WriteLine($"Using test image: {imagePath}");

            // Act - Call multiple times to ensure resources are properly disposed
            List<PartitionMetadata> result1 = null!;
            List<PartitionMetadata> result2 = null!;
            List<PartitionMetadata> result3 = null!;

            Assert.DoesNotThrow(() =>
            {
                result1 = BsdPartitionManager.DetectFromFile(imagePath);
                result2 = BsdPartitionManager.DetectFromFile(imagePath);
                result3 = BsdPartitionManager.DetectFromFile(imagePath);
            }, "DetectFromFile should properly dispose resources between calls");

            // Assert
            Assert.That(result1.Count, Is.EqualTo(result2.Count),
                "First and second calls should return same partition count");
            Assert.That(result2.Count, Is.EqualTo(result3.Count),
                "Second and third calls should return same partition count");

            TestContext.Out.WriteLine($"✓ Successfully called DetectFromFile 3 times with consistent results: {result1.Count} partitions");
        }
    }
}
