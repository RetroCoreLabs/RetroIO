//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Storage.Local.FileSystems.Cpm
{
    /// <summary>
    /// Where the Commodore 128 CP/M 3 BIOS keeps each CP/M record on a 1541 or 1571 (GCR) disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The C128 BIOS disk module (CXDISK, 15 Apr 86) reads a CP/M block as four 256-byte records and
    /// asks the C128's Z80 ROM where each one is: <c>RCALL FR$trk$sect</c>, which is <c>RST 5</c>
    /// with L = 50h, a jump through the table at Z80 address 0100h to the routine at 03E3h. That
    /// routine takes the record number from FD16h and leaves the track in FD03h and the sector in
    /// FD04h. <see cref="TrackAndSector"/> runs its steps one by one, in the same order:
    ///  - a record of 680 or more is on side 2: subtract 680 and add 35 to the track;
    ///  - add 2, so track 1 positions 0 and 1 are never used (sector 0 holds the "CBM" boot sector);
    ///  - zone 1, 21 sectors a track from track 1: if the count is 357 or more, subtract 357 and add
    ///    1, so track 18 position 0 (the Commodore header and block map) is skipped;
    ///  - zone 2, 19 sectors a track from track 18: 133 records;
    ///  - zone 3, 18 sectors a track from track 25: 108 records;
    ///  - zone 4, 17 sectors a track from track 31: the rest;
    ///  - within the zone, count whole tracks; the remainder is the position on the track;
    ///  - the position becomes a sector number through the skew table for the zone's track length,
    ///    four tables one after another at Z80 ROM address 0FB5h (21, 19, 18 and 17 entries).
    /// </para>
    /// <para>
    /// Track 18 is not reserved for CP/M: apart from its sector 0, it holds CP/M data like any other
    /// track. A CBM DOS reader that follows the "directory" chain on track 18 of such a disk reads
    /// CP/M data and finds links that lead off the disk.
    /// </para>
    /// <para>
    /// Checked against a real 1571 disk (Turbo Pascal for the C128): the directory and the files
    /// read back correctly through this map.
    /// </para>
    /// </remarks>
    public sealed class C128CpmRecordMap : ICpmRecordMap
    {
        // Z80 ROM 0FB5h: the skew tables, 21 + 19 + 18 + 17 bytes, copied from the ROM.
        private static readonly byte[] Skew21 = { 0, 5, 10, 15, 20, 4, 9, 14, 19, 3, 8, 13, 18, 2, 7, 12, 17, 1, 6, 11, 16 };
        private static readonly byte[] Skew19 = { 0, 5, 10, 15, 1, 6, 11, 16, 2, 7, 12, 17, 3, 8, 13, 18, 4, 9, 14 };
        private static readonly byte[] Skew18 = { 0, 5, 10, 15, 2, 7, 12, 17, 4, 9, 14, 1, 6, 11, 16, 3, 8, 13 };
        private static readonly byte[] Skew17 = { 0, 5, 10, 15, 3, 8, 13, 1, 6, 11, 16, 4, 9, 14, 2, 7, 12 };

        private readonly bool _doubleSided;
        private readonly int _recordCount;

        // Records (256-byte sectors) in a D64 or D71 image before each track on one side; index 1-35.
        private static readonly int[] TrackStart = BuildTrackStarts();

        private const int SectorsPerSide = 683;

        /// <summary>
        /// Creates the map for a single-sided (1541) or double-sided (1571) C128 CP/M disk.
        /// </summary>
        /// <param name="doubleSided">
        /// <c>true</c> for a double-sided 1571 disk, where the last byte of track 1 sector 0 is FFh.
        /// </param>
        /// <param name="recordCount">
        /// Number of records in the data area: the number of blocks times the records per block.
        /// </param>
        public C128CpmRecordMap(bool doubleSided, int recordCount)
        {
            _doubleSided = doubleSided;
            _recordCount = recordCount;
        }

        /// <inheritdoc/>
        public int RecordSize => 256;

        /// <inheritdoc/>
        public int RecordCount => _recordCount;

        /// <inheritdoc/>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="record"/> is not in the data area.
        /// </exception>
        public long ImageOffset(int record)
        {
            if (record < 0 || record >= _recordCount)
            {
                throw new ArgumentOutOfRangeException(nameof(record), record, "not a record of this CP/M disk");
            }

            TrackAndSector(record, out int track, out int sector);
            int side = track > 35 ? 1 : 0;
            int sideTrack = track - side * 35;
            if (side == 1 && !_doubleSided)
            {
                throw new ArgumentOutOfRangeException(nameof(record), record, "the record is on side 2 of a single-sided disk");
            }

            return ((long)side * SectorsPerSide + TrackStart[sideTrack] + sector) * 256;
        }

        /// <summary>
        /// Runs the C128 Z80 ROM routine at 03E3h: gives the Commodore track and sector of a record.
        /// </summary>
        /// <param name="record">
        /// CP/M 256-byte record number, the value the BIOS puts in FD16h.
        /// </param>
        /// <param name="track">
        /// The track, 1 to 70 (what the ROM leaves in FD03h).
        /// </param>
        /// <param name="sector">
        /// The sector (what the ROM leaves in FD04h).
        /// </param>
        public static void TrackAndSector(int record, out int track, out int sector)
        {
            // 03E3h: assume side 2 (track offset 35), test record - 680.
            int trackOffset = 35;
            int count = record - 680;
            if (count < 0)
            {
                trackOffset = 0;
                count += 680;
            }

            // 03F8h: two INC HL.
            count += 2;

            // 03FAh: zone 1, 21 sectors, tracks counted from 0.
            int sectorsPerTrack = 21;
            int baseTrack = 0;
            count -= 357;
            if (count < 0)
            {
                count += 357;
            }
            else
            {
                // 0405h: INC HL, then zone 2.
                count += 1;
                sectorsPerTrack = 19;
                baseTrack = 17;
                count -= 133;
                if (count < 0)
                {
                    count += 133;
                }
                else
                {
                    sectorsPerTrack = 18;
                    baseTrack = 24;
                    count -= 108;
                    if (count < 0)
                    {
                        count += 108;
                    }
                    else
                    {
                        sectorsPerTrack = 17;
                        baseTrack = 30;
                    }
                }
            }

            // 0421h: count whole tracks (INC C before each subtraction), keep the remainder.
            int trackInSide = baseTrack;
            do
            {
                trackInSide++;
                count -= sectorsPerTrack;
            }
            while (count >= 0);

            count += sectorsPerTrack;
            track = trackOffset + trackInSide;

            // 0433h: the skew table for this track length.
            byte[] skew = sectorsPerTrack switch
            {
                21 => Skew21,
                19 => Skew19,
                18 => Skew18,
                _ => Skew17,
            };
            sector = skew[count];
        }

        // Records before each track on one side of a 1541 disk: 21, 19, 18 and 17 sectors a track.
        private static int[] BuildTrackStarts()
        {
            int[] starts = new int[36];
            int total = 0;
            for (int track = 1; track <= 35; track++)
            {
                starts[track] = total;
                total += track <= 17 ? 21 : track <= 24 ? 19 : track <= 30 ? 18 : 17;
            }

            return starts;
        }
    }
}
