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
    public class DragonDosFileReadTests
    {
        [Test]
        public void ReadFile_RealVdkImage_ShouldSucceed()
        {
            string testImagePath = TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\vdk\6809TUT1.vdk");

            if (!File.Exists(testImagePath))
            {
                Assert.Ignore($"Test image not found: {testImagePath}");
            }

            IFileSystemDriver driver = new DragonVdkDriver();

            using (FileStream stream = File.OpenRead(testImagePath))
            {
                driver.Mount(stream, readOnly: true);

                DirectoryEntry[] entries = driver.ListDirectory("/");
                Assert.That(entries.Length, Is.GreaterThan(0), "Should have files");

                Console.WriteLine($"Found {entries.Length} files in directory:");
                for (int i = 0; i < Math.Min(5, entries.Length); i++)
                {
                    Console.WriteLine($"  {i + 1}. {entries[i].Name} - {entries[i].Metadata.Size} bytes");
                }

                // Try to read the second file (RW.BIN based on output)
                if (entries.Length > 1)
                {
                    string fileName = entries[1].Name;
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
        public void ReadFile_AllFiles_ShouldSucceed()
        {
            string testImagePath = TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\vdk\6809TUT1.vdk");

            if (!File.Exists(testImagePath))
            {
                Assert.Ignore($"Test image not found: {testImagePath}");
            }

            IFileSystemDriver driver = new DragonVdkDriver();

            using (FileStream stream = File.OpenRead(testImagePath))
            {
                driver.Mount(stream, readOnly: true);

                DirectoryEntry[] entries = driver.ListDirectory("/");

                int successCount = 0;
                int failCount = 0;

                for (int i = 0; i < Math.Min(10, entries.Length); i++)
                {
                    string fileName = entries[i].Name;
                    try
                    {
                        byte[] data = driver.ReadFile(fileName);
                        Console.WriteLine($"✓ {fileName}: {data.Length} bytes");
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"✗ {fileName}: {ex.GetType().Name} - {ex.Message}");
                        failCount++;
                    }
                }

                Console.WriteLine($"\nResults: {successCount} succeeded, {failCount} failed out of {Math.Min(10, entries.Length)} files");

                Assert.That(failCount, Is.EqualTo(0), $"{failCount} files failed to read");
            }

            driver.Dispose();
        }
    }
}
