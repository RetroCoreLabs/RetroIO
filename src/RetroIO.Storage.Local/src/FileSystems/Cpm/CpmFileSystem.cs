//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RetroIO.Storage.Local.FileSystems.Cpm
{
    /// <summary>
    /// Reads the files of a CP/M 2.2 or CP/M 3 disk image, given its disk parameters and record map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The directory starts at block 0 and is an array of 32-byte entries (CP/M 2.2 Alteration
    /// Guide, section 9; CP/M 3 Programmer's Guide, section 2). Each entry:
    ///  - byte 0: user number 0 to 15; E5h marks an empty entry; 20h is the CP/M 3 disk label and
    ///    21h a CP/M 3 time stamp entry, which are not files;
    ///  - bytes 1-8 and 9-11: name and type in ASCII; bit 7 of the type bytes holds the read-only,
    ///    system and archive attributes;
    ///  - byte 12 (EX) and byte 14 (S2): the extent number, low 5 bits and high bits;
    ///  - byte 15 (RC): records of 128 bytes used in the last logical extent of this entry;
    ///  - bytes 16-31: block numbers, 16 of one byte or 8 of two bytes (low byte first).
    /// </para>
    /// <para>
    /// A file is every entry with the same user number and name. Its blocks are those of its entries
    /// in extent order, and its length in 128-byte records is the extent number of its last entry
    /// times 128 plus that entry's RC. CP/M keeps no length finer than a record, so a text file ends
    /// with Ctrl-Z padding, exactly as it is on the disk.
    /// </para>
    /// </remarks>
    public sealed class CpmFileSystem
    {
        private readonly byte[] _image;
        private readonly CpmDiskParameters _parameters;
        private readonly ICpmRecordMap _map;
        private readonly List<CpmFile> _files = new List<CpmFile>();

        private const int EntrySize = 32;
        private const int RecordBytes = 128;
        private const byte EmptyEntry = 0xE5;
        private const byte DiskLabelEntry = 0x20;

        /// <summary>
        /// Reads the directory of a CP/M disk image.
        /// </summary>
        /// <param name="image">
        /// The whole disk image.
        /// </param>
        /// <param name="parameters">
        /// The disk parameters of the machine and format.
        /// </param>
        /// <param name="map">
        /// Where each data record lies in the image.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when an argument is null.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// Thrown when a directory entry names a block that is not on the disk.
        /// </exception>
        public CpmFileSystem(byte[] image, CpmDiskParameters parameters, ICpmRecordMap map)
        {
            _image = image ?? throw new ArgumentNullException(nameof(image));
            _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            _map = map ?? throw new ArgumentNullException(nameof(map));
            ReadDirectory();
        }

        /// <summary>
        /// Gets the CP/M 3 disk label, or an empty string when the disk has none.
        /// </summary>
        public string Label { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the files on the disk, in directory order of their first entry.
        /// </summary>
        public IReadOnlyList<CpmFile> Files => _files;

        /// <summary>
        /// Finds a file by user number and name.
        /// </summary>
        /// <param name="user">
        /// User number, 0 to 15.
        /// </param>
        /// <param name="name">
        /// File name as NAME.TYP, in any case.
        /// </param>
        /// <returns>
        /// The file, or <c>null</c> when there is no such file.
        /// </returns>
        public CpmFile? Find(int user, string name)
        {
            for (int i = 0; i < _files.Count; i++)
            {
                if (_files[i].User == user && string.Equals(_files[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return _files[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Reads a whole file.
        /// </summary>
        /// <param name="file">
        /// A file from <see cref="Files"/>.
        /// </param>
        /// <returns>
        /// The file's records, <see cref="CpmFile.Size"/> bytes.
        /// </returns>
        public byte[] Read(CpmFile file)
        {
            byte[] data = new byte[file.Size];
            int recordsPerBlock = _parameters.BlockSize / _map.RecordSize;
            int written = 0;
            for (int b = 0; b < file.Blocks.Count && written < data.Length; b++)
            {
                int firstRecord = file.Blocks[b] * recordsPerBlock;
                for (int r = 0; r < recordsPerBlock && written < data.Length; r++)
                {
                    long offset = _map.ImageOffset(firstRecord + r);
                    int count = Math.Min(_map.RecordSize, data.Length - written);
                    Array.Copy(_image, offset, data, written, count);
                    written += count;
                }
            }

            return data;
        }

        // Reads every directory entry and groups the file entries by user number and name.
        private void ReadDirectory()
        {
            byte[] directory = ReadDirectoryBytes();
            Dictionary<string, CpmFileBuilder> byKey = new Dictionary<string, CpmFileBuilder>(StringComparer.Ordinal);
            List<CpmFileBuilder> order = new List<CpmFileBuilder>();
            for (int e = 0; e < _parameters.DirectoryEntries; e++)
            {
                int at = e * EntrySize;
                byte status = directory[at];
                if (status == DiskLabelEntry)
                {
                    Label = Name(directory, at);
                    continue;
                }

                if (status == EmptyEntry || status > 15)
                {
                    continue;
                }

                string name = Name(directory, at);
                string key = status + ":" + name;
                if (!byKey.TryGetValue(key, out CpmFileBuilder? builder))
                {
                    builder = new CpmFileBuilder(status, name, (directory[at + 9] & 0x80) != 0, (directory[at + 10] & 0x80) != 0);
                    byKey.Add(key, builder);
                    order.Add(builder);
                }

                builder.Add(directory, at, _parameters);
            }

            for (int i = 0; i < order.Count; i++)
            {
                _files.Add(order[i].Build(_parameters));
            }
        }

        // The directory blocks, read through the record map.
        private byte[] ReadDirectoryBytes()
        {
            int bytes = _parameters.DirectoryEntries * EntrySize;
            byte[] directory = new byte[bytes];
            int records = (bytes + _map.RecordSize - 1) / _map.RecordSize;
            for (int r = 0; r < records; r++)
            {
                long offset = _map.ImageOffset(r);
                int count = Math.Min(_map.RecordSize, bytes - r * _map.RecordSize);
                Array.Copy(_image, offset, directory, r * _map.RecordSize, count);
            }

            return directory;
        }

        // NAME.TYP from an entry, attribute bits removed, padding spaces dropped.
        private static string Name(byte[] directory, int at)
        {
            StringBuilder name = new StringBuilder(12);
            for (int i = 1; i <= 8; i++)
            {
                name.Append((char)(directory[at + i] & 0x7F));
            }

            string baseName = name.ToString().TrimEnd(' ');
            name.Clear();
            for (int i = 9; i <= 11; i++)
            {
                name.Append((char)(directory[at + i] & 0x7F));
            }

            string type = name.ToString().TrimEnd(' ');
            return type.Length == 0 ? baseName : baseName + "." + type;
        }

        // Collects the entries of one file while the directory is read.
        private sealed class CpmFileBuilder
        {
            private readonly int _user;
            private readonly string _name;
            private readonly bool _readOnly;
            private readonly bool _system;
            private readonly List<KeyValuePair<int, int[]>> _extents = new List<KeyValuePair<int, int[]>>();
            private int _lastExtent = -1;
            private int _lastRecordCount;

            public CpmFileBuilder(int user, string name, bool readOnly, bool system)
            {
                _user = user;
                _name = name;
                _readOnly = readOnly;
                _system = system;
            }

            // Adds one directory entry of this file.
            public void Add(byte[] directory, int at, CpmDiskParameters parameters)
            {
                int extent = (directory[at + 12] & 0x1F) | (directory[at + 14] << 5);
                int recordCount = directory[at + 15];
                bool wide = parameters.WideBlockNumbers;
                int slots = wide ? 8 : 16;
                List<int> blocks = new List<int>(slots);
                for (int s = 0; s < slots; s++)
                {
                    int block = wide
                        ? directory[at + 16 + s * 2] | (directory[at + 17 + s * 2] << 8)
                        : directory[at + 16 + s];
                    if (block == 0)
                    {
                        continue;
                    }

                    if (block >= parameters.BlockCount)
                    {
                        throw new InvalidDataException(
                            "CP/M directory entry for " + _name + " names block " + block + ", but the disk has "
                            + parameters.BlockCount + " blocks.");
                    }

                    blocks.Add(block);
                }

                _extents.Add(new KeyValuePair<int, int[]>(extent, blocks.ToArray()));
                if (extent > _lastExtent)
                {
                    _lastExtent = extent;
                    _lastRecordCount = recordCount;
                }
            }

            // The file: blocks in extent order, length from the last extent.
            public CpmFile Build(CpmDiskParameters parameters)
            {
                _extents.Sort(CompareExtent);
                List<int> blocks = new List<int>();
                for (int i = 0; i < _extents.Count; i++)
                {
                    blocks.AddRange(_extents[i].Value);
                }

                long records = (long)_lastExtent * 128 + _lastRecordCount;
                long size = records * RecordBytes;
                long capacity = (long)blocks.Count * parameters.BlockSize;
                if (size > capacity)
                {
                    throw new InvalidDataException(
                        "CP/M file " + _name + " says it has " + records + " records, more than its "
                        + blocks.Count + " blocks hold.");
                }

                return new CpmFile(_user, _name, (int)size, _readOnly, _system, blocks);
            }

            private static int CompareExtent(KeyValuePair<int, int[]> a, KeyValuePair<int, int[]> b) => a.Key.CompareTo(b.Key);
        }
    }
}
