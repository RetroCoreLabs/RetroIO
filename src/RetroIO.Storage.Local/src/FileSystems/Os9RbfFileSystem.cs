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
    /// Reader and writer for OS-9 and NitrOS-9 floppy disks (the Random Block File manager, RBF), as used on the Dragon and the Color Computer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Writing (create, replace and delete files, make and delete directories, rename and move) follows the NitrOS-9 sources for the RBF manager
    /// (level1/modules/rbf.asm and the layouts in defs/rbf.d). It is only implemented for volumes with one sector per allocation map bit, which
    /// is every floppy seen so far. File owners and permissions are not checked. Nothing is written to the device until a change has found all
    /// the space it needs.
    /// </para>
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

        // State of a change in progress (see BeginWrite): sectors held back until the change is complete, the working copy of the allocation
        // map with the map as it was, and the segment allocation size taken from sector 0.
        private Dictionary<uint, byte[]>? _pending;
        private byte[]? _workMap;
        private byte[]? _originalMap;
        private int _sas;

        /// <summary>
        /// Gets or sets the clock that supplies the date and time written into the file descriptors of files and directories that are created
        /// or changed. When null, the local time of the computer is used.
        /// </summary>
        public Func<DateTime>? Clock { get; set; }

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
        /// Writes a file, creating it or replacing its contents.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A new file gets a file descriptor sector (the first free sector), a segment list for exactly the sectors its data needs, and an entry
        /// in the parent directory (the first slot whose first byte is zero, otherwise a new entry at the end, which makes the directory file
        /// 32 bytes longer). This is the order the RBF manager uses when it creates a file (level1/modules/rbf.asm, label Create).
        /// </para>
        /// <para>
        /// An existing file keeps its descriptor sector, owner, attributes and creation date. Its old segments are released and new ones are
        /// allocated for the new data, as the RBF manager does when it truncates a file (rbf.asm, labels L0EFE and ClrFBits).
        /// </para>
        /// <para>
        /// Nothing is written to the device until every allocation has succeeded, so a full disk or a full segment list leaves the volume as it was.
        /// </para>
        /// </remarks>
        /// <param name="path">
        /// The file, with slashes between the names. The directory that holds it must exist.
        /// </param>
        /// <param name="data">
        /// The bytes of the file.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="data"/> is null.
        /// </exception>
        /// <exception cref="DirectoryNotFoundException">
        /// Thrown when the directory that should hold the file does not exist.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the volume is mounted read-only, the name is not a legal OS-9 name, the path names a directory, the disk is full, or
        /// the file would need more than 48 segments.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// Thrown when the volume has more than one sector per allocation map bit (not implemented, see the class remarks).
        /// </exception>
        public void WriteFile(string path, byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            BeginWrite();
            try
            {
                SplitPath(path, out uint parentFd, out byte[] parentDesc, out string name);
                ValidateName(name);

                byte[] parentData = ReadFileData(parentDesc);
                int entryOffset = FindEntry(parentData, name);

                uint fdLsn;
                byte[] fd;
                bool isNew = entryOffset < 0;
                if (isNew)
                {
                    fdLsn = AllocateSectors(1)[0].Lsn;
                    fd = new byte[SectorSize];
                    fd[FdAttributes] = NewFileAttributes;
                    SetCreated(fd);
                    fd[FdLinkCount] = 1;
                }
                else
                {
                    fdLsn = ReadUInt24(parentData, entryOffset + DirNameLength);
                    fd = ReadSectorChecked(fdLsn);
                    if ((fd[FdAttributes] & DirectoryAttribute) != 0)
                        throw new IOException("Is a directory: " + path);

                    FreeSegments(ReadSegments(fd));
                }

                int sectors = (int)(((long)data.Length + SectorSize - 1) / SectorSize);
                List<Segment> segments = sectors > 0 ? AllocateSectors(sectors) : new List<Segment>();
                StoreSegments(fd, segments);
                WriteUInt32(fd, FdSize, data.Length);
                SetModified(fd);

                long written = 0;
                for (int s = 0; s < segments.Count; s++)
                {
                    for (int k = 0; k < segments[s].Count; k++)
                    {
                        byte[] sector = new byte[SectorSize];
                        int take = (int)Math.Min(SectorSize, data.Length - written);
                        Buffer.BlockCopy(data, (int)written, sector, 0, take);
                        written += take;
                        PutPending(segments[s].Lsn + (uint)k, sector);
                    }
                }

                PutPending(fdLsn, fd);
                if (isNew)
                    AddDirectoryEntry(parentFd, parentDesc, name, fdLsn);

                CommitWrite();
            }
            catch
            {
                AbortWrite();
                throw;
            }
        }

        /// <summary>
        /// Deletes a file.
        /// </summary>
        /// <remarks>
        /// Follows the RBF manager (level1/modules/rbf.asm, label Delete): the link count in the descriptor goes down by one, and when it
        /// reaches zero the segments and the descriptor sector are released in the allocation map and the descriptor is left with size zero and an
        /// empty segment list. The first byte of the directory entry is set to zero, which marks the slot as free.
        /// File permissions and owners are not checked.
        /// </remarks>
        /// <param name="path">
        /// The file, with slashes between the names.
        /// </param>
        /// <exception cref="FileNotFoundException">
        /// Thrown when the file does not exist.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the volume is mounted read-only or the path names a directory.
        /// </exception>
        public void DeleteFile(string path)
        {
            BeginWrite();
            try
            {
                SplitPath(path, out uint parentFd, out byte[] parentDesc, out string name);
                byte[] parentData = ReadFileData(parentDesc);
                int entryOffset = FindEntry(parentData, name);
                if (entryOffset < 0 || IsDotName(name))
                    throw new FileNotFoundException("File not found: " + path);

                uint fdLsn = ReadUInt24(parentData, entryOffset + DirNameLength);
                byte[] fd = ReadSectorChecked(fdLsn);
                if ((fd[FdAttributes] & DirectoryAttribute) != 0)
                    throw new IOException("Is a directory: " + path);

                ReleaseDescriptor(fdLsn, fd);
                ClearDirectoryEntry(parentFd, parentDesc, entryOffset);
                CommitWrite();
            }
            catch
            {
                AbortWrite();
                throw;
            }
        }

        /// <summary>
        /// Creates a directory.
        /// </summary>
        /// <remarks>
        /// Follows the RBF manager (level1/modules/rbf.asm, label MakDir) and the directories on the real disk: a descriptor sector with the directory
        /// attribute and permissions 0xBF, followed by the segment allocation size (byte 77 of sector 0, 8 on the real disk) of sectors as one
        /// segment. The file size is 64 bytes: the entry for the parent directory (name two dots) and the entry for the directory itself (name one dot).
        /// </remarks>
        /// <param name="path">
        /// The new directory, with slashes between the names. The directory that holds it must exist.
        /// </param>
        /// <exception cref="DirectoryNotFoundException">
        /// Thrown when the directory that should hold the new one does not exist.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the volume is mounted read-only, the name is not a legal OS-9 name, the name is taken, or the disk is full.
        /// </exception>
        public void CreateDirectory(string path)
        {
            BeginWrite();
            try
            {
                SplitPath(path, out uint parentFd, out byte[] parentDesc, out string name);
                ValidateName(name);

                byte[] parentData = ReadFileData(parentDesc);
                if (FindEntry(parentData, name) >= 0)
                    throw new IOException("A file or directory with this name exists: " + path);

                uint fdLsn = AllocateSectors(1)[0].Lsn;
                List<Segment> segments = AllocateDirectorySectors();

                byte[] fd = new byte[SectorSize];
                fd[FdAttributes] = NewDirectoryAttributes;
                SetCreated(fd);
                fd[FdLinkCount] = 1;
                StoreSegments(fd, segments);
                WriteUInt32(fd, FdSize, 2 * DirEntrySize);
                SetModified(fd);

                // Sector one of the new directory holds the entry for the parent and the entry for the directory itself. The rest is zero.
                for (int s = 0; s < segments.Count; s++)
                {
                    for (int k = 0; k < segments[s].Count; k++)
                        PutPending(segments[s].Lsn + (uint)k, new byte[SectorSize]);
                }

                byte[] first = new byte[SectorSize];
                PutDirName(first, 0, "..");
                WriteUInt24(first, DirNameLength, (int)parentFd);
                PutDirName(first, DirEntrySize, ".");
                WriteUInt24(first, DirEntrySize + DirNameLength, (int)fdLsn);
                PutPending(segments[0].Lsn, first);

                PutPending(fdLsn, fd);
                AddDirectoryEntry(parentFd, parentDesc, name, fdLsn);
                CommitWrite();
            }
            catch
            {
                AbortWrite();
                throw;
            }
        }

        /// <summary>
        /// Deletes an empty directory.
        /// </summary>
        /// <remarks>
        /// The directory must hold nothing but the entries for itself and its parent (free slots do not count). Its segments and descriptor sector
        /// are released like those of a file. The deldir command empties a directory and then calls the plain delete service on it
        /// (level1/cmds/deldir.asm, I$Delete), and level1/modules/rbf.asm, label Delete, has no separate path for directories. The directory attribute
        /// stays set in the released descriptor.
        /// </remarks>
        /// <param name="path">
        /// The directory, with slashes between the names.
        /// </param>
        /// <exception cref="DirectoryNotFoundException">
        /// Thrown when the directory does not exist or the path names a file.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the volume is mounted read-only, the path is the root directory, or the directory is not empty.
        /// </exception>
        public void DeleteDirectory(string path)
        {
            BeginWrite();
            try
            {
                if ((path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 0)
                    throw new IOException("The root directory of an OS-9 disk cannot be deleted");

                SplitPath(path ?? string.Empty, out uint parentFd, out byte[] parentDesc, out string name);
                byte[] parentData = ReadFileData(parentDesc);
                int entryOffset = FindEntry(parentData, name);
                if (entryOffset < 0 || IsDotName(name))
                    throw new DirectoryNotFoundException("Directory not found: " + path);

                uint fdLsn = ReadUInt24(parentData, entryOffset + DirNameLength);
                byte[] fd = ReadSectorChecked(fdLsn);
                if ((fd[FdAttributes] & DirectoryAttribute) == 0)
                    throw new DirectoryNotFoundException("Not a directory: " + path);

                byte[] data = ReadFileData(fd);
                for (int offset = 0; offset + DirEntrySize <= data.Length; offset += DirEntrySize)
                {
                    if (data[offset] == 0)
                        continue;

                    string entryName = ReadName(data, offset, DirNameLength);
                    if (!IsDotName(entryName))
                        throw new IOException("The directory is not empty: " + path);
                }

                ReleaseDescriptor(fdLsn, fd);
                ClearDirectoryEntry(parentFd, parentDesc, entryOffset);
                CommitWrite();
            }
            catch
            {
                AbortWrite();
                throw;
            }
        }

        /// <summary>
        /// Renames a file or directory, or moves it to another directory.
        /// </summary>
        /// <remarks>
        /// Only directory entries change. In the same directory the name field of the entry is rewritten (the rename command does the same,
        /// level1/cmds/rename.asm; it writes only the new name and leaves the bytes after it, this code also clears them). The rename command
        /// cannot move a file; a move here writes a new entry in the other directory and frees the old slot, and the descriptor, data and allocation
        /// map stay as they are. A moved directory gets its entry for the parent (name two dots) pointed at the new parent.
        /// A name that differs only in case from the old name is allowed.
        /// </remarks>
        /// <param name="oldPath">
        /// The current path.
        /// </param>
        /// <param name="newPath">
        /// The new path. The directory that will hold it must exist.
        /// </param>
        /// <exception cref="FileNotFoundException">
        /// Thrown when <paramref name="oldPath"/> does not exist.
        /// </exception>
        /// <exception cref="DirectoryNotFoundException">
        /// Thrown when the directory that should hold the new name does not exist.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the volume is mounted read-only, the new name is not a legal OS-9 name or is taken, or a directory would move into itself.
        /// </exception>
        public void Rename(string oldPath, string newPath)
        {
            BeginWrite();
            try
            {
                SplitPath(oldPath, out uint oldParentFd, out byte[] oldParentDesc, out string oldName);
                byte[] oldParentData = ReadFileData(oldParentDesc);
                int oldOffset = FindEntry(oldParentData, oldName);
                if (oldOffset < 0 || IsDotName(oldName))
                    throw new FileNotFoundException("File not found: " + oldPath);

                SplitPath(newPath, out uint newParentFd, out byte[] newParentDesc, out string newName);
                ValidateName(newName);

                uint targetFd = ReadUInt24(oldParentData, oldOffset + DirNameLength);
                byte[] newParentData = newParentFd == oldParentFd ? oldParentData : ReadFileData(newParentDesc);
                int existing = FindEntry(newParentData, newName);
                if (existing >= 0 && !(newParentFd == oldParentFd && existing == oldOffset))
                    throw new IOException("A file or directory with this name exists: " + newPath);

                if (newParentFd == oldParentFd)
                {
                    List<Segment> segments = ReadSegments(oldParentDesc);
                    byte[] entry = new byte[DirEntrySize];
                    PutDirName(entry, 0, newName);
                    WriteUInt24(entry, DirNameLength, (int)targetFd);
                    PatchFile(segments, oldOffset, entry);
                    SetModified(oldParentDesc);
                    PutPending(oldParentFd, oldParentDesc);
                }
                else
                {
                    byte[] target = ReadSectorChecked(targetFd);
                    bool isDirectory = (target[FdAttributes] & DirectoryAttribute) != 0;
                    if (isDirectory)
                        CheckNotInside(targetFd, newParentFd);

                    AddDirectoryEntry(newParentFd, newParentDesc, newName, targetFd);
                    ClearDirectoryEntry(oldParentFd, ReadSectorChecked(oldParentFd), oldOffset);

                    if (isDirectory)
                    {
                        // The moved directory keeps its content; only its entry for the parent changes.
                        List<Segment> targetSegments = ReadSegments(target);
                        byte[] targetData = ReadFileData(target);
                        int dotDot = FindEntry(targetData, "..");
                        if (dotDot < 0)
                            throw new InvalidDataException("The directory has no entry for its parent: " + oldPath);

                        byte[] link = new byte[3];
                        WriteUInt24(link, 0, (int)newParentFd);
                        PatchFile(targetSegments, dotDot + DirNameLength, link);
                    }
                }

                CommitWrite();
            }
            catch
            {
                AbortWrite();
                throw;
            }
        }

        // ===================================================================
        // PRIVATE METHODS - WRITING
        // ===================================================================

        // Attribute byte of a new file: owner read and write, public read. The same as the startup file on the real disk (0x0B).
        private const byte NewFileAttributes = 0x0B;

        // Attribute byte of a new directory: directory, owner and public read, write and execute. The same as the directories on the real disk (0xBF).
        private const byte NewDirectoryAttributes = 0xBF;

        // Offsets in the file descriptor (defs/rbf.d, FD.ATT to FD.Creat).
        private const int FdAttributes = 0;
        private const int FdOwner = 1;
        private const int FdModified = 3;
        private const int FdLinkCount = 8;
        private const int FdSize = 9;
        private const int FdCreated = 13;

        // Segment allocation size: byte 14 of the option area, which starts at byte 63 of sector 0 (defs/rbf.d, IT.SAS and DD.OPT).
        private const int SasOffset = 63 + 14;

        // One allocation map sector holds 256 bytes, so 2048 bits. RBF searches the map one sector at a time, so a segment never crosses
        // this boundary (level1/modules/rbf.asm, label FatScan: "force one sector of clusters max.").
        private const int BitsPerMapSector = 2048;

        private struct Segment
        {
            public uint Lsn;
            public int Count;
        }

        // Starts a change: checks that the volume can be written and takes a working copy of the allocation map. Sectors that change are held
        // in memory until CommitWrite, so a failure half way leaves the device as it was.
        private void BeginWrite()
        {
            EnsureInitialized();
            if (_device!.IsReadOnly)
                throw new IOException("The volume is mounted read-only");

            // A cluster of more than one sector puts the rest of the cluster in the segment list of every new descriptor (rbf.asm, Create).
            // That path is not tested against a real disk here, so it is refused instead of guessed.
            if (_sectorsPerBit != 1)
                throw new NotSupportedException("Writing is only implemented for volumes with one sector per allocation map bit (this one has " + _sectorsPerBit + ")");

            _pending = new Dictionary<uint, byte[]>();
            _workMap = ReadAllocationMap();
            _originalMap = (byte[])_workMap.Clone();

            byte[] id = ReadSectorChecked(0);
            _sas = id[SasOffset];
            if (_sas == 0 || _sas > _totalSectors / 2)
                _sas = 1;
        }

        // Writes the changed map sectors and every held sector to the device, then flushes the device.
        private void CommitWrite()
        {
            int mapSectors = (_mapBytes + SectorSize - 1) / SectorSize;
            for (int m = 0; m < mapSectors; m++)
            {
                int start = m * SectorSize;
                int length = Math.Min(SectorSize, _mapBytes - start);
                bool changed = false;
                for (int i = 0; i < length; i++)
                {
                    if (_workMap![start + i] != _originalMap![start + i])
                    {
                        changed = true;
                        break;
                    }
                }

                if (!changed)
                    continue;

                // The last map sector can hold bytes past the end of the map. They stay as they were.
                byte[] current = ReadSectorChecked((uint)(1 + m));
                Buffer.BlockCopy(_workMap!, start, current, 0, length);
                PutPending((uint)(1 + m), current);
            }

            uint[] keys = new uint[_pending!.Count];
            _pending.Keys.CopyTo(keys, 0);
            Array.Sort(keys);
            for (int i = 0; i < keys.Length; i++)
            {
                _device!.WriteBlock(BlockAddress.FromLBA(keys[i]), _pending[keys[i]]);
            }

            _device!.Flush();
            AbortWrite();
        }

        // Drops the held sectors and the working map.
        private void AbortWrite()
        {
            _pending = null;
            _workMap = null;
            _originalMap = null;
        }

        private void PutPending(uint lsn, byte[] sector)
        {
            _pending![lsn] = (byte[])sector.Clone();
        }

        // Splits a path into the parent directory (its descriptor sector number and contents) and the last name.
        private void SplitPath(string path, out uint parentFd, out byte[] parentDesc, out string name)
        {
            string[] parts = (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                throw new IOException("The path names the root directory");

            name = parts[parts.Length - 1];
            StringBuilder parent = new StringBuilder();
            for (int i = 0; i < parts.Length - 1; i++)
            {
                parent.Append('/').Append(parts[i]);
            }

            try
            {
                parentFd = ResolvePath(parent.ToString(), out _);
            }
            catch (FileNotFoundException)
            {
                throw new DirectoryNotFoundException("Directory not found: " + parent);
            }

            parentDesc = ReadSectorChecked(parentFd);
            if ((parentDesc[FdAttributes] & DirectoryAttribute) == 0)
                throw new DirectoryNotFoundException("Not a directory: " + parent);
        }

        private static bool IsDotName(string name)
        {
            return name == "." || name == "..";
        }

        // The rules of F$PrsNam (level1/modules/kernel/fprsnam.asm): at most 29 characters; the first is a letter, a digit, an underscore or a dollar
        // sign; later ones may also be a period or a hyphen. A name may start with one period when the next character is not a period.
        private static void ValidateName(string name)
        {
            if (name.Length == 0 || name.Length > DirNameLength)
                throw new IOException("An OS-9 name has 1 to " + DirNameLength + " characters: " + name);

            if (IsDotName(name))
                throw new IOException("Not a legal OS-9 name: " + name);

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool letterOrDigit = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                bool basic = letterOrDigit || c == '_' || c == '$';
                bool later = c == '.' || c == '-';

                bool ok;
                if (i == 0)
                    ok = basic || (c == '.' && name.Length > 1 && name[1] != '.');
                else
                    ok = basic || later;

                if (!ok)
                    throw new IOException("Not a legal OS-9 name: " + name);
            }
        }

        // Finds the live entry with this name (case ignored) in the contents of a directory. Gives -1 when there is none.
        private static int FindEntry(byte[] data, string name)
        {
            for (int offset = 0; offset + DirEntrySize <= data.Length; offset += DirEntrySize)
            {
                if (data[offset] == 0)
                    continue;

                if (string.Equals(ReadName(data, offset, DirNameLength), name, StringComparison.OrdinalIgnoreCase))
                    return offset;
            }

            return -1;
        }

        // Puts a name into a directory entry: the characters, with bit 7 set on the last one. Bytes after it stay as they are (zero in a new entry).
        private static void PutDirName(byte[] entry, int offset, string name)
        {
            for (int i = 0; i < name.Length; i++)
            {
                entry[offset + i] = (byte)name[i];
            }

            entry[offset + name.Length - 1] |= 0x80;
        }

        // Adds an entry to a directory: the first free slot (first byte zero), or a new entry at the end of the file. A directory that has
        // no room left gets a new segment of the segment allocation size (RBF never shrinks a directory, rbf.asm L0EFE).
        private void AddDirectoryEntry(uint dirFd, byte[] dirDesc, string name, uint targetFd)
        {
            byte[] data = ReadFileData(dirDesc);
            long size = data.Length;
            List<Segment> segments = ReadSegments(dirDesc);

            long slot = -1;
            for (long offset = 0; offset + DirEntrySize <= size; offset += DirEntrySize)
            {
                if (data[offset] == 0)
                {
                    slot = offset;
                    break;
                }
            }

            if (slot < 0)
            {
                slot = size;
                long capacity = 0;
                for (int i = 0; i < segments.Count; i++)
                {
                    capacity += (long)segments[i].Count * SectorSize;
                }

                if (slot + DirEntrySize > capacity)
                {
                    List<Segment> more = AllocateDirectorySectors();
                    for (int i = 0; i < more.Count; i++)
                    {
                        for (int k = 0; k < more[i].Count; k++)
                            PutPending(more[i].Lsn + (uint)k, new byte[SectorSize]);

                        AppendSegment(segments, more[i]);
                    }

                    StoreSegments(dirDesc, segments);
                }

                WriteUInt32(dirDesc, FdSize, size + DirEntrySize);
            }

            byte[] entry = new byte[DirEntrySize];
            PutDirName(entry, 0, name);
            WriteUInt24(entry, DirNameLength, (int)targetFd);
            PatchFile(segments, slot, entry);

            SetModified(dirDesc);
            PutPending(dirFd, dirDesc);
        }

        // Frees a directory slot by setting the first byte of the entry to zero (rbf.asm, Delete: "clear first byte of filename").
        private void ClearDirectoryEntry(uint dirFd, byte[] dirDesc, long entryOffset)
        {
            List<Segment> segments = ReadSegments(dirDesc);
            byte[] one = new byte[1];
            PatchFile(segments, entryOffset, one);
            SetModified(dirDesc);
            PutPending(dirFd, dirDesc);
        }

        // Writes bytes into a file at a byte offset. The bytes must lie inside one sector, which holds for a 32-byte entry at a multiple of 32
        // and for the three bytes of a sector number inside an entry.
        private void PatchFile(List<Segment> segments, long offset, byte[] bytes)
        {
            long sectorIndex = offset / SectorSize;
            int within = (int)(offset % SectorSize);
            if (within + bytes.Length > SectorSize)
                throw new InvalidOperationException("A patch must stay inside one sector");

            uint lsn = 0;
            bool found = false;
            long remaining = sectorIndex;
            for (int i = 0; i < segments.Count && !found; i++)
            {
                if (remaining < segments[i].Count)
                {
                    lsn = segments[i].Lsn + (uint)remaining;
                    found = true;
                }
                else
                {
                    remaining -= segments[i].Count;
                }
            }

            if (!found)
                throw new InvalidDataException("The offset " + offset + " is outside the segments of the file");

            byte[] sector = ReadSectorChecked(lsn);
            Buffer.BlockCopy(bytes, 0, sector, within, bytes.Length);
            PutPending(lsn, sector);
        }

        // Releases the segments and the descriptor sector of a file whose last link goes away. The descriptor is left with size zero and an
        // empty segment list, as RBF leaves it after it truncates the file to nothing (rbf.asm, Delete and L0EFE).
        private void ReleaseDescriptor(uint fdLsn, byte[] fd)
        {
            byte links = fd[FdLinkCount];
            if (links > 1)
            {
                fd[FdLinkCount] = (byte)(links - 1);
                PutPending(fdLsn, fd);
                return;
            }

            FreeSegments(ReadSegments(fd));
            ClearBit(fdLsn);

            fd[FdLinkCount] = 0;
            WriteUInt32(fd, FdSize, 0);
            Array.Clear(fd, SegmentListStart, SectorSize - SegmentListStart);
            PutPending(fdLsn, fd);
        }

        // A directory is never entered inside itself: walk from the new parent up through the entries named two dots to the root.
        private void CheckNotInside(uint movedFd, uint newParentFd)
        {
            uint current = newParentFd;
            for (int step = 0; step < 1000; step++)
            {
                if (current == movedFd)
                    throw new IOException("A directory cannot be moved into itself");

                byte[] desc = ReadSectorChecked(current);
                byte[] data = ReadFileData(desc);
                int dotDot = FindEntry(data, "..");
                if (dotDot < 0)
                    return;

                uint up = ReadUInt24(data, dotDot + DirNameLength);
                if (up == current)
                    return;

                current = up;
            }

            throw new InvalidDataException("The directory tree is deeper than 1000 levels or loops");
        }

        // ---- segments ----

        private static List<Segment> ReadSegments(byte[] descriptor)
        {
            List<Segment> list = new List<Segment>();
            for (int i = 0; i < MaxSegments; i++)
            {
                int at = SegmentListStart + (i * SegmentSize);
                int count = ReadUInt16(descriptor, at + 3);
                if (count == 0)
                    break;

                Segment segment;
                segment.Lsn = ReadUInt24(descriptor, at);
                segment.Count = count;
                list.Add(segment);
            }

            return list;
        }

        // Stores a segment list in a descriptor and clears the rest of the list. The file size is set separately.
        private static void StoreSegments(byte[] descriptor, List<Segment> segments)
        {
            if (segments.Count > MaxSegments)
                throw new IOException("The segment list is full (" + MaxSegments + " segments); the free space is too fragmented for this file");

            Array.Clear(descriptor, SegmentListStart, SectorSize - SegmentListStart);
            for (int i = 0; i < segments.Count; i++)
            {
                int at = SegmentListStart + (i * SegmentSize);
                WriteUInt24(descriptor, at, (int)segments[i].Lsn);
                descriptor[at + 3] = (byte)(segments[i].Count >> 8);
                descriptor[at + 4] = (byte)segments[i].Count;
            }
        }

        // Adds a segment at the end of a list. When it starts right after the last one inside the same allocation map sector, the last one grows
        // instead (rbf.asm L0D4E to L0D96), as long as the count still fits in 16 bits.
        private static void AppendSegment(List<Segment> list, Segment added)
        {
            if (list.Count > 0)
            {
                Segment last = list[list.Count - 1];
                long end = (long)last.Lsn + last.Count;
                if (end == added.Lsn && (end - 1) / BitsPerMapSector == added.Lsn / BitsPerMapSector && last.Count + added.Count <= 0xFFFF)
                {
                    last.Count += added.Count;
                    list[list.Count - 1] = last;
                    return;
                }
            }

            list.Add(added);
        }

        // ---- allocation map ----

        private bool BitIsSet(long bit)
        {
            return (_workMap![(int)(bit >> 3)] & (0x80 >> (int)(bit & 7))) != 0;
        }

        private void SetBit(long bit)
        {
            _workMap![(int)(bit >> 3)] |= (byte)(0x80 >> (int)(bit & 7));
        }

        private void ClearBit(long bit)
        {
            _workMap![(int)(bit >> 3)] &= (byte)~(0x80 >> (int)(bit & 7));
        }

        private void FreeSegments(List<Segment> segments)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                for (int k = 0; k < segments[i].Count; k++)
                {
                    ClearBit(segments[i].Lsn + k);
                }
            }
        }

        // Finds and marks free sectors, the way RBF does (rbf.asm, FatScan): the map is searched one allocation map sector (2048 bits) at a time,
        // from the first; the first run in a map sector that is long enough for the request (at most 2048) is taken. When no map sector has such
        // a run, the longest run found is taken and the search goes on for the rest. A run never crosses a map sector.
        private List<Segment> AllocateSectors(int count)
        {
            List<Segment> result = new List<Segment>();
            long total = _totalSectors;
            long maxBits = (long)_mapBytes * 8;
            if (maxBits < total)
                total = maxBits;

            int remaining = count;
            while (remaining > 0)
            {
                int want = Math.Min(remaining, BitsPerMapSector);
                long takeStart = -1;
                int takeLength = 0;
                long bestStart = -1;
                int bestLength = 0;

                for (long groupStart = 0; groupStart < total && takeStart < 0; groupStart += BitsPerMapSector)
                {
                    long groupEnd = Math.Min(groupStart + BitsPerMapSector, total);
                    long runStart = -1;
                    int runLength = 0;
                    for (long bit = groupStart; bit < groupEnd; bit++)
                    {
                        if (BitIsSet(bit))
                        {
                            runLength = 0;
                            continue;
                        }

                        if (runLength == 0)
                            runStart = bit;

                        runLength++;
                        if (runLength > bestLength)
                        {
                            bestLength = runLength;
                            bestStart = runStart;
                        }

                        if (runLength == want)
                        {
                            takeStart = runStart;
                            takeLength = want;
                            break;
                        }
                    }
                }

                if (takeStart < 0)
                {
                    if (bestLength == 0)
                    {
                        // Give back what this request marked, so a request that fails leaves the working map as it was.
                        FreeSegments(result);
                        throw new IOException("The disk is full");
                    }

                    takeStart = bestStart;
                    takeLength = bestLength;
                }

                for (int k = 0; k < takeLength; k++)
                {
                    SetBit(takeStart + k);
                }

                Segment segment;
                segment.Lsn = (uint)takeStart;
                segment.Count = takeLength;
                AppendSegment(result, segment);
                remaining -= takeLength;
            }

            return result;
        }

        // The sectors a directory gets when it needs room: the segment allocation size from sector 0, or a single sector when that does not fit.
        private List<Segment> AllocateDirectorySectors()
        {
            try
            {
                return AllocateSectors(_sas);
            }
            catch (IOException) when (_sas > 1)
            {
                return AllocateSectors(1);
            }
        }

        // ---- dates ----

        private DateTime Now()
        {
            return Clock != null ? Clock() : DateTime.Now;
        }

        // Date last modified: year since 1900, month, day, hour, minute (defs/rbf.d, FD.DAT; the seconds that F$Time returns are dropped).
        private void SetModified(byte[] fd)
        {
            DateTime now = Now();
            fd[FdModified] = (byte)(now.Year - 1900);
            fd[FdModified + 1] = (byte)now.Month;
            fd[FdModified + 2] = (byte)now.Day;
            fd[FdModified + 3] = (byte)now.Hour;
            fd[FdModified + 4] = (byte)now.Minute;
        }

        // A new descriptor gets the date as both modified and created (rbf.asm, Create: "save it as creation date"). The owner is zero.
        private void SetCreated(byte[] fd)
        {
            SetModified(fd);
            fd[FdCreated] = fd[FdModified];
            fd[FdCreated + 1] = fd[FdModified + 1];
            fd[FdCreated + 2] = fd[FdModified + 2];
            fd[FdOwner] = 0;
            fd[FdOwner + 1] = 0;
        }

        private static void WriteUInt24(byte[] data, int offset, int value)
        {
            data[offset] = (byte)(value >> 16);
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)value;
        }

        private static void WriteUInt32(byte[] data, int offset, long value)
        {
            data[offset] = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)value;
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
                    ReadRaw(starts[s] + (uint)k, sector);
                    int take = (int)Math.Min(SectorSize, size - written);
                    Buffer.BlockCopy(sector, 0, result, (int)written, take);
                    written += take;
                }
            }

            return result;
        }

        // Reads a sector, giving the held copy when a change in progress has already rewritten it.
        private void ReadRaw(uint lsn, byte[] sector)
        {
            if (_pending != null && _pending.TryGetValue(lsn, out byte[]? held))
            {
                Buffer.BlockCopy(held, 0, sector, 0, SectorSize);
                return;
            }

            _device!.ReadBlock(BlockAddress.FromLBA(lsn), sector);
        }

        private byte[] ReadSectorChecked(uint lsn)
        {
            if (lsn >= _totalSectors)
                throw new InvalidDataException("Sector " + lsn + " is outside the volume");

            byte[] sector = new byte[SectorSize];
            ReadRaw(lsn, sector);
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
