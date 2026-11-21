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
    /// A CP/M disk with 128-byte sectors whose BIOS translates each logical sector through a skew
    /// table, stored as a raw image in physical sector order.
    /// </summary>
    /// <remarks>
    /// CP/M 2.2 asks the BIOS to translate every logical sector of the data area (SECTRAN, with the
    /// table the Disk Parameter Header points at); logical sector s of a track is physical sector
    /// table[s], counted from 1. The reserved tracks are not translated, as the BDOS never reads them.
    /// </remarks>
    public sealed class SkewedCpmRecordMap : ICpmRecordMap
    {
        private readonly long _dataStart;
        private readonly int _recordCount;
        private readonly byte[] _table;

        /// <summary>
        /// Creates the map.
        /// </summary>
        /// <param name="dataStart">
        /// Byte offset in the image of the first track after the reserved tracks.
        /// </param>
        /// <param name="recordCount">
        /// Number of 128-byte records in the data area.
        /// </param>
        /// <param name="table">
        /// The skew table: for each logical sector of a track, its physical sector number from 1.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when the table is not a reordering of 1 to its length.
        /// </exception>
        public SkewedCpmRecordMap(long dataStart, int recordCount, byte[] table)
        {
            bool[] seen = new bool[table.Length];
            for (int i = 0; i < table.Length; i++)
            {
                int physical = table[i] - 1;
                if (physical < 0 || physical >= table.Length || seen[physical])
                {
                    throw new ArgumentException("the skew table must hold each sector 1 to " + table.Length + " once", nameof(table));
                }

                seen[physical] = true;
            }

            _dataStart = dataStart;
            _recordCount = recordCount;
            _table = table;
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

            int track = record / _table.Length;
            int physical = _table[record % _table.Length] - 1;
            return _dataStart + ((long)track * _table.Length + physical) * 128;
        }
    }
}
