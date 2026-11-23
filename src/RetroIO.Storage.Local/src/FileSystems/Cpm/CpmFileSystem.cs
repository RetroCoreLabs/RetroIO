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
        /// Gets the disk image, with every change made through this instance.
        /// </summary>
        public byte[] Image => _image;

        /// <summary>
        /// Gets the CP/M 3 disk label, or an empty string when the disk has none.
        /// </summary>
        public string Label { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the files on the disk, in directory order of their first entry.
        /// </summary>
        public IReadOnlyList<CpmFile> Files => _files;

        /// <summary>
        /// Gathers the directory of a CP/M disk image through its record map.
        /// </summary>
        /// <param name="image">
        /// The disk image, or at least its start up to the end of the directory.
        /// </param>
        /// <param name="parameters">
        /// The disk parameters, which give the directory size.
        /// </param>
        /// <param name="map">
        /// Where each data record lies in the image.
        /// </param>
        /// <returns>
        /// The directory, <see cref="CpmDiskParameters.DirectoryEntries"/> times 32 bytes, or an empty
        /// array when <paramref name="image"/> ends before the directory does.
        /// </returns>
        public static byte[] DirectoryBytes(ReadOnlySpan<byte> image, CpmDiskParameters parameters, ICpmRecordMap map)
        {
            int bytes = parameters.DirectoryEntries * EntrySize;
            byte[] directory = new byte[bytes];
            int records = (bytes + map.RecordSize - 1) / map.RecordSize;
            for (int r = 0; r < records; r++)
            {
                long offset = map.ImageOffset(r);
                int count = Math.Min(map.RecordSize, bytes - r * map.RecordSize);
                if (offset + count > image.Length)
                {
                    return Array.Empty<byte>();
                }

                image.Slice((int)offset, count).CopyTo(directory.AsSpan(r * map.RecordSize, count));
            }

            return directory;
        }

        /// <summary>
        /// Checks every entry of a directory against the CP/M directory format.
        /// </summary>
        /// <remarks>
        /// Used to recognise a CP/M disk that carries no mark of its own (the C128 BIOS treats every
        /// 1581 disk in CP/M mode as CP/M). An entry passes when it is one of:
        ///  - an empty entry, E5h;
        ///  - a file entry, user 0 to 15: printable name and type, extent low bits 0 to 31, record
        ///    count 0 to 128, and every block number 0 or inside the data area after the directory;
        ///  - a CP/M 3 password entry (10h to 1Fh), the disk label (20h) or a time stamp entry (21h).
        /// Anything else fails the whole directory.
        /// </remarks>
        /// <param name="directory">
        /// The directory bytes, <see cref="CpmDiskParameters.DirectoryEntries"/> times 32.
        /// </param>
        /// <param name="parameters">
        /// The disk parameters to check block numbers against.
        /// </param>
        /// <param name="fileEntries">
        /// Set to the number of file entries found.
        /// </param>
        /// <returns>
        /// <c>true</c> when every entry passes.
        /// </returns>
        public static bool LooksLikeDirectory(ReadOnlySpan<byte> directory, CpmDiskParameters parameters, out int fileEntries)
        {
            fileEntries = 0;
            if (directory.Length < parameters.DirectoryEntries * EntrySize)
            {
                return false;
            }

            for (int e = 0; e < parameters.DirectoryEntries; e++)
            {
                ReadOnlySpan<byte> entry = directory.Slice(e * EntrySize, EntrySize);
                byte status = entry[0];
                if (status == EmptyEntry || (status >= 0x10 && status <= 0x21))
                {
                    continue;
                }

                if (status > 15)
                {
                    return false;
                }

                for (int i = 1; i <= 11; i++)
                {
                    int c = entry[i] & 0x7F;
                    if (c < 0x20 || c > 0x7E)
                    {
                        return false;
                    }
                }

                if (entry[12] > 31 || entry[15] > 128)
                {
                    return false;
                }

                bool wide = parameters.WideBlockNumbers;
                int slots = wide ? 8 : 16;
                for (int s = 0; s < slots; s++)
                {
                    int block = wide ? entry[16 + s * 2] | (entry[17 + s * 2] << 8) : entry[16 + s];
                    if (block != 0 && (block < parameters.DirectoryBlocks || block >= parameters.BlockCount))
                    {
                        return false;
                    }
                }

                fileEntries++;
            }

            return true;
        }

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

        /// <summary>
        /// Writes a file, replacing any file of the same user number and name.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The data is stored in 128-byte records; a last partial record is filled with Ctrl-Z (1Ah),
        /// the CP/M end-of-file mark, because CP/M keeps no length finer than a record.
        /// </para>
        /// <para>
        /// Blocks are taken lowest number first from those no directory entry uses. The file gets one
        /// directory entry per (EXM + 1) logical extents of 16 KB, each in the first empty (E5h) entry.
        /// An entry's extent number (EX, with S2 for the high bits) is that of the last logical extent
        /// it holds and RC is the records used in that extent, which is how <see cref="Read"/> and
        /// CP/M itself work out the length.
        /// </para>
        /// <para>
        /// CP/M 3 time stamp entries are left as they are, so a stamped disk keeps the old stamps for
        /// a replaced file (stamping is not written yet).
        /// </para>
        /// </remarks>
        /// <param name="user">
        /// User number, 0 to 15.
        /// </param>
        /// <param name="name">
        /// File name as NAME.TYP; it is stored in upper case.
        /// </param>
        /// <param name="data">
        /// The file's bytes.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="name"/> is not a CP/M file name.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the file there is read-only, the disk is full or the directory is full. The
        /// disk is not changed.
        /// </exception>
        public void Write(int user, string name, byte[] data)
        {
            CheckUser(user);
            byte[] fileName = EncodeName(name);
            CpmFile? existing = Find(user, DecodeName(fileName));
            if (existing != null && existing.ReadOnly)
            {
                throw new IOException("File R/O: " + existing.Name + " is read-only.");
            }

            byte[] directory = DirectoryBytes(_image, _parameters, _map);
            if (existing != null)
            {
                MarkEntriesEmpty(directory, user, fileName);
            }

            bool[] used = UsedBlocks(directory);
            int records = (data.Length + RecordBytes - 1) / RecordBytes;
            int recordsPerBlock = _parameters.BlockSize / RecordBytes;
            int blocksNeeded = (records + recordsPerBlock - 1) / recordsPerBlock;
            int[] blocks = new int[blocksNeeded];
            int found = 0;
            for (int b = 0; b < _parameters.BlockCount && found < blocksNeeded; b++)
            {
                if (!used[b])
                {
                    blocks[found++] = b;
                }
            }

            if (found < blocksNeeded)
            {
                throw new IOException("Disk full: " + name + " needs " + blocksNeeded + " blocks, " + found + " are free.");
            }

            int slots = _parameters.WideBlockNumbers ? 8 : 16;
            int recordsPerEntry = slots * recordsPerBlock;
            int entriesNeeded = Math.Max(1, (records + recordsPerEntry - 1) / recordsPerEntry);
            int[] entryIndex = new int[entriesNeeded];
            int freeEntries = 0;
            for (int e = 0; e < _parameters.DirectoryEntries && freeEntries < entriesNeeded; e++)
            {
                if (directory[e * EntrySize] == EmptyEntry)
                {
                    entryIndex[freeEntries++] = e;
                }
            }

            if (freeEntries < entriesNeeded)
            {
                throw new IOException("Directory full: " + name + " needs " + entriesNeeded + " entries, " + freeEntries + " are free.");
            }

            for (int k = 0; k < entriesNeeded; k++)
            {
                int at = entryIndex[k] * EntrySize;
                Array.Clear(directory, at, EntrySize);
                directory[at] = (byte)user;
                Array.Copy(fileName, 0, directory, at + 1, 11);
                int recordsHere = Math.Min(recordsPerEntry, records - k * recordsPerEntry);
                int lastExtentInEntry = recordsHere > 0 ? (recordsHere - 1) / 128 : 0;
                int extent = k * (_parameters.ExtentMask + 1) + lastExtentInEntry;
                directory[at + 12] = (byte)(extent & 0x1F);
                directory[at + 14] = (byte)(extent >> 5);
                directory[at + 15] = (byte)(recordsHere - lastExtentInEntry * 128);
                int blocksHere = (recordsHere + recordsPerBlock - 1) / recordsPerBlock;
                for (int s = 0; s < blocksHere; s++)
                {
                    int block = blocks[k * slots + s];
                    if (slots == 8)
                    {
                        directory[at + 16 + s * 2] = (byte)block;
                        directory[at + 17 + s * 2] = (byte)(block >> 8);
                    }
                    else
                    {
                        directory[at + 16 + s] = (byte)block;
                    }
                }
            }

            // Data first, then the directory: until the directory is stored the old file stands.
            byte[] padded = new byte[records * RecordBytes];
            Array.Fill(padded, (byte)0x1A);
            Array.Copy(data, padded, data.Length);
            int mapRecordsPerBlock = _parameters.BlockSize / _map.RecordSize;
            for (int b = 0; b < blocksNeeded; b++)
            {
                int blockBytes = Math.Min(_parameters.BlockSize, padded.Length - b * _parameters.BlockSize);
                int mapRecords = (blockBytes + _map.RecordSize - 1) / _map.RecordSize;
                for (int r = 0; r < mapRecords; r++)
                {
                    long offset = _map.ImageOffset(blocks[b] * mapRecordsPerBlock + r);
                    int from = b * _parameters.BlockSize + r * _map.RecordSize;
                    int count = Math.Min(_map.RecordSize, padded.Length - from);
                    Array.Copy(padded, from, _image, offset, count);
                }
            }

            StoreDirectory(directory);
        }

        /// <summary>
        /// Deletes a file: every one of its directory entries becomes empty (E5h).
        /// </summary>
        /// <param name="user">
        /// User number, 0 to 15.
        /// </param>
        /// <param name="name">
        /// File name as NAME.TYP.
        /// </param>
        /// <exception cref="FileNotFoundException">
        /// Thrown when there is no such file.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the file is read-only.
        /// </exception>
        public void Delete(int user, string name)
        {
            CpmFile file = Find(user, name) ?? throw new FileNotFoundException("File not found: " + name);
            if (file.ReadOnly)
            {
                throw new IOException("File R/O: " + file.Name + " is read-only.");
            }

            byte[] directory = DirectoryBytes(_image, _parameters, _map);
            MarkEntriesEmpty(directory, user, OnDiskName(file.Name));
            StoreDirectory(directory);
        }

        /// <summary>
        /// Renames a file, keeping its user number and attributes.
        /// </summary>
        /// <param name="user">
        /// User number, 0 to 15.
        /// </param>
        /// <param name="oldName">
        /// Current name as NAME.TYP.
        /// </param>
        /// <param name="newName">
        /// New name as NAME.TYP.
        /// </param>
        /// <exception cref="FileNotFoundException">
        /// Thrown when there is no file called <paramref name="oldName"/>.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the file is read-only or <paramref name="newName"/> already exists.
        /// </exception>
        public void Rename(int user, string oldName, string newName)
        {
            CpmFile file = Find(user, oldName) ?? throw new FileNotFoundException("File not found: " + oldName);
            byte[] target = EncodeName(newName);
            if (Find(user, DecodeName(target)) != null)
            {
                throw new IOException("File exists: " + DecodeName(target));
            }

            if (file.ReadOnly)
            {
                throw new IOException("File R/O: " + file.Name + " is read-only.");
            }

            byte[] source = OnDiskName(file.Name);
            byte[] directory = DirectoryBytes(_image, _parameters, _map);
            for (int e = 0; e < _parameters.DirectoryEntries; e++)
            {
                int at = e * EntrySize;
                if (IsEntryOf(directory, at, user, source))
                {
                    for (int i = 0; i < 11; i++)
                    {
                        // Keep the attribute bits (bit 7) the entry already has.
                        directory[at + 1 + i] = (byte)((directory[at + 1 + i] & 0x80) | target[i]);
                    }
                }
            }

            StoreDirectory(directory);
        }

        /// <summary>
        /// Sets the three attribute bits of a file in every one of its directory entries: R/O (bit 7
        /// of the first type byte), SYS (the second) and archive (the third, CP/M 3).
        /// </summary>
        /// <param name="user">
        /// User number, 0 to 15.
        /// </param>
        /// <param name="name">
        /// NAME.TYP.
        /// </param>
        /// <param name="readOnly">
        /// The R/O attribute.
        /// </param>
        /// <param name="system">
        /// The SYS attribute.
        /// </param>
        /// <param name="archive">
        /// The archive attribute.
        /// </param>
        /// <exception cref="FileNotFoundException">
        /// Thrown when there is no such file.
        /// </exception>
        public void SetAttributes(int user, string name, bool readOnly, bool system, bool archive)
        {
            CpmFile file = Find(user, name) ?? throw new FileNotFoundException("File not found: " + name);
            byte[] fileName = OnDiskName(file.Name);
            byte[] directory = DirectoryBytes(_image, _parameters, _map);
            for (int e = 0; e < _parameters.DirectoryEntries; e++)
            {
                int at = e * EntrySize;
                if (IsEntryOf(directory, at, user, fileName))
                {
                    directory[at + 9] = (byte)((directory[at + 9] & 0x7F) | (readOnly ? 0x80 : 0));
                    directory[at + 10] = (byte)((directory[at + 10] & 0x7F) | (system ? 0x80 : 0));
                    directory[at + 11] = (byte)((directory[at + 11] & 0x7F) | (archive ? 0x80 : 0));
                }
            }

            StoreDirectory(directory);
        }

        // Writes the directory back through the record map and reads it again.
        private void StoreDirectory(byte[] directory)
        {
            int records = (directory.Length + _map.RecordSize - 1) / _map.RecordSize;
            for (int r = 0; r < records; r++)
            {
                long offset = _map.ImageOffset(r);
                int count = Math.Min(_map.RecordSize, directory.Length - r * _map.RecordSize);
                Array.Copy(directory, r * _map.RecordSize, _image, offset, count);
            }

            ReadDirectory();
        }

        // Blocks in use: the directory's own blocks and every block a file entry lists.
        private bool[] UsedBlocks(byte[] directory)
        {
            bool[] used = new bool[_parameters.BlockCount];
            for (int b = 0; b < _parameters.DirectoryBlocks && b < used.Length; b++)
            {
                used[b] = true;
            }

            bool wide = _parameters.WideBlockNumbers;
            int slots = wide ? 8 : 16;
            for (int e = 0; e < _parameters.DirectoryEntries; e++)
            {
                int at = e * EntrySize;
                if (directory[at] > 15)
                {
                    continue;
                }

                for (int s = 0; s < slots; s++)
                {
                    int block = wide ? directory[at + 16 + s * 2] | (directory[at + 17 + s * 2] << 8) : directory[at + 16 + s];
                    if (block > 0 && block < used.Length)
                    {
                        used[block] = true;
                    }
                }
            }

            return used;
        }

        // Every entry of the file becomes empty.
        private void MarkEntriesEmpty(byte[] directory, int user, byte[] fileName)
        {
            for (int e = 0; e < _parameters.DirectoryEntries; e++)
            {
                int at = e * EntrySize;
                if (IsEntryOf(directory, at, user, fileName))
                {
                    directory[at] = EmptyEntry;
                }
            }
        }

        // An entry of the file: same user, same name and type with the attribute bits ignored.
        private static bool IsEntryOf(byte[] directory, int at, int user, byte[] fileName)
        {
            if (directory[at] != user)
            {
                return false;
            }

            for (int i = 0; i < 11; i++)
            {
                if ((directory[at + 1 + i] & 0x7F) != fileName[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static void CheckUser(int user)
        {
            if (user < 0 || user > 15)
            {
                throw new ArgumentOutOfRangeException(nameof(user), user, "CP/M user numbers are 0 to 15");
            }
        }

        // NAME.TYP to the 11 directory bytes: upper case, padded with spaces. A name is up to 8
        // characters and a type up to 3, none of them a space or one of: less-than, greater-than,
        // dot, comma, semicolon, colon, equals, question mark, star, square brackets (CP/M 2.2
        // Interface Guide, section 2).
        // The 11 name bytes of a file already in the directory. No rule is checked: a disk can hold
        // names CP/M's own command line will not take (a real Kaypro disk has ".PRN"), and such a
        // file must still be deletable and renamable.
        private static byte[] OnDiskName(string name)
        {
            int dot = name.LastIndexOf('.');
            string baseName = dot >= 0 ? name.Substring(0, dot) : name;
            string type = dot >= 0 ? name.Substring(dot + 1) : string.Empty;
            byte[] encoded = new byte[11];
            Array.Fill(encoded, (byte)' ');
            for (int i = 0; i < baseName.Length && i < 8; i++)
            {
                encoded[i] = (byte)baseName[i];
            }

            for (int i = 0; i < type.Length && i < 3; i++)
            {
                encoded[8 + i] = (byte)type[i];
            }

            return encoded;
        }

        private static byte[] EncodeName(string name)
        {
            string upper = name.Trim().ToUpperInvariant();
            int dot = upper.LastIndexOf('.');
            string baseName = dot >= 0 ? upper.Substring(0, dot) : upper;
            string type = dot >= 0 ? upper.Substring(dot + 1) : string.Empty;
            if (baseName.Length == 0 || baseName.Length > 8 || type.Length > 3)
            {
                throw new ArgumentException("A CP/M file name is 1 to 8 characters, a dot and up to 3 more: " + name, nameof(name));
            }

            byte[] encoded = new byte[11];
            Array.Fill(encoded, (byte)' ');
            for (int i = 0; i < baseName.Length; i++)
            {
                encoded[i] = NameByte(baseName[i], name);
            }

            for (int i = 0; i < type.Length; i++)
            {
                encoded[8 + i] = NameByte(type[i], name);
            }

            return encoded;
        }

        private static byte NameByte(char c, string name)
        {
            if (c <= ' ' || c > '~' || "<>.,;:=?*[]".IndexOf(c) >= 0)
            {
                throw new ArgumentException("'" + c + "' is not allowed in a CP/M file name: " + name, nameof(name));
            }

            return (byte)c;
        }

        private static string DecodeName(byte[] encoded)
        {
            string baseName = Encoding.ASCII.GetString(encoded, 0, 8).TrimEnd(' ');
            string type = Encoding.ASCII.GetString(encoded, 8, 3).TrimEnd(' ');
            return type.Length == 0 ? baseName : baseName + "." + type;
        }

        // Reads every directory entry and groups the file entries by user number and name.
        private void ReadDirectory()
        {
            _files.Clear();
            Label = string.Empty;
            byte[] directory = DirectoryBytes(_image, _parameters, _map);
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
                    builder = new CpmFileBuilder(status, name, (directory[at + 9] & 0x80) != 0, (directory[at + 10] & 0x80) != 0, (directory[at + 11] & 0x80) != 0);
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
            private readonly bool _archive;
            private readonly List<KeyValuePair<int, int[]>> _extents = new List<KeyValuePair<int, int[]>>();
            private int _lastExtent = -1;
            private int _lastRecordCount;

            public CpmFileBuilder(int user, string name, bool readOnly, bool system, bool archive)
            {
                _archive = archive;
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

                return new CpmFile(_user, _name, (int)size, _readOnly, _system, _archive, blocks);
            }

            private static int CompareExtent(KeyValuePair<int, int[]> a, KeyValuePair<int, int[]> b) => a.Key.CompareTo(b.Key);
        }
    }
}
