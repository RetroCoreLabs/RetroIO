//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Storage.Local.FileSystems.Cpm
{
    /// <summary>
    /// Says where each data record of a CP/M disk lies in the disk image.
    /// </summary>
    /// <remarks>
    /// CP/M numbers the data area as one run of records from 0; the machine's BIOS decides which
    /// physical track and sector each one is (reserved tracks, sector skew, two sides, speed zones).
    /// One implementation per machine family runs that BIOS rule. Record 0 is the first record of
    /// block 0, where the directory starts.
    /// </remarks>
    public interface ICpmRecordMap
    {
        /// <summary>
        /// Gets the size of one record in bytes, the unit <see cref="ImageOffset"/> counts in.
        /// </summary>
        int RecordSize { get; }

        /// <summary>
        /// Gets the number of records in the data area.
        /// </summary>
        int RecordCount { get; }

        /// <summary>
        /// Gets the byte offset in the image of a data record.
        /// </summary>
        /// <param name="record">
        /// Record number in the data area, from 0.
        /// </param>
        /// <returns>
        /// Offset of the first byte of <paramref name="record"/> in the image.
        /// </returns>
        long ImageOffset(int record);
    }
}
