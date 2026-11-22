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
    /// Finds the CP/M records of an Amstrad CPC disk inside a CPC DSK image file.
    /// </summary>
    /// <remarks>
    /// <para><b>The DSK container</b></para>
    /// A DSK file starts with a 256-byte disk header, then one block per track, each a 256-byte
    /// "Track-Info" header followed by the sector data in the order the sectors were found on the
    /// track. Two kinds exist:
    ///  - "MV - CPC": every track block has the size in header bytes 32h-33h (low byte first).
    ///  - "EXTENDED CPC DSK": byte 34h on holds one size per track, in units of 256 bytes; 0 means
    ///    the track was not formatted.
    /// The track header gives the sector count at 15h and, from 18h, eight bytes per sector:
    /// C, H, R (the sector ID), N, two status bytes and, in an extended image, the data length.
    /// <para><b>The Amstrad formats</b></para>
    /// Each track has nine 512-byte sectors. Their IDs tell the format: 41h-49h is the CPC system
    /// format (two reserved tracks for the CP/M system), C1h-C9h the CPC data format (none), and
    /// 01h-09h the Amstrad PCW single-sided format (one reserved track; measured on real PCW disks:
    /// the directory starts track 1). A track's
    /// sectors are read in ID order, whatever order they sit in on the track (a real disk has
    /// them interleaved 45h, 41h, 46h, ...). Both measured on real disks.
    /// </remarks>
    public sealed class AmstradCpmRecordMap : ICpmRecordMap
    {
        private const int SectorSize = 512;
        private const int SectorsPerTrack = 9;

        // Image offset of each 512-byte CP/M-visible sector, after the reserved tracks, in order.
        private readonly long[] _sectorOffsets;

        private AmstradCpmRecordMap(long[] sectorOffsets)
        {
            _sectorOffsets = sectorOffsets;
        }

        /// <inheritdoc/>
        public int RecordSize => 128;

        /// <inheritdoc/>
        public int RecordCount => _sectorOffsets.Length * 4;

        /// <summary>
        /// Reads the DSK container and builds the map.
        /// </summary>
        /// <param name="image">
        /// The whole DSK file.
        /// </param>
        /// <param name="firstSectorId">
        /// The lowest sector ID on the first track: 41h for the CPC system format, C1h for the CPC
        /// data format, 01h for the PCW format.
        /// </param>
        /// <param name="trackCount">
        /// Number of tracks on side 0.
        /// </param>
        /// <returns>
        /// The map, or <c>null</c> when the image is not a single-sided Amstrad CP/M disk in DSK form.
        /// </returns>
        public static AmstradCpmRecordMap? TryRead(ReadOnlySpan<byte> image, out byte firstSectorId, out int trackCount)
        {
            firstSectorId = 0;
            trackCount = 0;
            bool extended = StartsWith(image, "EXTENDED CPC DSK");
            if (!extended && !StartsWith(image, "MV - CPC"))
            {
                return null;
            }

            int tracks = image[0x30];
            int sides = image[0x31];
            if (sides != 1 || tracks == 0)
            {
                return null;
            }

            long[] all = new long[tracks * SectorsPerTrack];
            long at = 256;
            for (int t = 0; t < tracks; t++)
            {
                int blockSize = extended ? image[0x34 + t] * 256 : image[0x32] | (image[0x33] << 8);
                if (blockSize == 0 || at + blockSize > image.Length || !StartsWith(image.Slice((int)at), "Track-Info"))
                {
                    return null;
                }

                ReadOnlySpan<byte> header = image.Slice((int)at, 256);
                if (header[0x15] != SectorsPerTrack || header[0x14] != 2)
                {
                    return null;
                }

                byte lowest = 0xFF;
                for (int s = 0; s < SectorsPerTrack; s++)
                {
                    byte id = header[0x18 + s * 8 + 2];
                    if (id < lowest)
                    {
                        lowest = id;
                    }
                }

                if (t == 0)
                {
                    if (lowest != 0x41 && lowest != 0xC1 && lowest != 0x01)
                    {
                        return null;
                    }

                    firstSectorId = lowest;
                }
                else if (lowest != firstSectorId)
                {
                    return null;
                }

                long data = at + 256;
                for (int s = 0; s < SectorsPerTrack; s++)
                {
                    int length = SectorSize;
                    if (extended)
                    {
                        length = header[0x18 + s * 8 + 6] | (header[0x18 + s * 8 + 7] << 8);
                    }

                    int index = header[0x18 + s * 8 + 2] - firstSectorId;
                    if (length != SectorSize || index < 0 || index >= SectorsPerTrack || all[t * SectorsPerTrack + index] != 0)
                    {
                        return null;
                    }

                    all[t * SectorsPerTrack + index] = data;
                    data += length;
                }

                at += blockSize;
            }

            int reserved = ReservedTracks(firstSectorId);
            long[] visible = new long[(tracks - reserved) * SectorsPerTrack];
            Array.Copy(all, reserved * SectorsPerTrack, visible, 0, visible.Length);
            trackCount = tracks;
            return new AmstradCpmRecordMap(visible);
        }

        /// <summary>
        /// Gives the number of reserved tracks of an Amstrad format.
        /// </summary>
        /// <param name="firstSectorId">
        /// The lowest sector ID: 41h (CPC system), C1h (CPC data) or 01h (PCW).
        /// </param>
        /// <returns>
        /// Two, none or one.
        /// </returns>
        public static int ReservedTracks(byte firstSectorId)
        {
            return firstSectorId == 0x41 ? 2 : firstSectorId == 0x01 ? 1 : 0;
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="record"/> is not on the disk.
        /// </exception>
        public long ImageOffset(int record)
        {
            if (record < 0 || record >= RecordCount)
            {
                throw new ArgumentOutOfRangeException(nameof(record), record, "not a record of this CP/M disk");
            }

            return _sectorOffsets[record / 4] + (record % 4) * 128;
        }

        private static bool StartsWith(ReadOnlySpan<byte> data, string text)
        {
            if (data.Length < text.Length)
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (data[i] != text[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
