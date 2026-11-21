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
    /// Where the Commodore 128 CP/M 3 BIOS keeps each CP/M record on a 1581 disk (a D81 image).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The BIOS disk module (CXDISK, label <c>c1581$adj</c>) treats the 1581 as a disk of 159
    /// CP/M tracks of five 1 KB sectors (<c>dpb 1024,5,159,2048,128,0</c>) and turns a CP/M track
    /// and sector into a drive track, side and 512-byte sector like this:
    ///  - a CP/M track of 78 or more gets 1 added, so CP/M "track 78" is never used; that is the
    ///    bottom side of drive track 39, the half of Commodore track 40 with the Commodore header,
    ///    block map and directory;
    ///  - the drive track is that number divided by 2, and an odd number means the top side;
    ///  - the drive's 512-byte sector is 2 times the CP/M sector plus 1, and two of them (the BIOS
    ///    count <c>vic$count</c> = 2) make one 1 KB CP/M sector.
    /// No skew is used (the BIOS installs a sector translate of 0).
    /// </para>
    /// <para>
    /// A D81 image holds each Commodore track (drive track + 1) as 40 sectors of 256 bytes: the bottom
    /// side first (sectors 0 to 19, two per 512-byte drive sector), then the top side (20 to 39).
    /// That order was checked on real disks: read the other way, the directory is garbage. The
    /// track 78 skip was checked the same way: Turbo Pascal source files placed past it read back
    /// clean with the skip and damaged without it.
    /// </para>
    /// </remarks>
    public sealed class C1581CpmRecordMap : ICpmRecordMap
    {
        private readonly int _recordCount;

        private const int CpmSectorsPerTrack = 5;
        private const int RecordsPerCpmSector = 4;
        private const int SkippedCpmTrack = 78;
        private const int ImageSectorsPerTrack = 40;
        private const int ImageSectorsPerSide = 20;

        /// <summary>
        /// Creates the map for a C128 CP/M 1581 disk.
        /// </summary>
        /// <param name="recordCount">
        /// Number of 256-byte records in the data area: the number of blocks times 8.
        /// </param>
        public C1581CpmRecordMap(int recordCount)
        {
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

            int cpmSector = record / RecordsPerCpmSector;
            int part = record % RecordsPerCpmSector;
            DriveAddress(cpmSector / CpmSectorsPerTrack, cpmSector % CpmSectorsPerTrack, out int driveTrack, out bool top, out int driveSector);

            // The 1 KB CP/M sector is drive sectors driveSector and driveSector + 1 (512 bytes each),
            // which are image sectors 2 * (driveSector - 1) onward on that side.
            int physical = driveSector + part / 2;
            int imageSector = 2 * (physical - 1) + part % 2 + (top ? ImageSectorsPerSide : 0);
            return ((long)driveTrack * ImageSectorsPerTrack + imageSector) * 256;
        }

        /// <summary>
        /// Runs the BIOS step <c>c1581$adj</c>: gives the drive track, side and first 512-byte sector.
        /// </summary>
        /// <param name="cpmTrack">
        /// CP/M track, 0 to 158 (the BIOS's <c>@trk</c>).
        /// </param>
        /// <param name="cpmSector">
        /// CP/M 1 KB sector, 0 to 4 (<c>@sect</c>).
        /// </param>
        /// <param name="driveTrack">
        /// Drive track from 0 (<c>vic$trk</c>).
        /// </param>
        /// <param name="top">
        /// <c>true</c> for the top side (bit 7 of <c>VIC$sect</c>).
        /// </param>
        /// <param name="driveSector">
        /// First 512-byte drive sector, an odd number from 1 to 9.
        /// </param>
        public static void DriveAddress(int cpmTrack, int cpmSector, out int driveTrack, out bool top, out int driveSector)
        {
            int track = cpmTrack >= SkippedCpmTrack ? cpmTrack + 1 : cpmTrack;
            driveTrack = track >> 1;
            top = (track & 1) != 0;
            driveSector = 2 * cpmSector + 1;
        }
    }
}
