//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Storage.Local.FileSystems
{
    /// <summary>
    /// Builds the sectors of a disk freshly formatted by the DragonDOS 1.2 <c>DSKINIT</c> command.
    /// </summary>
    /// <remarks>
    /// Follows the routine in the DragonDOS 1.2 source (ddos12.asm: CmdDskInit, BuildBAM, WriteBAM,
    /// MakeBlankDir and the SectorIDLayout2 table; dosdefs.asm for the constants), step by step:
    ///  - Every sector is written by the format with 256 bytes of E5h, 18 sectors per side.
    ///  - A three-sector buffer is cleared. In its first sector, bytes FCH to FFH get the number of
    ///    tracks, the sectors per track (18 or 36) and the complement of each.
    ///  - The first 90 bytes of the buffer (180 on a double-sided disk) are set to FFh: the free
    ///    sectors of a 40-track side. Only a double-sided 80-track disk also gets 180 bytes of FFh at
    ///    the start of the second sector. So a single-sided 80-track disk is marked free on its first
    ///    40 tracks only: this is what the routine does, and it is kept.
    ///  - Three bytes at the BAM offset of track 20 and of track 16 become 00h 00h FCh: 18 sectors in
    ///    use. On a double-sided disk that is only the first side of each of those tracks.
    ///  - The first two buffer sectors are written to sectors 1 and 2 of track 20, then of track 16.
    ///  - The third buffer sector gets attribute 89h (deleted, end of directory, continuation) at
    ///    every 25-byte entry, and is written to sectors 3 to 18 of track 16. The routine then jumps
    ///    to the DOS reset instead of returning, so track 20's directory sectors keep the E5h of the
    ///    format (the source notes this itself).
    /// <para>
    /// The sectors are in DragonDOS logical sector order: sector s of track t is at
    /// (t x sectors per track + s - 1) x 256, both sides of a cylinder forming one track.
    /// </para>
    /// </remarks>
    public static class DragonDosFormat
    {
        private const int SectorSize = 256;
        private const int SectorsPerSide = 18;
        private const int DirPrimary = 20;
        private const int DirBackup = 16;
        private const int DirEntryLength = 25;
        private const int DirEntriesPerSector = 10;
        private const byte AttrAtFormat = 0x89;

        /// <summary>
        /// Creates the sectors of a blank DragonDOS disk.
        /// </summary>
        /// <param name="tracks">
        /// 40 or 80.
        /// </param>
        /// <param name="sides">
        /// 1 or 2.
        /// </param>
        /// <returns>
        /// Every sector in logical order, tracks x sides x 18 x 256 bytes.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="tracks"/> or <paramref name="sides"/> is not one DSKINIT takes.
        /// </exception>
        public static byte[] CreateBlank(int tracks, int sides)
        {
            if (tracks != 40 && tracks != 80)
            {
                throw new ArgumentOutOfRangeException(nameof(tracks), tracks, "DSKINIT formats 40 or 80 tracks");
            }

            if (sides != 1 && sides != 2)
            {
                throw new ArgumentOutOfRangeException(nameof(sides), sides, "DSKINIT formats 1 or 2 sides");
            }

            int sectorsPerTrack = SectorsPerSide * sides;
            byte[] image = new byte[tracks * sectorsPerTrack * SectorSize];
            image.AsSpan().Fill(0xE5);

            // BuildBAM, on the cleared three-sector buffer.
            byte[] buffer = new byte[3 * SectorSize];
            bool doubleSided = sides == 2;
            buffer[0xFD] = (byte)sectorsPerTrack;
            buffer[0xFF] = (byte)~sectorsPerTrack;
            buffer[0xFC] = (byte)tracks;
            buffer[0xFE] = (byte)~tracks;
            int freeBytes = SectorsPerSide * 40 / 8 * (doubleSided ? 2 : 1);
            buffer.AsSpan(0, freeBytes).Fill(0xFF);
            if (doubleSided && tracks == 80)
            {
                buffer.AsSpan(SectorSize, 0xB4).Fill(0xFF);
            }

            int perCylinder = doubleSided ? 2 : 1;
            MarkInUse(buffer, SectorsPerSide * DirPrimary * perCylinder / 8);
            MarkInUse(buffer, SectorsPerSide * DirBackup * perCylinder / 8);

            // WriteBAM to track 20 and then track 16, sectors 1 and 2.
            WriteSector(image, sectorsPerTrack, DirPrimary, 1, buffer.AsSpan(0, SectorSize));
            WriteSector(image, sectorsPerTrack, DirPrimary, 2, buffer.AsSpan(SectorSize, SectorSize));
            WriteSector(image, sectorsPerTrack, DirBackup, 1, buffer.AsSpan(0, SectorSize));
            WriteSector(image, sectorsPerTrack, DirBackup, 2, buffer.AsSpan(SectorSize, SectorSize));

            // MakeBlankDir: the directory sector, written to track 16 only (see the remarks).
            Span<byte> directory = buffer.AsSpan(2 * SectorSize, SectorSize);
            for (int e = 0; e < DirEntriesPerSector; e++)
            {
                directory[e * DirEntryLength] = AttrAtFormat;
            }

            for (int s = 3; s <= 18; s++)
            {
                WriteSector(image, sectorsPerTrack, DirBackup, s, directory);
            }

            return image;
        }

        // 00h 00h FCh: sixteen sectors and two more in use (LC4C4).
        private static void MarkInUse(byte[] buffer, int offset)
        {
            buffer[offset] = 0x00;
            buffer[offset + 1] = 0x00;
            buffer[offset + 2] = 0xFC;
        }

        private static void WriteSector(byte[] image, int sectorsPerTrack, int track, int sector, ReadOnlySpan<byte> data)
        {
            data.CopyTo(image.AsSpan((track * sectorsPerTrack + sector - 1) * SectorSize, SectorSize));
        }
    }
}
