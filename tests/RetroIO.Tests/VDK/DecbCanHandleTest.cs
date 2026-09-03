//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FileSystems;
using RetroIO.Storage.Local.Streams;
using RetroIO.Tests;

namespace RetroIO.Tests.VDK
{
    [TestFixture]
    public class DecbCanHandleTest
    {
        [Test]
        public void CanHandle_SyzygyDsk_Debug()
        {
            string testImagePath = TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\dsk\syzygy.dsk");

            if (!File.Exists(testImagePath))
            {
                Assert.Ignore($"Test image not found: {testImagePath}");
            }

            using (FileStream stream = File.OpenRead(testImagePath))
            {
                // Create block device
                BlockDeviceDescriptor descriptor = new BlockDeviceDescriptor
                {
                    Name = "CoCo DSK 35T/SS",
                    BlockSize = 256,
                    AddressMode = BlockAddressMode.LBA,
                    Cylinders = 35,
                    Heads = 1,
                    UniformSectorsPerTrack = 18,
                    TotalBlocks = 630,
                    ExpectedImageSize = 161280,
                    SupportsVariableGeometry = false
                };

                GenericBlockDevice blockDevice = new GenericBlockDevice(descriptor);
                blockDevice.Mount(stream, readOnly: true);

                BlockDeviceInfo? info = blockDevice.GetBlockDeviceInfo();
                Console.WriteLine($"Block device info:");
                Console.WriteLine($"  Cylinders: {info?.CylinderCount}");
                Console.WriteLine($"  SPT: {info?.SectorsPerTrack}");

                // Test DECB detection
                DecbFileSystem decbFs = new DecbFileSystem();
                bool canHandle = decbFs.CanHandle(blockDevice);

                Console.WriteLine($"  DecbFileSystem.CanHandle: {canHandle}");

                // Read FAT manually
                uint fatLsn = (uint)((17 * 18) + (2 - 1));
                byte[] fatSector = new byte[256];
                blockDevice.ReadBlock(BlockAddress.FromLBA(fatLsn), fatSector);

                int freeCount = 0;
                int allocCount = 0;
                for (int i = 0; i < 68; i++)
                {
                    if (fatSector[i] == 0xFF)
                    {
                        freeCount++;
                    }
                    else if (fatSector[i] < 68 || fatSector[i] >= 0xC0)
                    {
                        allocCount++;
                    }
                }

                Console.WriteLine($"  FAT free: {freeCount}, alloc: {allocCount}");

                Assert.That(canHandle, Is.True, "Should be able to handle DECB disk");
            }
        }
    }
}
