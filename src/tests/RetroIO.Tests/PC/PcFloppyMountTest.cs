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
using RetroIO.Storage.Local.Drivers;
using RetroIO.Tests;

namespace RetroIO.Tests.PC
{
    [TestFixture]
    public class PcFloppyMountTest
    {
        [Test]
        public void Mount_720K_FAT12_ShouldSucceed()
        {
            string testImagePath = TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\img\i1.img");

            if (!File.Exists(testImagePath))
            {
                Assert.Ignore($"Test image not found: {testImagePath}");
            }

            IFileSystemDriver driver = new PcFloppyDriver();

            using (FileStream stream = File.OpenRead(testImagePath))
            {
                try
                {
                    driver.Mount(stream, readOnly: true);

                    Console.WriteLine($"Filesystem: {driver.FileSystemType}");
                    Console.WriteLine($"Volume: {driver.VolumeLabel}");

                    DirectoryEntry[] entries = driver.ListDirectory("/");
                    Console.WriteLine($"Found {entries.Length} entries");

                    for (int i = 0; i < Math.Min(10, entries.Length); i++)
                    {
                        Console.WriteLine($"  {entries[i].Name} - {entries[i].Metadata.Size} bytes");
                    }

                    Assert.That(driver.FileSystemType, Is.EqualTo("FAT12"));
                    Assert.That(entries.Length, Is.GreaterThan(0), "Should have files");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.GetType().Name}: {ex.Message}");
                    if (ex.InnerException != null)
                    {
                        Console.WriteLine($"Inner: {ex.InnerException.Message}");
                    }
                    Console.WriteLine($"Stack: {ex.StackTrace}");
                    throw;
                }
            }

            driver.Dispose();
        }
    }
}
