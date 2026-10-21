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
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.FileSystems
{
    /// <summary>
    /// Read-only reader for OS-9 and NitrOS-9 floppy disks (the Random Block File manager, RBF), as used on the Dragon and the Color Computer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The layout was written from the field definitions of the RBF manager (identification sector, file descriptor and directory entry) and
    /// then checked against a real NitrOS-9 disk: every sector the allocation bitmap marks as used is reached by walking the directory tree
    /// from the root, apart from the rest of the reserved first track.
    /// </para>
    /// <para>
    /// Identification sector (logical sector 0): total sectors (3 bytes at 0), sectors per track (1 byte at 3), allocation map size in bytes
    /// (2 bytes at 4), sectors per map bit (2 bytes at 6), root directory file descriptor sector (3 bytes at 8), disk format (1 byte at 16, bit 0
    /// set means double sided), creation date (5 bytes at 26) and volume name (up to 32 bytes at 31, the last character has bit 7 set).
    /// All multi-byte numbers are big-endian.
    /// </para>
    /// <para>
    /// The allocation map starts at logical sector 1. Bit 7 of the first byte is logical sector 0, and a set bit means used.
    /// </para>
    /// <para>
    /// File descriptor (one sector): attributes (1 byte at 0), owner (2 at 1), date last modified (5 at 3: year since 1900, month, day, hour, minute),
    /// link count (1 at 8), file size (4 at 9), creation date (3 at 13: year since 1900, month, day), then the segment list from byte 16.
    /// A segment is 5 bytes: first logical sector (3) and number of sectors (2). The list ends at the first segment with a count of zero.
    /// </para>
    /// <para>
    /// A directory is a file of 32-byte entries: a 29 byte name (the last character has bit 7 set) and the 3 byte logical sector of the file descriptor.
    /// An entry whose first byte is zero is unused (RBF clears that byte when it deletes a file). Each directory holds an entry for itself and one for its parent.
    /// </para>
    /// <para>
    /// Names are compared without regard to case, as RBF does.
    /// </para>
    /// </remarks>
    public class Os9RbfFileSystem : IBlockFileSystem
    {
        private const int SectorSize = 256;
        private const int DirEntrySize = 32;
        private const int DirNameLength = 29;
        private const int SegmentListStart = 16;
        private const int SegmentSize = 5;
        private const int MaxSegments = (SectorSize - SegmentListStart) / SegmentSize;
        private const byte DirectoryAttribute = 0x80;

        private IBlockDevice? _device;
        private uint _totalSectors;
        private int _mapBytes;
        private int _sectorsPerBit;
        private uint _rootFd;
        private string _volumeLabel = string.Empty;

        /// <summary>
        /// Gets the name of the file system.
        /// </summary>
        public string FileSystemType => "OS-9";

        /// <summary>
        /// Gets the volume name from the identification sector.
        /// </summary>
        public string VolumeLabel => _volumeLabel;

        /// <summary>
        /// Gets the separator between a file name and its extension. OS-9 names have no extension part, so the dot is only part of the name.
        /// </summary>
        public string ExtensionSeparator => ".";

        /// <summary>
        /// Reads the identification sector and prepares the file system for use.
        /// </summary>
        /// <param name="device">
        /// The block device that holds the disk, 256 byte sectors addressed by logical sector number.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="device"/> is null.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// Thrown when the disk is not an OS-9 volume.
        /// </exception>
        public void Initialize(IBlockDevice device)
        {
            if (device == null)
                throw new ArgumentNullException(nameof(device));

            byte[] id = new byte[SectorSize];
            device.ReadBlock(BlockAddress.FromLBA(0), id);

            string? problem = Validate(device, id);
            if (problem != null)
                throw new InvalidDataException("Not an OS-9 volume: " + problem);

            _device = device;
            _totalSectors = ReadUInt24(id, 0);
            _mapBytes = ReadUInt16(id, 4);
            _sectorsPerBit = ReadUInt16(id, 6);
            _rootFd = ReadUInt24(id, 8);
            _volumeLabel = ReadName(id, 31, 32);
        }

        /// <summary>
        /// Tells whether the disk holds an OS-9 volume.
        /// </summary>
        /// <param name="device">
        /// The block device to look at.
        /// </param>
        /// <returns>
        /// True when sector 0 describes a volume that fits the device, the allocation map covers every sector, and the root directory is a directory
        /// file inside the volume that holds the entries for itself and its parent. Otherwise false.
        /// </returns>
        public bool CanHandle(IBlockDevice device)
        {
            BlockDeviceInfo? info = device.GetBlockDeviceInfo();
            if (info == null || info.BlockSize != SectorSize || info.TotalBlocks < 3)
                return false;

            byte[] id = new byte[SectorSize];
            device.ReadBlock(BlockAddress.FromLBA(0), id);
            return Validate(device, id) == null;
        }

        /// <summary>
        /// Lists a directory.
        /// </summary>
        /// <param name="path">
        /// The directory, with slashes between the names. The empty string and a single slash mean the root directory.
        /// </param>
        /// <returns>
        /// The entries of the directory, without the entries for the directory itself and its parent.
        /// </returns>
        /// <exception cref="DirectoryNotFoundException">
        /// Thrown when a name on the path does not exist or is not a directory.
        /// </exception>
        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureInitialized();

            uint fd = ResolvePath(path, out string resolvedPath);
            byte[] descriptor = ReadSectorChecked(fd);
            if ((descriptor[0] & DirectoryAttribute) == 0)
                throw new DirectoryNotFoundException("Not a directory: " + path);

            byte[] data = ReadFileData(descriptor);
            List<DirectoryEntry> entries = new List<DirectoryEntry>();
            for (int offset = 0; offset + DirEntrySize <= data.Length; offset += DirEntrySize)
            {
                if (data[offset] == 0)
                    continue;

                string name = ReadName(data, offset, DirNameLength);
                if (name == "." || name == "..")
                    continue;

                uint childFd = ReadUInt24(data, offset + DirNameLength);
                byte[] child = ReadSectorChecked(childFd);
                string childPath = resolvedPath == "/" ? "/" + name : resolvedPath + "/" + name;

                entries.Add(new DirectoryEntry
                {
                    Name = name,
                    Metadata = BuildMetadata(name, childPath, child)
                });
            }

            return entries.ToArray();
        }

        /// <summary>
        /// Reads a whole file.
        /// </summary>
        /// <param name="path">
        /// The file, with slashes between the names.
        /// </param>
        /// <returns>
        /// The bytes of the file, as many as the file size in its descriptor says.
        /// </returns>
        /// <exception cref="FileNotFoundException">
        /// Thrown when a name on the path does not exist.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the path names a directory.
        /// </exception>
        public byte[] ReadFile(string path)
        {
            EnsureInitialized();

            uint fd = ResolvePath(path, out _);
            byte[] descriptor = ReadSectorChecked(fd);
            if ((descriptor[0] & DirectoryAttribute) != 0)
                throw new IOException("Is a directory: " + path);

            return ReadFileData(descriptor);
        }

        /// <summary>
        /// Gets the details of a file or directory.
        /// </summary>
        /// <param name="path">
        /// The file or directory.
        /// </param>
        /// <returns>
        /// The metadata, or null when the path does not exist.
        /// </returns>
        public FileMetadata? GetMetadata(string path)
        {
            EnsureInitialized();

            uint fd;
            string resolvedPath;
            try
            {
                fd = ResolvePath(path, out resolvedPath);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }

            byte[] descriptor = ReadSectorChecked(fd);
            string name = resolvedPath == "/" ? "/" : resolvedPath.Substring(resolvedPath.LastIndexOf('/') + 1);
            return BuildMetadata(name, resolvedPath, descriptor);
        }

        /// <summary>
        /// Counts the free sectors by reading the allocation map.
        /// </summary>
        /// <returns>
        /// The number of free sectors: the clear bits of the map times the sectors per bit.
        /// </returns>
        public int GetBlocksFree()
        {
            EnsureInitialized();

            long clusters = ((long)_totalSectors + _sectorsPerBit - 1) / _sectorsPerBit;
            byte[] map = ReadAllocationMap();
            long free = 0;
            for (long bit = 0; bit < clusters; bit++)
            {
                if ((map[(int)(bit >> 3)] & (0x80 >> (int)(bit & 7))) == 0)
                    free++;
            }

            return (int)(free * _sectorsPerBit);
        }

        /// <summary>
        /// Always throws IOException: the OS-9 file system has no write support, so the volume is read-only.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <param name="data">
        /// Not used.
        /// </param>
        /// <exception cref="IOException">
        /// Always thrown.
        /// </exception>
        public void WriteFile(string path, byte[] data)
        {
            throw new IOException("The OS-9 file system has no write support, so the volume is read-only");
        }

        /// <summary>
        /// Always throws IOException: the OS-9 file system has no write support, so the volume is read-only.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <exception cref="IOException">
        /// Always thrown.
        /// </exception>
        public void DeleteFile(string path)
        {
            throw new IOException("The OS-9 file system has no write support, so the volume is read-only");
        }

        /// <summary>
        /// Always throws IOException: the OS-9 file system has no write support, so the volume is read-only.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <exception cref="IOException">
        /// Always thrown.
        /// </exception>
        public void CreateDirectory(string path)
        {
            throw new IOException("The OS-9 file system has no write support, so the volume is read-only");
        }

        /// <summary>
        /// Always throws IOException: the OS-9 file system has no write support, so the volume is read-only.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <exception cref="IOException">
        /// Always thrown.
        /// </exception>
        public void DeleteDirectory(string path)
        {
            throw new IOException("The OS-9 file system has no write support, so the volume is read-only");
        }

        /// <summary>
        /// Always throws IOException: the OS-9 file system has no write support, so the volume is read-only.
        /// </summary>
        /// <param name="oldPath">
        /// Not used.
        /// </param>
        /// <param name="newPath">
        /// Not used.
        /// </param>
        /// <exception cref="IOException">
        /// Always thrown.
        /// </exception>
        public void Rename(string oldPath, string newPath)
        {
            throw new IOException("The OS-9 file system has no write support, so the volume is read-only");
        }

        // ===================================================================
        // PRIVATE METHODS
        // ===================================================================

        // Returns null when sector 0 describes a usable OS-9 volume, otherwise the reason it does not.
        private static string? Validate(IBlockDevice device, byte[] id)
        {
            BlockDeviceInfo? info = device.GetBlockDeviceInfo();
            long deviceSectors = info != null ? info.TotalBlocks : 0;

            uint total = ReadUInt24(id, 0);
            int mapBytes = ReadUInt16(id, 4);
            int sectorsPerBit = ReadUInt16(id, 6);
            uint rootFd = ReadUInt24(id, 8);

            if (total < 3)
                return "total sectors is " + total;
            if (deviceSectors > 0 && total > deviceSectors)
                return "the volume is larger than the device (" + total + " of " + deviceSectors + " sectors)";
            if (mapBytes == 0 || sectorsPerBit == 0)
                return "allocation map size or sectors per bit is zero";
            if ((long)mapBytes * 8 * sectorsPerBit < total)
                return "the allocation map does not cover every sector";
            if (rootFd < 2 || rootFd >= total)
                return "the root directory descriptor is outside the volume";

            byte[] descriptor = new byte[SectorSize];
            device.ReadBlock(BlockAddress.FromLBA(rootFd), descriptor);
            if ((descriptor[0] & DirectoryAttribute) == 0)
                return "the root descriptor is not a directory";

            long size = ReadUInt32(descriptor, 9);
            if (size < 2 * DirEntrySize || size % DirEntrySize != 0)
                return "the root directory size is not a whole number of entries";

            uint firstLsn = ReadUInt24(descriptor, SegmentListStart);
            int firstCount = ReadUInt16(descriptor, SegmentListStart + 3);
            if (firstCount == 0 || firstLsn < 2 || (long)firstLsn + firstCount > total)
                return "the root directory segment is outside the volume";

            byte[] first = new byte[SectorSize];
            device.ReadBlock(BlockAddress.FromLBA(firstLsn), first);
            bool hasSelf = false;
            bool hasParent = false;
            for (int i = 0; i < 2; i++)
            {
                string name = ReadName(first, i * DirEntrySize, DirNameLength);
                if (name == ".")
                    hasSelf = true;
                else if (name == "..")
                    hasParent = true;
            }

            if (!hasSelf || !hasParent)
                return "the root directory does not start with the entries for itself and its parent";

            return null;
        }

        private void EnsureInitialized()
        {
            if (_device == null)
                throw new InvalidOperationException("Filesystem not initialized");
        }

        // Walks the names of a path from the root and returns the descriptor sector of the last one.
        private uint ResolvePath(string path, out string resolvedPath)
        {
            string[] parts = (path ?? string.Empty).Split('/');
            uint fd = _rootFd;
            StringBuilder built = new StringBuilder();

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length == 0)
                    continue;

                byte[] descriptor = ReadSectorChecked(fd);
                if ((descriptor[0] & DirectoryAttribute) == 0)
                    throw new DirectoryNotFoundException("Not a directory: " + built);

                uint? found = FindInDirectory(descriptor, part);
                if (found == null)
                    throw new FileNotFoundException("File not found: " + path);

                fd = found.Value;
                built.Append('/').Append(part);
            }

            resolvedPath = built.Length == 0 ? "/" : built.ToString();
            return fd;
        }

        private uint? FindInDirectory(byte[] directoryDescriptor, string name)
        {
            byte[] data = ReadFileData(directoryDescriptor);
            for (int offset = 0; offset + DirEntrySize <= data.Length; offset += DirEntrySize)
            {
                if (data[offset] == 0)
                    continue;

                if (string.Equals(ReadName(data, offset, DirNameLength), name, StringComparison.OrdinalIgnoreCase))
                    return ReadUInt24(data, offset + DirNameLength);
            }

            return null;
        }

        // Reads the bytes of the file a descriptor describes, following its segment list and cutting the result at the file size.
        private byte[] ReadFileData(byte[] descriptor)
        {
            long size = ReadUInt32(descriptor, 9);
            long capacity = 0;
            List<uint> starts = new List<uint>();
            List<int> counts = new List<int>();

            for (int i = 0; i < MaxSegments; i++)
            {
                int at = SegmentListStart + (i * SegmentSize);
                int count = ReadUInt16(descriptor, at + 3);
                if (count == 0)
                    break;

                uint start = ReadUInt24(descriptor, at);
                if ((long)start + count > _totalSectors)
                    throw new InvalidDataException("A file segment lies outside the volume (sector " + start + ", " + count + " sectors)");

                starts.Add(start);
                counts.Add(count);
                capacity += (long)count * SectorSize;
            }

            if (size > capacity)
                throw new InvalidDataException("The file size (" + size + ") is larger than its segments (" + capacity + " bytes)");

            byte[] result = new byte[size];
            byte[] sector = new byte[SectorSize];
            long written = 0;
            for (int s = 0; s < starts.Count && written < size; s++)
            {
                for (int k = 0; k < counts[s] && written < size; k++)
                {
                    _device!.ReadBlock(BlockAddress.FromLBA(starts[s] + (uint)k), sector);
                    int take = (int)Math.Min(SectorSize, size - written);
                    Buffer.BlockCopy(sector, 0, result, (int)written, take);
                    written += take;
                }
            }

            return result;
        }

        private byte[] ReadSectorChecked(uint lsn)
        {
            if (lsn >= _totalSectors)
                throw new InvalidDataException("Sector " + lsn + " is outside the volume");

            byte[] sector = new byte[SectorSize];
            _device!.ReadBlock(BlockAddress.FromLBA(lsn), sector);
            return sector;
        }

        private byte[] ReadAllocationMap()
        {
            byte[] map = new byte[_mapBytes];
            byte[] sector = new byte[SectorSize];
            int copied = 0;
            uint lsn = 1;
            while (copied < _mapBytes)
            {
                _device!.ReadBlock(BlockAddress.FromLBA(lsn), sector);
                int take = Math.Min(SectorSize, _mapBytes - copied);
                Buffer.BlockCopy(sector, 0, map, copied, take);
                copied += take;
                lsn++;
            }

            return map;
        }

        private static FileMetadata BuildMetadata(string name, string path, byte[] descriptor)
        {
            byte attributes = descriptor[0];
            bool isDirectory = (attributes & DirectoryAttribute) != 0;

            return new FileMetadata
            {
                Name = name,
                Path = path,
                IsDirectory = isDirectory,
                Size = ReadUInt32(descriptor, 9),
                ModifiedTime = ReadDate(descriptor, 3, true),
                CreatedTime = ReadDate(descriptor, 13, false),
                FileType = isDirectory ? "Directory" : "File",
                Attributes = FormatAttributes(attributes),
                UrnIdentifier = UrnIdentifier.Encode(path)
            };
        }

        // Attribute bits: 0 read, 1 write, 2 execute, 3 public read, 4 public write, 5 public execute, 6 sharable, 7 directory.
        // Shown the way OS-9 listings show them: directory, sharable, then public execute, write, read and owner execute, write, read.
        private static string FormatAttributes(byte attributes)
        {
            char[] text = new char[8];
            string letters = "dsewrewr";
            for (int i = 0; i < 8; i++)
            {
                text[i] = (attributes & (0x80 >> i)) != 0 ? letters[i] : '-';
            }

            return new string(text);
        }

        // Year counts from 1900. A date that is not a real date (all zero on a disk that was never given a clock) gives null.
        private static DateTime? ReadDate(byte[] data, int offset, bool withTime)
        {
            int year = 1900 + data[offset];
            int month = data[offset + 1];
            int day = data[offset + 2];
            int hour = withTime ? data[offset + 3] : 0;
            int minute = withTime ? data[offset + 4] : 0;

            if (month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59)
                return null;

            return new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        }

        // A name is up to maxLength characters. The last character has bit 7 set, and a name without that bit ends at maxLength.
        private static string ReadName(byte[] data, int offset, int maxLength)
        {
            StringBuilder name = new StringBuilder();
            for (int i = 0; i < maxLength && offset + i < data.Length; i++)
            {
                byte b = data[offset + i];
                if (b == 0 && i == 0)
                    break;

                name.Append((char)(b & 0x7F));
                if ((b & 0x80) != 0)
                    break;
            }

            return name.ToString();
        }

        private static uint ReadUInt24(byte[] data, int offset)
        {
            return (uint)((data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2]);
        }

        private static int ReadUInt16(byte[] data, int offset)
        {
            return (data[offset] << 8) | data[offset + 1];
        }

        private static long ReadUInt32(byte[] data, int offset)
        {
            return ((long)data[offset] << 24) | ((long)data[offset + 1] << 16) | ((long)data[offset + 2] << 8) | data[offset + 3];
        }
    }
}
