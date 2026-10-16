//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.BSD.Partitions;

namespace RetroIO.Tests
{
    [TestFixture]
    public class BsdPartitionDetectionDiagnostics
    {
        [Test]
        [Category("Partition")]
        [Category("Diagnostic")]
        public void DIAGNOSTIC_BsdPartitionManager_DirectTest()
        {
            // Try multiple possible paths
            string[] candidates = new string[]
            {
                @"D:\ND\HDD\sun2-sunos-3.2.img",
                @"D:\ND\HDD\SUN2.img",
                TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\bsd211.img"),
                TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\sun2-sunos-3.2.img"),
                TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\sun2-s3.5-disk.img")
            };

            string? imagePath = null;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i]))
                {
                    imagePath = candidates[i];
                    break;
                }
            }

            if (imagePath == null)
            {
                Assert.Ignore("Test skipped: No disk image found");
                return;
            }

            TestContext.Out.WriteLine($"\n=== DIAGNOSTIC TEST: BSD Partition Detection ===");
            TestContext.Out.WriteLine($"Image path: {imagePath}");

            // First check what type of partition table this image has
            var detectionResult = PartitionDetector.DetectFromFile(imagePath);
            TestContext.Out.WriteLine($"Detected partition table type: {detectionResult.TableType}");

            // Skip if it's a Sun label - this test is specifically for BSD partition manager
            if (detectionResult.TableType == PartitionTableType.SunLabel)
            {
                TestContext.Out.WriteLine("Image has Sun Disk Label, not BSD Disk Label");
                TestContext.Out.WriteLine("BsdPartitionManager only handles BSD labels - skipping test");
                Assert.Ignore("Test skipped: Image has Sun Disk Label, not BSD Disk Label");
                return;
            }

            var fileInfo = new FileInfo(imagePath);
            TestContext.Out.WriteLine($"File size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 512:N0} sectors)");

            // Create block device manually to see what happens
            TestContext.Out.WriteLine($"\nCreating block device...");
            var descriptor = new BlockDeviceDescriptor
            {
                Name = "Diagnostic Test",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = (uint)(fileInfo.Length / 512)
            };

            var device = new GenericBlockDevice(descriptor);
            TestContext.Out.WriteLine($"Block device created: BlockSize={descriptor.BlockSize}, TotalBlocks={descriptor.TotalBlocks}");

            using (var stream = File.OpenRead(imagePath))
            {
                TestContext.Out.WriteLine($"Opening stream: Length={stream.Length}, CanRead={stream.CanRead}");

                device.Mount(stream, readOnly: true);
                TestContext.Out.WriteLine($"Device mounted successfully");

                // Try to read sector 1 directly
                TestContext.Out.WriteLine($"\nReading sector 1 directly...");
                byte[] sector1 = new byte[512];
                int bytesRead = device.ReadBlock(BlockAddress.FromLBA(1), sector1);
                TestContext.Out.WriteLine($"Read {bytesRead} bytes from sector 1");
                TestContext.Out.WriteLine($"First 32 bytes: {BitConverter.ToString(sector1, 0, Math.Min(32, sector1.Length))}");

                // Check for BSD magic at offset 0 (should be 0x82564557)
                uint magic = BitConverter.ToUInt32(sector1, 0);
                TestContext.Out.WriteLine($"Magic at offset 0: 0x{magic:X8} (expected 0x82564557 for BSD)");

                // Create partition manager
                TestContext.Out.WriteLine($"\nCreating BsdPartitionManager...");
                var partMgr = new BsdPartitionManager(device);

                TestContext.Out.WriteLine($"Calling GetDiskLabel()...");
                var diskLabel = partMgr.GetDiskLabel();

                if (diskLabel == null)
                {
                    TestContext.Out.WriteLine($"ERROR: GetDiskLabel() returned null!");
                    TestContext.Out.WriteLine($"\nTrying to read sector 0 to see if label is there...");
                    byte[] sector0 = new byte[512];
                    device.ReadBlock(BlockAddress.FromLBA(0), sector0);
                    uint magic0 = BitConverter.ToUInt32(sector0, 0);
                    TestContext.Out.WriteLine($"Sector 0 magic at offset 0: 0x{magic0:X8}");

                    Assert.Fail("Disk label not found");
                }
                else if (!diskLabel.IsValid())
                {
                    TestContext.Out.WriteLine($"ERROR: Disk label is invalid!");
                    TestContext.Out.WriteLine($"  Magic: 0x{diskLabel.Magic:X8} (expected 0x82564557)");
                    TestContext.Out.WriteLine($"  NumPartitions: {diskLabel.NumPartitions}");

                    Assert.Fail($"Disk label invalid: Magic=0x{diskLabel.Magic:X8}");
                }
                else
                {
                    TestContext.Out.WriteLine($"SUCCESS: Valid disk label found!");
                    TestContext.Out.WriteLine($"  Magic: 0x{diskLabel.Magic:X8}");
                    TestContext.Out.WriteLine($"  NumPartitions: {diskLabel.NumPartitions}");
                    TestContext.Out.WriteLine($"  TypeName: {diskLabel.TypeName}");
                    TestContext.Out.WriteLine($"  PackName: {diskLabel.PackName}");

                    var partitions = partMgr.ListPartitions();
                    TestContext.Out.WriteLine($"\nPartitions found: {partitions.Count}");

                    for (int i = 0; i < partitions.Count; i++)
                    {
                        var (letter, part) = partitions[i];
                        TestContext.Out.WriteLine($"  {letter}: {part.FileSystemTypeName}, Offset={part.Offset}, Size={part.Size} sectors");
                    }
                }
            }

            TestContext.Out.WriteLine($"\n=== END DIAGNOSTIC TEST ===\n");
        }
    }
}
