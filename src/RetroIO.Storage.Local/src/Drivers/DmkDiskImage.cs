//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// A DMK disk image, decoded into the 256-byte sectors it holds.
    /// </summary>
    /// <remarks>
    /// <para><b>The DMK format</b></para>
    /// DMK is the virtual disk format of David Keil's TRS-80 and Color Computer emulators. Unlike the JVC and VDK formats it keeps the
    /// disk as the controller wrote it: every track holds the raw bytes of the track (gaps, address marks, sector headers, data and CRCs),
    /// minus the clock bits. This class follows Keil's own technical notes (the technical information page, cocotech.htm, of the documentation
    /// that comes with his Color Computer emulator) and was checked against four real SuperDOS/DragonDOS disks in DMK form (one 40 track single sided,
    /// one 40 track double sided, one 80 track single sided, one 80 track double sided), where every sector header and every data field
    /// passed its CRC.
    /// <para><b>Disk header, 16 bytes at the start of the file</b></para>
    ///  - Byte 0: write protect. FFH means protected, 00H means writable.
    ///  - Byte 1: number of tracks (cylinders), one more than the highest track written. Keil says never to change it.
    ///  - Bytes 2 and 3: track length, low byte first, and it includes the 128-byte track header. Keil's values: 1900H (the default, 6400 bytes),
    ///    0CC0H (single density 5.25 inch), 14E0H (single density 8 inch), 2940H (double density 8 inch, the stated maximum).
    ///    The four real images all use 1900H.
    ///  - Byte 4: option flags. Bit 4 set means single sided. Bit 6 set means single density only, so each data byte is stored once.
    ///    Bit 7 set means ignore density, a flag of older files. The other bits are undefined.
    ///  - Bytes 5 to 11: reserved.
    ///  - Bytes 12 to 15: zero in the native format. The value 12345678H marks a file that describes a real disk drive instead of a disk image.
    /// <para><b>Track blocks</b></para>
    /// After the header come the tracks, one block of track-length bytes each. For a double sided disk the blocks go cylinder by cylinder
    /// with side 0 then side 1 inside each cylinder (cylinder 0 side 0, cylinder 0 side 1, cylinder 1 side 0, and so on). That is the order
    /// the sector headers of the real images confirm. The file is exactly 16 + tracks x sides x track length bytes long, and the real images match.
    /// <para><b>Track header, the first 128 bytes of a block</b></para>
    /// Up to 64 pointers of 2 bytes each, low byte first. Each points at the sector header (the IDAM, the byte FEH) of one sector,
    /// counted from the start of the block, so a header at offset 10H in the track data is the pointer 90H. The list is in ascending order and
    /// ends at the first pointer of 0000H (or after 64 pointers). Bit 15 of a pointer is set for a double density sector and clear for single
    /// density, bit 14 is undefined, and both are masked off to get the offset. Real images list a sector twice now and then, with the same
    /// offset; this reader keeps the first pointer of each sector.
    /// <para><b>Track data and a sector</b></para>
    /// In double density the bytes of a sector run as follows (the bytes between the fields are the gaps, 4EH and 00H):
    ///  - The sector header: FEH, cylinder, side, sector number, size code, then the CRC high byte and low byte. The size code gives the data
    ///    length: 128 shifted left by the code (0 is 128, 1 is 256, 2 is 512, 3 is 1024). The real images all have code 1.
    ///  - Gap 2: 22 bytes of 4EH and 12 bytes of 00H (the real images show 22 and 12).
    ///  - Sync: three bytes A1H. These may be missing in an image: 244 of the 720 sectors of the single sided real image have none, on
    ///    first and later sectors alike, so a reader must not require them.
    ///  - The data address mark: FBH for ordinary data, F8H for a deleted data mark.
    ///  - The data, as many bytes as the size code says, then the CRC high byte and low byte.
    /// In single density every byte of the track data is stored twice, including the headers and the CRCs, and the CRC is worked out as if
    /// the byte was written once. This reader does not decode single density, because there is no real single density image here to prove it against.
    /// <para><b>The CRCs</b></para>
    /// Both CRCs are the 16-bit CRC of the disk controller: polynomial 1021H, start value FFFFH, high bit first. The CRC covers the three sync
    /// bytes A1H as well, even where the image has dropped them, then the header bytes FEH, cylinder, side, sector and size code for the header
    /// CRC, or the data address mark and the data for the data CRC. Keil warns that changing sector data without changing its CRC causes errors.
    /// <para><b>What this reader does with it</b></para>
    /// It checks every CRC and stops with an error naming the cylinder, side and sector when one is wrong. It supports 256-byte sectors with
    /// the numbers 1 to N on every track, double density. Anything else (single density, other sizes, other numbering, a real-disk file)
    /// stops with NotSupportedException and a message that says what was found, so no wrong disk is read quietly.
    /// </remarks>
    public sealed class DmkDiskImage
    {
        private const int DiskHeaderSize = 16;
        private const int TrackHeaderSize = 128;
        private const int MaxPointers = 64;
        private const int SectorBytes = 256;
        private const int SizeCode256 = 1;
        private const int MaxImageSize = 64 * 1024 * 1024;

        private readonly byte[] _sectors;

        private DmkDiskImage(int cylinders, int sides, int trackLength, bool writeProtected, int sectorsPerTrack, byte[] sectors)
        {
            Cylinders = cylinders;
            Sides = sides;
            TrackLength = trackLength;
            WriteProtected = writeProtected;
            SectorsPerTrack = sectorsPerTrack;
            _sectors = sectors;
        }

        /// <summary>
        /// Gets the number of cylinders (the track count of the header).
        /// </summary>
        public int Cylinders { get; }

        /// <summary>
        /// Gets the number of sides, 1 or 2.
        /// </summary>
        public int Sides { get; }

        /// <summary>
        /// Gets the length in bytes of one track block, including its 128-byte header.
        /// </summary>
        public int TrackLength { get; }

        /// <summary>
        /// Gets a value indicating whether the header marks the disk as write protected.
        /// </summary>
        public bool WriteProtected { get; }

        /// <summary>
        /// Gets the number of sectors on every track.
        /// </summary>
        public int SectorsPerTrack { get; }

        /// <summary>
        /// Tells whether the first bytes of a file look like the header of a DMK image of exactly this size.
        /// </summary>
        /// <param name="header">
        /// The first bytes of the file, at least 16.
        /// </param>
        /// <param name="fileSize">
        /// The size of the whole file in bytes.
        /// </param>
        /// <returns>
        /// True when bytes 12 to 15 are zero, the track count is at least 1, the track length is above the 128-byte track header, and the file size is
        /// 16 plus tracks times sides times track length. A DMK file has no signature, so this is the whole test.
        /// </returns>
        public static bool LooksLikeDmk(ReadOnlySpan<byte> header, long fileSize)
        {
            if (header.Length < DiskHeaderSize)
            {
                return false;
            }

            if (header[12] != 0 || header[13] != 0 || header[14] != 0 || header[15] != 0)
            {
                return false;
            }

            int tracks = header[1];
            int trackLength = header[2] | (header[3] << 8);
            int sides = (header[4] & 0x10) != 0 ? 1 : 2;
            if (tracks < 1 || trackLength <= TrackHeaderSize)
            {
                return false;
            }

            return fileSize == DiskHeaderSize + ((long)tracks * sides * trackLength);
        }

        /// <summary>
        /// Reads a DMK image and decodes its sectors.
        /// </summary>
        /// <param name="stream">
        /// The image, read from its start to its end.
        /// </param>
        /// <returns>
        /// The decoded disk.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="stream"/> is null.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// Thrown when the header, a pointer, a sector header, a data mark or a CRC is not what the format says, or the file size does not fit the header.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// Thrown for a disk this reader does not decode: single density, sector sizes other than 256, sector numbers other than 1 to N,
        /// a real-disk specification file, or tracks with different sector counts.
        /// </exception>
        public static DmkDiskImage Parse(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            byte[] file = ReadAll(stream);
            if (file.Length < DiskHeaderSize)
            {
                throw new InvalidDataException("The DMK image is shorter than its 16-byte header (" + file.Length + " bytes)");
            }

            bool writeProtected = file[0] == 0xFF;
            int cylinders = file[1];
            int trackLength = file[2] | (file[3] << 8);
            byte flags = file[4];
            int sides = (flags & 0x10) != 0 ? 1 : 2;

            if (file[12] != 0 || file[13] != 0 || file[14] != 0 || file[15] != 0)
            {
                throw new NotSupportedException("DMK header bytes 12 to 15 are not zero (" + file[12].ToString("X2") + file[13].ToString("X2") + file[14].ToString("X2") + file[15].ToString("X2") + "): a real-disk specification file or an unknown extension");
            }

            if ((flags & 0x40) != 0)
            {
                throw new NotSupportedException("DMK single-density-only images (option flag bit 6) are not supported");
            }

            if ((flags & 0x80) != 0)
            {
                throw new NotSupportedException("DMK images that ignore density (option flag bit 7) are not supported");
            }

            if (cylinders < 1)
            {
                throw new InvalidDataException("The DMK header has no tracks");
            }

            if (trackLength <= TrackHeaderSize)
            {
                throw new InvalidDataException("The DMK track length " + trackLength + " leaves no room for track data");
            }

            long expected = DiskHeaderSize + ((long)cylinders * sides * trackLength);
            if (file.Length != expected)
            {
                throw new InvalidDataException(
                    "The DMK file is " + file.Length + " bytes but its header says " + cylinders + " tracks x " + sides + " sides x " + trackLength + " + 16 = " + expected);
            }

            byte[]? sectors = null;
            int sectorsPerTrack = 0;

            for (int cylinder = 0; cylinder < cylinders; cylinder++)
            {
                for (int side = 0; side < sides; side++)
                {
                    int blockStart = DiskHeaderSize + (((cylinder * sides) + side) * trackLength);
                    byte[][] found = ReadTrack(file, blockStart, trackLength, cylinder, side);

                    int count = 0;
                    int highest = 0;
                    for (int id = 0; id < found.Length; id++)
                    {
                        if (found[id] != null)
                        {
                            count++;
                            highest = id;
                        }
                    }

                    if (sectors == null)
                    {
                        if (count == 0 || highest != count || found[0] != null)
                        {
                            throw new NotSupportedException(
                                "DMK cylinder " + cylinder + " side " + side + " does not hold the sector numbers 1 to N (found " + count + " sectors, highest number " + highest + ")");
                        }

                        sectorsPerTrack = count;
                        sectors = new byte[cylinders * sides * sectorsPerTrack * SectorBytes];
                    }

                    if (count != sectorsPerTrack || highest != sectorsPerTrack || found[0] != null)
                    {
                        throw new NotSupportedException(
                            "DMK cylinder " + cylinder + " side " + side + " has " + count + " sectors (highest number " + highest + ") but the first track has " + sectorsPerTrack);
                    }

                    int trackStart = ((cylinder * sides) + side) * sectorsPerTrack * SectorBytes;
                    for (int id = 1; id <= sectorsPerTrack; id++)
                    {
                        Buffer.BlockCopy(found[id], 0, sectors, trackStart + ((id - 1) * SectorBytes), SectorBytes);
                    }
                }
            }

            return new DmkDiskImage(cylinders, sides, trackLength, writeProtected, sectorsPerTrack, sectors!);
        }

        /// <summary>
        /// Gets the sectors of the disk in logical order: cylinder by cylinder, side 0 then side 1 inside a cylinder, sector 1 to N inside a track.
        /// </summary>
        /// <returns>
        /// A copy of the sector data, cylinders x sides x sectors per track x 256 bytes long.
        /// </returns>
        public byte[] GetSectorImage()
        {
            byte[] copy = new byte[_sectors.Length];
            Buffer.BlockCopy(_sectors, 0, copy, 0, copy.Length);
            return copy;
        }

        /// <summary>
        /// Gets the same sectors behind a 5-byte JVC header, so that the DSK/JVC driver can mount them with the right geometry.
        /// </summary>
        /// <returns>
        /// The JVC image: sectors per track, sides, size code 1, first sector number 1, attribute flag 0, then the sectors.
        /// </returns>
        public byte[] ToJvcImage()
        {
            byte[] jvc = new byte[5 + _sectors.Length];
            jvc[0] = (byte)SectorsPerTrack;
            jvc[1] = (byte)Sides;
            jvc[2] = SizeCode256;
            jvc[3] = 1;
            jvc[4] = 0;
            Buffer.BlockCopy(_sectors, 0, jvc, 5, _sectors.Length);
            return jvc;
        }

        // Returns the sectors of one track block, indexed by sector number.
        private static byte[][] ReadTrack(byte[] file, int blockStart, int trackLength, int cylinder, int side)
        {
            byte[][] found = new byte[256][];

            for (int index = 0; index < MaxPointers; index++)
            {
                int pointer = file[blockStart + (index * 2)] | (file[blockStart + (index * 2) + 1] << 8);
                if (pointer == 0)
                {
                    break;
                }

                if ((pointer & 0x8000) == 0)
                {
                    throw new NotSupportedException("DMK cylinder " + cylinder + " side " + side + " has a single density sector, which is not supported");
                }

                int offset = pointer & 0x3FFF;
                if (offset < TrackHeaderSize || offset + 7 > trackLength)
                {
                    throw new InvalidDataException("DMK cylinder " + cylinder + " side " + side + ": sector pointer " + offset + " is outside the track");
                }

                int header = blockStart + offset;
                if (file[header] != 0xFE)
                {
                    throw new InvalidDataException("DMK cylinder " + cylinder + " side " + side + ": no sector header mark (FE) at offset " + offset);
                }

                int idCylinder = file[header + 1];
                int idSide = file[header + 2];
                int sector = file[header + 3];
                int sizeCode = file[header + 4];
                string where = "DMK cylinder " + cylinder + " side " + side + " sector " + sector;

                if (idCylinder != cylinder || idSide != side)
                {
                    throw new InvalidDataException(where + ": the sector header says cylinder " + idCylinder + " side " + idSide);
                }

                int storedHeaderCrc = (file[header + 5] << 8) | file[header + 6];
                if (Crc(file, header, 5) != storedHeaderCrc)
                {
                    throw new InvalidDataException(where + ": sector header CRC error");
                }

                if (sizeCode != SizeCode256)
                {
                    throw new NotSupportedException(where + " has size code " + sizeCode + "; only 256-byte sectors (code 1) are supported");
                }

                if (found[sector] != null)
                {
                    continue;
                }

                // Skip the gap (4E and 00) and the sync bytes (A1, which an image may have dropped) to the data address mark.
                int position = header + 7;
                int trackEnd = blockStart + trackLength;
                while (position < trackEnd && (file[position] == 0x4E || file[position] == 0x00 || file[position] == 0xA1))
                {
                    position++;
                }

                if (position >= trackEnd || (file[position] != 0xFB && file[position] != 0xF8))
                {
                    throw new InvalidDataException(where + ": no data address mark (FB or F8) after the sector header");
                }

                if (position + 1 + SectorBytes + 2 > trackEnd)
                {
                    throw new InvalidDataException(where + ": the sector data runs past the end of the track");
                }

                int storedDataCrc = (file[position + 1 + SectorBytes] << 8) | file[position + 2 + SectorBytes];
                if (Crc(file, position, 1 + SectorBytes) != storedDataCrc)
                {
                    throw new InvalidDataException(where + ": sector data CRC error");
                }

                byte[] data = new byte[SectorBytes];
                Buffer.BlockCopy(file, position + 1, data, 0, SectorBytes);
                found[sector] = data;
            }

            return found;
        }

        // The disk controller CRC: polynomial 1021H, start value FFFFH, high bit first, run over the three sync bytes A1H first and then the bytes.
        private static int Crc(byte[] data, int offset, int length)
        {
            int crc = 0xFFFF;
            crc = CrcByte(crc, 0xA1);
            crc = CrcByte(crc, 0xA1);
            crc = CrcByte(crc, 0xA1);
            for (int i = 0; i < length; i++)
            {
                crc = CrcByte(crc, data[offset + i]);
            }

            return crc;
        }

        private static int CrcByte(int crc, byte value)
        {
            crc ^= value << 8;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
            }

            return crc;
        }

        private static byte[] ReadAll(Stream stream)
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
                if (stream.Length > MaxImageSize)
                {
                    throw new InvalidDataException("The DMK image is larger than " + MaxImageSize + " bytes");
                }
            }

            using (MemoryStream copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                if (copy.Length > MaxImageSize)
                {
                    throw new InvalidDataException("The DMK image is larger than " + MaxImageSize + " bytes");
                }

                return copy.ToArray();
            }
        }
    }
}
