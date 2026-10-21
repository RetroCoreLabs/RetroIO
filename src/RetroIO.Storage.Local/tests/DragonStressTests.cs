//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Long random sequences of create, overwrite, delete, rename and read on Dragon and CoCo disk images, through the VDK, DSK/JVC and DMK
    /// drivers, carrying DragonDOS, DECB and OS-9 volumes. After every operation the image file is checked against an in-memory model.
    /// </summary>
    /// <remarks>
    /// <para><b>What is checked after EVERY operation</b></para>
    ///  - Every file the model holds reads back byte for byte the same, and its size in the listing is right.
    ///  - The listing holds exactly the files of the model plus the files that were on the image at the start.
    ///  - Space: the free count of the driver equals the free count the test works out itself from the raw bytes of the image file (the BAM of
    ///    DragonDOS, the FAT of DECB, the allocation map of OS-9), and free space plus the space the files use equals the total. A sector that is
    ///    used by two files, a file sector that the map calls free, or a sector the map calls used that no file owns (a lost block) fails at once.
    ///  - DragonDOS: the backup directory on track 16 is a copy of track 20.
    ///  - DMK: every sector header CRC and data CRC of the whole file is valid (checked by a CRC routine of the test, not the one under test).
    /// A failure message names the variant, the seed, the operation number and what the operation was, so the run can be repeated.
    /// <para><b>Per variant, at the end</b></para>
    /// A full disk is filled until a write fails; the failing write must throw an IOException and leave the file byte for byte as it was.
    /// Then every file is deleted and the free space must be what it was at the start (OS-9: less the sectors the root directory grew by, because
    /// RBF never shortens a directory file). The image file must then open exclusively after the driver is disposed.
    /// <para><b>Memory and speed</b></para>
    /// Memory is compared with GC.GetTotalMemory(true) after 20 percent of the operations and at the end. Speed is compared as a ratio inside
    /// one run: a probe (write, read and delete of an 8 KB file, 10 times) is timed at operation 50 and at the last operation, both at about the
    /// same fill level because the random walk is steered towards half full. The numbers are printed. No wall-clock time is asserted.
    /// <para><b>What cannot be run</b></para>
    /// DECB exists on 35 track disks only and the VDK driver takes 40 and 80 track disks only, so there is no DECB-in-VDK variant. A headerless JVC
    /// image cannot tell 40 tracks two sided from 80 tracks one sided, so only the one sided ones are run without a header. The pydmk program could
    /// not be installed here (no such package on the Python package index), so DMK is read back by the test's own decoder only. DECB and OS-9
    /// are read back by Toolshed (decb, os9 dcheck) when WSL is there; DragonDOS has no outside tool in these tests (the XRoar emulator needs ROM files).
    /// </remarks>
    [TestFixture]
    public class DragonStressTests
    {
        private const int Sector = 256;

        // ------------------------------------------------------------------ variants

        public enum Kind { DragonDos, Decb, Os9 }

        public enum Box { Vdk, Jvc, JvcHeader, Dmk }

        public sealed class Variant
        {
            public string Name = string.Empty;
            public Kind Kind;
            public Box Box;
            public int Tracks;
            public int Heads;
            public string? RealImage;      // folder and name of a real image to start from, or null for a blank disk
            public string? RealFolder;

            public override string ToString()
            {
                return Name;
            }
        }

        private static Variant V(string name, Kind kind, Box box, int tracks, int heads, string? folder = null, string? image = null)
        {
            return new Variant { Name = name, Kind = kind, Box = box, Tracks = tracks, Heads = heads, RealFolder = folder, RealImage = image };
        }

        public static IEnumerable<Variant> DefaultVariants()
        {
            yield return V("DragonDOS in VDK SS40", Kind.DragonDos, Box.Vdk, 40, 1);
            yield return V("DragonDOS in VDK DS40", Kind.DragonDos, Box.Vdk, 40, 2);
            yield return V("DragonDOS in VDK SS80", Kind.DragonDos, Box.Vdk, 80, 1);
            yield return V("DragonDOS in VDK DS80", Kind.DragonDos, Box.Vdk, 80, 2);
            yield return V("DragonDOS in JVC SS40 no header", Kind.DragonDos, Box.Jvc, 40, 1);
            yield return V("DragonDOS in JVC DS40 header", Kind.DragonDos, Box.JvcHeader, 40, 2);
            yield return V("DragonDOS in JVC SS80 header", Kind.DragonDos, Box.JvcHeader, 80, 1);
            yield return V("DragonDOS in JVC DS80 header", Kind.DragonDos, Box.JvcHeader, 80, 2);
            yield return V("DragonDOS in DMK SS40 built", Kind.DragonDos, Box.Dmk, 40, 1);
            yield return V("DragonDOS in DMK real FEdit SS40", Kind.DragonDos, Box.Dmk, 40, 1, "dmk", "FEdit.dmk");
            yield return V("DragonDOS in DMK real FEdit DS40", Kind.DragonDos, Box.Dmk, 40, 2, "dmk", "FEdit_40_2.dmk");
            yield return V("DECB in JVC 35 no header", Kind.Decb, Box.Jvc, 35, 1);
            yield return V("DECB in JVC 35 header", Kind.Decb, Box.JvcHeader, 35, 1);
            yield return V("DECB in DMK 35 built", Kind.Decb, Box.Dmk, 35, 1);
            yield return V("OS-9 in JVC real NitrOS-9", Kind.Os9, Box.Jvc, 40, 2, "dsk", "NITR09.DSK");
            yield return V("OS-9 in VDK real NitrOS-9", Kind.Os9, Box.Vdk, 40, 2, "dsk", "NITR09.DSK");
            yield return V("OS-9 in DMK real NitrOS-9", Kind.Os9, Box.Dmk, 40, 2, "dsk", "NITR09.DSK");
        }

        public static IEnumerable<Variant> StressVariants()
        {
            foreach (Variant v in DefaultVariants())
            {
                yield return v;
            }

            yield return V("DragonDOS in DMK real FEdit SS80", Kind.DragonDos, Box.Dmk, 80, 1, "dmk", "FEdit_80_1.dmk");
            yield return V("DragonDOS in DMK real FEdit DS80", Kind.DragonDos, Box.Dmk, 80, 2, "dmk", "FEdit_80_2.dmk");
        }

        // ------------------------------------------------------------------ test entry points

        [TestCaseSource(nameof(DefaultVariants))]
        public void RandomOperations_500_KeepEveryCheck(Variant variant)
        {
            new Run(variant, 500, 20261021 + variant.Name.Length * 7, false).Execute();
        }

        [TestCaseSource(nameof(StressVariants))]
        [Category("Stress")]
        [Explicit("5000 operations per variant; run on purpose")]
        public void RandomOperations_5000_KeepEveryCheck(Variant variant)
        {
            new Run(variant, 5000, 777 + variant.Name.Length * 13, true).Execute();
        }

        // ------------------------------------------------------------------ the helper byte routines

        private static byte[] Header12(int tracks, int heads)
        {
            return new byte[] { (byte)'d', (byte)'k', 12, 0, 0x10, 0x10, (byte)'X', 0x10, (byte)tracks, (byte)heads, 0, 0 };
        }

        private static byte[] Join(byte[] a, byte[] b)
        {
            byte[] all = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, all, 0, a.Length);
            Buffer.BlockCopy(b, 0, all, a.Length, b.Length);
            return all;
        }

        private static readonly int[] CrcTable = BuildCrcTable();

        private static int[] BuildCrcTable()
        {
            int[] table = new int[256];
            for (int i = 0; i < 256; i++)
            {
                int crc = i << 8;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
                }

                table[i] = crc;
            }

            return table;
        }

        // Disk controller CRC over the three A1H sync bytes, then the given bytes.
        private static int Crc(byte[] data, int offset, int length)
        {
            int crc = 0xFFFF;
            for (int i = 0; i < 3; i++)
            {
                crc = ((crc << 8) & 0xFFFF) ^ CrcTable[(crc >> 8) ^ 0xA1];
            }

            for (int i = 0; i < length; i++)
            {
                crc = ((crc << 8) & 0xFFFF) ^ CrcTable[(crc >> 8) ^ data[offset + i]];
            }

            return crc;
        }

        // Builds a DMK file from a sector image (cylinder by cylinder, side 0 then side 1, sector 1 to N), track length 1900H.
        private static byte[] BuildDmk(byte[] sectors, int cylinders, int heads, int spt)
        {
            const int trackLength = 0x1900;
            byte[] file = new byte[16 + (cylinders * heads * trackLength)];
            file[1] = (byte)cylinders;
            file[2] = trackLength & 0xFF;
            file[3] = trackLength >> 8;
            file[4] = heads == 1 ? (byte)0x10 : (byte)0;

            for (int cylinder = 0; cylinder < cylinders; cylinder++)
            {
                for (int side = 0; side < heads; side++)
                {
                    int track = (cylinder * heads) + side;
                    int block = 16 + (track * trackLength);
                    int pos = 0x80;
                    for (int s = 0; s < spt; s++)
                    {
                        for (int i = 0; i < 32; i++)
                        {
                            file[block + pos++] = 0x4E;
                        }

                        int pointer = 0x8000 | pos;
                        file[block + (s * 2)] = (byte)(pointer & 0xFF);
                        file[block + (s * 2) + 1] = (byte)(pointer >> 8);

                        int header = block + pos;
                        file[header] = 0xFE;
                        file[header + 1] = (byte)cylinder;
                        file[header + 2] = (byte)side;
                        file[header + 3] = (byte)(s + 1);
                        file[header + 4] = 1;
                        int idCrc = Crc(file, header, 5);
                        file[header + 5] = (byte)(idCrc >> 8);
                        file[header + 6] = (byte)(idCrc & 0xFF);
                        pos += 7;
                        for (int i = 0; i < 22; i++)
                        {
                            file[block + pos++] = 0x4E;
                        }

                        for (int i = 0; i < 12; i++)
                        {
                            file[block + pos++] = 0x00;
                        }

                        for (int i = 0; i < 3; i++)
                        {
                            file[block + pos++] = 0xA1;
                        }

                        int mark = block + pos;
                        file[mark] = 0xFB;
                        Buffer.BlockCopy(sectors, ((track * spt) + s) * Sector, file, mark + 1, Sector);
                        int dataCrc = Crc(file, mark, 257);
                        file[mark + 257] = (byte)(dataCrc >> 8);
                        file[mark + 258] = (byte)(dataCrc & 0xFF);
                        pos += 259;
                    }

                    while (pos < trackLength)
                    {
                        file[block + pos++] = 0x4E;
                    }
                }
            }

            return file;
        }

        // Decodes a DMK file with the test's own routine. Every header CRC and data CRC must be valid; the sectors come back in logical order.
        private static byte[] DecodeDmk(byte[] file, out string problem)
        {
            problem = string.Empty;
            int tracks = file[1];
            int trackLength = file[2] | (file[3] << 8);
            int heads = (file[4] & 0x10) != 0 ? 1 : 2;
            int blocks = tracks * heads;
            if (file.Length != 16 + (blocks * trackLength))
            {
                problem = "the DMK file length is " + file.Length + ", not " + (16 + (blocks * trackLength));
                return Array.Empty<byte>();
            }

            int spt = 0;
            byte[]? sectors = null;
            for (int track = 0; track < blocks; track++)
            {
                int block = 16 + (track * trackLength);
                int count = 0;
                bool[] seen = new bool[256];
                for (int p = 0; p < 64; p++)
                {
                    int pointer = file[block + (p * 2)] | (file[block + (p * 2) + 1] << 8);
                    if (pointer == 0)
                    {
                        break;
                    }

                    int idNumber = file[block + (pointer & 0x3FFF) + 3];
                    if (!seen[idNumber])
                    {
                        seen[idNumber] = true;
                        count++;
                    }
                }

                if (sectors == null)
                {
                    spt = count;
                    sectors = new byte[blocks * spt * Sector];
                }

                if (count != spt)
                {
                    problem = "track " + track + " has " + count + " sectors, expected " + spt;
                    return sectors;
                }

                bool[] done = new bool[256];
                for (int p = 0; p < 64; p++)
                {
                    int pointer = file[block + (p * 2)] | (file[block + (p * 2) + 1] << 8);
                    if (pointer == 0)
                    {
                        break;
                    }

                    int header = block + (pointer & 0x3FFF);
                    if (done[file[header + 3]])
                    {
                        continue;
                    }

                    done[file[header + 3]] = true;
                    if (file[header] != 0xFE || ((file[header + 5] << 8) | file[header + 6]) != Crc(file, header, 5))
                    {
                        problem = "bad header CRC at track " + track + " sector index " + p;
                        return sectors;
                    }

                    int number = file[header + 3];
                    int pos = header + 7;
                    int end = block + trackLength;
                    while (pos < end && (file[pos] == 0x4E || file[pos] == 0x00 || file[pos] == 0xA1))
                    {
                        pos++;
                    }

                    if (pos + 259 > end || (file[pos] != 0xFB && file[pos] != 0xF8))
                    {
                        problem = "no data mark at track " + track + " sector " + number;
                        return sectors;
                    }

                    if (((file[pos + 257] << 8) | file[pos + 258]) != Crc(file, pos, 257))
                    {
                        problem = "bad data CRC at track " + track + " sector " + number;
                        return sectors;
                    }

                    Buffer.BlockCopy(file, pos + 1, sectors, ((track * spt) + (number - 1)) * Sector, Sector);
                }
            }

            return sectors!;
        }

        // ------------------------------------------------------------------ the independent file system checks (from the raw sectors)

        public sealed class Fsck
        {
            public string Problem = string.Empty;
            public int Free;            // free units as the map says
            public int Total;           // units in the map
            public int Owned;           // units the files own
            public int Lost;            // units the map calls used that nothing owns, less the baseline
            public int MaxExtents;      // most extents (DragonDOS), granule chains (DECB) or segments (OS-9) of one file
            public int FilesOverFourExtents;
            public int DirectorySectors; // OS-9: sectors of the root directory file
            public bool BackupEqual = true;
            public int UsedUnitsPerFile;
        }

        // DragonDOS: BAM in sectors 1 and 2 of track 20, directory in sectors 3 to 18, backup on track 16.
        private static Fsck CheckDragonDos(byte[] s, int tracks, int heads, int baselineLost)
        {
            Fsck r = new Fsck();
            int spt = 18 * heads;
            int total = tracks * spt;
            int dir = 20 * spt;
            r.Total = total;

            byte[] bam = new byte[512];
            Buffer.BlockCopy(s, dir * Sector, bam, 0, 512);
            if (bam[0xFC] != tracks || bam[0xFD] != spt || bam[0xFE] != (byte)~tracks || bam[0xFF] != (byte)~spt)
            {
                r.Problem = "the BAM geometry bytes are damaged";
                return r;
            }

            int[] owner = new int[total];
            for (int n = 0; n < 160; n++)
            {
                int o = ((dir + 2 + (n / 10)) * Sector) + ((n % 10) * 25);
                int attr = s[o];
                if ((attr & 0x08) != 0)
                {
                    break;
                }

                if ((attr & 0x81) != 0)
                {
                    continue;
                }

                int extents = 0;
                int cur = n;
                int guard = 0;
                while (true)
                {
                    int co = ((dir + 2 + (cur / 10)) * Sector) + ((cur % 10) * 25);
                    int first = cur == n ? 12 : 1;
                    int count = cur == n ? 4 : 7;
                    for (int e = 0; e < count; e++)
                    {
                        int at = co + first + (e * 3);
                        int lsn = (s[at] << 8) | s[at + 1];
                        int len = s[at + 2];
                        if (len == 0)
                        {
                            continue;
                        }

                        extents++;
                        for (int k = 0; k < len; k++)
                        {
                            if (lsn + k >= total)
                            {
                                r.Problem = "file entry " + n + " has a sector past the end of the disk";
                                return r;
                            }

                            if (owner[lsn + k] != 0)
                            {
                                r.Problem = "sector " + (lsn + k) + " is used by two files (entry " + n + " and entry " + (owner[lsn + k] - 1) + ")";
                                return r;
                            }

                            owner[lsn + k] = n + 1;
                            r.Owned++;
                        }
                    }

                    if ((s[co] & 0x20) == 0)
                    {
                        break;
                    }

                    cur = s[co + 24];
                    if (cur == 0 || ++guard > 160)
                    {
                        r.Problem = "the continuation chain of entry " + n + " is broken";
                        return r;
                    }
                }

                if (extents > r.MaxExtents)
                {
                    r.MaxExtents = extents;
                }

                if (extents > 4)
                {
                    r.FilesOverFourExtents++;
                }
            }

            int usedBits = 0;
            for (int lsn = 0; lsn < total; lsn++)
            {
                bool free = (bam[((lsn / 1440) * Sector) + ((lsn % 1440) / 8)] & (1 << (lsn % 8))) != 0;
                if (free)
                {
                    r.Free++;
                    if (owner[lsn] != 0)
                    {
                        r.Problem = "sector " + lsn + " belongs to file entry " + (owner[lsn] - 1) + " but the BAM says it is free";
                        return r;
                    }
                }
                else
                {
                    usedBits++;
                }
            }

            r.Lost = usedBits - r.Owned - baselineLost;
            for (int i = 0; i < 18; i++)
            {
                for (int b = 0; b < Sector; b++)
                {
                    if (s[((dir + i) * Sector) + b] != s[(((16 * spt) + i) * Sector) + b])
                    {
                        r.BackupEqual = false;
                    }
                }
            }

            return r;
        }

        // DECB: FAT in sector 2 of track 17, 68 granules (two per track, track 17 skipped), directory in sectors 3 to 11.
        private static Fsck CheckDecb(byte[] s)
        {
            Fsck r = new Fsck();
            int fat = ((17 * 18) + 1) * Sector;
            int dir = ((17 * 18) + 2) * Sector;
            r.Total = 68;
            int[] owner = new int[68];
            for (int e = 0; e < 72; e++)
            {
                int o = dir + (e * 32);
                if (s[o] == 0xFF)
                {
                    break;
                }

                if (s[o] == 0x00)
                {
                    continue;
                }

                int g = s[o + 13];
                int length = 0;
                while (true)
                {
                    if (g >= 68)
                    {
                        r.Problem = "file entry " + e + " points at granule " + g;
                        return r;
                    }

                    if (owner[g] != 0)
                    {
                        r.Problem = "granule " + g + " is used twice (entry " + e + " and entry " + (owner[g] - 1) + ")";
                        return r;
                    }

                    owner[g] = e + 1;
                    r.Owned++;
                    length++;
                    int next = s[fat + g];
                    if (next >= 0xC0)
                    {
                        break;
                    }

                    if (next == 0xFF)
                    {
                        r.Problem = "the chain of entry " + e + " runs into a free granule";
                        return r;
                    }

                    g = next;
                }

                if (length > r.MaxExtents)
                {
                    r.MaxExtents = length;
                }
            }

            for (int g = 0; g < 68; g++)
            {
                bool free = s[fat + g] == 0xFF;
                if (free)
                {
                    r.Free++;
                    if (owner[g] != 0)
                    {
                        r.Problem = "granule " + g + " is in a file but the FAT says free";
                        return r;
                    }
                }
                else if (owner[g] == 0)
                {
                    r.Lost++;
                }
            }

            return r;
        }

        // OS-9: identification sector 0, allocation map from sector 1 (bit 7 of byte 0 is sector 0, one sector per bit), descriptors, segment lists.
        private static Fsck CheckOs9(byte[] s)
        {
            Fsck r = new Fsck();
            int total = (s[0] << 16) | (s[1] << 8) | s[2];
            int mapBytes = (s[4] << 8) | s[5];
            int perBit = (s[6] << 8) | s[7];
            int rootFd = (s[8] << 16) | (s[9] << 8) | s[10];
            if (perBit != 1)
            {
                r.Problem = "sectors per bit is " + perBit;
                return r;
            }

            r.Total = total;
            int[] owner = new int[total];
            owner[0] = 1;
            int mapSectors = (mapBytes + Sector - 1) / Sector;
            for (int i = 0; i < mapSectors; i++)
            {
                owner[1 + i] = 1;
            }

            List<int> pending = new List<int>();
            pending.Add(rootFd);
            bool root = true;
            int dirsSeen = 0;
            while (pending.Count > 0)
            {
                int fd = pending[pending.Count - 1];
                pending.RemoveAt(pending.Count - 1);
                if (fd <= 0 || fd >= total)
                {
                    r.Problem = "a directory entry points at descriptor sector " + fd;
                    return r;
                }

                if (owner[fd] != 0)
                {
                    r.Problem = "descriptor sector " + fd + " is used twice";
                    return r;
                }

                owner[fd] = 1;
                int fdo = fd * Sector;
                int size = (s[fdo + 9] << 24) | (s[fdo + 10] << 16) | (s[fdo + 11] << 8) | s[fdo + 12];
                bool isDir = (s[fdo] & 0x80) != 0;
                int segments = 0;
                int sectorsOfFile = 0;
                List<int> fileSectors = new List<int>();
                for (int g = 0; g < 48; g++)
                {
                    int at = fdo + 16 + (g * 5);
                    int lsn = (s[at] << 16) | (s[at + 1] << 8) | s[at + 2];
                    int count = (s[at + 3] << 8) | s[at + 4];
                    if (count == 0)
                    {
                        break;
                    }

                    segments++;
                    for (int k = 0; k < count; k++)
                    {
                        if (lsn + k >= total)
                        {
                            r.Problem = "descriptor " + fd + " has a segment past the end of the disk";
                            return r;
                        }

                        if (owner[lsn + k] != 0)
                        {
                            r.Problem = "sector " + (lsn + k) + " is used twice (file descriptor " + fd + ")";
                            return r;
                        }

                        owner[lsn + k] = 1;
                        fileSectors.Add(lsn + k);
                        sectorsOfFile++;
                    }
                }

                if (segments > r.MaxExtents)
                {
                    r.MaxExtents = segments;
                }

                if (segments > 4)
                {
                    r.FilesOverFourExtents++;
                }

                if (root)
                {
                    r.DirectorySectors = sectorsOfFile;
                    root = false;
                }

                if (isDir)
                {
                    dirsSeen++;
                    int entries = size / 32;
                    for (int e = 0; e < entries; e++)
                    {
                        int sectorIndex = (e * 32) / Sector;
                        if (sectorIndex >= fileSectors.Count)
                        {
                            r.Problem = "directory " + fd + " is longer than its segments";
                            return r;
                        }

                        int eo = (fileSectors[sectorIndex] * Sector) + ((e * 32) % Sector);
                        if (s[eo] == 0)
                        {
                            continue;
                        }

                        bool dot = s[eo] == 0xAE || (s[eo] == 0x2E && (s[eo + 1] == 0xAE || s[eo + 1] == 0x00 || s[eo + 1] == 0x2E));
                        if (dot)
                        {
                            continue;
                        }

                        int child = (s[eo + 29] << 16) | (s[eo + 30] << 8) | s[eo + 31];
                        pending.Add(child);
                    }
                }
            }

            for (int sec = 0; sec < total; sec++)
            {
                bool used = (s[Sector + (sec >> 3)] & (0x80 >> (sec & 7))) != 0;
                if (!used)
                {
                    r.Free++;
                    if (owner[sec] != 0)
                    {
                        r.Problem = "sector " + sec + " is in use by a file but the map says free";
                        return r;
                    }
                }
                else if (owner[sec] == 0)
                {
                    r.Lost++;
                }
                else
                {
                    r.Owned++;
                }
            }

            return r;
        }

        // ------------------------------------------------------------------ one run

        private sealed class Run
        {
            private readonly Variant _v;
            private readonly int _ops;
            private readonly int _seed;
            private readonly bool _stress;
            private readonly Random _rnd;
            private readonly string _folder;
            private string _path = string.Empty;
            private IFileSystemDriver _driver = null!;
            private FileStream _stream = null!;
            private readonly Dictionary<string, byte[]> _model = new Dictionary<string, byte[]>();
            private readonly Dictionary<string, byte[]> _initial = new Dictionary<string, byte[]>();
            private readonly List<string> _initialNames = new List<string>();
            private int _nextId = 1;
            private int _op;
            private string _what = string.Empty;
            private int _free0;
            private int _lostBaseline;
            private int _ownedBaseline;
            private int _totalUnits;
            private int _unitBytes;
            private int _os9DirSectors0;
            private int _maxExtentsSeen;
            private int _filesOverFour;
            private int _fragPhase;
            private long _usedBytesCapacity;
            private readonly Stopwatch _clock = new Stopwatch();
            private readonly Stopwatch _tStep = new Stopwatch();
            private readonly Stopwatch _tRaw = new Stopwatch();
            private readonly Stopwatch _tFsck = new Stopwatch();
            private readonly Stopwatch _tList = new Stopwatch();
            private readonly Stopwatch _tRead = new Stopwatch();
            private double _sizeScale = 1.0;

            public Run(Variant v, int ops, int seed, bool stress)
            {
                _v = v;
                _ops = ops;
                _seed = seed;
                _stress = stress;
                _rnd = new Random(seed);
                _folder = Path.Combine(Path.GetTempPath(), "dragon-stress-" + Guid.NewGuid().ToString("N"));
            }

            private string Where()
            {
                return _v.Name + ", seed " + _seed + ", operation " + _op + " (" + _what + ")";
            }

            private void Fail(string message)
            {
                Assert.Fail(message + " [" + Where() + "]");
            }

            // ---- start

            private static string? ImagesFolder()
            {
                string? fromEnvironment = Environment.GetEnvironmentVariable("RETROIO_DRAGON_TEST_IMAGES");
                return !string.IsNullOrEmpty(fromEnvironment)
                    ? fromEnvironment
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "RetroFS", "test-images");
            }

            private static byte[] BlankDecbSectors()
            {
                byte[] image = new byte[35 * 18 * Sector];
                int fat = ((17 * 18) + 1) * Sector;
                int dir = ((17 * 18) + 2) * Sector;
                for (int i = 0; i < 68; i++)
                {
                    image[fat + i] = 0xFF;
                }

                for (int i = 0; i < 9 * Sector; i++)
                {
                    image[dir + i] = 0xFF;
                }

                return image;
            }

            private byte[] StartSectors()
            {
                if (_v.RealImage != null)
                {
                    string source = Path.Combine(ImagesFolder()!, _v.RealFolder!, _v.RealImage);
                    if (!File.Exists(source))
                    {
                        Assert.Ignore("Test image not found: " + _v.RealImage);
                    }

                    byte[] raw = File.ReadAllBytes(source);
                    if (_v.Box == Box.Dmk && _v.Kind == Kind.DragonDos)
                    {
                        return raw; // the DMK file itself; handled in Open
                    }

                    return raw; // NITR09.DSK is a headerless JVC file: its bytes are the sectors
                }

                if (_v.Kind == Kind.Decb)
                {
                    return BlankDecbSectors();
                }

                return DragonDosTestDisk.Format(_v.Tracks, _v.Heads);
            }

            private void Open()
            {
                Directory.CreateDirectory(_folder);
                byte[] start = StartSectors();
                byte[] file;
                int spt = _v.Kind == Kind.Decb ? 18 : 18;
                bool realDmk = _v.Box == Box.Dmk && _v.RealImage != null && _v.Kind == Kind.DragonDos;
                switch (_v.Box)
                {
                    case Box.Vdk:
                        file = Join(Header12(_v.Tracks, _v.Heads), start);
                        _path = Path.Combine(_folder, "disk.vdk");
                        break;
                    case Box.Jvc:
                        file = start;
                        _path = Path.Combine(_folder, "disk.dsk");
                        break;
                    case Box.JvcHeader:
                        file = Join(new byte[] { (byte)spt, (byte)_v.Heads, 1, 1, 0 }, start);
                        _path = Path.Combine(_folder, "disk.jvc");
                        break;
                    default:
                        file = realDmk ? start : BuildDmk(start, _v.Tracks, _v.Heads, spt);
                        _path = Path.Combine(_folder, "disk.dmk");
                        break;
                }

                File.WriteAllBytes(_path, file);
                Mount();
            }

            private void Mount()
            {
                _stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                switch (_v.Box)
                {
                    case Box.Vdk:
                        _driver = new DragonVdkDriver();
                        break;
                    case Box.Dmk:
                        _driver = new DragonDmkDriver();
                        break;
                    default:
                        _driver = new DragonDskDriver();
                        break;
                }

                _driver.Mount(_stream, false);
            }

            private void Close()
            {
                _driver.Dispose();
                _stream.Dispose();
            }

            private int Free()
            {
                switch (_driver)
                {
                    case DragonVdkDriver vdk:
                        return vdk.GetBlocksFree();
                    case DragonDmkDriver dmk:
                        return dmk.GetBlocksFree();
                    default:
                        return ((DragonDskDriver)_driver).GetBlocksFree();
                }
            }

            // The bytes of the image file as they are now, read through the same handle the driver writes with (opening the file again for
            // every check cost 7 ms each here, more than all the rest of the work). Every driver sets the position before it reads or writes.
            private byte[] Raw()
            {
                _stream.Position = 0;
                byte[] bytes = new byte[_stream.Length];
                _stream.ReadExactly(bytes, 0, bytes.Length);
                return bytes;
            }

            // The logical sectors in the file as they are now.
            private byte[] SectorsNow(byte[] raw)
            {
                if (_v.Box == Box.Dmk)
                {
                    byte[] decoded = DecodeDmk(raw, out string problem);
                    if (problem.Length > 0)
                    {
                        Fail("DMK check: " + problem);
                    }

                    return decoded;
                }

                int header = _v.Box == Box.Vdk ? (raw[2] | (raw[3] << 8)) : (int)(raw.Length % 128);
                byte[] sectors = new byte[raw.Length - header];
                Buffer.BlockCopy(raw, header, sectors, 0, sectors.Length);
                return sectors;
            }

            private Fsck Check(byte[] sectors)
            {
                switch (_v.Kind)
                {
                    case Kind.DragonDos:
                        return CheckDragonDos(sectors, _dosTracks, _dosHeads, _lostBaseline);
                    case Kind.Decb:
                        return CheckDecb(sectors);
                    default:
                        return CheckOs9(sectors);
                }
            }

            private int _dosTracks;
            private int _dosHeads;

            // ---- names and sizes

            private string NewName()
            {
                int id = _nextId++;
                string[] ext = { "BIN", "BAS", "TXT", "DAT" };
                if (_v.Kind == Kind.Os9)
                {
                    return "f" + id.ToString("D5");
                }

                return "N" + id.ToString("D5") + "." + ext[id % ext.Length];
            }

            private int PickSize()
            {
                int unit = _unitBytes;
                int free = Free() * Sector;
                int limit = Math.Max(0, free - 6 * 1024);
                int[] fixedSizes = { 0, 1, 255, 256, 257, 511, 512, 513, 2303, 2304, 2305, 4608, 4609 };
                int size;
                // The sizes grow with the disk, so that a big disk reaches the same fill level in about the same number of operations.
                int pick = _rnd.Next(10);
                if (pick < 3)
                {
                    size = fixedSizes[_rnd.Next(fixedSizes.Length)];
                }
                else if (pick < 5)
                {
                    size = _rnd.Next(1, 40) * Sector;
                }
                else if (pick < 6)
                {
                    size = (_rnd.Next(1, 40) * Sector) + 1;
                }
                else if (pick < 9)
                {
                    size = _rnd.Next(0, 12000);
                }
                else
                {
                    size = _rnd.Next(12000, 30000);
                }

                if (pick >= 3 && _sizeScale > 1.0)
                {
                    size = (int)(size * _sizeScale);
                }

                if (unit > 0 && size > limit)
                {
                    size = limit > 0 ? _rnd.Next(0, limit) : 0;
                }

                return size;
            }

            private byte[] Content(int size)
            {
                byte[] data = new byte[size];
                _rnd.NextBytes(data);
                return data;
            }

            private string Pick()
            {
                int index = _rnd.Next(_model.Count);
                int i = 0;
                Dictionary<string, byte[]>.Enumerator e = _model.GetEnumerator();
                while (e.MoveNext())
                {
                    if (i++ == index)
                    {
                        return e.Current.Key;
                    }
                }

                return string.Empty;
            }

            // ---- the checks after an operation

            private void VerifyAll(bool quick)
            {
                _tRaw.Start();
                byte[] raw = Raw();
                _tRaw.Stop();
                _tFsck.Start();
                byte[] sectors = SectorsNow(raw);
                Fsck f = Check(sectors);
                _tFsck.Stop();
                if (f.Problem.Length > 0)
                {
                    Fail("file system check: " + f.Problem);
                }

                int lost = _v.Kind == Kind.Os9 ? f.Lost - _lostBaseline : f.Lost;
                if (lost != 0)
                {
                    Fail("lost blocks: " + lost + " unit(s) are marked used and no file owns them (more than at the start)");
                }

                if (!f.BackupEqual)
                {
                    Fail("the backup directory on track 16 differs from track 20");
                }

                int driverFree = Free();
                int checkFree = _v.Kind == Kind.Decb ? f.Free * 9 : f.Free;
                if (driverFree != checkFree)
                {
                    Fail("the driver says " + driverFree + " free but the image says " + checkFree);
                }

                // Free plus the space the files use equals the total.
                if (_v.Kind == Kind.DragonDos)
                {
                    long modelSectors = 0;
                    Dictionary<string, byte[]>.Enumerator e = _model.GetEnumerator();
                    while (e.MoveNext())
                    {
                        modelSectors += (e.Current.Value.Length + Sector - 1) / Sector;
                    }

                    // free + owned by files (initial files included) + the fixed overhead (the directory tracks and anything else that was used at the start) = total
                    long expectedOwned = modelSectors + _ownedBaseline;
                    if (f.Owned != expectedOwned)
                    {
                        Fail("the files own " + f.Owned + " sectors but the model needs " + expectedOwned);
                    }

                    long overhead = _totalUnits - _free0 - _ownedBaseline;
                    if (f.Free + f.Owned + overhead != f.Total)
                    {
                        Fail("free " + f.Free + " + owned " + f.Owned + " + overhead " + overhead + " is not the total " + f.Total);
                    }
                }
                else if (_v.Kind == Kind.Decb)
                {
                    long modelGranules = 0;
                    Dictionary<string, byte[]>.Enumerator e = _model.GetEnumerator();
                    while (e.MoveNext())
                    {
                        modelGranules += Math.Max(1, (e.Current.Value.Length + 2303) / 2304);
                    }

                    if (f.Owned != modelGranules)
                    {
                        Fail("the files own " + f.Owned + " granules but the model needs " + modelGranules);
                    }

                    if (f.Free + f.Owned != f.Total)
                    {
                        Fail("free " + f.Free + " + owned " + f.Owned + " is not the total " + f.Total);
                    }
                }
                else
                {
                    // OS-9: the reserved sectors of the first track are marked used and belong to no file (the baseline, 16 on the real disk).
                    if (f.Free + f.Owned + _lostBaseline != f.Total)
                    {
                        Fail("free " + f.Free + " + owned " + f.Owned + " + reserved " + _lostBaseline + " is not the total " + f.Total);
                    }
                }

                if (f.MaxExtents > _maxExtentsSeen)
                {
                    _maxExtentsSeen = f.MaxExtents;
                }

                if (f.FilesOverFourExtents > _filesOverFour)
                {
                    _filesOverFour = f.FilesOverFourExtents;
                }

                // Listing equals the model plus the files that were there at the start.
                _tList.Start();
                DirectoryEntry[] list = _driver.ListDirectory("/");
                _tList.Stop();
                StringComparison cmp = _v.Kind == Kind.Os9 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                int expectedCount = _model.Count + _initialNames.Count;
                if (list.Length != expectedCount)
                {
                    Fail("the listing has " + list.Length + " entries, the model expects " + expectedCount);
                }

                for (int i = 0; i < list.Length; i++)
                {
                    string name = list[i].Name;
                    if (_model.TryGetValue(NormalName(name), out byte[]? data))
                    {
                        if (list[i].Metadata.Size != data.Length)
                        {
                            Fail("the listing says " + name + " has " + list[i].Metadata.Size + " bytes, the model " + data.Length);
                        }
                    }
                    else if (!InitialHas(name, cmp))
                    {
                        Fail("the listing has " + name + " which is not in the model");
                    }
                }

                // Every model file reads back byte for byte.
                _tRead.Start();
                Dictionary<string, byte[]>.Enumerator me = _model.GetEnumerator();
                while (me.MoveNext())
                {
                    byte[] got = _driver.ReadFile("/" + me.Current.Key);
                    if (!got.AsSpan().SequenceEqual(me.Current.Value))
                    {
                        Fail("file " + me.Current.Key + " reads back different: " + got.Length + " bytes, expected " + me.Current.Value.Length);
                    }
                }

                _tRead.Stop();
                if (!quick)
                {
                    for (int i = 0; i < _initialNames.Count; i++)
                    {
                        if (_initial.TryGetValue(_initialNames[i], out byte[]? init))
                        {
                            byte[] got = _driver.ReadFile("/" + _initialNames[i]);
                            if (!got.AsSpan().SequenceEqual(init))
                            {
                                Fail("the file " + _initialNames[i] + " that was on the image at the start has changed");
                            }
                        }
                    }
                }
            }

            private string NormalName(string listed)
            {
                if (_v.Kind == Kind.Os9)
                {
                    return listed;
                }

                return listed.ToUpperInvariant();
            }

            private bool InitialHas(string name, StringComparison cmp)
            {
                for (int i = 0; i < _initialNames.Count; i++)
                {
                    if (string.Equals(_initialNames[i], name, cmp))
                    {
                        return true;
                    }
                }

                return false;
            }

            // ---- operations

            private void DoCreate()
            {
                string name = NewName();
                int size = PickSize();
                byte[] data = Content(size);
                _what = "create " + name + " " + size + " bytes";
                _driver.WriteFile("/" + name, data);
                _model[name] = data;
            }

            private void DoOverwrite()
            {
                string name = Pick();
                int size = PickSize();
                byte[] data = Content(size);
                _what = "overwrite " + name + " " + _model[name].Length + " -> " + size + " bytes";
                _driver.WriteFile("/" + name, data);
                _model[name] = data;
            }

            private void DoDelete()
            {
                string name = Pick();
                _what = "delete " + name;
                _driver.DeleteFile("/" + name);
                _model.Remove(name);
            }

            private void DoRename()
            {
                string name = Pick();
                string target = NewName();
                _what = "rename " + name + " to " + target;
                _driver.RenameFile("/" + name, "/" + target);
                byte[] data = _model[name];
                _model.Remove(name);
                _model[target] = data;
            }

            private void DoFragmentStep()
            {
                // Make single free sectors in the middle of the used area, so that later files need more than four extents:
                // many small files, then every second one deleted. Spread over several operations.
                if (_fragPhase < 24)
                {
                    string name = NewName();
                    byte[] data = Content(_v.Kind == Kind.Decb ? 2304 : Sector);
                    _what = "fragmenting: create " + name;
                    _driver.WriteFile("/" + name, data);
                    _model[name] = data;
                    _fragNames.Add(name);
                }
                else
                {
                    int index = ((_fragPhase - 24) * 2) + 1;
                    if (index < _fragNames.Count && _model.ContainsKey(_fragNames[index]))
                    {
                        _what = "fragmenting: delete " + _fragNames[index];
                        _driver.DeleteFile("/" + _fragNames[index]);
                        _model.Remove(_fragNames[index]);
                    }
                    else
                    {
                        _what = "fragmenting: nothing to delete";
                    }
                }

                _fragPhase++;
            }

            private readonly List<string> _fragNames = new List<string>();

            private void Step()
            {
                _op++;
                bool inFrag = _op >= 10 && _op < 10 + 36 && _fragPhase < 36;
                if (inFrag)
                {
                    DoFragmentStep();
                    return;
                }

                // Steer towards about half full so that the fill level is the same early and late in the run.
                long used = _usedBytesCapacity - ((long)Free() * Sector);
                double fill = (double)used / _usedBytesCapacity;
                int roll = _rnd.Next(100);
                int create = fill < 0.35 ? 55 : (fill > 0.55 ? 20 : 38);
                int overwrite = 20;
                int delete = fill > 0.55 ? 40 : 18;
                if (_model.Count == 0)
                {
                    DoCreate();
                    return;
                }

                if (_model.Count > 36)
                {
                    delete = 60;
                }

                if (roll < create && _model.Count <= 36)
                {
                    DoCreate();
                }
                else if (roll < create + overwrite)
                {
                    DoOverwrite();
                }
                else if (roll < create + overwrite + delete)
                {
                    DoDelete();
                }
                else
                {
                    DoRename();
                }
            }

            // Managed memory in use after a full collection, less the bytes the model itself holds (they change with the random sizes).
            private long MemoryWithoutModel()
            {
                long model = 0;
                Dictionary<string, byte[]>.Enumerator e = _model.GetEnumerator();
                while (e.MoveNext())
                {
                    model += e.Current.Value.Length;
                }

                return GC.GetTotalMemory(true) - model;
            }

            // ---- speed probe: write, read and delete an 8 KB file, ten times

            private double Probe()
            {
                byte[] data = new byte[8192];
                _rnd.NextBytes(data);
                string name = _v.Kind == Kind.Os9 ? "probe" : "PROBE.BIN";
                double best = double.MaxValue;
                for (int round = 0; round < 3; round++)
                {
                    _clock.Restart();
                    for (int i = 0; i < 5; i++)
                    {
                        _driver.WriteFile("/" + name, data);
                        byte[] back = _driver.ReadFile("/" + name);
                        if (back.Length != data.Length)
                        {
                            Fail("probe file reads back wrong");
                        }

                        _driver.DeleteFile("/" + name);
                    }

                    _clock.Stop();
                    best = Math.Min(best, _clock.Elapsed.TotalMilliseconds);
                }

                return best;
            }

            // ---- the run

            public void Execute()
            {
                try
                {
                    Open();
                    _op = 0;
                    _what = "start";

                    byte[] raw0 = Raw();
                    byte[] sectors0 = SectorsNow(raw0);
                    if (_v.Kind == Kind.DragonDos)
                    {
                        _dosTracks = _v.Tracks;
                        _dosHeads = _v.Heads;
                        if (_v.RealImage != null)
                        {
                            // The real DMK disks: tracks and sides come from the file.
                            _dosTracks = raw0[1];
                            _dosHeads = (raw0[4] & 0x10) != 0 ? 1 : 2;
                        }
                    }

                    _free0 = Free();
                    DirectoryEntry[] list0 = _driver.ListDirectory("/");
                    for (int i = 0; i < list0.Length; i++)
                    {
                        string n = list0[i].Name;
                        _initialNames.Add(n);
                        if (!list0[i].Metadata.IsDirectory)
                        {
                            _initial[n] = _driver.ReadFile("/" + n);
                        }
                    }

                    Fsck f0 = Check(sectors0);
                    if (f0.Problem.Length > 0)
                    {
                        Fail("the starting image fails the check: " + f0.Problem);
                    }

                    if (_v.Kind == Kind.DragonDos)
                    {
                        // Lost blocks at the start (sectors used by the format that no file owns) are the baseline: the directory tracks, boot area.
                        int usedBits = f0.Total - f0.Free;
                        _lostBaseline = usedBits - f0.Owned;
                        _ownedBaseline = f0.Owned;
                        _totalUnits = f0.Total;
                    }
                    else
                    {
                        _lostBaseline = f0.Lost;
                        _totalUnits = f0.Total;
                    }

                    if (_v.Kind == Kind.Os9)
                    {
                        _os9DirSectors0 = f0.DirectorySectors;
                    }

                    _unitBytes = Sector;
                    _usedBytesCapacity = (long)_free0 * Sector;
                    _sizeScale = Math.Max(1.0, Math.Min(4.0, _usedBytesCapacity / (160.0 * 1024)));
                    VerifyAll(false);

                    // Memory and speed marks. A probe is timed every 50 operations; the last one is compared with the first one at about the same fill level.
                    long memoryAtMark = 0;
                    int markMemory = Math.Max(20, _ops / 5);
                    List<double[]> samples = new List<double[]>();
                    List<long> memorySamples = new List<long>();
                    Stopwatch total = Stopwatch.StartNew();
                    for (int i = 0; i < _ops; i++)
                    {
                        _tStep.Start();
                        try
                        {
                            Step();
                        }
                        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException || ex is InvalidOperationException || ex is UnauthorizedAccessException)
                        {
                            Fail("an operation that should work threw " + ex.GetType().Name + ": " + ex.Message + "; the driver says " + Free() + " free");
                        }

                        _tStep.Stop();
                        VerifyAll(_op % 25 != 0);

                        if (_op == _ops)
                        {
                            // The probe file needs room: free some if the random walk left the disk nearly full.
                            while (_model.Count > 0 && (long)Free() * Sector < 64 * 1024)
                            {
                                _op++;
                                DoDelete();
                                VerifyAll(true);
                            }
                        }

                        if ((_op % 50 == 0 || _op == _ops) && (long)Free() * Sector >= 64 * 1024)
                        {
                            double fill = 1.0 - ((double)Free() * Sector / _usedBytesCapacity);
                            samples.Add(new[] { _op, fill, Probe() });
                        }

                        if (_op == markMemory)
                        {
                            memoryAtMark = MemoryWithoutModel();
                        }

                        if (_op % Math.Max(1, _ops / 10) == 0)
                        {
                            memorySamples.Add(MemoryWithoutModel());
                        }
                    }

                    total.Stop();
                    long memoryEnd = MemoryWithoutModel();
                    Assert.That(samples.Count, Is.GreaterThan(1), "no speed probe could run (" + Where() + ")");
                    double[] last = samples[samples.Count - 1];
                    double[]? first = null;
                    for (int i = 0; i < samples.Count - 1 && first == null; i++)
                    {
                        if (Math.Abs(samples[i][1] - last[1]) <= 0.12)
                        {
                            first = samples[i];
                        }
                    }

                    string probeText = "no earlier probe at the same fill level (" + last[1].ToString("P0") + ")";
                    double ratio = 1.0;
                    if (first != null)
                    {
                        ratio = last[2] / first[2];
                        probeText = "probe " + first[2].ToString("F1") + " ms at op " + first[0] + " (fill " + first[1].ToString("P0") + ") and " + last[2].ToString("F1")
                            + " ms at op " + last[0] + " (fill " + last[1].ToString("P0") + "), ratio " + ratio.ToString("F2");
                    }

                    TestContext.Out.WriteLine(_v.Name + ": " + _ops + " operations in " + total.ElapsedMilliseconds + " ms (" + (total.ElapsedMilliseconds / (double)_ops).ToString("F2")
                        + " ms each with every check; operation " + _tStep.ElapsedMilliseconds + " ms, file read " + _tRaw.ElapsedMilliseconds + " ms, image check " + _tFsck.ElapsedMilliseconds
                        + " ms, listing " + _tList.ElapsedMilliseconds + " ms, read back " + _tRead.ElapsedMilliseconds + " ms); " + probeText
                        + "; managed memory without the model " + (memoryAtMark / 1024) + " KB at op " + markMemory + " and " + (memoryEnd / 1024) + " KB at the end (samples "
                        + string.Join(" ", memorySamples.ConvertAll(m => (m / 1024).ToString())) + " KB); most extents of a file " + _maxExtentsSeen
                        + ", files over four extents at once " + _filesOverFour);

                    if (first != null)
                    {
                        Assert.That(ratio, Is.LessThan(MaxProbeRatio), "the probe got " + ratio.ToString("F2") + " times slower between op " + first[0] + " and the end at about the same fill level (" + Where() + ")");
                    }
                    else
                    {
                        Assert.Warn("speed ratio not asserted: " + probeText);
                    }

                    // A leak shows as growth in nearly every interval. Measured on all variants (500 operations, 10 samples, model bytes taken off): most
                    // intervals change by 0 to 2 KB, and now and then ONE interval jumps up once by 12 to 410 KB and stays there (a buffer or cache that is
                    // allocated once; it differs from run to run with the same seed, so it is not a per-operation leak). A leak that keeps one object of 100
                    // bytes per operation grows 5 KB per interval in all of them. So: more than 6 of the 9 intervals growing by more than 512 bytes, and
                    // more than 32 KB in all, fails. A single big jump alone does not.
                    int growing = 0;
                    for (int i = 1; i < memorySamples.Count; i++)
                    {
                        if (memorySamples[i] - memorySamples[i - 1] > 512)
                        {
                            growing++;
                        }
                    }

                    bool leak = growing > 6 && memoryEnd - memoryAtMark > 32 * 1024;
                    Assert.That(leak, Is.False, "managed memory keeps growing: " + growing + " of " + (memorySamples.Count - 1) + " intervals grew, from " + memoryAtMark + " to " + memoryEnd + " bytes (" + Where() + ")");

                    if (_v.Kind == Kind.DragonDos)
                    {
                        Assert.That(_filesOverFour, Is.GreaterThan(0), "no file ever had more than four extents, so no continuation entry was exercised (" + Where() + ")");
                    }

                    string seenAtEnd = RunToolshed();
                    if (seenAtEnd.Length > 0)
                    {
                        TestContext.Out.WriteLine(seenAtEnd);
                    }

                    FillTheDisk();
                    DeleteEverything();

                    Close();
                    using (FileStream exclusive = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        Assert.That(exclusive.Length, Is.GreaterThan(0));
                    }
                }
                finally
                {
                    try
                    {
                        _driver?.Dispose();
                        _stream?.Dispose();
                    }
                    catch (Exception)
                    {
                    }

                    try
                    {
                        if (Directory.Exists(_folder))
                        {
                            Directory.Delete(_folder, true);
                        }
                    }
                    catch (IOException)
                    {
                    }
                }
            }

            // Measured ratios between the last and an earlier probe at the same fill level: 0.62 to 1.50 over the 17 variants; 3.0 is twice the worst.
            private const double MaxProbeRatio = 3.0;

            private void FillTheDisk()
            {
                _what = "fill the disk";
                int guard = 0;
                while (true)
                {
                    _op++;
                    byte[] before = Raw();
                    string name = NewName();
                    byte[] data = Content(20000 + (guard % 3 * 1000));
                    _what = "fill: create " + name + " " + data.Length + " bytes";
                    bool full = false;
                    try
                    {
                        _driver.WriteFile("/" + name, data);
                    }
                    catch (IOException)
                    {
                        full = true;
                    }

                    if (full)
                    {
                        byte[] after = Raw();
                        if (!after.AsSpan().SequenceEqual(before))
                        {
                            Fail("the write that failed because the disk is full changed the image file");
                        }

                        VerifyAll(false);
                        break;
                    }

                    _model[name] = data;
                    VerifyAll(true);
                    if (++guard > 80)
                    {
                        Fail("the disk never filled up");
                    }
                }

                // Smaller and smaller files go into what is left; each one that fits is checked as before.
                int[] sizes = { 5000, 1000, 256, 1 };
                for (int i = 0; i < sizes.Length; i++)
                {
                    _op++;
                    string name = NewName();
                    byte[] data = Content(sizes[i]);
                    _what = "fill: try " + name + " " + sizes[i] + " bytes";
                    byte[] before = Raw();
                    try
                    {
                        _driver.WriteFile("/" + name, data);
                        _model[name] = data;
                    }
                    catch (IOException)
                    {
                        if (!Raw().AsSpan().SequenceEqual(before))
                        {
                            Fail("a failed write changed the image file");
                        }
                    }

                    VerifyAll(true);
                }
            }

            private void DeleteEverything()
            {
                while (_model.Count > 0)
                {
                    _op++;
                    DoDelete();
                    VerifyAll(true);
                }

                _what = "all deleted";
                VerifyAll(false);
                int free = Free();
                if (_v.Kind == Kind.Os9)
                {
                    // RBF never shortens a directory file, so the sectors it grew by stay used.
                    byte[] sectors = SectorsNow(Raw());
                    int grew = CheckOs9(sectors).DirectorySectors - _os9DirSectors0;
                    if (free != _free0 - grew)
                    {
                        Fail("after deleting everything " + free + " sectors are free, expected " + (_free0 - grew) + " (start " + _free0 + ", root directory grew by " + grew + ")");
                    }
                }
                else if (free != _free0)
                {
                    Fail("after deleting everything " + free + " units are free, at the start " + _free0);
                }
            }

            // Reads the final image with Toolshed when it is there (DECB and OS-9). Returns a note, or an empty string.
            private string RunToolshed()
            {
                if (_v.Kind == Kind.DragonDos || _v.Box != Box.Jvc)
                {
                    return string.Empty;
                }

                string tool = _v.Kind == Kind.Decb ? "decb" : "os9";
                string image = ToWsl(_path);
                int code = Wsl(out string probe, tool);
                if (code == -999 || (_v.Kind == Kind.Decb && probe.IndexOf("Toolshed", StringComparison.Ordinal) < 0))
                {
                    return _v.Name + ": Toolshed " + tool + " is not available through WSL Ubuntu here, so the independent read-back was skipped";
                }

                if (_v.Kind == Kind.Os9)
                {
                    Wsl(out string check, "os9", "dcheck", image);
                    if (check.Contains("execvpe(os9) failed") || check.Contains("not found"))
                    {
                        return _v.Name + ": Toolshed os9 is not installed in WSL Ubuntu, so the independent read-back was skipped";
                    }

                    Assert.That(check, Does.Contain("file structure is intact"), check + " [" + Where() + "]");
                    Assert.That(check, Does.Contain("0 clusters in file structure but not in allocation map"), check + " [" + Where() + "]");
                    Assert.That(check, Does.Contain("0 previously allocated cluster found"), check + " [" + Where() + "]");
                }

                int compared = 0;
                string outFile = Path.Combine(_folder, "toolshed.out");
                Dictionary<string, byte[]>.Enumerator e = _model.GetEnumerator();
                while (e.MoveNext() && compared < 8)
                {
                    if (File.Exists(outFile))
                    {
                        File.Delete(outFile);
                    }

                    string spec = image + "," + e.Current.Key;
                    int exit = _v.Kind == Kind.Decb
                        ? Wsl(out string text, "decb", "copy", "-b", spec, ToWsl(outFile))
                        : Wsl(out text, "os9", "copy", spec, ToWsl(outFile));
                    Assert.That(File.Exists(outFile), Is.True, tool + " copy of " + e.Current.Key + " gave no file (exit " + exit + "): " + text + " [" + Where() + "]");
                    Assert.That(File.ReadAllBytes(outFile).AsSpan().SequenceEqual(e.Current.Value), Is.True,
                        "Toolshed reads " + e.Current.Key + " different from the model [" + Where() + "]");
                    compared++;
                }

                return _v.Name + ": Toolshed " + tool + " read back " + compared + " files identical" + (_v.Kind == Kind.Os9 ? " and dcheck found the file structure intact" : string.Empty);
            }

            private static string ToWsl(string windowsPath)
            {
                string full = Path.GetFullPath(windowsPath);
                return "/mnt/" + char.ToLowerInvariant(full[0]) + full.Substring(2).Replace('\\', '/');
            }

            // Runs a program in WSL Ubuntu. Returns its exit code, or -999 when WSL cannot start.
            private static int Wsl(out string output, params string[] arguments)
            {
                output = string.Empty;
                try
                {
                    ProcessStartInfo info = new ProcessStartInfo("wsl.exe");
                    info.ArgumentList.Add("-d");
                    info.ArgumentList.Add("Ubuntu");
                    info.ArgumentList.Add("--exec");
                    for (int i = 0; i < arguments.Length; i++)
                    {
                        info.ArgumentList.Add(arguments[i]);
                    }

                    info.RedirectStandardOutput = true;
                    info.RedirectStandardError = true;
                    info.UseShellExecute = false;
                    info.CreateNoWindow = true;
                    using (Process? process = Process.Start(info))
                    {
                        if (process == null)
                        {
                            return -999;
                        }

                        System.Threading.Tasks.Task<string> error = process.StandardError.ReadToEndAsync();
                        string standardOutput = process.StandardOutput.ReadToEnd();
                        if (!process.WaitForExit(120000))
                        {
                            process.Kill(true);
                            return -998;
                        }

                        output = standardOutput + error.Result;
                        return process.ExitCode;
                    }
                }
                catch (Exception)
                {
                    return -999;
                }
            }
        }
    }
}
