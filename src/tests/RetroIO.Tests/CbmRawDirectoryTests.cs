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
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Tests
{
    /// <summary>
    /// Raw PETSCII names on Commodore disks, for directory art.
    /// </summary>
    [TestFixture]
    public class CbmRawDirectoryTests
    {
        private static CommodoreDiskDriver NewDisk(string layoutId, out MemoryStream stream)
        {
            CommodoreDiskDriverProvider provider = new CommodoreDiskDriverProvider();
            byte[] image = provider.CreateBlank(layoutId, new Dictionary<string, string>());
            stream = new MemoryStream();
            stream.Write(image, 0, image.Length);
            stream.Position = 0;
            return (CommodoreDiskDriver)provider.Mount(stream, false, layoutId);
        }

        private static byte[] ArtLine(byte fill)
        {
            // Graphic characters, a shifted space inside the name, more graphics after it.
            byte[] name = new byte[CbmDosFileSystem.RawNameLength];
            for (int i = 0; i < name.Length; i++)
            {
                name[i] = i == 5 ? (byte)0xA0 : fill;
            }

            return name;
        }

        [TestCase("cbm-d64")]
        [TestCase("cbm-d81")]
        public void ArtLines_GoWhereAsked_AndKeepEveryByte(string layoutId)
        {
            CommodoreDiskDriver disk = NewDisk(layoutId, out MemoryStream stream);
            disk.WriteFile("/FIRST", new byte[] { 1, 2, 3 });
            disk.WriteFile("/SECOND", new byte[] { 4 });

            disk.InsertDelEntry(0, ArtLine(0xB0));
            disk.InsertDelEntry(2, ArtLine(0x60));
            disk.InsertDelEntry(4, ArtLine(0xDD));

            CbmRawDirectoryEntry[] raw = disk.ReadRawDirectory();
            Assert.That(raw.Length, Is.EqualTo(5));
            Assert.That(raw[0].Name, Is.EqualTo(ArtLine(0xB0)));
            Assert.That(raw[0].TypeByte, Is.EqualTo(CbmDosFileSystem.ClosedDelType));
            Assert.That(raw[0].Blocks, Is.EqualTo(0));
            Assert.That(raw[2].Name, Is.EqualTo(ArtLine(0x60)));
            Assert.That(raw[4].Name, Is.EqualTo(ArtLine(0xDD)));

            // The files keep their place, their type and their data.
            DirectoryEntry[] listed = disk.ListDirectory("/");
            Assert.That(listed[1].Name, Is.EqualTo("FIRST"));
            Assert.That(listed[3].Name, Is.EqualTo("SECOND"));
            Assert.That(listed[0].Metadata.FileType, Is.EqualTo("DEL"));
            Assert.That(disk.ReadFile("/FIRST"), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(disk.ReadFile("/SECOND"), Is.EqualTo(new byte[] { 4 }));
        }

        [Test]
        public void ManyArtLines_GrowTheDirectory_AndSurviveARemount()
        {
            CommodoreDiskDriver disk = NewDisk("cbm-d64", out MemoryStream stream);
            for (int i = 0; i < 20; i++)
            {
                disk.InsertDelEntry(i, ArtLine((byte)(0xA1 + i)));
            }

            disk.WriteFile("/AFTER", new byte[] { 9 });

            CommodoreDiskDriver again = (CommodoreDiskDriver)new CommodoreDiskDriverProvider().Mount(new MemoryStream(stream.ToArray(), writable: false), true, "cbm-d64");
            CbmRawDirectoryEntry[] raw = again.ReadRawDirectory();
            Assert.That(raw.Length, Is.EqualTo(21));
            for (int i = 0; i < 20; i++)
            {
                Assert.That(raw[i].Name, Is.EqualTo(ArtLine((byte)(0xA1 + i))), "line " + i);
            }

            Assert.That(again.ReadFile("/AFTER"), Is.EqualTo(new byte[] { 9 }));
        }

        [Test]
        public void RawName_ReplacesOnlyTheName()
        {
            CommodoreDiskDriver disk = NewDisk("cbm-d64", out _);
            disk.WriteFile("/GAME", new byte[] { 7, 7 });
            disk.WriteRawName(0, ArtLine(0x71));

            CbmRawDirectoryEntry[] raw = disk.ReadRawDirectory();
            Assert.That(raw[0].Name, Is.EqualTo(ArtLine(0x71)));
            Assert.That(raw[0].TypeByte, Is.EqualTo(0x82), "still a closed PRG");
            Assert.Throws<ArgumentOutOfRangeException>(() => disk.WriteRawName(1, ArtLine(0x71)));
            Assert.Throws<ArgumentException>(() => disk.WriteRawName(0, new byte[15]));
        }

        [TestCase("cbm-d64")]
        [TestCase("cbm-d81")]
        public void RawDiskNameAndId_AreStoredAsGiven(string layoutId)
        {
            CommodoreDiskDriver disk = NewDisk(layoutId, out _);
            byte[] name = ArtLine(0xA6);
            byte[] id = { 0xB1, 0xA0 };
            disk.WriteRawDiskHeader(name, id);
            Assert.That(disk.ReadRawDiskName(), Is.EqualTo(name));
            Assert.That(disk.ReadRawDiskId(), Is.EqualTo(id));

            // The disk still takes a file afterwards.
            disk.WriteFile("/X", new byte[] { 1 });
            Assert.That(disk.ReadFile("/X"), Is.EqualTo(new byte[] { 1 }));
        }

        [Test]
        public void InsertPastTheEnd_IsRefused()
        {
            CommodoreDiskDriver disk = NewDisk("cbm-d64", out _);
            Assert.Throws<ArgumentOutOfRangeException>(() => disk.InsertDelEntry(1, ArtLine(0xB0)));
        }
    }
}
