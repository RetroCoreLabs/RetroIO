//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Local.Drivers;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests of the sector rebuild in <c>DmkDiskImage.SetSectorImage</c> on a small DMK file that the test builds byte by byte, with its own
    /// CRC routine, so that every byte can be looked at.
    /// </summary>
    /// <remarks>
    /// The image has 2 cylinders, one side, 3 sectors of 256 bytes per track and the default track length 1900H. Track 0 has the three odd cases:
    /// sector 1 with the three A1H sync bytes and an ordinary data mark FBH, sector 2 with a deleted data mark F8H and no sync bytes, sector 3
    /// with FBH and no sync bytes. The CRC is the disk controller CRC: polynomial 1021H, start value FFFFH, high bit first, over the three A1H sync bytes
    /// (also when the file dropped them), the mark and the data.
    /// </remarks>
    [TestFixture]
    public class DmkRebuildTests
    {
        private const int TrackLength = 0x1900;
        private const int Cylinders = 2;
        private const int SectorsPerTrack = 3;

        private static int Crc(params byte[] bytes)
        {
            int crc = 0xFFFF;
            byte[] all = new byte[bytes.Length + 3];
            all[0] = 0xA1;
            all[1] = 0xA1;
            all[2] = 0xA1;
            Array.Copy(bytes, 0, all, 3, bytes.Length);
            for (int i = 0; i < all.Length; i++)
            {
                crc ^= all[i] << 8;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
                }
            }

            return crc;
        }

        private static byte[] Sector(int seed)
        {
            byte[] data = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                data[i] = (byte)(seed + i);
            }

            return data;
        }

        // Where things ended up in the file, for the assertions.
        private sealed class Layout
        {
            public readonly int[] Header = new int[Cylinders * SectorsPerTrack];
            public readonly int[] Mark = new int[Cylinders * SectorsPerTrack];
        }

        private static byte[] Build(Layout layout)
        {
            byte[] file = new byte[16 + (Cylinders * TrackLength)];
            file[1] = Cylinders;
            file[2] = TrackLength & 0xFF;
            file[3] = TrackLength >> 8;
            file[4] = 0x10; // single sided

            for (int cylinder = 0; cylinder < Cylinders; cylinder++)
            {
                int block = 16 + (cylinder * TrackLength);
                int pos = 0x80;
                for (int s = 0; s < SectorsPerTrack; s++)
                {
                    for (int i = 0; i < 32; i++)
                    {
                        file[block + pos++] = 0x4E;
                    }

                    int header = pos;
                    int pointer = 0x8000 | header;
                    file[block + (s * 2)] = (byte)(pointer & 0xFF);
                    file[block + (s * 2) + 1] = (byte)(pointer >> 8);

                    byte[] id = { 0xFE, (byte)cylinder, 0, (byte)(s + 1), 1 };
                    Array.Copy(id, 0, file, block + pos, 5);
                    pos += 5;
                    int idCrc = Crc(id);
                    file[block + pos++] = (byte)(idCrc >> 8);
                    file[block + pos++] = (byte)(idCrc & 0xFF);

                    for (int i = 0; i < 22; i++)
                    {
                        file[block + pos++] = 0x4E;
                    }

                    for (int i = 0; i < 12; i++)
                    {
                        file[block + pos++] = 0x00;
                    }

                    bool sync = cylinder == 1 || s == 0;
                    if (sync)
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            file[block + pos++] = 0xA1;
                        }
                    }

                    byte mark = (cylinder == 0 && s == 1) ? (byte)0xF8 : (byte)0xFB;
                    int markPos = pos;
                    file[block + pos++] = mark;
                    byte[] data = Sector((cylinder * 50) + (s * 10));
                    Array.Copy(data, 0, file, block + pos, 256);
                    pos += 256;
                    byte[] covered = new byte[257];
                    covered[0] = mark;
                    Array.Copy(data, 0, covered, 1, 256);
                    int dataCrc = Crc(covered);
                    file[block + pos++] = (byte)(dataCrc >> 8);
                    file[block + pos++] = (byte)(dataCrc & 0xFF);

                    layout.Header[(cylinder * SectorsPerTrack) + s] = block + header;
                    layout.Mark[(cylinder * SectorsPerTrack) + s] = block + markPos;
                }

                while (pos < TrackLength)
                {
                    file[block + pos++] = 0x4E;
                }
            }

            return file;
        }

        private static DmkDiskImage Parse(byte[] file)
        {
            return DmkDiskImage.Parse(new MemoryStream(file));
        }

        private static void AssertCrcs(byte[] file, Layout layout, int logical)
        {
            int header = layout.Header[logical];
            int idCrc = Crc(file[header], file[header + 1], file[header + 2], file[header + 3], file[header + 4]);
            Assert.That((file[header + 5] << 8) | file[header + 6], Is.EqualTo(idCrc), "header CRC of sector " + logical);

            int mark = layout.Mark[logical];
            byte[] covered = new byte[257];
            Array.Copy(file, mark, covered, 0, 257);
            int dataCrc = Crc(covered);
            Assert.That((file[mark + 257] << 8) | file[mark + 258], Is.EqualTo(dataCrc), "data CRC of sector " + logical);
        }

        [Test]
        public void BuiltImage_ParsesWithTheTestsOwnCrcs()
        {
            Layout layout = new Layout();
            DmkDiskImage image = Parse(Build(layout));

            Assert.That(image.SectorsPerTrack, Is.EqualTo(3));
            Assert.That(image.GetSectorImage().AsSpan(256, 256).ToArray(), Is.EqualTo(Sector(10)), "sector 2 of track 0, the one with the deleted mark");
        }

        [Test]
        public void SetSectorImage_NothingChanged_ChangesNoByte()
        {
            Layout layout = new Layout();
            byte[] file = Build(layout);
            DmkDiskImage image = Parse(file);

            int changed = image.SetSectorImage(image.GetSectorImage());

            Assert.That(changed, Is.EqualTo(0));
            Assert.That(image.ToFileBytes(), Is.EqualTo(file), "the deleted mark F8H of the unchanged sector 2 stays as it is");
        }

        [Test]
        public void SetSectorImage_OneSectorChanged_RewritesItsDataAndCrc_AndNothingElse()
        {
            Layout layout = new Layout();
            byte[] file = Build(layout);
            DmkDiskImage image = Parse(file);
            byte[] sectors = image.GetSectorImage();
            byte[] fresh = Sector(200);
            Array.Copy(fresh, 0, sectors, 2 * 256, 256); // sector 3 of track 0

            int changed = image.SetSectorImage(sectors);
            byte[] after = image.ToFileBytes();

            Assert.That(changed, Is.EqualTo(1));
            Assert.That(after.AsSpan(layout.Mark[2] + 1, 256).ToArray(), Is.EqualTo(fresh));
            AssertCrcs(after, layout, 2);
            for (int i = 0; i < file.Length; i++)
            {
                bool inside = i >= layout.Mark[2] && i < layout.Mark[2] + 259;
                if (!inside)
                {
                    Assert.That(after[i], Is.EqualTo(file[i]), "byte " + i + " is outside the changed sector and must not change");
                }
            }

            Assert.That(Parse(after).GetSectorImage(), Is.EqualTo(sectors), "the rebuilt file decodes to the new sectors, every CRC checked");
        }

        [Test]
        public void SetSectorImage_ChangedSectorWithADeletedMark_GetsAnOrdinaryMark_AndAValidCrc()
        {
            Layout layout = new Layout();
            byte[] file = Build(layout);
            DmkDiskImage image = Parse(file);
            byte[] sectors = image.GetSectorImage();
            byte[] fresh = Sector(77);
            Array.Copy(fresh, 0, sectors, 256, 256); // sector 2 of track 0 has the mark F8H

            image.SetSectorImage(sectors);
            byte[] after = image.ToFileBytes();

            Assert.That(file[layout.Mark[1]], Is.EqualTo(0xF8));
            Assert.That(after[layout.Mark[1]], Is.EqualTo(0xFB), "a write makes an ordinary data mark");
            AssertCrcs(after, layout, 1);
            Assert.That(after[layout.Mark[1] - 1], Is.EqualTo(0x00), "no sync bytes were added: the file did not have them");
        }

        [Test]
        public void SetSectorImage_SectorOnTheSecondTrack_UsesItsOwnCylinderInTheHeader()
        {
            Layout layout = new Layout();
            byte[] file = Build(layout);
            DmkDiskImage image = Parse(file);
            byte[] sectors = image.GetSectorImage();
            sectors[(4 * 256) + 5] ^= 0xFF; // sector 2 of cylinder 1

            Assert.That(image.SetSectorImage(sectors), Is.EqualTo(1));
            byte[] after = image.ToFileBytes();

            int header = layout.Header[4];
            Assert.That(after[header], Is.EqualTo(0xFE));
            Assert.That(after[header + 1], Is.EqualTo(1), "cylinder");
            Assert.That(after[header + 2], Is.EqualTo(0), "side");
            Assert.That(after[header + 3], Is.EqualTo(2), "sector number");
            Assert.That(after[header + 4], Is.EqualTo(1), "size code");
            AssertCrcs(after, layout, 4);
        }

        [Test]
        public void SetSectorImage_WrongLength_Throws()
        {
            DmkDiskImage image = Parse(Build(new Layout()));

            Assert.That(() => image.SetSectorImage(new byte[100]), Throws.InstanceOf<ArgumentException>());
        }
    }
}
