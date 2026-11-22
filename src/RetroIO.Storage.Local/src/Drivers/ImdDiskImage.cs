//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// An ImageDisk (.IMD) floppy image: decoded to a flat image of its sectors and encoded back.
    /// </summary>
    /// <remarks>
    /// <para><b>The file</b></para>
    /// Follows the format description that comes with ImageDisk 1.18 (Dave Dunfield, IMD.TXT):
    ///  - an ASCII header starting "IMD ", with a comment, ended by a 1Ah byte;
    ///  - then one record per track: mode, cylinder, head, sector count, sector size code
    ///    (size is 128 shifted left by it), the sector numbering map, a cylinder map when head bit 7
    ///    is set, a head map when head bit 6 is set, then one data record per sector;
    ///  - a data record starts with a type byte: 0 the sector could not be read (no data), an odd
    ///    type (1, 3, 5, 7) is followed by the whole sector, an even type (2, 4, 6, 8) by one byte
    ///    that fills it. Types 3 and 4 are deleted-data sectors, 5 and 6 had a read error, 7 and 8
    ///    both.
    /// <para><b>The flat image</b></para>
    /// Tracks in cylinder order, side 0 before side 1, and each track's sectors in sector-number
    /// order, whatever order they were found in. An unreadable sector reads as zeros.
    /// <para><b>Writing back</b></para>
    /// <see cref="Encode"/> keeps the header, the track order and records, and the deleted and error
    /// marks of each sector, and stores a sector of one repeated byte in the short form, as
    /// ImageDisk does. A sector that could not be read stays type 0 while it is all zeros; once
    /// something is written into it, it is stored as a normal sector.
    /// </remarks>
    public sealed class ImdDiskImage
    {
        private readonly byte[] _header;
        private readonly List<ImdTrack> _tracks;
        private readonly byte[] _flat;

        private ImdDiskImage(byte[] header, List<ImdTrack> tracks, byte[] flat)
        {
            _header = header;
            _tracks = tracks;
            _flat = flat;
        }

        /// <summary>
        /// Gets the sectors as one flat image, in the order the remarks describe.
        /// </summary>
        public byte[] Flat => _flat;

        /// <summary>
        /// Gets the comment from the file header (the text after the first line).
        /// </summary>
        public string Comment
        {
            get
            {
                int lineEnd = Array.IndexOf(_header, (byte)'\n');
                int start = lineEnd < 0 ? _header.Length : lineEnd + 1;
                char[] text = new char[_header.Length - start];
                for (int i = 0; i < text.Length; i++)
                {
                    text[i] = (char)_header[start + i];
                }

                return new string(text).TrimEnd('\r', '\n');
            }
        }

        /// <summary>
        /// Tells whether a file starts like an ImageDisk image.
        /// </summary>
        /// <param name="data">
        /// The start of the file.
        /// </param>
        /// <returns>
        /// <c>true</c> when the file starts with "IMD ".
        /// </returns>
        public static bool IsImd(ReadOnlySpan<byte> data)
        {
            return data.Length >= 4 && data[0] == 'I' && data[1] == 'M' && data[2] == 'D' && data[3] == ' ';
        }

        /// <summary>
        /// Decodes an ImageDisk file.
        /// </summary>
        /// <param name="file">
        /// The whole .IMD file.
        /// </param>
        /// <returns>
        /// The decoded image.
        /// </returns>
        /// <exception cref="InvalidDataException">
        /// Thrown when the file is not a complete ImageDisk image, or two sectors of one track have the
        /// same number.
        /// </exception>
        public static ImdDiskImage Decode(byte[] file)
        {
            ImdDiskImage? image = TryDecode(file, out string? error);
            return image ?? throw new InvalidDataException(error);
        }

        /// <summary>
        /// Decodes an ImageDisk file, or says why it cannot.
        /// </summary>
        /// <param name="file">
        /// The whole .IMD file.
        /// </param>
        /// <param name="error">
        /// Why the file is not a usable ImageDisk image, or <c>null</c>.
        /// </param>
        /// <returns>
        /// The decoded image, or <c>null</c> with <paramref name="error"/> set.
        /// </returns>
        public static ImdDiskImage? TryDecode(byte[] file, out string? error)
        {
            error = null;
            if (!IsImd(file))
            {
                error = "Not an ImageDisk file: it does not start with \"IMD \".";
                return null;
            }

            int end = Array.IndexOf(file, (byte)0x1A);
            if (end < 0)
            {
                error = "ImageDisk header has no 1Ah end mark.";
                return null;
            }

            byte[] header = new byte[end];
            Array.Copy(file, header, end);
            List<ImdTrack> tracks = new List<ImdTrack>();
            int p = end + 1;
            long flatLength = 0;
            while (p < file.Length)
            {
                if (!Need(file, p, 5, out error))
                {
                    return null;
                }
                ImdTrack track = new ImdTrack
                {
                    Mode = file[p],
                    Cylinder = file[p + 1],
                    HeadByte = file[p + 2],
                    SectorCount = file[p + 3],
                    SizeCode = file[p + 4],
                };
                p += 5;
                if (track.SizeCode > 6)
                {
                    error = "ImageDisk track " + track.Cylinder + ": sector size code " + track.SizeCode + " is not one of 0-6.";
                    return null;
                }

                int n = track.SectorCount;
                if (!Need(file, p, n, out error))
                {
                    return null;
                }
                track.Numbers = Slice(file, p, n);
                p += n;
                if ((track.HeadByte & 0x80) != 0)
                {
                    if (!Need(file, p, n, out error))
                    {
                        return null;
                    }
                    track.CylinderMap = Slice(file, p, n);
                    p += n;
                }

                if ((track.HeadByte & 0x40) != 0)
                {
                    if (!Need(file, p, n, out error))
                    {
                        return null;
                    }
                    track.HeadMap = Slice(file, p, n);
                    p += n;
                }

                int size = 128 << track.SizeCode;
                track.Types = new byte[n];
                track.Data = new byte[n][];
                for (int s = 0; s < n; s++)
                {
                    if (!Need(file, p, 1, out error))
                    {
                        return null;
                    }
                    byte type = file[p++];
                    if (type > 8)
                    {
                        error = "ImageDisk track " + track.Cylinder + ": sector record type " + type + " is not one of 0-8.";
                        return null;
                    }

                    byte[] sector = new byte[size];
                    if (type != 0 && (type & 1) == 1)
                    {
                        if (!Need(file, p, size, out error))
                        {
                            return null;
                        }
                        Array.Copy(file, p, sector, 0, size);
                        p += size;
                    }
                    else if (type != 0)
                    {
                        if (!Need(file, p, 1, out error))
                        {
                            return null;
                        }
                        Array.Fill(sector, file[p++]);
                    }

                    track.Types[s] = type;
                    track.Data[s] = sector;
                }

                track.Order = SortedByNumber(track, out error);
                if (error != null)
                {
                    return null;
                }
                flatLength += (long)n * size;
                tracks.Add(track);
            }

            // Cylinder order, side 0 before side 1; the file normally has them so already.
            tracks.Sort(CompareTracks);
            byte[] flat = new byte[flatLength];
            long at = 0;
            for (int t = 0; t < tracks.Count; t++)
            {
                ImdTrack track = tracks[t];
                track.FlatStart = at;
                for (int i = 0; i < track.Order.Length; i++)
                {
                    byte[] sector = track.Data[track.Order[i]];
                    Array.Copy(sector, 0, flat, at, sector.Length);
                    at += sector.Length;
                }
            }

            return new ImdDiskImage(header, tracks, flat);
        }

        /// <summary>
        /// Encodes the image as an ImageDisk file, with the sectors as they now are in
        /// <see cref="Flat"/>.
        /// </summary>
        /// <returns>
        /// The .IMD file bytes.
        /// </returns>
        public byte[] Encode()
        {
            using MemoryStream output = new MemoryStream();
            output.Write(_header, 0, _header.Length);
            output.WriteByte(0x1A);
            for (int t = 0; t < _tracks.Count; t++)
            {
                ImdTrack track = _tracks[t];
                output.WriteByte(track.Mode);
                output.WriteByte(track.Cylinder);
                output.WriteByte(track.HeadByte);
                output.WriteByte(track.SectorCount);
                output.WriteByte(track.SizeCode);
                output.Write(track.Numbers, 0, track.Numbers.Length);
                if (track.CylinderMap != null)
                {
                    output.Write(track.CylinderMap, 0, track.CylinderMap.Length);
                }

                if (track.HeadMap != null)
                {
                    output.Write(track.HeadMap, 0, track.HeadMap.Length);
                }

                // Sectors go back in the order the file had them; their data comes from the flat image.
                int size = 128 << track.SizeCode;
                for (int s = 0; s < track.SectorCount; s++)
                {
                    long from = track.FlatStart + (long)PlaceInOrder(track, s) * size;
                    ReadOnlySpan<byte> sector = new ReadOnlySpan<byte>(_flat, (int)from, size);
                    byte type = track.Types[s];
                    if (type == 0)
                    {
                        if (IsFilledWith(sector, 0))
                        {
                            output.WriteByte(0);
                            continue;
                        }

                        type = 1;
                    }

                    // Keep the deleted and error marks; choose the long or short form by the data.
                    byte normal = (byte)(((type - 1) | 1) & 0xFF);
                    if (IsFilledWith(sector, sector[0]))
                    {
                        output.WriteByte((byte)(normal + 1));
                        output.WriteByte(sector[0]);
                    }
                    else
                    {
                        output.WriteByte(normal);
                        output.Write(sector);
                    }
                }
            }

            return output.ToArray();
        }

        private static int[] SortedByNumber(ImdTrack track, out string? error)
        {
            error = null;
            int n = track.SectorCount;
            int[] order = new int[n];
            for (int i = 0; i < n; i++)
            {
                order[i] = i;
            }

            // Insertion sort by sector number; a track has at most a few dozen sectors.
            for (int i = 1; i < n; i++)
            {
                int item = order[i];
                int j = i - 1;
                while (j >= 0 && track.Numbers[order[j]] > track.Numbers[item])
                {
                    order[j + 1] = order[j];
                    j--;
                }

                order[j + 1] = item;
            }

            for (int i = 1; i < n; i++)
            {
                if (track.Numbers[order[i]] == track.Numbers[order[i - 1]])
                {
                    error = "ImageDisk cylinder " + track.Cylinder + " head " + (track.HeadByte & 1) + " has sector " + track.Numbers[order[i]] + " twice.";
                }
            }

            return order;
        }

        private static int PlaceInOrder(ImdTrack track, int fileIndex)
        {
            for (int i = 0; i < track.Order.Length; i++)
            {
                if (track.Order[i] == fileIndex)
                {
                    return i;
                }
            }

            throw new InvalidOperationException("sector missing from the track order");
        }

        private static int CompareTracks(ImdTrack a, ImdTrack b)
        {
            int byCylinder = a.Cylinder.CompareTo(b.Cylinder);
            return byCylinder != 0 ? byCylinder : (a.HeadByte & 1).CompareTo(b.HeadByte & 1);
        }

        private static bool IsFilledWith(ReadOnlySpan<byte> data, byte value)
        {
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != value)
                {
                    return false;
                }
            }

            return true;
        }

        private static byte[] Slice(byte[] file, int at, int length)
        {
            byte[] part = new byte[length];
            Array.Copy(file, at, part, 0, length);
            return part;
        }

        private static bool Need(byte[] file, int at, int length, out string? error)
        {
            error = at + length > file.Length ? "ImageDisk file ends in the middle of a track record (offset " + at + ")." : null;
            return error == null;
        }

        private sealed class ImdTrack
        {
            public byte Mode;
            public byte Cylinder;
            public byte HeadByte;
            public byte SectorCount;
            public byte SizeCode;
            public byte[] Numbers = Array.Empty<byte>();
            public byte[]? CylinderMap;
            public byte[]? HeadMap;
            public byte[] Types = Array.Empty<byte>();
            public byte[][] Data = Array.Empty<byte[]>();
            public int[] Order = Array.Empty<int>();
            public long FlatStart;
        }
    }
}
