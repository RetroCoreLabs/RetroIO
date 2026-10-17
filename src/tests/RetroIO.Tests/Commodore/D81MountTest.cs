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

namespace RetroIO.Tests.Commodore
{
    [TestFixture]
    public class D81MountTest
    {
        [Test]
        public void Mount_AussieGamesD81_ShouldSucceed()
        {
            string testImagePath = TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\d81\Die_Hard_1_1989_Activision.d81");

            if (!File.Exists(testImagePath))
            {
                Assert.Ignore($"Test image not found: {testImagePath}");
            }

            IFileSystemDriver driver = new CommodoreDiskDriver();

            using (FileStream stream = File.OpenRead(testImagePath))
            {
                try
                {
                    driver.Mount(stream, readOnly: true);

                    Console.WriteLine($"Filesystem: {driver.FileSystemType}");
                    Console.WriteLine($"Volume: {driver.VolumeLabel}");

                    DirectoryEntry[] entries = driver.ListDirectory("/");
                    Console.WriteLine($"Found {entries.Length} entries");

                    for (int i = 0; i < Math.Min(5, entries.Length); i++)
                    {
                        Console.WriteLine($"  {entries[i].Name} - {entries[i].Metadata.Size} bytes");
                    }

                    Assert.That(driver.FileSystemType, Is.EqualTo("CBM-DOS"));
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
