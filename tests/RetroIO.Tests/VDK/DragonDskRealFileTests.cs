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
using RetroIO.Storage.Local.Drivers;
using RetroIO.Tests;

namespace RetroIO.Tests.VDK
{
    [TestFixture]
    public class DragonDskRealFileTests
    {
        [Test]
        public void Mount_SyzygyDsk_ShouldSucceed()
        {
            string testImagePath = TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\dsk\syzygy.dsk");

            if (!File.Exists(testImagePath))
            {
                Assert.Ignore($"Test image not found: {testImagePath}");
            }

            IFileSystemDriver driver = new DragonDskDriver();

            using (FileStream stream = File.OpenRead(testImagePath))
            {
                driver.Mount(stream, readOnly: true);

                Console.WriteLine($"Mounted: {driver.FileSystemType}");
                Console.WriteLine($"Volume: {driver.VolumeLabel}");

                Assert.That(driver.FileSystemType, Is.EqualTo("DECB"));
            }

            driver.Dispose();
        }

        [Test]
        public void ListDirectory_SyzygyDsk_ShouldShowFiles()
        {
            string testImagePath = TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\dsk\syzygy.dsk");

            if (!File.Exists(testImagePath))
            {
                Assert.Ignore($"Test image not found: {testImagePath}");
            }

            IFileSystemDriver driver = new DragonDskDriver();

            using (FileStream stream = File.OpenRead(testImagePath))
            {
                driver.Mount(stream, readOnly: true);

                DirectoryEntry[] entries = driver.ListDirectory("/");
                Assert.That(entries.Length, Is.GreaterThan(0), "Should have files");

                Console.WriteLine($"Found {entries.Length} files in directory:");
                for (int i = 0; i < Math.Min(10, entries.Length); i++)
                {
                    Console.WriteLine($"  {i + 1}. {entries[i].Name} - {entries[i].Metadata.Size} bytes");
                }
            }

            driver.Dispose();
        }

        [Test]
        public void ReadFile_SyzygyDsk_ShouldSucceed()
        {
            string testImagePath = TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\dsk\syzygy.dsk");

            if (!File.Exists(testImagePath))
            {
                Assert.Ignore($"Test image not found: {testImagePath}");
            }

            IFileSystemDriver driver = new DragonDskDriver();

            using (FileStream stream = File.OpenRead(testImagePath))
            {
                driver.Mount(stream, readOnly: true);

                DirectoryEntry[] entries = driver.ListDirectory("/");
                Assert.That(entries.Length, Is.GreaterThan(0), "Should have files");

                // Try to read the first file
                if (entries.Length > 0)
                {
                    string fileName = entries[0].Name;
                    Console.WriteLine($"\nAttempting to read: {fileName}");

                    try
                    {
                        byte[] data = driver.ReadFile(fileName);
                        Console.WriteLine($"Successfully read {data.Length} bytes");
                        Assert.That(data.Length, Is.GreaterThan(0), "File should have content");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error: {ex.GetType().Name}: {ex.Message}");
                        Console.WriteLine($"Stack trace:\n{ex.StackTrace}");
                        if (ex.InnerException != null)
                        {
                            Console.WriteLine($"Inner: {ex.InnerException.Message}");
                        }
                        throw;
                    }
                }
            }

            driver.Dispose();
        }

        [Test]
        public void DetectFormat_SyzygyDsk_ShouldIdentifyCorrectly()
        {
            string testImagePath = TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\dsk\syzygy.dsk");

            if (!File.Exists(testImagePath))
            {
                Assert.Ignore($"Test image not found: {testImagePath}");
            }

            FileInfo fileInfo = new FileInfo(testImagePath);

            // File size is 161,280 bytes = 35 tracks × 18 spt × 256 bytes (CoCo standard, no header)
            Console.WriteLine($"File size: {fileInfo.Length} bytes");

            int headerSize = (int)(fileInfo.Length % 256);
            Console.WriteLine($"Header size (modulo 256): {headerSize} bytes");

            long dataSize = fileInfo.Length - headerSize;
            int tracks = (int)(dataSize / (18 * 256));
            Console.WriteLine($"Calculated tracks: {tracks}");

            Assert.That(headerSize, Is.EqualTo(0), "Should have no header (exact multiple of 256)");
            Assert.That(tracks, Is.EqualTo(35), "Should be 35-track CoCo format");
        }

        [Test]
        public void Mount_SyzygyDsk_ShouldDetectDECB()
        {
            string testImagePath = TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\dsk\syzygy.dsk");

            if (!File.Exists(testImagePath))
            {
                Assert.Ignore($"Test image not found: {testImagePath}");
            }

            IFileSystemDriver driver = new DragonDskDriver();

            using (FileStream stream = File.OpenRead(testImagePath))
            {
                driver.Mount(stream, readOnly: true);

                Console.WriteLine($"Filesystem Type: {driver.FileSystemType}");
                Assert.That(driver.FileSystemType, Is.EqualTo("DECB"), "Should detect DECB filesystem");
            }

            driver.Dispose();
        }
    }
}
