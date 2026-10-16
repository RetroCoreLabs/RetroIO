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
using RetroIO.Tests;

namespace RetroIO.Tests.Commodore
{
    [TestFixture]
    public class D81BamDebugTest
    {
        [Test]
        public void ReadBAM_DieHardD81_ShowStructure()
        {
            string testImagePath = TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\d81\Die_Hard_1_1989_Activision.d81");

            if (!File.Exists(testImagePath))
            {
                Assert.Ignore($"Test image not found: {testImagePath}");
            }

            using (FileStream stream = File.OpenRead(testImagePath))
            {
                // D81 specs
                GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.D81);
                device.Mount(stream, readOnly: true);

                // Read BAM sector (Track 40, Sector 0)
                byte[] bamSector = new byte[256];
                device.ReadBlock(BlockAddress.FromTrackSector(40, 0), bamSector);

                Console.WriteLine("D81 BAM Sector (Track 40, Sector 0):");
                Console.WriteLine($"  Byte 0 (Dir track): {bamSector[0]} (expected 40)");
                Console.WriteLine($"  Byte 1 (Dir sector): {bamSector[1]} (expected 1 or 3)");
                Console.WriteLine($"  Byte 2 (DOS version): 0x{bamSector[2]:X2} (expected 0x41 'A' or 0x44 'D')");
                Console.WriteLine($"  Byte 3: 0x{bamSector[3]:X2}");

                // These are documented D81 BAM spec fields (not a guess — the "expected" values
                // in the prints above are the real spec), so unlike a speculative byte-layout
                // guess it's safe to assert on them directly. Previously this whole test only
                // ever printed values and could never fail no matter what bytes were read.
                Assert.That(bamSector[0], Is.EqualTo(40), "D81 BAM directory track must be 40");
                Assert.That(bamSector[1], Is.EqualTo(1).Or.EqualTo(3), "D81 BAM directory sector must be 1 or 3");
                Assert.That(bamSector[2], Is.EqualTo(0x41).Or.EqualTo(0x44), "D81 DOS version byte must be 'A' (0x41) or 'D' (0x44)");

                // Volume label at 0x04-0x13 (16 bytes)
                Console.WriteLine("\n  Volume label bytes (0x04-0x13):");
                Console.Write("    ");
                for (int i = 0x04; i <= 0x13; i++)
                {
                    Console.Write($"{bamSector[i]:X2} ");
                }
                Console.WriteLine();

                Console.Write("    ASCII: '");
                for (int i = 0x04; i <= 0x13; i++)
                {
                    byte b = bamSector[i];
                    if (b >= 32 && b <= 126)
                        Console.Write((char)b);
                    else if (b == 0xA0)
                        Console.Write(".");
                    else
                        Console.Write("?");
                }
                Console.WriteLine("'");

                // Read first directory sector (Track 40, Sector 1 or 3)
                byte dirSector = bamSector[1];
                byte[] dirSectorData = new byte[256];
                device.ReadBlock(BlockAddress.FromTrackSector(40, dirSector), dirSectorData);

                Console.WriteLine($"\nFirst Directory Sector (Track 40, Sector {dirSector}):");
                Console.WriteLine($"  Byte 0 (Next track): {dirSectorData[0]}");
                Console.WriteLine($"  Byte 1 (Next sector): {dirSectorData[1]}");

                // Byte 2 of the first directory entry is the CBM DOS file-type byte: low nibble
                // is the type (0=DEL, 1=SEQ, 2=PRG, 3=USR, 4=REL), bit 7 is the "closed" flag.
                // A real, mountable disk's first entry must have a type in that documented range.
                int fileType = dirSectorData[2] & 0x0F;
                Assert.That(fileType, Is.LessThanOrEqualTo(4), "CBM DOS file type nibble must be DEL/SEQ/PRG/USR/REL (0-4)");

                // Show first directory entry (starts at byte 2)
                Console.WriteLine("\n  First directory entry (bytes 2-33):");
                for (int i = 0; i < 8; i++)
                {
                    Console.Write($"    {i * 4:X2}: ");
                    for (int j = 0; j < 4; j++)
                    {
                        int offset = 2 + (i * 4) + j;
                        Console.Write($"{dirSectorData[offset]:X2} ");
                    }
                    Console.WriteLine();
                }
            }
        }
    }
}
