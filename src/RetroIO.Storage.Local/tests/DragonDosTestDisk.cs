//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Builds blank DragonDOS disks and gives raw access to them for the DragonDOS tests. The layout is written out by hand from the
    /// DragonDOS 1.2 source (dosdefs.asm, BuildBAM and MakeBlankDir in ddos12.asm), not by the code under test.
    /// </summary>
    /// <remarks>
    /// A formatted disk: the BAM in sectors 1 and 2 of track 20 (1 = free, the lowest sector of a byte is bit 0), every sector free except the
    /// 18 sectors of track 20 and the 18 of the backup track 16 (side 0 only on a double sided disk), the geometry bytes FCH to FFH of sector 1,
    /// and 16 directory sectors of ten entries that are all 89H (deleted, end of directory, continuation) followed by zero bytes. Track 16 is
    /// a byte for byte copy of the same sectors.
    /// </remarks>
    internal static class DragonDosTestDisk
    {
        public const int SectorSize = 256;
        public const int EntrySize = 25;

        public static int SectorsPerTrack(int heads)
        {
            return 18 * heads;
        }

        public static BlockDeviceDescriptor Descriptor(int tracks, int heads)
        {
            if (tracks == 40 && heads == 1)
            {
                return BlockDeviceDescriptor.DragonVDK_SS40;
            }

            if (tracks == 80 && heads == 1)
            {
                return BlockDeviceDescriptor.DragonVDK_SS80;
            }

            if (tracks == 40 && heads == 2)
            {
                return BlockDeviceDescriptor.DragonVDK_DS40;
            }

            return BlockDeviceDescriptor.DragonVDK_DS80;
        }

        /// <summary>
        /// Formats a blank disk and returns its sectors (no file header).
        /// </summary>
        public static byte[] Format(int tracks, int heads)
        {
            int spt = SectorsPerTrack(heads);
            int total = tracks * spt;
            byte[] image = new byte[total * SectorSize];
            int dirLsn = 20 * spt;
            int backupLsn = 16 * spt;

            byte[] bam = new byte[2 * SectorSize];
            for (int lsn = 0; lsn < total; lsn++)
            {
                SetBit(bam, lsn, true);
            }

            for (int s = 0; s < 18; s++)
            {
                SetBit(bam, dirLsn + s, false);
                SetBit(bam, backupLsn + s, false);
            }

            bam[0xFC] = (byte)tracks;
            bam[0xFD] = (byte)spt;
            bam[0xFE] = (byte)~tracks;
            bam[0xFF] = (byte)~spt;

            Array.Copy(bam, 0, image, dirLsn * SectorSize, 2 * SectorSize);
            Array.Copy(bam, 0, image, backupLsn * SectorSize, 2 * SectorSize);

            for (int sector = 0; sector < 16; sector++)
            {
                for (int entry = 0; entry < 10; entry++)
                {
                    image[((dirLsn + 2 + sector) * SectorSize) + (entry * EntrySize)] = 0x89;
                    image[((backupLsn + 2 + sector) * SectorSize) + (entry * EntrySize)] = 0x89;
                }
            }

            return image;
        }

        public static void SetBit(byte[] bam, int lsn, bool free)
        {
            int index = ((lsn / 1440) * SectorSize) + ((lsn % 1440) / 8);
            if (free)
            {
                bam[index] |= (byte)(1 << (lsn % 8));
            }
            else
            {
                bam[index] &= (byte)~(1 << (lsn % 8));
            }
        }

        public static bool IsFreeInImage(byte[] image, int heads, int lsn)
        {
            int dirLsn = 20 * SectorsPerTrack(heads);
            int index = (dirLsn * SectorSize) + ((lsn / 1440) * SectorSize) + ((lsn % 1440) / 8);
            return (image[index] & (1 << (lsn % 8))) != 0;
        }

        /// <summary>
        /// Gets the 25 bytes of directory entry n from the primary or the backup directory.
        /// </summary>
        public static byte[] Entry(byte[] image, int heads, int n, bool backup)
        {
            int spt = SectorsPerTrack(heads);
            int lsn = ((backup ? 16 : 20) * spt) + 2 + (n / 10);
            byte[] entry = new byte[EntrySize];
            Array.Copy(image, (lsn * SectorSize) + ((n % 10) * EntrySize), entry, 0, EntrySize);
            return entry;
        }

        /// <summary>
        /// Gets the bytes of the 18 sectors of a directory track, sector 1 to 18 one after the other.
        /// </summary>
        public static byte[] Track(byte[] image, int heads, int track)
        {
            int spt = SectorsPerTrack(heads);
            byte[] bytes = new byte[18 * SectorSize];
            Array.Copy(image, track * spt * SectorSize, bytes, 0, bytes.Length);
            return bytes;
        }

        public static byte[] Pattern(int length, int seed)
        {
            byte[] data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (byte)((i * 31) + (i >> 8) + seed);
            }

            return data;
        }

        /// <summary>
        /// Mounts the image bytes read-write on a block device and a new DragonDOS file system. The device is returned so that the test can flush it.
        /// </summary>
        public static DragonDosFileSystem Mount(byte[] image, int tracks, int heads, out GenericBlockDevice device, bool readOnly = false)
        {
            device = new GenericBlockDevice(Descriptor(tracks, heads));
            device.Mount(new MemoryStream(image, writable: true), readOnly);
            DragonDosFileSystem fs = new DragonDosFileSystem();
            fs.Initialize(device);
            return fs;
        }
    }
}
