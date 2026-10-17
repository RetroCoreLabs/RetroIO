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
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Tests
{
    [TestFixture]
    public class NdfsImageAnalysisTest
    {
        private static string ImagePath => TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\ndfs");

        [Test]
        public void AnalyzeAllNdfsImages()
        {
            var images = new List<string>
            {
                "FLOPPY-1.2MB.IMG",
                "FLOPPY-360KB.img",
                "SMD-ID-100.IMG",
                "SMD-ID-102.IMG",
                "WD0.img"
            };

            Console.WriteLine("=== NDFS Disk Image Analysis ===\n");

            int analyzed = 0;
            var failures = new List<string>();

            foreach (var imageName in images)
            {
                var fullPath = Path.Combine(ImagePath, imageName);
                if (!File.Exists(fullPath))
                {
                    Console.WriteLine($"Image: {imageName} - NOT FOUND\n");
                    continue;
                }

                analyzed++;
                try
                {
                    AnalyzeImage(imageName, fullPath);
                }
                catch (Exception ex)
                {
                    // Previously this method's only output was Console.WriteLine — an exception
                    // here (bad master block parse, truncated read, etc.) would have propagated
                    // and failed the test anyway for a SINGLE image, but there was no assertion
                    // confirming ANY image was ever actually analyzed successfully, so a run
                    // where every image was silently NOT FOUND passed identically to a run where
                    // every image parsed perfectly. Collect failures so the summary below can
                    // distinguish "nothing to check" from "everything failed".
                    failures.Add($"{imageName}: {ex.Message}");
                }
            }

            // Previously this test had ZERO assertions — it always passed regardless of whether
            // any image was found or whether parsing produced garbage. All 5 named images are
            // confirmed present on this machine (checked directly), so require at least one
            // successful analysis and zero parse failures.
            Assert.That(failures, Is.Empty, $"Image analysis failed for: {string.Join("; ", failures)}");
            Assert.That(analyzed, Is.GreaterThan(0),
                $"None of the expected images were found under {ImagePath} — fixture path may have moved");
        }

        private void AnalyzeImage(string name, string path)
        {
            Console.WriteLine($"Image: {name}");
            Console.WriteLine($"Path: {path}");

            var fileInfo = new FileInfo(path);
            long sizeBytes = fileInfo.Length;
            double sizeMB = sizeBytes / (1024.0 * 1024.0);
            int pages = (int)(sizeBytes / NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);

            Console.WriteLine($"  Size: {sizeBytes:N0} bytes ({sizeMB:F2} MB)");
            Console.WriteLine($"  Pages (2KB): {pages}");

            // Read first page (master block)
            byte[] firstPage = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            using (var fs = File.OpenRead(path))
            {
                fs.ReadExactly(firstPage, 0, NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);
            }

            var masterBlock = MasterBlock.FromBytes(firstPage);

            // Real assertions, not just print statements — a real NDFS image must parse to a
            // valid master block with a non-empty directory name.
            Assert.That(masterBlock.IsValid(), Is.True, $"{name}: master block should be valid");
            Assert.That(masterBlock.DirectoryName, Is.Not.Null.And.Not.Empty, $"{name}: directory name should not be empty");

            Console.WriteLine($"  Directory Name: '{masterBlock.DirectoryName}'");
            Console.WriteLine($"  Object File Pointer: {FormatPointer(masterBlock.ObjectFilePointer)}");
            Console.WriteLine($"  User File Pointer: {FormatPointer(masterBlock.UserFilePointer)}");
            Console.WriteLine($"  Bit File Pointer: {FormatPointer(masterBlock.BitFilePointer)}");
            Console.WriteLine($"  Unreserved Pages: {masterBlock.UnreservedPages}");
            Console.WriteLine($"  Extended Info Valid: {masterBlock.ExtValid}");
            if (masterBlock.ExtValid)
            {
                Console.WriteLine($"  Extended Pages Available: {masterBlock.ExtPagesAvailable}");
                Console.WriteLine($"  System Number: {masterBlock.ExtLastSystemNumber}");
                Console.WriteLine($"  Checksum: 0x{masterBlock.ExtCheckSum:X4} (Calculated: 0x{masterBlock.ExtCalculatedChecksum:X4})");
                Console.WriteLine($"  Checksum State: {masterBlock.ChecksumState}");
            }
            Console.WriteLine($"  Has FLOMON: {masterBlock.HasFlomon}");

            // Analyze boot sector (first 1024 bytes before master block)
            AnalyzeBootSector(firstPage);

            Console.WriteLine();
        }

        private void AnalyzeBootSector(byte[] firstPage)
        {
            // Boot sector is typically first 1024 bytes (0x000-0x3FF)
            // Master block starts at 0x7E0 (2016 decimal)

            // Check for non-zero boot code
            bool hasBootCode = false;
            for (int i = 0; i < 1024; i++)
            {
                if (firstPage[i] != 0)
                {
                    hasBootCode = true;
                    break;
                }
            }

            Console.WriteLine($"  Boot Sector Present: {hasBootCode}");

            if (hasBootCode)
            {
                // Extract first 32 bytes as signature
                Console.Write("  Boot Signature: ");
                for (int i = 0; i < 32; i++)
                {
                    Console.Write($"{firstPage[i]:X2} ");
                }
                Console.WriteLine();
            }
        }

        private string FormatPointer(BlockPointer? pointer)
        {
            if (pointer == null || !pointer.IsValid())
                return "NULL";

            return $"BlockID={pointer.BlockID}, Type={pointer.Type}";
        }
    }
}
