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
    /// Reader and writer for OS-9 and NitrOS-9 disks (the Random Block File manager, RBF), as used on the Dragon and the Color Computer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Writing (create, replace and delete files, make and delete directories, rename and move) follows the NitrOS-9 sources for the RBF manager
    /// (level1/modules/rbf.asm and the layouts in defs/rbf.d) and for the commands that drive it (level1/cmds/rename.asm, deldir.asm, attr.asm).
    /// Each member says which RBF calls it stands for. The allocation map is searched and changed the way RBF does it, cluster by cluster
    /// (labels FatScan and ClrFBits), so volumes with more than one sector per map bit are written too. Nothing is written to the device
    /// until a change has passed every check and found all the space it needs.
    /// </para>
    /// <para>
    /// Owner and permission bits are checked as RBF checks them for the calling process (rbf.asm, labels ChkAttrs and FindFile). The calling
    /// user is <see cref="User"/>; 0 is the super user, who is treated as the owner of every file. Reading (listing and reading files) does not
    /// check permissions.
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
    /// The allocation map starts at logical sector 1. Bit 7 of the first byte is the first cluster, and a set bit means used. A cluster is the
    /// number of sectors per map bit; cluster n holds sectors n times that number and up.
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

        // The allocation map sector RBF searched last (V.MapSct in the drive table). It lives as long as the volume is mounted, as the drive
        // table does, and starts at zero. _mapSctAtStart restores it when a change fails.
        private int _mapSct;
        private int _mapSctAtStart;

        // Log2 of the sectors per map bit, set when a change starts.
        private int _bitShift;

        /// <summary>
        /// Gets or sets the clock that supplies the date and time written into the file descriptors of files and directories that are created
        /// or changed. When null, the local time of the computer is used.
        /// </summary>
        public Func<DateTime>? Clock { get; set; }

        /// <summary>
        /// Gets or sets the OS-9 user number the changes are made for (P$User of the calling process). It is checked against the owner and
        /// permission bits of files and directories, and it is the owner of new files and directories. 0, the default, is the super user,
        /// whom RBF treats as the owner of every file.
        /// </summary>
        public ushort User { get; set; }

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
            _mapSct = 0;
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
        /// A new file is what I$Create, I$Write and I$Close make (rbf.asm, labels Create, Write and Close). Create takes one cluster for the file
        /// descriptor (the first sector of the cluster; the rest of the cluster becomes the first segment of the file), then a directory entry
        /// (the first slot whose first byte is zero, otherwise a new entry at the end, which makes the directory 32 bytes longer). The write
        /// then adds segments until the data fits; each request is at least the segment allocation size of the volume. Close cuts the file back to
        /// the data, releasing the whole clusters past the last byte (label L0EFE). The new file belongs to <see cref="User"/> and has the
        /// attributes 0x0B (owner read and write, public read; the same as the startup file on a real disk).
        /// </para>
        /// <para>
        /// An existing file is what I$Open in write mode, I$SetStt SS.Size 0, I$Write and I$Close do: it keeps its descriptor sector, owner,
        /// attributes and creation date, gets a new date last modified, is cut to nothing and then written as above.
        /// </para>
        /// <para>
        /// Permissions (rbf.asm, labels FindFile and ChkAttrs): <see cref="User"/> needs write permission on every directory of the path, the root
        /// directory included, and on an existing file.
        /// </para>
        /// <para>
        /// The unused bytes at the end of the last sector keep what the sector held before, since RBF reads a sector before it writes part of it
        /// (label Read4D9). Nothing is written to the device until every allocation has succeeded, so a full disk or a full segment list leaves
        /// the volume as it was.
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
        /// Thrown when the volume is mounted read-only, the name is not a legal OS-9 name, the path names a directory, the user may not write
        /// there, the disk is full, or the file would need more segments than RBF lets a file grow to (47).
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// Thrown when the sectors per map bit is not a power of two, which the RBF arithmetic cannot handle.
        /// </exception>
        public void WriteFile(string path, byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            BeginWrite();
            try
            {
                SplitPathChecked(path, ModeWrite, out uint parentFd, out byte[] parentDesc, out string name, out _);
                ValidateName(name);

                byte[] parentData = ReadFileData(parentDesc);
                int entryOffset = FindEntry(parentData, name);

                uint fdLsn;
                byte[] fd;
                if (entryOffset < 0)
                {
                    fd = CreateDescriptor(parentFd, parentDesc, name, NewFileAttributes, out fdLsn);
                }
                else
                {
                    fdLsn = ReadUInt24(parentData, entryOffset + DirNameLength);
                    fd = ReadSectorChecked(fdLsn);
                    if ((fd[FdAttributes] & DirectoryAttribute) != 0)
                        throw new IOException("Is a directory: " + path);

                    // I$Open in write mode: the file must give write permission, and the date last modified is set (rbf.asm, Open1BB).
                    RequireAccess(fd, ModeWrite, path);
                    SetModified(fd);

                    // I$SetStt SS.Size 0 cuts the file only when it is longer than that (rbf.asm, Sst659 to Sst682).
                    if (ReadUInt32(fd, FdSize) > 0)
                    {
                        WriteUInt32(fd, FdSize, 0);
                        Truncate(fd, 0);
                    }
                }

                // I$Write of all the bytes from position 0, then I$Close: the size is stored and the file is cut at its end.
                Extend(fd, data.Length);
                WriteData(fd, data);
                WriteUInt32(fd, FdSize, data.Length);
                Truncate(fd, data.Length);
                PutPending(fdLsn, fd);

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
        /// Follows I$Delete (level1/modules/rbf.asm, label Delete): the user needs write permission on every directory of the path and on the
        /// file. The link count in the descriptor goes down by one. When it reaches zero the file is cut to nothing as I$Close does (label L0EFE),
        /// the cluster of the descriptor is released together with what is left of the first segment, and the descriptor is left with size zero
        /// and link count zero. The first byte of the directory entry is set to zero, which marks the slot as free. The NitrOS-9 Level 2 manager
        /// writes the old descriptor back instead (level2/modules/rbf.asm, Del3D4), keeping its size and segments; this code does what Level 1 does.
        /// </remarks>
        /// <param name="path">
        /// The file, with slashes between the names.
        /// </param>
        /// <exception cref="FileNotFoundException">
        /// Thrown when the file does not exist.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the volume is mounted read-only, the path names a directory, or the user may not delete the file.
        /// </exception>
        public void DeleteFile(string path)
        {
            BeginWrite();
            try
            {
                SplitPathChecked(path, ModeWrite, out uint parentFd, out byte[] parentDesc, out string name, out _);
                byte[] parentData = ReadFileData(parentDesc);
                int entryOffset = FindEntry(parentData, name);
                if (entryOffset < 0 || IsDotName(name))
                    throw new FileNotFoundException("File not found: " + path);

                uint fdLsn = ReadUInt24(parentData, entryOffset + DirNameLength);
                byte[] fd = ReadSectorChecked(fdLsn);
                if ((fd[FdAttributes] & DirectoryAttribute) != 0)
                    throw new IOException("Is a directory: " + path);

                RequireAccess(fd, ModeShare | ModeWrite, path);
                ReleaseDescriptor(fdLsn, fd);
                ClearDirectoryEntry(parentDesc, entryOffset);
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
        /// Follows I$MakDir (level1/modules/rbf.asm, label MakDir): the user needs write permission on every directory of the path. A descriptor
        /// and a directory entry are made as for a new file, then the directory is given room for 64 bytes (at least the segment allocation size
        /// of the volume, 8 sectors on the real disks, unless the rest of the descriptor's cluster already holds them), and its first sector is
        /// cleared and gets the entry for the parent (name two dots) and the entry for the directory itself (name one dot). The file size is 64.
        /// A new directory belongs to <see cref="User"/> and has the attributes 0xBF (directory, owner and public read, write and execute; the
        /// same as the directories on the real disks). I$MakDir closes no path, so the directory is not cut back (ioman.asm, IMakDir).
        /// </remarks>
        /// <param name="path">
        /// The new directory, with slashes between the names. The directory that holds it must exist.
        /// </param>
        /// <exception cref="DirectoryNotFoundException">
        /// Thrown when the directory that should hold the new one does not exist.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the volume is mounted read-only, the name is not a legal OS-9 name, the name is taken, the user may not write there, or
        /// the disk is full.
        /// </exception>
        public void CreateDirectory(string path)
        {
            BeginWrite();
            try
            {
                SplitPathChecked(path, ModeDirectory | ModeWrite, out uint parentFd, out byte[] parentDesc, out string name, out _);
                ValidateName(name);

                byte[] parentData = ReadFileData(parentDesc);
                if (FindEntry(parentData, name) >= 0)
                    throw new IOException("A file or directory with this name exists: " + path);

                // Create masks the directory bit out of the attributes it is given; MakDir sets it afterwards.
                byte[] fd = CreateDescriptor(parentFd, parentDesc, name, (byte)(NewDirectoryAttributes & ~DirectoryAttribute), out uint fdLsn);

                Extend(fd, 2 * DirEntrySize);
                fd[FdAttributes] |= DirectoryAttribute;
                WriteUInt32(fd, FdSize, 2 * DirEntrySize);

                // The first sector of the new directory is cleared and holds the entry for the parent and the entry for the directory itself.
                byte[] first = new byte[SectorSize];
                PutDirName(first, 0, "..");
                WriteUInt24(first, DirNameLength, (int)parentFd);
                PutDirName(first, DirEntrySize, ".");
                WriteUInt24(first, DirEntrySize + DirNameLength, (int)fdLsn);
                PutPending(FileSector(fd, 0), first);

                PutPending(fdLsn, fd);
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
        /// <para>
        /// Follows the deldir command (level1/cmds/deldir.asm) run with the whole path. It changes into every directory of the path in read and
        /// write mode (I$ChgDir, which sets their date last modified), opens the directory for reading, runs "attr -d" on it, which needs the
        /// user to be the owner or the super user and clears the directory bit (level1/cmds/attr.asm; its open in update mode sets the date last
        /// modified), and then deletes it with I$Delete like a file. So the user needs read and write permission on every directory of the path
        /// and on the directory itself, and its released descriptor no longer has the directory bit.
        /// </para>
        /// <para>
        /// The directory must hold nothing but the entries for itself and its parent (free slots do not count); deldir would delete the contents
        /// first, which this member does not do.
        /// </para>
        /// </remarks>
        /// <param name="path">
        /// The directory, with slashes between the names.
        /// </param>
        /// <exception cref="DirectoryNotFoundException">
        /// Thrown when the directory does not exist or the path names a file.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the volume is mounted read-only, the path is the root directory, the directory is not empty, or the user may not delete it.
        /// </exception>
        public void DeleteDirectory(string path)
        {
            BeginWrite();
            try
            {
                if ((path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 0)
                    throw new IOException("The root directory of an OS-9 disk cannot be deleted");

                SplitPathChecked(path ?? string.Empty, ModeRead | ModeWrite, out uint parentFd, out byte[] parentDesc, out string name, out List<uint> walked);
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

                // deldir opens the directory for reading; attr -d needs the owner or the super user and opens it in update mode; I$Delete then
                // needs write permission on it once the directory bit is gone.
                RequireAccess(fd, ModeDirectory | ModeRead, path);
                if (User != 0 && User != ReadUInt16(fd, FdOwner))
                    throw new IOException("Only the owner or the super user may delete this directory (attr refuses to clear its directory bit): " + path);

                RequireAccess(fd, ModeDirectory | ModeRead | ModeWrite, path);

                // Every directory deldir changed into gets a new date last modified; the parent is the last of them.
                for (int i = 0; i < walked.Count; i++)
                {
                    byte[] walkedDesc = walked[i] == parentFd ? parentDesc : ReadSectorChecked(walked[i]);
                    SetModified(walkedDesc);
                    PutPending(walked[i], walkedDesc);
                }

                SetModified(fd);
                fd[FdAttributes] &= unchecked((byte)~DirectoryAttribute);
                ReleaseDescriptor(fdLsn, fd);
                ClearDirectoryEntry(parentDesc, entryOffset);
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
        /// <para>
        /// A rename in the same directory follows the rename command (level1/cmds/rename.asm): it opens the file in write mode (a directory in
        /// directory and write mode), which sets its date last modified, changes into the directory that holds it in read and write mode, and
        /// opens that directory in update mode; both set the directory's date last modified. Then it writes the new name over the old one,
        /// as many bytes as the new name has, with bit 7 on the last; bytes left over from a longer old name stay. A name that differs only in
        /// case from the old name is allowed, as the command allows it.
        /// </para>
        /// <para>
        /// The rename command cannot move a file. A move here does the same opens in both directories, writes a new entry in the other directory
        /// as Create does and frees the old slot as Delete does; the descriptor, data and allocation map stay as they are. A moved directory gets
        /// its entry for the parent (name two dots) pointed at the new parent.
        /// </para>
        /// <para>
        /// Permissions: read and write on every directory of both paths, write on the file or directory that is renamed.
        /// </para>
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
        /// Thrown when the volume is mounted read-only, the new name is not a legal OS-9 name or is taken, a directory would move into itself,
        /// or the user may not make the change.
        /// </exception>
        public void Rename(string oldPath, string newPath)
        {
            BeginWrite();
            try
            {
                SplitPathChecked(oldPath, ModeRead | ModeWrite, out uint oldParentFd, out byte[] oldParentDesc, out string oldName, out _);
                byte[] oldParentData = ReadFileData(oldParentDesc);
                int oldOffset = FindEntry(oldParentData, oldName);
                if (oldOffset < 0 || IsDotName(oldName))
                    throw new FileNotFoundException("File not found: " + oldPath);

                SplitPathChecked(newPath, ModeRead | ModeWrite, out uint newParentFd, out byte[] newParentDesc, out string newName, out _);
                ValidateName(newName);

                uint targetFd = ReadUInt24(oldParentData, oldOffset + DirNameLength);
                byte[] target = ReadSectorChecked(targetFd);
                bool isDirectory = (target[FdAttributes] & DirectoryAttribute) != 0;
                RequireAccess(target, isDirectory ? ModeDirectory | ModeWrite : ModeWrite, oldPath);
                RequireAccess(oldParentDesc, ModeDirectory | ModeRead | ModeWrite, oldPath);
                RequireAccess(newParentDesc, ModeDirectory | ModeRead | ModeWrite, newPath);

                bool sameParent = newParentFd == oldParentFd;
                byte[] newParentData = sameParent ? oldParentData : ReadFileData(newParentDesc);
                int existing = FindEntry(newParentData, newName);
                if (existing >= 0 && !(sameParent && existing == oldOffset))
                    throw new IOException("A file or directory with this name exists: " + newPath);

                if (isDirectory && !sameParent)
                    CheckNotInside(targetFd, newParentFd);

                // The open of the file in write mode sets its date last modified.
                SetModified(target);
                PutPending(targetFd, target);

                // Changing into the directory and opening it in update mode set its date last modified.
                SetModified(oldParentDesc);
                PutPending(oldParentFd, oldParentDesc);

                if (sameParent)
                {
                    byte[] nameBytes = new byte[newName.Length];
                    PutDirName(nameBytes, 0, newName);
                    PatchFile(oldParentDesc, oldOffset, nameBytes);
                }
                else
                {
                    SetModified(newParentDesc);
                    PutPending(newParentFd, newParentDesc);
                    AddDirectoryEntry(newParentDesc, newName, targetFd);
                    PutPending(newParentFd, newParentDesc);
                    ClearDirectoryEntry(oldParentDesc, oldOffset);

                    if (isDirectory)
                    {
                        // The moved directory keeps its content; only its entry for the parent changes.
                        byte[] targetData = ReadFileData(target);
                        int dotDot = FindEntry(targetData, "..");
                        if (dotDot < 0)
                            throw new InvalidDataException("The directory has no entry for its parent: " + oldPath);

                        byte[] link = new byte[3];
                        WriteUInt24(link, 0, (int)newParentFd);
                        PatchFile(target, dotDot + DirNameLength, link);
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

        // Segment allocation size: byte 14 of the option area, which starts at byte 63 of sector 0 (defs/rbf.d, IT.SAS and DD.OPT). RBF takes
        // it from the path options of the drive (PD.SAS); format copies the path options of the drive that formatted the disk into DD.OPT
        // (level1/cmds/format.asm, I$GetStt SS.Opt into LSN0+DD.OPT), so this is the value of that drive.
        private const int SasOffset = 63 + 14;

        // One allocation map sector holds 256 bytes, so 2048 bits. RBF searches the map one sector at a time, so a segment never crosses
        // this boundary (level1/modules/rbf.asm, label FatScan: "force one sector of clusters max.").
        private const int BitsPerMapSector = 2048;

        // RBF grows a file to at most 47 segments: L0D07 looks for a free slot only up to the slot before the last (the 48th slot is read
        // but never filled, rbf.asm L0D29 to L0D4A).
        private const int MaxGrowSegments = 47;

        // Access mode bits (defs/os9.d: READ., WRITE., EXEC., PREAD., PWRIT., PEXEC., SHARE., DIR.).
        private const int ModeRead = 0x01;
        private const int ModeWrite = 0x02;
        private const int ModeExec = 0x04;
        private const int ModeShare = 0x40;
        private const int ModeDirectory = 0x80;

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

            // RBF turns sectors into clusters by shifting until the lowest set bit of DD.BIT falls out (rbf.asm FatScan L0DDD and ClrFBits L0FE5).
            // That is a division only when DD.BIT is a power of two.
            if ((_sectorsPerBit & (_sectorsPerBit - 1)) != 0)
                throw new NotSupportedException("The volume has " + _sectorsPerBit + " sectors per allocation map bit; RBF only works with a power of two");

            _bitShift = 0;
            while ((1 << _bitShift) < _sectorsPerBit)
            {
                _bitShift++;
            }

            _pending = new Dictionary<uint, byte[]>();
            _workMap = ReadAllocationMap();
            _originalMap = (byte[])_workMap.Clone();
            _mapSctAtStart = _mapSct;

            byte[] id = ReadSectorChecked(0);
            _sas = id[SasOffset];
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
            _pending = null;
            _workMap = null;
            _originalMap = null;
        }

        // Drops the held sectors and the working map, and puts back the map sector RBF would search first.
        private void AbortWrite()
        {
            _pending = null;
            _workMap = null;
            _originalMap = null;
            _mapSct = _mapSctAtStart;
        }

        private void PutPending(uint lsn, byte[] sector)
        {
            _pending![lsn] = (byte[])sector.Clone();
        }

        // ---- paths and permissions ----

        // Splits a path into the parent directory (its descriptor sector number and contents) and the last name, and checks each directory
        // on the way the way FindFile does (rbf.asm, Sst865 to L08C1): before a directory is searched, the user must have the access mode
        // plus the directory bit on it (ChkAttrs). The root directory is the first one checked, as for a path that starts with the device name.
        // walked gets the descriptor sectors of the directories from the root to the parent.
        private void SplitPathChecked(string path, int mode, out uint parentFd, out byte[] parentDesc, out string name, out List<uint> walked)
        {
            string[] parts = (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                throw new IOException("The path names the root directory");

            name = parts[parts.Length - 1];
            walked = new List<uint>();
            uint current = _rootFd;
            byte[] currentDesc = ReadSectorChecked(current);
            StringBuilder built = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if ((currentDesc[FdAttributes] & DirectoryAttribute) == 0)
                    throw new DirectoryNotFoundException("Not a directory: " + (built.Length == 0 ? "/" : built.ToString()));

                RequireAccess(currentDesc, mode | ModeDirectory, built.Length == 0 ? "/" : built.ToString());
                walked.Add(current);
                if (i == parts.Length - 1)
                    break;

                byte[] data = ReadFileData(currentDesc);
                int offset = FindEntry(data, parts[i]);
                built.Append('/').Append(parts[i]);
                if (offset < 0)
                    throw new DirectoryNotFoundException("Directory not found: " + built);

                current = ReadUInt24(data, offset + DirNameLength);
                currentDesc = ReadSectorChecked(current);
            }

            parentFd = current;
            parentDesc = currentDesc;
        }

        // ChkAttrs (rbf.asm): keeps the read, write and execute bits of the mode, moves them to the public bits when the user is neither the
        // super user (0) nor the owner, adds the directory bit of the mode, and requires those bits in the attributes. The directory bit is
        // compared both ways: a mode without it fails on a directory and a mode with it fails on a file. The sharable bit is not checked here.
        private bool HasAccess(byte[] descriptor, int mode)
        {
            int access = mode & (ModeExec | ModeRead | ModeWrite);
            int directoryBits = mode & (ModeDirectory | ModeShare);
            int owner = ReadUInt16(descriptor, FdOwner);
            if (User != 0 && User != owner)
                access <<= 3;

            int required = (access | directoryBits) & ~ModeShare;
            return ((required | ModeDirectory) & descriptor[FdAttributes]) == required;
        }

        private void RequireAccess(byte[] descriptor, int mode, string? path)
        {
            if (!HasAccess(descriptor, mode))
                throw new IOException("Not accessible for user " + User + " (OS-9 error 214, E$FNA): " + path);
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

        // ---- creating and deleting ----

        // The Create part of I$Create and I$MakDir (rbf.asm, Create): one cluster for the descriptor (FatScan for one sector), then the directory
        // entry, then the descriptor itself: the given attributes, the user as owner, the date as modified and created, link count 1, and, when a
        // cluster has more than one sector, the rest of the descriptor's cluster as the first segment. The file size is zero. The descriptor
        // is held as pending; the caller changes it further and stores it again.
        private byte[] CreateDescriptor(uint parentFd, byte[] parentDesc, string name, byte attributes, out uint fdLsn)
        {
            Segment cluster = FatScan(1);
            fdLsn = cluster.Lsn;

            AddDirectoryEntry(parentDesc, name, fdLsn);
            PutPending(parentFd, parentDesc);

            byte[] fd = new byte[SectorSize];
            fd[FdAttributes] = attributes;
            fd[FdOwner] = (byte)(User >> 8);
            fd[FdOwner + 1] = (byte)User;
            SetModified(fd);
            fd[FdCreated] = fd[FdModified];
            fd[FdCreated + 1] = fd[FdModified + 1];
            fd[FdCreated + 2] = fd[FdModified + 2];
            fd[FdLinkCount] = 1;
            if (cluster.Count > 1)
            {
                // "addd #1 ... adcb #$00": the segment starts at the sector after the descriptor and holds the rest of the cluster.
                WriteUInt24(fd, SegmentListStart, (int)(fdLsn + 1));
                WriteUInt16(fd, SegmentListStart + 3, cluster.Count - 1);
            }

            PutPending(fdLsn, fd);
            return fd;
        }

        // Adds an entry to a directory (rbf.asm, Create from CreatAC): the first slot whose first byte is zero, otherwise a new entry at the end;
        // the directory then grows by 32 bytes (Writ599 and L0C6F) and its new size is stored (MDir263). The 32 bytes of the entry are cleared
        // (Creat169) and get the name and the descriptor sector. The directory descriptor is changed in place; the caller stores it. Its date last
        // modified does not change: no part of Create sets it.
        private void AddDirectoryEntry(byte[] dirDesc, string name, uint targetFd)
        {
            byte[] data = ReadFileData(dirDesc);
            long size = data.Length;

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
                Extend(dirDesc, size + DirEntrySize);
                WriteUInt32(dirDesc, FdSize, size + DirEntrySize);
            }

            byte[] entry = new byte[DirEntrySize];
            PutDirName(entry, 0, name);
            WriteUInt24(entry, DirNameLength, (int)targetFd);
            PatchFile(dirDesc, slot, entry);
        }

        // Frees a directory slot by setting the first byte of the entry to zero (rbf.asm, Delete: "clear first byte of filename"). The
        // directory's date last modified does not change.
        private void ClearDirectoryEntry(byte[] dirDesc, long entryOffset)
        {
            byte[] one = new byte[1];
            PatchFile(dirDesc, entryOffset, one);
        }

        // The part of I$Delete after the permission check (rbf.asm, Delete from Del332): the link count goes down; while it is above zero only
        // the descriptor is written. At zero the file is cut to nothing (L0EFE with size 0), then ClrFBits releases the descriptor sector and
        // as many sectors after it as the first segment still holds ("ldd <$13,x / addd #1": with clusters of more than one sector, the rest of
        // the descriptor's cluster).
        private void ReleaseDescriptor(uint fdLsn, byte[] fd)
        {
            fd[FdLinkCount] = (byte)(fd[FdLinkCount] - 1);
            if (fd[FdLinkCount] != 0)
            {
                PutPending(fdLsn, fd);
                return;
            }

            WriteUInt32(fd, FdSize, 0);
            Truncate(fd, 0);
            ClrFBits(fdLsn, ReadUInt16(fd, SegmentListStart + 3) + 1);
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

        // ---- file contents ----

        // The logical sector of a file's sector number index, through its segment list. Throws when the index is past the segments.
        private uint FileSector(byte[] fd, long index)
        {
            long remaining = index;
            for (int i = 0; i < MaxSegments; i++)
            {
                int count = SlotCount(fd, i);
                if (count == 0)
                    break;

                if (remaining < count)
                    return SlotLsn(fd, i) + (uint)remaining;

                remaining -= count;
            }

            throw new InvalidDataException("Sector " + index + " is outside the segments of the file");
        }

        // Writes the data from the start of the file. A whole sector is written over; the last sector, when the data ends inside it, is read
        // first and only its first bytes are replaced, as RBF reads a sector before it writes part of it (rbf.asm, Read4D9: only a write of a
        // whole sector from its start skips the read).
        private void WriteData(byte[] fd, byte[] data)
        {
            long sectors = ((long)data.Length + SectorSize - 1) / SectorSize;
            for (long k = 0; k < sectors; k++)
            {
                uint lsn = FileSector(fd, k);
                long start = k * SectorSize;
                int take = (int)Math.Min(SectorSize, data.Length - start);
                byte[] sector = take == SectorSize ? new byte[SectorSize] : ReadSectorChecked(lsn);
                Buffer.BlockCopy(data, (int)start, sector, 0, take);
                PutPending(lsn, sector);
            }
        }

        // Writes bytes into a file at a byte offset. The bytes must lie inside one sector, which holds for a 32-byte entry at a multiple of 32
        // and for the three bytes of a sector number inside an entry.
        private void PatchFile(byte[] fd, long offset, byte[] bytes)
        {
            int within = (int)(offset % SectorSize);
            if (within + bytes.Length > SectorSize)
                throw new InvalidOperationException("A patch must stay inside one sector");

            uint lsn = FileSector(fd, offset / SectorSize);
            byte[] sector = ReadSectorChecked(lsn);
            Buffer.BlockCopy(bytes, 0, sector, within, bytes.Length);
            PutPending(lsn, sector);
        }

        // ---- segments ----

        private static int SlotCount(byte[] fd, int slot)
        {
            return ReadUInt16(fd, SegmentListStart + (slot * SegmentSize) + 3);
        }

        private static uint SlotLsn(byte[] fd, int slot)
        {
            return ReadUInt24(fd, SegmentListStart + (slot * SegmentSize));
        }

        // The number of segments in use: the slots before the first one with a count of zero.
        private static int UsedSlots(byte[] fd)
        {
            int used = 0;
            while (used < MaxSegments && SlotCount(fd, used) != 0)
            {
                used++;
            }

            return used;
        }

        // Makes room in a file for newSize bytes (rbf.asm, L0C6F with L0CB4 and L0D07). While the segments hold fewer sectors than the size
        // needs, the missing number of sectors is asked from FatScan; a request below 256 sectors is raised to the segment allocation size
        // (PD.SAS), and one above 65535 is cut to 65535. FatScan may give fewer sectors than asked (the longest free run) and the loop asks again.
        // In RBF the loop compares against the segment that holds the current position; for one write from the start of the file, the only
        // write done here, that comes to the same as comparing against all the segments.
        private void Extend(byte[] fd, long newSize)
        {
            long needed = (newSize + SectorSize - 1) / SectorSize;
            long allocated = 0;
            int used = UsedSlots(fd);
            for (int i = 0; i < used; i++)
            {
                allocated += SlotCount(fd, i);
            }

            while (needed > allocated)
            {
                long request = needed - allocated;
                if (request > 0xFFFF)
                    request = 0xFFFF;

                if (request < 256 && request < _sas)
                    request = _sas;

                Segment segment = FatScan((int)request);
                AddSegment(fd, segment);
                allocated += segment.Count;
            }
        }

        // Adds a segment from FatScan to a descriptor (rbf.asm, L0D07). With an empty list it becomes the first segment. When 47 segments are
        // in use the new sectors are released again and the segment list is full (E$SLF). Otherwise it joins the last segment when it starts
        // right after it, both start in the same region of 2048 clusters (the high 16 bits of the two sector numbers, exclusive or, shifted
        // right 3, masked with the complement of DD.BIT - 1, must be zero) and the count stays within 16 bits; if not, it takes the next slot.
        private void AddSegment(byte[] fd, Segment segment)
        {
            int used = UsedSlots(fd);
            if (used == 0)
            {
                StoreSlot(fd, 0, segment.Lsn, segment.Count);
                return;
            }

            if (used >= MaxGrowSegments)
            {
                ClrFBits(segment.Lsn, segment.Count);
                throw new IOException("The segment list is full (RBF lets a file grow to " + MaxGrowSegments + " segments, OS-9 error 217, E$SLF); the free space is too fragmented for this file");
            }

            int last = used - 1;
            uint lastLsn = SlotLsn(fd, last);
            int lastCount = SlotCount(fd, last);
            bool adjacent = ((lastLsn + (uint)lastCount) & 0xFFFFFF) == segment.Lsn;
            int mask = ~(_sectorsPerBit - 1) & 0xFFFF;
            bool sameRegion = (((int)((lastLsn >> 8) ^ (segment.Lsn >> 8)) >> 3) & mask) == 0;
            if (adjacent && sameRegion && lastCount + segment.Count <= 0xFFFF)
            {
                StoreSlot(fd, last, lastLsn, lastCount + segment.Count);
                return;
            }

            StoreSlot(fd, used, segment.Lsn, segment.Count);
        }

        private static void StoreSlot(byte[] fd, int slot, uint lsn, int count)
        {
            int at = SegmentListStart + (slot * SegmentSize);
            WriteUInt24(fd, at, (int)lsn);
            WriteUInt16(fd, at + 3, count);
        }

        // Cuts a file to newSize bytes (rbf.asm, L0EFE, as I$Close and I$SetStt SS.Size do it; I$Delete with size 0). The segment that holds the
        // byte at newSize keeps the sectors up to the one holding that byte, rounded up to whole clusters; the clusters after it are released and
        // its count is cut. Every later segment is released and its slot cleared, up to the slot before the last (the loop stops one slot early).
        // When no segment holds that byte, nothing is released. The slot of a segment cut to nothing keeps its sector number with a count of zero.
        // The file size is set by the caller.
        private void Truncate(byte[] fd, long newSize)
        {
            long cpSector = newSize >> 8;
            bool partial = (newSize & 0xFF) != 0;

            // L10B2: find the segment that holds the position.
            long start = 0;
            int slot = 0;
            bool found = false;
            for (; slot < MaxSegments; slot++)
            {
                int count = SlotCount(fd, slot);
                if (count == 0)
                    break;

                if (start + count > cpSector)
                {
                    found = true;
                    break;
                }

                start += count;
            }

            if (found)
            {
                int count = SlotCount(fd, slot);
                long free = start + count - cpSector;
                if (partial)
                    free--;

                free &= ~(long)(_sectorsPerBit - 1);
                if (free > 0)
                {
                    int kept = count - (int)free;
                    ClrFBits(SlotLsn(fd, slot) + (uint)kept, (int)free);
                    WriteUInt16(fd, SegmentListStart + (slot * SegmentSize) + 3, kept);
                }
            }

            // L0F79 to L0FC1: release the segments after it. The loop ends before the last slot.
            for (int next = slot + 1; next < MaxSegments - 1; next++)
            {
                int count = SlotCount(fd, next);
                if (count == 0)
                    break;

                ClrFBits(SlotLsn(fd, next), count);
                StoreSlot(fd, next, 0, 0);
            }
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

        // Searches the allocation map for sectors and marks them used, as FatScan does (rbf.asm). The request in sectors becomes clusters,
        // rounded up ((DD.BIT - 1 + sectors) shifted down, 65535 when the sum overflows 16 bits), and at most 2048 clusters, one map sector.
        // The map is searched one map sector at a time with F$SchBit (level1/modules/kernel/fallbit.asm: the first run that is long enough,
        // otherwise the first longest run). The search starts at the map sector of the last allocation (V.MapSct, set back to the first map
        // sector when it is not below the number of whole map sectors) and goes round the map once. The first map sector with a long
        // enough run wins; when none has one, the first longest run found is taken. A map sector number is 8 bits, so on a map of 256 sectors
        // the search wraps to sector 0, which counts as the reserved sector (V.ResBit is 0) and is skipped: the last map sector is never searched.
        // The result is in sectors: the first cluster times DD.BIT and the number of clusters times DD.BIT.
        private Segment FatScan(int sectors)
        {
            int sum = _sectorsPerBit - 1 + sectors;
            if (sum > 0xFFFF)
                sum = 0xFFFF;

            int want = sum >> _bitShift;
            if (want >= 0x800)
                want = 0x800;

            int mapHigh = (_mapBytes >> 8) & 0xFF;
            int mapLow = _mapBytes & 0xFF;
            if (_mapSct >= mapHigh)
                _mapSct = 0;

            int b = _mapSct;
            int bestSector = 0;
            int bestStart = 0;
            int bestLength = 0;
            while (true)
            {
                b = (b + 1) & 0xFF;
                if (b != 0)
                {
                    int bytes = b <= mapHigh ? SectorSize : mapLow;
                    long firstBit = (long)(b - 1) * BitsPerMapSector;
                    int found = SearchBits(firstBit, bytes * 8, want, out int runStart);
                    if (found == want)
                        return AllocateRun(b, runStart, want);

                    if (found > bestLength)
                    {
                        bestLength = found;
                        bestStart = runStart;
                        bestSector = b;
                    }
                }

                // L0E70: go on with the next map sector, or round to the start after the last one.
                if (b > mapHigh || (b == mapHigh && mapLow == 0))
                    b = 0;

                if (b == _mapSct)
                    break;
            }

            if (bestSector == 0)
                throw new IOException("The disk is full");

            return AllocateRun(bestSector, bestStart, bestLength);
        }

        // F$SchBit over the bits of one map sector: gives the length of the first run of free bits that reaches want, or else of the first
        // longest run, and where it starts (relative to firstBit).
        private int SearchBits(long firstBit, int bitCount, int want, out int start)
        {
            int runStart = 0;
            int runLength = 0;
            int bestStart = 0;
            int bestLength = 0;
            for (int i = 0; i < bitCount; i++)
            {
                long bit = firstBit + i;
                bool used = (bit >> 3) >= _workMap!.Length || BitIsSet(bit);
                if (used)
                {
                    runLength = 0;
                    continue;
                }

                if (runLength == 0)
                    runStart = i;

                runLength++;
                if (runLength >= want)
                {
                    start = runStart;
                    return want;
                }

                if (runLength > bestLength)
                {
                    bestLength = runLength;
                    bestStart = runStart;
                }
            }

            start = bestStart;
            return bestLength;
        }

        // F$AllBit on the run, then the cluster number becomes a sector number. V.MapSct becomes the map sector of the run.
        private Segment AllocateRun(int mapSector, int startBit, int clusters)
        {
            long firstCluster = ((long)(mapSector - 1) * BitsPerMapSector) + startBit;
            long lsn = firstCluster << _bitShift;
            long count = (long)clusters << _bitShift;
            if (lsn + count > _totalSectors)
                throw new InvalidDataException("The allocation map marks sectors past the end of the volume as free (sector " + lsn + ", " + count + " sectors)");

            for (int k = 0; k < clusters; k++)
            {
                SetBit(firstCluster + k);
            }

            _mapSct = mapSector - 1;

            Segment segment;
            segment.Lsn = (uint)lsn;
            segment.Count = (int)count;
            return segment;
        }

        // Releases sectors in the allocation map, as ClrFBits does (rbf.asm): the first sector is rounded up to a whole cluster and the count
        // down to whole clusters, so a part of a cluster is not released. RBF reads the one map sector that holds the first cluster and clears
        // the bits there (F$DelBit), so a run that goes on past that map sector, and a map sector number that wraps past 255 to sector 0, are
        // things RBF gets wrong; both are refused here before anything changes. A cluster past the map (above DD.MAP) is the E$IBA error.
        private void ClrFBits(long lsn, int count)
        {
            long firstCluster = (lsn + _sectorsPerBit - 1) >> _bitShift;
            int clusters = count >> _bitShift;
            if (clusters == 0)
                return;

            long mapSectorIndex = firstCluster / BitsPerMapSector;
            int mapHigh = (_mapBytes >> 8) & 0xFF;
            if ((mapSectorIndex & 0xFF) > mapHigh)
                throw new InvalidDataException("Sector " + lsn + " lies past the allocation map (OS-9 error 244, E$IBA)");

            if (mapSectorIndex >= 255)
                throw new IOException("RBF cannot release sector " + lsn + ": its map sector number wraps past 255 to sector 0");

            if ((firstCluster % BitsPerMapSector) + clusters > BitsPerMapSector)
                throw new IOException("RBF cannot release sectors " + lsn + " to " + (lsn + count - 1) + ": the run crosses an allocation map sector, and RBF clears bits in one map sector only");

            if (((firstCluster + clusters - 1) >> 3) >= _workMap!.Length)
                throw new InvalidDataException("Sectors " + lsn + " to " + (lsn + count - 1) + " lie past the allocation map");

            for (int k = 0; k < clusters; k++)
            {
                ClearBit(firstCluster + k);
            }
        }

        // ---- dates ----

        private DateTime Now()
        {
            return Clock != null ? Clock() : DateTime.Now;
        }

        // Date last modified: year since 1900, month, day, hour, minute (defs/rbf.d, FD.DAT; SetMTime keeps the link count that the sixth byte
        // of F$Time would overwrite, so the seconds are dropped).
        private void SetModified(byte[] fd)
        {
            DateTime now = Now();
            fd[FdModified] = (byte)(now.Year - 1900);
            fd[FdModified + 1] = (byte)now.Month;
            fd[FdModified + 2] = (byte)now.Day;
            fd[FdModified + 3] = (byte)now.Hour;
            fd[FdModified + 4] = (byte)now.Minute;
        }

        private static void WriteUInt16(byte[] data, int offset, int value)
        {
            data[offset] = (byte)(value >> 8);
            data[offset + 1] = (byte)value;
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
