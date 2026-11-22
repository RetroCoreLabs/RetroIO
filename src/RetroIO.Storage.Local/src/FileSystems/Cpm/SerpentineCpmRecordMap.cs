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
    /// A double-sided CP/M disk whose BIOS counts tracks down side 0 and back up side 1, stored as a
    /// raw image in cylinder order (cylinder 0 side 0, cylinder 0 side 1, cylinder 1 side 0, ...).
    /// </summary>
    /// <remarks>
    /// CP/M track t, for t below the cylinder count, is cylinder t on side 0; the next tracks go back
    /// from the last cylinder on side 1, so track (cylinders + k) is cylinder (cylinders - 1 - k) on
    /// side 1. Measured on the IBM PC-XT CP/M-86 1.10 320 KB disk: only side 0 holds data on the
    /// low cylinders, both sides on the last nine, and every .CMD file read this way starts with a
    /// code group header that fits its size.
    /// </remarks>
    public sealed class SerpentineCpmRecordMap : ICpmRecordMap
    {
        private readonly int _cylinders;
        private readonly int _trackBytes;
        private readonly int _reservedTracks;
        private readonly int _recordCount;

        /// <summary>
        /// Creates the map.
        /// </summary>
        /// <param name="cylinders">
        /// Cylinders per side.
        /// </param>
        /// <param name="trackBytes">
        /// Bytes in one track of one side.
        /// </param>
        /// <param name="reservedTracks">
        /// CP/M tracks before the data area, counted the same way.
        /// </param>
        /// <param name="recordCount">
        /// Number of 128-byte records in the data area.
        /// </param>
        public SerpentineCpmRecordMap(int cylinders, int trackBytes, int reservedTracks, int recordCount)
        {
            _cylinders = cylinders;
            _trackBytes = trackBytes;
            _reservedTracks = reservedTracks;
            _recordCount = recordCount;
        }

        /// <inheritdoc/>
        public int RecordSize => 128;

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

            long cpmByte = (long)_reservedTracks * _trackBytes + (long)record * 128;
            int track = (int)(cpmByte / _trackBytes);
            int within = (int)(cpmByte % _trackBytes);
            int physicalTrack = track < _cylinders ? track * 2 : (2 * _cylinders - 1 - track) * 2 + 1;
            return (long)physicalTrack * _trackBytes + within;
        }
    }
}
