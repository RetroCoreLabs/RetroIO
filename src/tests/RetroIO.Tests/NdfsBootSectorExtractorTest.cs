//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace RetroIO.Tests
{
    [TestFixture]
    public class NdfsBootSectorExtractorTest
    {
        private const int BOOT_SECTOR_SIZE = 1024;
        private static string ImagePath => TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\ndfs");

        [Test]
        public void ExtractFloppyBootSector()
        {
            ExtractBootSector("FLOPPY-360KB.img", "FLOPPY_BOOT_SECTOR_360KB");
        }

        [Test]
        public void ExtractFloppy12MBBootSector()
        {
            ExtractBootSector("FLOPPY-1.2MB.IMG", "FLOPPY_BOOT_SECTOR_12MB");
        }

        [Test]
        public void ExtractSMDBootSector()
        {
            ExtractBootSector("SMD-ID-100.IMG", "HARD_DISK_BOOT_SECTOR_SMD");
        }

        [Test]
        public void ExtractWinchesterBootSector()
        {
            ExtractBootSector("WD0.img", "HARD_DISK_BOOT_SECTOR_WINCHESTER");
        }

        private void ExtractBootSector(string imageName, string constantName)
        {
            var fullPath = Path.Combine(ImagePath, imageName);
            if (!File.Exists(fullPath))
            {
                Assert.Inconclusive($"Image not found: {fullPath}");
                return;
            }

            byte[] bootSector = new byte[BOOT_SECTOR_SIZE];
            using (var fs = File.OpenRead(fullPath))
            {
                int read = fs.Read(bootSector, 0, BOOT_SECTOR_SIZE);
                if (read != BOOT_SECTOR_SIZE)
                {
                    Assert.Fail($"Failed to read {BOOT_SECTOR_SIZE} bytes from {imageName}");
                    return;
                }
            }

            Console.WriteLine($"=== Boot Sector: {imageName} ===");
            Console.WriteLine($"Constant Name: {constantName}");
            Console.WriteLine();
            Console.WriteLine("```csharp");
            Console.WriteLine($"private static readonly byte[] {constantName} = new byte[{BOOT_SECTOR_SIZE}]");
            Console.WriteLine("{");

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < BOOT_SECTOR_SIZE; i++)
            {
                if (i % 16 == 0)
                {
                    if (i > 0)
                    {
                        Console.WriteLine(sb.ToString());
                        sb.Clear();
                    }
                    sb.Append("    ");
                }

                sb.Append($"0x{bootSector[i]:X2}");
                if (i < BOOT_SECTOR_SIZE - 1)
                {
                    sb.Append(", ");
                }
            }

            if (sb.Length > 0)
            {
                Console.WriteLine(sb.ToString());
            }

            Console.WriteLine("};");
            Console.WriteLine("```");
            Console.WriteLine();

            // Check if boot sector is all zeros (no boot code)
            bool hasBootCode = false;
            for (int i = 0; i < BOOT_SECTOR_SIZE; i++)
            {
                if (bootSector[i] != 0)
                {
                    hasBootCode = true;
                    break;
                }
            }

            Console.WriteLine($"Has Boot Code: {hasBootCode}");
            Console.WriteLine();
        }
    }
}
