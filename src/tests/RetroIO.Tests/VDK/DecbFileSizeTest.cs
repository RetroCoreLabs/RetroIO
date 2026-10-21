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

namespace RetroIO.Tests.VDK
{
    [TestFixture]
    public class DecbFileSizeTest
    {
        // Reads syzygy.dsk (a 35 track DECB disk with two machine code files) and checks the files against what the
        // disk itself says, not against what the reader printed before. A DECB BIN file is a list of load records
        // (00, length, address, data) ended by FF 00 00 exec-address, so the file explains its own length.
        // Oracle worked out from the raw sectors: the FAT chain of SYZYGY30.BIN is 13 granules, the last with 6
        // sectors, and 11 bytes are used in its last sector: 28939 bytes. Its one load record holds 28929 bytes
        // (5 + 28929 + 5 = 28939), so the records end exactly at the end of the file. SYZYGY10.BIN is 11 granules,
        // the last with 8 sectors and a full last sector: 25088 bytes, one load record of 24714 bytes at 0F76
        // and the trailer record ending at 24724.
        [Test]
        public void ReadFile_SyzygyBins_MatchTheirOwnLoadRecords()
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
                Assert.That(entries.Length, Is.EqualTo(2));

                byte[] ten = driver.ReadFile("SYZYGY10.BIN");
                Assert.That(ten.Length, Is.EqualTo(25088));
                Assert.That(entries[0].Metadata.Size, Is.EqualTo(25088));
                Assert.That(EndOfLoadRecords(ten, out int tenExec), Is.EqualTo(24724));
                Assert.That(tenExec, Is.EqualTo(0x0F76));

                byte[] thirty = driver.ReadFile("SYZYGY30.BIN");
                Assert.That(thirty.Length, Is.EqualTo(28939));
                Assert.That(entries[1].Metadata.Size, Is.EqualTo(28939));
                Assert.That(EndOfLoadRecords(thirty, out int thirtyExec), Is.EqualTo(28939));
                Assert.That(thirtyExec, Is.EqualTo(0x14EE));

                // The two files are different files. Before the entry offsets were fixed, SYZYGY30.BIN returned the
                // data of SYZYGY10.BIN and SYZYGY10.BIN started one granule late.
                Assert.That(thirty[0], Is.EqualTo(0));
                Assert.That((thirty[3] << 8) | thirty[4], Is.EqualTo(0x0E00));
                Assert.That((ten[3] << 8) | ten[4], Is.EqualTo(0x0F76));
            }

            driver.Dispose();
        }

        // Walks the load records of a DECB BIN file and returns the offset just after the FF trailer record.
        private static int EndOfLoadRecords(byte[] data, out int execAddress)
        {
            int offset = 0;
            execAddress = -1;
            while (offset + 5 <= data.Length)
            {
                if (data[offset] == 0)
                {
                    int length = (data[offset + 1] << 8) | data[offset + 2];
                    offset += 5 + length;
                }
                else if (data[offset] == 0xFF)
                {
                    execAddress = (data[offset + 3] << 8) | data[offset + 4];
                    return offset + 5;
                }
                else
                {
                    Assert.Fail("Bad load record type " + data[offset] + " at offset " + offset);
                }
            }

            return -1;
        }
    }
}
