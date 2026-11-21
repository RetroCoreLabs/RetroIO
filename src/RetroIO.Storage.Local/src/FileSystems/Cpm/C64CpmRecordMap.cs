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
    /// Where a Commodore 64 CP/M 2.2 disk keeps each CP/M record on a 1541 disk (a D64 image).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The C128 BIOS reads these disks as its "C64" type (CXDISK, <c>dpb$c64$cpm</c> and
    /// <c>set$up$c64</c>): <c>dpb 256,17,34,1024,64,3</c>, so 34 CP/M tracks of 17 sectors of 256
    /// bytes, three reserved tracks, 1 KB blocks and 64 directory entries. The CP/M track becomes
    /// the Commodore track, with 1 added from the directory track (<c>dir$track</c> = 18, CXEQU)
    /// upward, so Commodore track 18 is stepped over; the sector is used as it is, with no skew.
    /// Sectors 17 and up of the outer tracks are not used.
    /// </para>
    /// <para>
    /// Record 0 of the data area is CP/M track 3 sector 0, Commodore track 3 sector 0, where the
    /// directory starts. Checked on a real C64 CP/M 2.2 system disk: the directory lists the CP/M
    /// 2.2 programs (MOVCPM, PIP, ED, ASM, DDT, STAT, ...).
    /// </para>
    /// </remarks>
    public sealed class C64CpmRecordMap : ICpmRecordMap
    {
        private readonly int _recordCount;

        private const int SectorsPerTrack = 17;
        private const int ReservedTracks = 3;
        private const int DirectoryTrack = 18;

        /// <summary>
        /// Creates the map for a C64 CP/M 2.2 disk.
        /// </summary>
        /// <param name="recordCount">
        /// Number of 256-byte records in the data area: the number of blocks times 4.
        /// </param>
        public C64CpmRecordMap(int recordCount)
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

            TrackAndSector(record / SectorsPerTrack + ReservedTracks, record % SectorsPerTrack, out int track, out int sector);
            return ((long)SectorsBefore(track) + sector) * 256;
        }

        /// <summary>
        /// Runs the BIOS step <c>set$up$c64</c>: gives the Commodore track and sector of a CP/M track and sector.
        /// </summary>
        /// <param name="cpmTrack">
        /// CP/M track, 0 to 33 (<c>@trk</c>).
        /// </param>
        /// <param name="cpmSector">
        /// CP/M sector, 0 to 16.
        /// </param>
        /// <param name="track">
        /// Commodore track (<c>vic$trk</c>): the CP/M track, plus 1 from track 18 up.
        /// </param>
        /// <param name="sector">
        /// Commodore sector (<c>VIC$sect</c>): the CP/M sector unchanged.
        /// </param>
        public static void TrackAndSector(int cpmTrack, int cpmSector, out int track, out int sector)
        {
            track = cpmTrack >= DirectoryTrack ? cpmTrack + 1 : cpmTrack;
            sector = cpmSector;
        }

        // 256-byte sectors in a D64 image before a Commodore track: 21, 19, 18 and 17 sectors a track.
        private static int SectorsBefore(int track)
        {
            int total = 0;
            for (int t = 1; t < track; t++)
            {
                total += t <= 17 ? 21 : t <= 24 ? 19 : t <= 30 ? 18 : 17;
            }

            return total;
        }
    }
}
