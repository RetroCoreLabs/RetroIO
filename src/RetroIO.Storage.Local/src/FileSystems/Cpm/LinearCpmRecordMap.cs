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
    /// A CP/M disk whose data area follows the reserved tracks as one run of 128-byte records.
    /// </summary>
    /// <remarks>
    /// This is the case for a raw image whose sectors are stored in the order the BIOS reads them,
    /// with no skew between: the Kaypro II and Kaypro 4 raw images, where the data area starts one
    /// track (10 sectors of 512 bytes) into the image. Checked on real Kaypro disks: the directory
    /// and the text files read back clean.
    /// </remarks>
    public sealed class LinearCpmRecordMap : ICpmRecordMap
    {
        private readonly long _dataStart;
        private readonly int _recordCount;

        /// <summary>
        /// Creates the map.
        /// </summary>
        /// <param name="dataStart">
        /// Byte offset in the image where the data area starts (after the reserved tracks).
        /// </param>
        /// <param name="recordCount">
        /// Number of 128-byte records in the data area.
        /// </param>
        public LinearCpmRecordMap(long dataStart, int recordCount)
        {
            _dataStart = dataStart;
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

            return _dataStart + (long)record * 128;
        }
    }
}
