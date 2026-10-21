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
    /// DragonDOS filesystem implementation for Dragon 32/64 VDK disk images.
    /// Supports 40/80 track, single/double-sided formats.
    /// Reads, creates, overwrites, deletes and renames files.
    /// </summary>
    /// <remarks>
    /// <para><b>The DragonDOS disk layout (from the DragonDOS 1.2 source, dosdefs.asm and ddos12.asm)</b></para>
    /// The directory track is track 20 (DirPrimary). A DragonDOS track holds the sectors of every head of a cylinder, so it has 18 sectors on a
    /// single sided disk and 36 on a double sided one, and the logical sector number (LSN) of sector s of track t is t x sectors per track + s - 1.
    /// <para><b>Directory track</b></para>
    ///  - Sectors 1 and 2: the block availability map (BAM). One bit per sector, 1 means free, the lowest sector of a byte is bit 0. Sector 1 holds
    ///    the first 1440 LSNs in its bytes 0 to 179, sector 2 the next 1440. Bytes FCH to FFH of sector 1 are the number of tracks, the sectors
    ///    per track, and the complement of each.
    ///  - Sectors 3 to 18: the directory, 10 entries of 25 bytes per sector, 160 entries in all, numbered from 0.
    /// <para><b>Directory entry</b></para>
    ///  - Byte 0: attributes. Bit 7 deleted (the entry may be reused), bit 5 the entry is continued in another entry, bit 3 end of directory
    ///    (no entry after this one is used), bit 1 write protected, bit 0 the entry is a continuation entry.
    ///  - A file entry: bytes 1 to 8 name, 9 to 11 extension (both padded with zero bytes), then four extents at 12, 15, 18 and 21.
    ///  - A continuation entry: seven extents at 1, 4, 7, 10, 13, 16 and 19.
    ///  - An extent is 3 bytes: the first LSN (high byte first) and the number of sectors (1 to 255). A count of zero is an unused extent.
    ///  - Byte 24: when bit 5 is set it is the number of the next entry of the file (never 0); otherwise it is the number of bytes used in the last
    ///    sector of the file (0 means all 256). So the length of a file comes from the LAST entry of its chain.
    ///  - A formatted disk has entries filled with 89H (deleted, end of directory, continuation) and zero. Deleting a file sets the attribute byte of
    ///    every entry of the file to 81H and sets the BAM bits of its extents; the names stay.
    /// <para><b>Backup directory</b></para>
    /// Every sector of the directory track that the DOS changes is copied to the same sector number of track 16 (DirBackup, routine DOSSyncDir), BAM
    /// sectors included. The real images in the test folder have the two tracks byte for byte equal. This class does the same on every write.
    /// <para><b>What this class chooses itself</b></para>
    /// Where a new file is placed on the disk is not fixed by the format. The DOS prefers free sectors after the end of the data it has just written,
    /// then free runs on tracks outside the directory track. This class continues the last extent when the next sector is free, and otherwise takes the
    /// first free run after the directory track, then the first free run before it. Overwriting a file deletes it and creates it again (the DOS
    /// BASIC command instead renames the old file to BAK; that is a BASIC feature, not the file system).
    /// </remarks>
    public class DragonDosFileSystem : IBlockFileSystem
    {
        private IBlockDevice? _device;
        private string _volumeLabel = string.Empty;
        private byte[]? _bam;
        private int _sectorsPerTrack;
        private int _trackCount;
        private bool _doubleSided;
        private bool _validBam;

        private const int PRIMARY_DIR_TRACK = 20;
        private const int BACKUP_DIR_TRACK = 16;
        private const int DIR_ENTRY_SIZE = 25;
        private const int ENTRIES_PER_SECTOR = 10;
        private const int MAX_FILES = 160;
        private const int SECTOR_SIZE = 256;

        // dosdefs.asm: first directory sector on the directory track (sectors 1 and 2 are the BAM), number of directory sectors,
        // the attribute bits, and the entry length and layout.
        private const int FIRST_DIR_SECTOR = 3;
        private const int DIR_SECTORS = 16;
        private const int AttrDeleted = 0x80;
        private const int AttrContinued = 0x20;
        private const int AttrEndOfDir = 0x08;
        private const int AttrWriteProtect = 0x02;
        private const int AttrIsContinuation = 0x01;
        private const int AttrAfterDelete = 0x81;
        private const int FileEntryExtents = 4;
        private const int ContinuationEntryExtents = 7;
        private const int FileEntryFirstExtent = 12;
        private const int ContinuationFirstExtent = 1;
        private const int EntryFlagOffset = 24;
        private const int MaxExtentSectors = 255;

        // dosdefs.asm BAMLSNPerSec (MaxLSN / 2) and BAMEntriesSec: the LSNs one BAM sector covers, and the bytes it uses.
        private const int BamLsnPerSector = 1440;

        // dosdefs.asm DirTracks: offset of the geometry bytes in the first BAM sector. The four bytes are
        // tracks, sectors per track, complement of tracks, complement of sectors per track.
        private const int GeometryTracksOffset = 0xFC;

        // DragonDOS has no volume name field; this is the label shown for a disk with a valid BAM.
        private const string DefaultVolumeLabel = "DRAGON DISK";

        /// <summary>
        /// Gets the file system type identifier: <c>DragonDOS</c>.
        /// </summary>
        public string FileSystemType => "DragonDOS";
        /// <summary>
        /// Gets the volume label set by <see cref="Initialize"/>, or an empty string before it.
        /// </summary>
        /// <remarks>
        /// DragonDOS stores no volume name, so the label is <c>DRAGON DISK</c> when the BAM geometry bytes
        /// at $FC to $FF check out, or <c>VDK DISK</c> when they fail.
        /// </remarks>
        public string VolumeLabel => _volumeLabel;
        /// <summary>
        /// Gets <c>.</c>, the separator between file name and extension in DragonDOS.
        /// </summary>
        public string ExtensionSeparator => ".";  // DragonDOS uses name.ext format

        /// <summary>
        /// Attaches the file system to a block device and reads the BAM from track 20, sectors 1 and 2.
        /// </summary>
        /// <remarks>
        /// The track count and sectors per track come from bytes $FC to $FF of the BAM when their complement bytes check out;
        /// otherwise they come from the device (or 18 sectors and 40 tracks) and the label is set to <c>VDK DISK</c>.
        /// </remarks>
        /// <param name="device">
        /// Block device holding the disk image, addressed by LBA.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="device"/> is <c>null</c>.
        /// </exception>
        public void Initialize(IBlockDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));

            // Read BAM from Track 20, Sectors 1-2
            byte[] bamSector1 = new byte[SECTOR_SIZE];
            byte[] bamSector2 = new byte[SECTOR_SIZE];

            // Determine sectors per track from device info
            BlockDeviceInfo? info = device.GetBlockDeviceInfo();
            if (info != null && info.SectorsPerTrack > 0)
            {
                _sectorsPerTrack = LogicalSectorsPerTrack(info);
                _doubleSided = (_sectorsPerTrack == 36);
            }
            else
            {
                // Default to single-sided
                _sectorsPerTrack = 18;
                _doubleSided = false;
            }

            // Calculate LSN for Track 20, Sector 1
            uint bamLsn = CalculateLSN(PRIMARY_DIR_TRACK, 0, 1);

            device.ReadBlock(BlockAddress.FromLBA(bamLsn), bamSector1);
            device.ReadBlock(BlockAddress.FromLBA(bamLsn + 1), bamSector2);

            // Combine BAM sectors
            _bam = new byte[512];
            Array.Copy(bamSector1, 0, _bam, 0, SECTOR_SIZE);
            Array.Copy(bamSector2, 0, _bam, SECTOR_SIZE, SECTOR_SIZE);

            // Extract disk geometry from BAM (dosdefs.asm DirTracks, DirSecPerTrk and their complements,
            // $FC to $FF of the first BAM sector; DosGetDiskGeometry in ddos12.asm validates them).
            // Note: Some VDK images (especially game images) may not have valid DragonDOS BAM
            if (!TryReadGeometry(bamSector1, out int bamTracks, out int bamSectorsPerTrack))
            {
                // This may not be a DragonDOS disk, try to use VDK header geometry instead
                _sectorsPerTrack = LogicalSectorsPerTrack(info);
                _trackCount = info != null && info.CylinderCount > 0 ? info.CylinderCount : 40;
                _doubleSided = (_sectorsPerTrack == 36);
                _volumeLabel = "VDK DISK";
                _validBam = false;
            }
            else
            {
                _validBam = true;
                _sectorsPerTrack = bamSectorsPerTrack;
                _trackCount = bamTracks;
                _doubleSided = (_sectorsPerTrack == 36);

                // DragonDOS stores no volume name. Bytes $00 to $FB of the first BAM sector are the
                // free-sector bitmap (dosdefs.asm BitmapPart1), so nothing there can be read as a label.
                _volumeLabel = DefaultVolumeLabel;
            }
        }

        /// <summary>
        /// Checks whether the device looks like a DragonDOS disk.
        /// </summary>
        /// <remarks>
        /// Reads the BAM sector at the start of track 20 (LBA 360 on a single-sided disk, 720 on a double-sided one) and checks that it gives
        /// 18 or 36 sectors per track and 40 or 80 tracks, each with a matching complement byte. The device is
        /// not changed. Any exception while reading gives <c>false</c>.
        /// </remarks>
        /// <param name="device">
        /// Block device to inspect.
        /// </param>
        /// <returns>
        /// <c>true</c> when the BAM geometry checks pass; otherwise <c>false</c>.
        /// </returns>
        public bool CanHandle(IBlockDevice device)
        {
            try
            {
                // Try reading BAM sector and check for valid geometry
                byte[] bamSector = new byte[SECTOR_SIZE];

                // DragonDOS counts both sides of a cylinder as one track (36 sectors on a double sided disk), so track 20
                // starts at 20 times the sectors of a cylinder: 360 on a single sided disk, 720 on a double sided one.
                uint lsn = (uint)(20 * LogicalSectorsPerTrack(device.GetBlockDeviceInfo()));
                device.ReadBlock(BlockAddress.FromLBA(lsn), bamSector);

                // Both pairs must pass their complement check (see TryReadGeometry) ...
                if (!TryReadGeometry(bamSector, out int tracks, out int spt))
                {
                    return false;
                }

                // ... and give a size DragonDOS formats: 18 or 36 sectors per track, 40 or 80 tracks.
                if (spt != 18 && spt != 36)
                {
                    return false;
                }

                if (tracks != 40 && tracks != 80)
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Lists the files in the root directory.
        /// </summary>
        /// <remarks>
        /// The 160 directory entries are read in order. A deleted entry, a continuation entry and an entry that is all zero are skipped, and the
        /// scan stops at the first entry with the end of directory bit (08H), as the DOS DIR command does.
        /// </remarks>
        /// <param name="path">
        /// Must be <c>null</c>, empty or <c>/</c>, because DragonDOS has only a root directory.
        /// </param>
        /// <returns>
        /// The directory entries.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the file system is not initialized.
        /// </exception>
        /// <exception cref="DirectoryNotFoundException">
        /// Thrown when <paramref name="path"/> names anything other than the root directory.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// Thrown when the disk is not a DragonDOS volume (its BAM geometry bytes fail the check), or the continuation chain of a file points
        /// at an entry that is not a continuation entry.
        /// </exception>
        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureDragonDosVolume();

            // DragonDOS only supports root directory
            if (!string.IsNullOrEmpty(path) && path != "/")
            {
                throw new DirectoryNotFoundException($"DragonDOS has only a root directory: {path}");
            }

            return ListRootDirectory();
        }

        /// <summary>
        /// Reads a whole file from its allocation blocks.
        /// </summary>
        /// <param name="path">
        /// File name as <c>NAME.EXT</c>, with or without a leading <c>/</c>. The part before the first dot is the name.
        /// </param>
        /// <returns>
        /// The file contents.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the file system is not initialized.
        /// </exception>
        /// <exception cref="System.IO.FileNotFoundException">
        /// Thrown when the file is not in the directory.
        /// </exception>
        public byte[] ReadFile(string path)
        {
            EnsureDragonDosVolume();

            SplitLookupName(path, out string filename, out string extension);

            // Find file in directory
            DragonDosEntry? entry = FindFileInDirectory(filename, extension);
            if (entry == null)
            {
                throw new System.IO.FileNotFoundException($"File not found: {path}");
            }

            // Read file data using allocation blocks
            return ReadFileData(entry);
        }

        /// <summary>
        /// Creates a file, or replaces the file with the same name.
        /// </summary>
        /// <remarks>
        /// The old file, if any, is deleted first (its entries are marked 81H and its BAM bits are set). Then the data is placed in free extents, the
        /// directory entry (with continuation entries when the file has more than four extents) is written, and the changed directory track sectors
        /// are written to track 20 and copied to track 16. All checks (name, space, free entries) are done before the first sector is written, so a
        /// failure leaves the disk as it was.
        /// </remarks>
        /// <param name="path">
        /// File name as <c>NAME.EXT</c>, with or without a leading <c>/</c>. The name has up to 8 characters and the extension up to 3, from A to Z
        /// (lower case is turned to upper case), 0 to 9 and the minus sign.
        /// </param>
        /// <param name="data">
        /// The file contents.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="data"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when the name is not a valid DragonDOS file name.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the volume is read-only, is not a DragonDOS volume, the existing file is write protected, the disk is full or the directory is full.
        /// </exception>
        public void WriteFile(string path, byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            EnsureWritable();
            ParseNewName(path, out string filename, out string extension);

            DirectoryTable table = LoadTable();

            int existing = FindHeadNumber(table, filename, extension);
            if (existing >= 0)
            {
                DragonDosEntry old = ParseFileEntry(table.Dir, existing)!;
                if ((old.Attributes & AttrWriteProtect) != 0)
                {
                    throw new IOException("The file " + path + " is write protected");
                }

                ReleaseFile(table, old);
            }

            int sectorsNeeded = (data.Length + SECTOR_SIZE - 1) / SECTOR_SIZE;
            List<AllocationBlock> extents = AllocateExtents(table, sectorsNeeded);

            // The first four extents fit the file entry, every further seven need one continuation entry.
            int continuations = extents.Count <= FileEntryExtents ? 0 : (extents.Count - FileEntryExtents + ContinuationEntryExtents - 1) / ContinuationEntryExtents;
            int[] slots = FindFreeEntries(table, 1 + continuations);

            byte lastBytes = (byte)(data.Length % SECTOR_SIZE);
            int extentIndex = 0;
            for (int slotIndex = 0; slotIndex < slots.Length; slotIndex++)
            {
                int offset = EntryOffset(slots[slotIndex]);
                Array.Clear(table.Dir, offset, DIR_ENTRY_SIZE);

                int firstExtent;
                int capacity;
                if (slotIndex == 0)
                {
                    table.Dir[offset] = 0;
                    WritePaddedName(table.Dir, offset + 1, filename, 8);
                    WritePaddedName(table.Dir, offset + 9, extension, 3);
                    firstExtent = FileEntryFirstExtent;
                    capacity = FileEntryExtents;
                }
                else
                {
                    table.Dir[offset] = AttrIsContinuation;
                    firstExtent = ContinuationFirstExtent;
                    capacity = ContinuationEntryExtents;
                }

                for (int e = 0; e < capacity && extentIndex < extents.Count; e++)
                {
                    int at = offset + firstExtent + (e * 3);
                    table.Dir[at] = (byte)(extents[extentIndex].LSN >> 8);
                    table.Dir[at + 1] = (byte)(extents[extentIndex].LSN & 0xFF);
                    table.Dir[at + 2] = extents[extentIndex].Count;
                    extentIndex++;
                }

                if (slotIndex + 1 < slots.Length)
                {
                    table.Dir[offset] |= AttrContinued;
                    table.Dir[offset + EntryFlagOffset] = (byte)slots[slotIndex + 1];
                }
                else
                {
                    table.Dir[offset + EntryFlagOffset] = lastBytes;
                }

                table.DirDirty[slots[slotIndex] / ENTRIES_PER_SECTOR] = true;
            }

            // Data first, then the directory track, so a failed data write never leaves a directory entry that points at nothing.
            int dataOffset = 0;
            byte[] sector = new byte[SECTOR_SIZE];
            for (int e = 0; e < extents.Count; e++)
            {
                for (int s = 0; s < extents[e].Count; s++)
                {
                    Array.Clear(sector, 0, SECTOR_SIZE);
                    int chunk = Math.Min(SECTOR_SIZE, data.Length - dataOffset);
                    Array.Copy(data, dataOffset, sector, 0, chunk);
                    dataOffset += chunk;
                    _device!.WriteBlock(BlockAddress.FromLBA((uint)(extents[e].LSN + s)), sector);
                }
            }

            CommitTable(table);
        }

        /// <summary>
        /// Deletes a file: every entry of the file is marked 81H and the BAM bits of its extents are set (free).
        /// </summary>
        /// <param name="path">
        /// File name as <c>NAME.EXT</c>, with or without a leading <c>/</c>.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the file system is not initialized.
        /// </exception>
        /// <exception cref="System.IO.FileNotFoundException">
        /// Thrown when the file is not in the directory.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the volume is read-only or not a DragonDOS volume, or the file is write protected.
        /// </exception>
        public void DeleteFile(string path)
        {
            EnsureWritable();
            SplitLookupName(path, out string filename, out string extension);

            DirectoryTable table = LoadTable();
            int number = FindHeadNumber(table, filename, extension);
            if (number < 0)
            {
                throw new System.IO.FileNotFoundException($"File not found: {path}");
            }

            DragonDosEntry entry = ParseFileEntry(table.Dir, number)!;
            if ((entry.Attributes & AttrWriteProtect) != 0)
            {
                throw new IOException("The file " + path + " is write protected");
            }

            ReleaseFile(table, entry);
            CommitTable(table);
        }

        /// <summary>
        /// Gets metadata of a file from its directory entry.
        /// </summary>
        /// <param name="path">
        /// File name as <c>NAME.EXT</c>, with or without a leading <c>/</c>.
        /// </param>
        /// <returns>
        /// The metadata, or <c>null</c> when the file is not in the directory.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the file system is not initialized.
        /// </exception>
        public FileMetadata? GetMetadata(string path)
        {
            EnsureDragonDosVolume();

            SplitLookupName(path, out string filename, out string extension);

            DragonDosEntry? entry = FindFileInDirectory(filename, extension);
            if (entry == null)
            {
                return null;
            }

            string fullName = string.IsNullOrEmpty(entry.Extension)
                ? entry.Filename
                : entry.Filename + "." + entry.Extension;

            return new FileMetadata
            {
                Name = fullName,
                Path = "/" + fullName,
                IsDirectory = false,
                Size = entry.FileSize,
                FileType = entry.Extension,
                Attributes = entry.IsDeleted ? "Deleted" : "Active",
                UrnIdentifier = UrnIdentifier.Encode(fullName)
            };
        }

        /// <summary>
        /// Counts the free sectors in the BAM.
        /// </summary>
        /// <remarks>
        /// The BAM is a bitmap, one bit per sector, 1 = free (DragonDOS 1.2 DOSGetFree). The set bits in the
        /// first 180 bytes of each of the two BAM sectors are counted. The BAM is read from the device on every call, so
        /// changes made by writes are counted.
        /// </remarks>
        /// <returns>
        /// The number of set bits in the two BAM sectors.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the file system is not initialized.
        /// </exception>
        public int GetBlocksFree()
        {
            EnsureInitialized();

            // DragonDOS 1.2 DOSGetFree (ddos12.asm, label LD162): the BAM is a bitmap with one bit per
            // sector, 1 = free and 0 = used (comment at GetBit). It lives in the first two sectors of
            // the directory track; the free count is the number of SET BITS in the first
            // BAMEntriesSec = 180 bytes of each sector (dosdefs.asm: MaxLSN/2/SectorsPerBAM, 8 sectors
            // per byte). The two sectors are held one after the other, 256 bytes each.
            const int BamEntriesPerSector = 180;
            int freeSectors = 0;
            byte[] bam = ReadBam();

            for (int sector = 0; sector < 2; sector++)
            {
                int start = sector * SECTOR_SIZE;
                for (int i = 0; i < BamEntriesPerSector; i++)
                {
                    freeSectors += System.Numerics.BitOperations.PopCount(bam[start + i]);
                }
            }

            return freeSectors;
        }

        /// <summary>
        /// Always throws NotSupportedException: DragonDOS has no directories other than the root.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public void CreateDirectory(string path)
        {
            throw new NotSupportedException("DragonDOS has no directories other than the root");
        }

        /// <summary>
        /// Does not delete anything: a DragonDOS disk has only the root directory.
        /// </summary>
        /// <param name="path">
        /// The directory to delete.
        /// </param>
        /// <exception cref="DirectoryNotFoundException">
        /// Thrown for any path other than the root, because no other directory exists (the same rule
        /// as <see cref="ListDirectory"/>).
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown for the root, which cannot be deleted.
        /// </exception>
        public void DeleteDirectory(string path)
        {
            if (!string.IsNullOrEmpty(path) && path != "/")
            {
                throw new DirectoryNotFoundException("DragonDOS has no directory '" + path + "'; only the root exists");
            }

            throw new IOException("The root directory of a DragonDOS disk cannot be deleted");
        }

        /// <summary>
        /// Renames a file: the 11 name and extension bytes of its first directory entry are replaced (DOSRename).
        /// </summary>
        /// <param name="oldPath">
        /// Current name as <c>NAME.EXT</c>.
        /// </param>
        /// <param name="newPath">
        /// New name as <c>NAME.EXT</c>, with the same rules as <see cref="WriteFile"/>.
        /// </param>
        /// <exception cref="System.IO.FileNotFoundException">
        /// Thrown when <paramref name="oldPath"/> is not in the directory.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when the new name is not a valid DragonDOS file name.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the volume is read-only or not a DragonDOS volume, the file is write protected, or a different file already has the new name.
        /// </exception>
        public void Rename(string oldPath, string newPath)
        {
            EnsureWritable();
            SplitLookupName(oldPath, out string oldName, out string oldExtension);
            ParseNewName(newPath, out string newName, out string newExtension);

            DirectoryTable table = LoadTable();
            int number = FindHeadNumber(table, oldName, oldExtension);
            if (number < 0)
            {
                throw new System.IO.FileNotFoundException($"File not found: {oldPath}");
            }

            DragonDosEntry entry = ParseFileEntry(table.Dir, number)!;
            if ((entry.Attributes & AttrWriteProtect) != 0)
            {
                throw new IOException("The file " + oldPath + " is write protected");
            }

            int clash = FindHeadNumber(table, newName, newExtension);
            if (clash >= 0 && clash != number)
            {
                throw new IOException("A file named " + newPath + " already exists");
            }

            int offset = EntryOffset(number);
            WritePaddedName(table.Dir, offset + 1, newName, 8);
            WritePaddedName(table.Dir, offset + 9, newExtension, 3);
            table.DirDirty[number / ENTRIES_PER_SECTOR] = true;
            CommitTable(table);
        }

        // ===================================================================
        // PRIVATE METHODS
        // ===================================================================

        /// <summary>
        /// Reads the geometry bytes of the first BAM sector and checks their complements.
        /// </summary>
        /// <remarks>
        /// dosdefs.asm: DirTracks = $FC, DirSecPerTrk = $FD, DirTracks1s = $FE (complement of the
        /// tracks byte), DirSecPerTrk1s = $FF (complement of the sectors per track byte). BuildBAM in
        /// ddos12.asm writes them; DosGetDiskGeometry loads $FE and $FF, complements them and compares
        /// the pair with $FC and $FD. Confirmed on a real image: 6809TUT1.VDK has 28 12 d7 ed there.
        /// </remarks>
        /// <param name="bamSector">
        /// The first BAM sector, at least 256 bytes.
        /// </param>
        /// <param name="tracks">
        /// The track count from byte $FC.
        /// </param>
        /// <param name="sectorsPerTrack">
        /// The sectors per track from byte $FD.
        /// </param>
        /// <returns>
        /// <c>true</c> when both complement bytes match; otherwise <c>false</c>.
        /// </returns>
        private static bool TryReadGeometry(byte[] bamSector, out int tracks, out int sectorsPerTrack)
        {
            tracks = bamSector[GeometryTracksOffset];
            sectorsPerTrack = bamSector[GeometryTracksOffset + 1];
            byte tracksComplement = bamSector[GeometryTracksOffset + 2];
            byte sectorsComplement = bamSector[GeometryTracksOffset + 3];

            return tracks == (~tracksComplement & 0xFF)
                && sectorsPerTrack == (~sectorsComplement & 0xFF);
        }

        // The directory track as it is in memory while one write operation works on it: the two BAM sectors, the sixteen directory sectors,
        // and which of them were changed and must be written back (to track 20 and to the backup on track 16).
        private sealed class DirectoryTable
        {
            public readonly byte[] Bam = new byte[2 * SECTOR_SIZE];
            public readonly byte[] Dir = new byte[DIR_SECTORS * SECTOR_SIZE];
            public readonly bool[] BamDirty = new bool[2];
            public readonly bool[] DirDirty = new bool[DIR_SECTORS];
        }

        // Offset of directory entry number n (0 to 159) inside the 4096 bytes of the directory sectors: ten entries of 25 bytes per sector.
        private static int EntryOffset(int number)
        {
            return ((number / ENTRIES_PER_SECTOR) * SECTOR_SIZE) + ((number % ENTRIES_PER_SECTOR) * DIR_ENTRY_SIZE);
        }

        private byte[] ReadBam()
        {
            byte[] bam = new byte[2 * SECTOR_SIZE];
            byte[] sector = new byte[SECTOR_SIZE];
            for (int i = 0; i < 2; i++)
            {
                _device!.ReadBlock(BlockAddress.FromLBA(CalculateLSN(PRIMARY_DIR_TRACK, 0, 1 + i)), sector);
                Array.Copy(sector, 0, bam, i * SECTOR_SIZE, SECTOR_SIZE);
            }

            return bam;
        }

        private DirectoryTable LoadTable()
        {
            DirectoryTable table = new DirectoryTable();
            byte[] bam = ReadBam();
            Array.Copy(bam, 0, table.Bam, 0, bam.Length);

            byte[] sector = new byte[SECTOR_SIZE];
            for (int i = 0; i < DIR_SECTORS; i++)
            {
                _device!.ReadBlock(BlockAddress.FromLBA(CalculateLSN(PRIMARY_DIR_TRACK, 0, FIRST_DIR_SECTOR + i)), sector);
                Array.Copy(sector, 0, table.Dir, i * SECTOR_SIZE, SECTOR_SIZE);
            }

            return table;
        }

        // Writes every changed sector of the table to the directory track and then to the same sector of the backup track (DOSSyncDir).
        private void CommitTable(DirectoryTable table)
        {
            byte[] sector = new byte[SECTOR_SIZE];
            for (int i = 0; i < 2; i++)
            {
                if (table.BamDirty[i])
                {
                    Array.Copy(table.Bam, i * SECTOR_SIZE, sector, 0, SECTOR_SIZE);
                    WriteDirectoryTrackSector(1 + i, sector);
                }
            }

            for (int i = 0; i < DIR_SECTORS; i++)
            {
                if (table.DirDirty[i])
                {
                    Array.Copy(table.Dir, i * SECTOR_SIZE, sector, 0, SECTOR_SIZE);
                    WriteDirectoryTrackSector(FIRST_DIR_SECTOR + i, sector);
                }
            }
        }

        private void WriteDirectoryTrackSector(int sectorNumber, byte[] sector)
        {
            _device!.WriteBlock(BlockAddress.FromLBA(CalculateLSN(PRIMARY_DIR_TRACK, 0, sectorNumber)), sector);
            _device.WriteBlock(BlockAddress.FromLBA(CalculateLSN(BACKUP_DIR_TRACK, 0, sectorNumber)), sector);
        }

        // BAM bit of one LSN: sector 1 of the directory track covers the first 1440 LSNs, sector 2 the next 1440; inside a byte the lowest LSN is bit 0.
        private static bool IsFree(byte[] bam, int lsn)
        {
            int index = ((lsn / BamLsnPerSector) * SECTOR_SIZE) + ((lsn % BamLsnPerSector) / 8);
            return (bam[index] & (1 << (lsn % 8))) != 0;
        }

        private static void SetFree(DirectoryTable table, int lsn, bool free)
        {
            int index = ((lsn / BamLsnPerSector) * SECTOR_SIZE) + ((lsn % BamLsnPerSector) / 8);
            if (free)
            {
                table.Bam[index] |= (byte)(1 << (lsn % 8));
            }
            else
            {
                table.Bam[index] &= (byte)~(1 << (lsn % 8));
            }

            table.BamDirty[lsn / BamLsnPerSector] = true;
        }

        // Marks every entry of the file deleted (81H) and sets the BAM bits of all its extents.
        private void ReleaseFile(DirectoryTable table, DragonDosEntry entry)
        {
            for (int b = 0; b < entry.Blocks.Length; b++)
            {
                int first = entry.Blocks[b].LSN;
                int count = entry.Blocks[b].Count;
                if (first + count > 2 * BamLsnPerSector)
                {
                    throw new InvalidDataException("An extent of " + entry.Filename + " lies outside the BAM (LSN " + first + ", " + count + " sectors)");
                }

                for (int s = 0; s < count; s++)
                {
                    SetFree(table, first + s, true);
                }
            }

            for (int c = 0; c < entry.Chain.Length; c++)
            {
                table.Dir[EntryOffset(entry.Chain[c])] = AttrAfterDelete;
                table.DirDirty[entry.Chain[c] / ENTRIES_PER_SECTOR] = true;
            }
        }

        // Takes free sectors for a file of the given size as a list of extents (at most 255 sectors each) and clears their BAM bits.
        // Policy (not fixed by the format): continue the last extent while the next sector is free, otherwise take the first free run after the
        // directory track, then the first free run before it. Throws IOException when the disk has too few free sectors.
        private List<AllocationBlock> AllocateExtents(DirectoryTable table, int sectors)
        {
            List<AllocationBlock> extents = new List<AllocationBlock>();
            int total = Math.Min(_trackCount * _sectorsPerTrack, 2 * BamLsnPerSector);
            int dirStart = (int)CalculateLSN(PRIMARY_DIR_TRACK, 0, 1);
            int afterDir = dirStart + _sectorsPerTrack;
            int remaining = sectors;

            while (remaining > 0)
            {
                if (extents.Count > 0)
                {
                    AllocationBlock last = extents[extents.Count - 1];
                    int next = last.LSN + last.Count;
                    if (last.Count < MaxExtentSectors && next < total && IsFree(table.Bam, next))
                    {
                        int add = 0;
                        while (add < remaining && last.Count + add < MaxExtentSectors && next + add < total && IsFree(table.Bam, next + add))
                        {
                            SetFree(table, next + add, false);
                            add++;
                        }

                        last.Count = (byte)(last.Count + add);
                        extents[extents.Count - 1] = last;
                        remaining -= add;
                        continue;
                    }
                }

                int start = FindFreeSector(table, afterDir, total);
                if (start < 0)
                {
                    start = FindFreeSector(table, 0, dirStart);
                }

                if (start < 0)
                {
                    throw new IOException("The disk is full");
                }

                int length = 0;
                while (length < remaining && length < MaxExtentSectors && start + length < total && IsFree(table.Bam, start + length))
                {
                    SetFree(table, start + length, false);
                    length++;
                }

                extents.Add(new AllocationBlock { LSN = (ushort)start, Count = (byte)length });
                remaining -= length;
            }

            return extents;
        }

        private static int FindFreeSector(DirectoryTable table, int from, int to)
        {
            for (int lsn = from; lsn < to; lsn++)
            {
                if (IsFree(table.Bam, lsn))
                {
                    return lsn;
                }
            }

            return -1;
        }

        // Finds the lowest entry numbers that can take new entries: deleted entries and entries that are all zero. A continuation entry may not
        // be entry 0, because a next-entry number of 0 means "none". Taking the lowest numbers keeps every entry with the end of directory bit after
        // all the entries in use.
        private int[] FindFreeEntries(DirectoryTable table, int count)
        {
            int[] found = new int[count];
            int have = 0;

            for (int n = 0; n < MAX_FILES && have < count; n++)
            {
                if (have > 0 && n == 0)
                {
                    continue;
                }

                int offset = EntryOffset(n);
                bool reusable = (table.Dir[offset] & AttrDeleted) != 0 || IsAllZero(table.Dir, offset);
                if (reusable)
                {
                    found[have] = n;
                    have++;
                }
            }

            if (have < count)
            {
                throw new IOException("The directory is full");
            }

            return found;
        }

        private static bool IsAllZero(byte[] data, int offset)
        {
            for (int i = 0; i < DIR_ENTRY_SIZE; i++)
            {
                if (data[offset + i] != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static void WritePaddedName(byte[] target, int offset, string text, int length)
        {
            for (int i = 0; i < length; i++)
            {
                target[offset + i] = i < text.Length ? (byte)text[i] : (byte)0;
            }
        }

        // Splits a path for a lookup. Nothing is rejected: an existing disk may hold names the DOS would not accept today.
        private static void SplitLookupName(string path, out string filename, out string extension)
        {
            if (path.StartsWith("/"))
            {
                path = path.Substring(1);
            }

            int dotIndex = path.IndexOf('.');
            if (dotIndex >= 0)
            {
                filename = path.Substring(0, dotIndex);
                extension = path.Substring(dotIndex + 1);
            }
            else
            {
                filename = path;
                extension = string.Empty;
            }
        }

        // Splits and checks a name that is about to be written, as the DOS name check does (ddos12.asm, DOSValidFilename): one dot at the most,
        // letters (lower case becomes upper case), digits and the minus sign only. The limits are 8 and 3 characters.
        private static void ParseNewName(string path, out string filename, out string extension)
        {
            SplitLookupName(path, out filename, out extension);
            filename = filename.ToUpperInvariant();
            extension = extension.ToUpperInvariant();

            if (filename.Length == 0 || filename.Length > 8 || extension.Length > 3 || extension.IndexOf('.') >= 0)
            {
                throw new ArgumentException("Not a DragonDOS file name (1 to 8 characters, a dot and up to 3 characters): " + path, nameof(path));
            }

            string both = filename + extension;
            for (int i = 0; i < both.Length; i++)
            {
                char c = both[i];
                bool valid = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-';
                if (!valid)
                {
                    throw new ArgumentException("Character '" + c + "' is not allowed in a DragonDOS file name: " + path, nameof(path));
                }
            }
        }

        // Reading the directory of a disk whose BAM geometry bytes fail their complement check would show random sector contents as files. Such
        // a disk is not a DragonDOS volume (a game disk with its own loader, for example), so the read members say so instead.
        private void EnsureDragonDosVolume()
        {
            EnsureInitialized();

            if (!_validBam)
            {
                throw new InvalidDataException("The disk is not a DragonDOS volume: bytes FCH to FFH of sector 1 of track 20 are not tracks, sectors per track and their complements");
            }
        }

        private void EnsureWritable()
        {
            EnsureDragonDosVolume();

            if (_device!.IsReadOnly)
            {
                throw new IOException("The volume is read-only");
            }
        }

        private DirectoryEntry[] ListRootDirectory()
        {
            List<DirectoryEntry> entries = new List<DirectoryEntry>();
            DirectoryTable table = LoadTable();

            for (int n = 0; n < MAX_FILES; n++)
            {
                byte attr = table.Dir[EntryOffset(n)];
                if ((attr & AttrEndOfDir) != 0)
                {
                    break;
                }

                DragonDosEntry? entry = ParseFileEntry(table.Dir, n);
                if (entry == null)
                {
                    continue;
                }

                string fullName = string.IsNullOrEmpty(entry.Extension)
                    ? entry.Filename
                    : entry.Filename + "." + entry.Extension;

                FileMetadata metadata = new FileMetadata
                {
                    Name = fullName,
                    Path = "/" + fullName,
                    IsDirectory = false,
                    Size = entry.FileSize,
                    FileType = entry.Extension,
                    Attributes = "File",
                    UrnIdentifier = UrnIdentifier.Encode(fullName)
                };

                entries.Add(new DirectoryEntry { Name = fullName, Metadata = metadata });
            }

            return entries.ToArray();
        }

        // Parses directory entry n and follows its continuation chain. Returns null for a deleted entry, a continuation entry and an entry
        // that is all zero. The length is counted from the sectors of all extents, and the bytes in the last sector come from the LAST entry of
        // the chain (ddos12.asm, FindFSNinU and DOSGetFLen).
        private DragonDosEntry? ParseFileEntry(byte[] dir, int number)
        {
            int offset = EntryOffset(number);
            byte attr = dir[offset];

            if ((attr & (AttrDeleted | AttrIsContinuation)) != 0 || IsAllZero(dir, offset))
            {
                return null;
            }

            string filename = Encoding.ASCII.GetString(dir, offset + 1, 8).Trim('\0', ' ');
            string extension = Encoding.ASCII.GetString(dir, offset + 9, 3).Trim('\0', ' ');

            List<AllocationBlock> blocks = new List<AllocationBlock>();
            List<int> chain = new List<int>();
            chain.Add(number);

            int current = number;
            byte lastSectorBytes;
            while (true)
            {
                int currentOffset = EntryOffset(current);
                bool isHead = current == number;
                int firstExtent = isHead ? FileEntryFirstExtent : ContinuationFirstExtent;
                int extentCount = isHead ? FileEntryExtents : ContinuationEntryExtents;

                for (int i = 0; i < extentCount; i++)
                {
                    int blockOffset = currentOffset + firstExtent + (i * 3);
                    ushort lsn = (ushort)((dir[blockOffset] << 8) | dir[blockOffset + 1]);
                    byte count = dir[blockOffset + 2];

                    if (count > 0)
                    {
                        blocks.Add(new AllocationBlock { LSN = lsn, Count = count });
                    }
                }

                if ((dir[currentOffset] & AttrContinued) == 0)
                {
                    lastSectorBytes = dir[currentOffset + EntryFlagOffset];
                    break;
                }

                int next = dir[currentOffset + EntryFlagOffset];
                if (next == 0 || next >= MAX_FILES || chain.Count >= MAX_FILES || (dir[EntryOffset(next)] & AttrIsContinuation) == 0)
                {
                    throw new InvalidDataException("DragonDOS directory entry " + current + " (" + filename + ") points at entry " + next + ", which is not a continuation entry");
                }

                chain.Add(next);
                current = next;
            }

            // Calculate file size
            int totalSectors = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                totalSectors += blocks[i].Count;
            }

            int fileSize;
            if (totalSectors == 0)
            {
                fileSize = 0;
            }
            else
            {
                fileSize = ((totalSectors - 1) * SECTOR_SIZE) + (lastSectorBytes == 0 ? SECTOR_SIZE : lastSectorBytes);
            }

            return new DragonDosEntry
            {
                Filename = filename,
                Extension = extension,
                Attributes = attr,
                Blocks = blocks.ToArray(),
                Chain = chain.ToArray(),
                LastSectorBytes = lastSectorBytes,
                FileSize = fileSize,
                IsDeleted = false,
                IsContinuation = false,
                IsEmpty = false
            };
        }

        // The number of the first entry of the named file, or -1. The scan stops at the end of directory bit like the DOS lookup.
        private int FindHeadNumber(DirectoryTable table, string filename, string extension)
        {
            for (int n = 0; n < MAX_FILES; n++)
            {
                if ((table.Dir[EntryOffset(n)] & AttrEndOfDir) != 0)
                {
                    break;
                }

                DragonDosEntry? entry = ParseFileEntry(table.Dir, n);
                if (entry != null &&
                    string.Equals(entry.Filename, filename, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entry.Extension, extension, StringComparison.OrdinalIgnoreCase))
                {
                    return n;
                }
            }

            return -1;
        }

        private DragonDosEntry? FindFileInDirectory(string filename, string extension)
        {
            DirectoryTable table = LoadTable();
            int number = FindHeadNumber(table, filename, extension);
            return number < 0 ? null : ParseFileEntry(table.Dir, number);
        }

        private byte[] ReadFileData(DragonDosEntry entry)
        {
            byte[] fileData = new byte[entry.FileSize];
            byte[] sectorData = new byte[SECTOR_SIZE];
            int written = 0;

            // Read all allocation blocks
            for (int i = 0; i < entry.Blocks.Length; i++)
            {
                AllocationBlock block = entry.Blocks[i];

                for (int s = 0; s < block.Count; s++)
                {
                    uint lsn = (uint)(block.LSN + s);
                    _device!.ReadBlock(BlockAddress.FromLBA(lsn), sectorData);

                    int take = Math.Min(SECTOR_SIZE, fileData.Length - written);
                    Array.Copy(sectorData, 0, fileData, written, take);
                    written += take;
                }
            }

            return fileData;
        }

        // Sectors in one DragonDOS track: the sectors of a cylinder over all its heads (18, or 36 on a double sided disk).
        // The device is asked for its head count; without device information a single sided disk of 18 sectors is assumed.
        private static int LogicalSectorsPerTrack(BlockDeviceInfo? info)
        {
            if (info == null || info.SectorsPerTrack <= 0)
            {
                return 18;
            }

            return info.SectorsPerTrack * Math.Max(1, (int)info.HeadCount);
        }

        private uint CalculateLSN(int track, int side, int sector)
        {
            if (_doubleSided)
            {
                // Double-sided: LSN = (track * 36) + (side * 18) + (sector - 1)
                return (uint)((track * 36) + (side * 18) + (sector - 1));
            }
            else
            {
                // Single-sided: LSN = (track * 18) + (sector - 1)
                return (uint)((track * 18) + (sector - 1));
            }
        }

        private void EnsureInitialized()
        {
            if (_device == null || _bam == null)
            {
                throw new InvalidOperationException("Filesystem not initialized");
            }
        }

        // ===================================================================
        // HELPER CLASSES
        // ===================================================================

        private sealed class DragonDosEntry
        {
            public string Filename { get; set; } = string.Empty;
            public string Extension { get; set; } = string.Empty;
            public byte Attributes { get; set; }
            public AllocationBlock[] Blocks { get; set; } = Array.Empty<AllocationBlock>();
            public int[] Chain { get; set; } = Array.Empty<int>();
            public byte LastSectorBytes { get; set; }
            public int FileSize { get; set; }
            public bool IsDeleted { get; set; }
            public bool IsContinuation { get; set; }
            public bool IsEmpty { get; set; }
        }

        private struct AllocationBlock
        {
            public ushort LSN { get; set; }
            public byte Count { get; set; }
        }
    }
}
