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
    /// CoCo DECB (Disk Extended Color BASIC) filesystem implementation.
    /// Used on Color Computer disk images (35 tracks, single-sided).
    /// Directory and FAT on Track 17, data on other tracks.
    /// </summary>
    public class DecbFileSystem : IBlockFileSystem
    {
        private IBlockDevice? _device;
        private string _volumeLabel = string.Empty;
        private byte[]? _fat;
        private int _sectorsPerTrack = 18;
        private int _trackCount = 35;

        private const int FAT_TRACK = 17;
        private const int FAT_SECTOR = 2;
        private const int DIR_START_SECTOR = 3;
        private const int DIR_END_SECTOR = 11;
        private const int DIR_ENTRY_SIZE = 32;
        private const int ENTRIES_PER_SECTOR = 8;
        private const int SECTOR_SIZE = 256;
        private const int GRANULE_SIZE = 2304; // 9 sectors * 256 bytes
        private const int SECTORS_PER_GRANULE = 9;
        private const int MAX_GRANULES = 68;

        /// <summary>
        /// Gets the file system type identifier: <c>DECB</c>.
        /// </summary>
        public string FileSystemType => "DECB";
        /// <summary>
        /// Gets <c>COCO DISK</c> after initialization, or an empty string before it. DECB disks have no stored label.
        /// </summary>
        public string VolumeLabel => _volumeLabel;
        /// <summary>
        /// Gets <c>.</c>, the separator between file name and extension in DECB.
        /// </summary>
        public string ExtensionSeparator => ".";  // DECB uses name.ext format

        /// <summary>
        /// Attaches the file system to a block device, takes the geometry from it and reads the FAT from track 17, sector 2.
        /// </summary>
        /// <remarks>
        /// When the device gives no sectors-per-track or cylinder count, 18 sectors and 35 tracks are used.
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

            BlockDeviceInfo? info = device.GetBlockDeviceInfo();
            if (info != null)
            {
                _sectorsPerTrack = info.SectorsPerTrack > 0 ? info.SectorsPerTrack : 18;
                _trackCount = info.CylinderCount > 0 ? info.CylinderCount : 35;
            }

            // Read FAT from Track 17, Sector 2
            uint fatLsn = CalculateLSN(FAT_TRACK, FAT_SECTOR);
            _fat = new byte[SECTOR_SIZE];
            device.ReadBlock(BlockAddress.FromLBA(fatLsn), _fat);

            // Try to extract volume label from first directory entry filename
            uint firstDirLsn = CalculateLSN(FAT_TRACK, DIR_START_SECTOR);
            byte[] firstDirSector = new byte[SECTOR_SIZE];
            device.ReadBlock(BlockAddress.FromLBA(firstDirLsn), firstDirSector);

            // Check if first entry exists (not 0x00 or 0xFF)
            if (firstDirSector[0] != 0x00 && firstDirSector[0] != 0xFF)
            {
                string firstFileName = Encoding.ASCII.GetString(firstDirSector, 0, 8).Trim();
                if (!string.IsNullOrWhiteSpace(firstFileName))
                {
                    _volumeLabel = $"COCO DISK";
                }
                else
                {
                    _volumeLabel = "COCO DISK";
                }
            }
            else
            {
                _volumeLabel = "COCO DISK";
            }
        }

        /// <summary>
        /// Checks whether the device looks like a DECB disk.
        /// </summary>
        /// <remarks>
        /// The device must report 35 cylinders and 18 sectors per track. The FAT sector (track 17, sector 2)
        /// is then read and its first 68 bytes must hold at least one free or allocated granule value.
        /// The device is not changed. Any exception while reading gives <c>false</c>.
        /// </remarks>
        /// <param name="device">
        /// Block device to inspect.
        /// </param>
        /// <returns>
        /// <c>true</c> when the geometry and the FAT sector fit DECB; otherwise <c>false</c>.
        /// </returns>
        public bool CanHandle(IBlockDevice device)
        {
            try
            {
                BlockDeviceInfo? info = device.GetBlockDeviceInfo();
                if (info == null) return false;

                // DECB is typically 35 tracks, 18 sectors per track
                if (info.CylinderCount != 35 || info.SectorsPerTrack != 18)
                {
                    return false;
                }

                // Read FAT sector to check validity
                uint fatLsn = (uint)((17 * 18) + (2 - 1)); // Track 17, Sector 2
                byte[] fatSector = new byte[SECTOR_SIZE];
                device.ReadBlock(BlockAddress.FromLBA(fatLsn), fatSector);

                // Count free granules (0xFF bytes) and allocated granules
                int freeCount = 0;
                int allocCount = 0;
                for (int i = 0; i < MAX_GRANULES; i++)
                {
                    if (fatSector[i] == 0xFF)
                    {
                        freeCount++;
                    }
                    else if (fatSector[i] < MAX_GRANULES || fatSector[i] >= 0xC0)
                    {
                        allocCount++;
                    }
                }

                // Valid DECB should have some allocated or free granules
                return (freeCount > 0 || allocCount > 0) && (freeCount + allocCount) > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Lists the files in the root directory (track 17, sectors 3 to 11).
        /// </summary>
        /// <param name="path">
        /// Must be <c>null</c>, empty or <c>/</c>, because DECB has only a root directory.
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
        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureInitialized();

            // DECB only supports root directory
            if (!string.IsNullOrEmpty(path) && path != "/")
            {
                throw new DirectoryNotFoundException($"DECB has only a root directory: {path}");
            }

            return ListRootDirectory();
        }

        /// <summary>
        /// Reads a whole file by following its granule chain in the FAT.
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
            EnsureInitialized();

            // Normalize path
            if (path.StartsWith("/"))
            {
                path = path.Substring(1);
            }

            // Split filename and extension
            string filename;
            string extension;
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

            // Find file in directory
            DecbEntry? entry = FindFileInDirectory(filename, extension);
            if (entry == null)
            {
                throw new System.IO.FileNotFoundException($"File not found: {path}");
            }

            // Read file data using granule chain
            return ReadFileData(entry);
        }

        /// <summary>
        /// Creates a file or replaces the contents of an existing one.
        /// </summary>
        /// <remarks>
        /// An existing file keeps its file type and ASCII flag. A new file gets them from its extension: BAS is type 0
        /// binary, DAT is type 1 ASCII, BIN is type 2 binary, TXT is type 3 ASCII, anything else is type 2 binary.
        /// The extension table is a choice made here, because the disk format does not tie a type to an extension.
        /// Use the overload with the type and the flag to set them yourself.
        /// </remarks>
        /// <param name="path">
        /// File name as <c>NAME.EXT</c>, with or without a leading <c>/</c>. Letters are stored in upper case.
        /// </param>
        /// <param name="data">
        /// The new contents.
        /// </param>
        /// <exception cref="IOException">
        /// Thrown when the volume is read-only, the directory has no free entry or the disk has too few free granules.
        /// The disk is not changed in the last two cases.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when the name does not fit a DECB name.
        /// </exception>
        public void WriteFile(string path, byte[] data)
        {
            EnsureInitialized();
            SplitName(path, out string name, out string extension);
            WriteFileCore(name, extension, data, 0, 0, false);
        }

        /// <summary>
        /// Creates a file or replaces an existing one and sets the file type and the ASCII flag in the directory entry.
        /// </summary>
        /// <param name="path">
        /// File name as <c>NAME.EXT</c>, with or without a leading <c>/</c>.
        /// </param>
        /// <param name="data">
        /// The new contents.
        /// </param>
        /// <param name="fileType">
        /// 0 BASIC program, 1 BASIC data, 2 machine language, 3 text.
        /// </param>
        /// <param name="asciiFlag">
        /// 0x00 for a binary file, 0xFF for an ASCII file.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the type is above 3 or the flag is neither 0x00 nor 0xFF.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the volume is read-only, the directory is full or the disk is full.
        /// </exception>
        public void WriteFile(string path, byte[] data, byte fileType, byte asciiFlag)
        {
            EnsureInitialized();
            if (fileType > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(fileType), "DECB file types are 0 to 3");
            }

            if (asciiFlag != 0x00 && asciiFlag != 0xFF)
            {
                throw new ArgumentOutOfRangeException(nameof(asciiFlag), "The DECB ASCII flag is 0x00 or 0xFF");
            }

            SplitName(path, out string name, out string extension);
            WriteFileCore(name, extension, data, fileType, asciiFlag, true);
        }

        /// <summary>
        /// Deletes a file: the first byte of its directory entry is set to 0 and every granule of its chain becomes free (0xFF) in the FAT.
        /// </summary>
        /// <param name="path">
        /// File name as <c>NAME.EXT</c>, with or without a leading <c>/</c>.
        /// </param>
        /// <exception cref="System.IO.FileNotFoundException">
        /// Thrown when the file is not in the directory.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the volume is read-only.
        /// </exception>
        public void DeleteFile(string path)
        {
            EnsureInitialized();
            EnsureWritable();
            SplitName(path, out string name, out string extension);
            ScanDirectory(name, extension, out int found, out int firstEmpty);
            if (found < 0)
            {
                throw new System.IO.FileNotFoundException($"File not found: {path}");
            }

            byte[] entry = ReadDirEntry(found);
            byte[] fat = (byte[])_fat!.Clone();
            FreeChain(fat, entry[13]);

            // Same as Toolshed kill: only the first byte of the entry is cleared, the rest stays.
            entry[0] = 0x00;
            CommitFat(fat);
            WriteDirEntry(found, entry);
            _device!.Flush();
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
            EnsureInitialized();

            if (path.StartsWith("/"))
            {
                path = path.Substring(1);
            }

            string filename;
            string extension;
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

            DecbEntry? entry = FindFileInDirectory(filename, extension);
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
                Attributes = GetFileTypeDescription(entry.FileType),
                UrnIdentifier = UrnIdentifier.Encode(fullName)
            };
        }

        /// <summary>
        /// Counts the free granules in the FAT and gives the result in sectors.
        /// </summary>
        /// <returns>
        /// The number of free 256-byte sectors: free granules times 9.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the file system is not initialized.
        /// </exception>
        public int GetBlocksFree()
        {
            EnsureInitialized();

            int freeGranules = 0;
            for (int i = 0; i < MAX_GRANULES; i++)
            {
                if (_fat![i] == 0xFF)
                {
                    freeGranules++;
                }
            }

            return freeGranules * SECTORS_PER_GRANULE;
        }

        /// <summary>
        /// Not supported, because DECB has no subdirectories. Always throws.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public void CreateDirectory(string path)
        {
            throw new NotSupportedException("DECB does not support subdirectories");
        }

        /// <summary>
        /// Not supported, because DECB has no subdirectories. Always throws.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public void DeleteDirectory(string path)
        {
            throw new NotSupportedException("DECB does not support subdirectories");
        }

        /// <summary>
        /// Renames a file by rewriting the name and extension in its directory entry. Nothing else changes.
        /// </summary>
        /// <param name="oldPath">
        /// Current name as <c>NAME.EXT</c>.
        /// </param>
        /// <param name="newPath">
        /// New name as <c>NAME.EXT</c>.
        /// </param>
        /// <exception cref="System.IO.FileNotFoundException">
        /// Thrown when <paramref name="oldPath"/> is not in the directory.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the volume is read-only or a file with the new name exists.
        /// </exception>
        public void Rename(string oldPath, string newPath)
        {
            EnsureInitialized();
            EnsureWritable();
            SplitName(oldPath, out string oldName, out string oldExtension);
            SplitName(newPath, out string newName, out string newExtension);

            ScanDirectory(oldName, oldExtension, out int found, out int firstEmpty);
            if (found < 0)
            {
                throw new System.IO.FileNotFoundException($"File not found: {oldPath}");
            }

            ScanDirectory(newName, newExtension, out int clash, out firstEmpty);
            if (clash >= 0)
            {
                throw new IOException($"A file named {newPath} exists");
            }

            byte[] entry = ReadDirEntry(found);
            PutName(entry, newName, newExtension);
            WriteDirEntry(found, entry);
            _device!.Flush();
        }

        // ===================================================================
        // PRIVATE METHODS
        // ===================================================================

        private DirectoryEntry[] ListRootDirectory()
        {
            List<DirectoryEntry> entries = new List<DirectoryEntry>();

            // Read directory sectors (Track 17, Sectors 3-11)
            for (int sector = DIR_START_SECTOR; sector <= DIR_END_SECTOR; sector++)
            {
                uint lsn = CalculateLSN(FAT_TRACK, sector);
                byte[] sectorData = new byte[SECTOR_SIZE];
                _device!.ReadBlock(BlockAddress.FromLBA(lsn), sectorData);

                // Parse 8 entries per sector
                for (int entryNum = 0; entryNum < ENTRIES_PER_SECTOR; entryNum++)
                {
                    int offset = entryNum * DIR_ENTRY_SIZE;
                    DecbEntry? entry = ParseDirectoryEntry(sectorData, offset);

                    if (entry != null && !entry.IsDeleted && !entry.IsEndOfDirectory)
                    {
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
                            Attributes = GetFileTypeDescription(entry.FileType),
                            UrnIdentifier = UrnIdentifier.Encode(fullName)
                        };

                        entries.Add(new DirectoryEntry { Name = fullName, Metadata = metadata });
                    }

                    // Stop if we hit end of directory marker
                    if (entry != null && entry.IsEndOfDirectory)
                    {
                        return entries.ToArray();
                    }
                }
            }

            return entries.ToArray();
        }

        private DecbEntry? ParseDirectoryEntry(byte[] data, int offset)
        {
            byte firstByte = data[offset];

            // Check for deleted file
            if (firstByte == 0x00)
            {
                return new DecbEntry { IsDeleted = true };
            }

            // Check for end of directory
            if (firstByte == 0xFF)
            {
                return new DecbEntry { IsEndOfDirectory = true };
            }

            // Parse file entry
            string filename = Encoding.ASCII.GetString(data, offset + 0, 8).Trim();
            string extension = Encoding.ASCII.GetString(data, offset + 8, 3).Trim();
            // DECB directory entry (32 bytes): name 0-7, extension 8-10, file type 11, ASCII flag 12, first granule 13,
            // bytes used in the last sector 14-15 (big-endian, as on the 6809). Checked on syzygy.dsk: SYZYGY30.BIN has
            // first granule 11 at offset 13 and 11 bytes at 14-15, and the granule chain then gives the size that its
            // own BIN load records add up to (28939). Reading these one byte late made it start at granule 0 and
            // return the data of SYZYGY10.BIN.
            byte fileType = data[offset + 11];
            byte asciiFlag = data[offset + 12];
            byte firstGranule = data[offset + 13];
            ushort lastSectorBytes = (ushort)((data[offset + 14] << 8) | data[offset + 15]);

            // Calculate file size by following granule chain
            int fileSize = CalculateFileSize(firstGranule, lastSectorBytes);

            return new DecbEntry
            {
                Filename = filename,
                Extension = extension,
                FileType = fileType,
                AsciiFlag = asciiFlag,
                FirstGranule = firstGranule,
                LastSectorBytes = lastSectorBytes,
                FileSize = fileSize,
                IsDeleted = false,
                IsEndOfDirectory = false
            };
        }

        private DecbEntry? FindFileInDirectory(string filename, string extension)
        {
            for (int sector = DIR_START_SECTOR; sector <= DIR_END_SECTOR; sector++)
            {
                uint lsn = CalculateLSN(FAT_TRACK, sector);
                byte[] sectorData = new byte[SECTOR_SIZE];
                _device!.ReadBlock(BlockAddress.FromLBA(lsn), sectorData);

                for (int entryNum = 0; entryNum < ENTRIES_PER_SECTOR; entryNum++)
                {
                    int offset = entryNum * DIR_ENTRY_SIZE;
                    DecbEntry? entry = ParseDirectoryEntry(sectorData, offset);

                    if (entry != null && entry.IsEndOfDirectory)
                    {
                        return null;
                    }

                    if (entry != null && !entry.IsDeleted)
                    {
                        if (string.Equals(entry.Filename, filename, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(entry.Extension, extension, StringComparison.OrdinalIgnoreCase))
                        {
                            return entry;
                        }
                    }
                }
            }

            return null;
        }

        private int CalculateFileSize(byte firstGranule, ushort lastSectorBytes)
        {
            if (firstGranule >= MAX_GRANULES)
            {
                return 0;
            }

            int totalSectors = 0;
            byte currentGranule = firstGranule;
            int granuleCount = 0;

            while (currentGranule < MAX_GRANULES && granuleCount < MAX_GRANULES)
            {
                byte fatEntry = _fat![currentGranule];

                if (fatEntry >= 0xC0) // Last granule (top 2 bits set)
                {
                    int sectorsInLastGranule = fatEntry & 0x1F; // Bottom 5 bits
                    totalSectors += sectorsInLastGranule;
                    break;
                }
                else if (fatEntry < MAX_GRANULES)
                {
                    totalSectors += SECTORS_PER_GRANULE;
                    currentGranule = fatEntry;
                    granuleCount++;
                }
                else
                {
                    break;
                }
            }

            if (totalSectors == 0)
            {
                return 0;
            }

            // The byte count of the last sector is taken as it stands. A full last sector is stored as 256 (syzygy.dsk
            // SYZYGY10.BIN holds 0100) and an empty file is one granule with one sector and a count of 0, which is how
            // Toolshed (libdecb gs_size: 2304 per full granule + 256 per full sector + the count) and DECB size it.
            int fileSize = ((totalSectors - 1) * SECTOR_SIZE) + Math.Min((int)lastSectorBytes, SECTOR_SIZE);
            return fileSize;
        }

        private byte[] ReadFileData(DecbEntry entry)
        {
            List<byte> fileData = new List<byte>(entry.FileSize);
            byte currentGranule = entry.FirstGranule;
            int granuleCount = 0;
            bool isLastGranule = false;
            int sectorsInLastGranule = 0;

            while (currentGranule < MAX_GRANULES && granuleCount < MAX_GRANULES)
            {
                byte fatEntry = _fat![currentGranule];

                if (fatEntry >= 0xC0) // Last granule
                {
                    isLastGranule = true;
                    sectorsInLastGranule = fatEntry & 0x1F;
                }

                // Calculate starting LSN for this granule
                // Granule 0-67 map to tracks (excluding track 17)
                int trackOffset = currentGranule / 2;
                if (trackOffset >= FAT_TRACK) trackOffset++; // Skip track 17

                int granuleInTrack = currentGranule % 2;
                int startSector = (granuleInTrack * SECTORS_PER_GRANULE) + 1;

                int sectorsToRead = isLastGranule ? sectorsInLastGranule : SECTORS_PER_GRANULE;

                for (int s = 0; s < sectorsToRead; s++)
                {
                    uint lsn = CalculateLSN(trackOffset, startSector + s);
                    byte[] sectorData = new byte[SECTOR_SIZE];
                    _device!.ReadBlock(BlockAddress.FromLBA(lsn), sectorData);

                    bool isLastSector = isLastGranule && (s == sectorsToRead - 1);
                    if (isLastSector)
                    {
                        int usedBytes = Math.Min((int)entry.LastSectorBytes, SECTOR_SIZE);
                        for (int j = 0; j < usedBytes; j++)
                        {
                            fileData.Add(sectorData[j]);
                        }
                    }
                    else
                    {
                        for (int j = 0; j < SECTOR_SIZE; j++)
                        {
                            fileData.Add(sectorData[j]);
                        }
                    }
                }

                if (isLastGranule)
                {
                    break;
                }

                currentGranule = fatEntry;
                granuleCount++;
            }

            return fileData.ToArray();
        }

        private uint CalculateLSN(int track, int sector)
        {
            return (uint)((track * _sectorsPerTrack) + (sector - 1));
        }

        private string GetFileTypeDescription(byte fileType)
        {
            return fileType switch
            {
                0 => "BASIC Program",
                1 => "BASIC Data",
                2 => "Machine Language",
                3 => "Text",
                _ => $"Type {fileType}"
            };
        }

        // ---- write support ----
        //
        // Rules used, all checked against real disks written by Toolshed 2.2 (decb dskini, copy, kill, rename, attr)
        // and against the Toolshed libdecb source (write, open/create, kill, rename, gs_size):
        //  - FAT: sector 2 of track 17, one byte per granule (68). 0xFF is free. A value below 0xC0 is the next granule
        //    of the chain. 0xC0 plus N (N = 1 to 9) ends the chain and says how many sectors of the last granule are used.
        //  - Directory: sectors 3 to 11 of track 17, 8 entries of 32 bytes, 72 in all. First byte 0x00 is a deleted
        //    entry (the rest stays), 0xFF is the end of the directory.
        //  - Entry: name 0-7, extension 8-10 (space padded), type 11, ASCII flag 12, first granule 13, bytes used in the
        //    last sector 14-15 (big-endian, 256 for a full sector, 0 for an empty file), 16 zero bytes.
        //  - An empty file still owns one granule (FAT value 0xC1, count 0).
        //  - Free granule search is the Toolshed port of what the DECB ROM does (find_free_granule): first granule of a
        //    file is searched from granule 34, each next granule from the one before it.

        private void EnsureWritable()
        {
            if (_device!.IsReadOnly)
            {
                throw new IOException("The volume is mounted read-only");
            }
        }

        private static bool IsNameChar(char c)
        {
            return c > ' ' && c < 0x7F && c != '.' && c != '/' && c != ',' && c != ':';
        }

        private static void SplitName(string path, out string name, out string extension)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (path.StartsWith("/"))
            {
                path = path.Substring(1);
            }

            int dot = path.IndexOf('.');
            if (dot >= 0)
            {
                name = path.Substring(0, dot);
                extension = path.Substring(dot + 1);
            }
            else
            {
                name = path;
                extension = string.Empty;
            }

            name = name.ToUpperInvariant();
            extension = extension.ToUpperInvariant();

            if (name.Length < 1 || name.Length > 8 || extension.Length > 3)
            {
                throw new ArgumentException($"Not a DECB name (1 to 8 characters, extension up to 3): {path}");
            }

            for (int i = 0; i < name.Length; i++)
            {
                if (!IsNameChar(name[i]))
                {
                    throw new ArgumentException($"Character not allowed in a DECB name: {path}");
                }
            }

            for (int i = 0; i < extension.Length; i++)
            {
                if (!IsNameChar(extension[i]))
                {
                    throw new ArgumentException($"Character not allowed in a DECB extension: {path}");
                }
            }
        }

        private static void PutName(byte[] entry, string name, string extension)
        {
            for (int i = 0; i < 8; i++)
            {
                entry[i] = i < name.Length ? (byte)name[i] : (byte)' ';
            }

            for (int i = 0; i < 3; i++)
            {
                entry[8 + i] = i < extension.Length ? (byte)extension[i] : (byte)' ';
            }
        }

        private byte[] ReadDirEntry(int index)
        {
            byte[] sector = new byte[SECTOR_SIZE];
            _device!.ReadBlock(BlockAddress.FromLBA(CalculateLSN(FAT_TRACK, DIR_START_SECTOR + (index / ENTRIES_PER_SECTOR))), sector);
            byte[] entry = new byte[DIR_ENTRY_SIZE];
            Array.Copy(sector, (index % ENTRIES_PER_SECTOR) * DIR_ENTRY_SIZE, entry, 0, DIR_ENTRY_SIZE);
            return entry;
        }

        private void WriteDirEntry(int index, byte[] entry)
        {
            uint lsn = CalculateLSN(FAT_TRACK, DIR_START_SECTOR + (index / ENTRIES_PER_SECTOR));
            byte[] sector = new byte[SECTOR_SIZE];
            _device!.ReadBlock(BlockAddress.FromLBA(lsn), sector);
            Array.Copy(entry, 0, sector, (index % ENTRIES_PER_SECTOR) * DIR_ENTRY_SIZE, DIR_ENTRY_SIZE);
            _device.WriteBlock(BlockAddress.FromLBA(lsn), sector);
        }

        // Looks for a name in the directory. found is the entry index or -1. firstEmpty is the first entry that is
        // deleted (0x00) or the end marker (0xFF), or -1 when all 72 are in use. The scan stops at the end marker.
        private void ScanDirectory(string name, string extension, out int found, out int firstEmpty)
        {
            found = -1;
            firstEmpty = -1;
            byte[] sector = new byte[SECTOR_SIZE];

            for (int s = 0; s <= DIR_END_SECTOR - DIR_START_SECTOR; s++)
            {
                _device!.ReadBlock(BlockAddress.FromLBA(CalculateLSN(FAT_TRACK, DIR_START_SECTOR + s)), sector);
                for (int e = 0; e < ENTRIES_PER_SECTOR; e++)
                {
                    int offset = e * DIR_ENTRY_SIZE;
                    int index = (s * ENTRIES_PER_SECTOR) + e;
                    byte first = sector[offset];

                    if (first == 0xFF)
                    {
                        if (firstEmpty < 0)
                        {
                            firstEmpty = index;
                        }

                        return;
                    }

                    if (first == 0x00)
                    {
                        if (firstEmpty < 0)
                        {
                            firstEmpty = index;
                        }

                        continue;
                    }

                    string entryName = Encoding.ASCII.GetString(sector, offset, 8).Trim();
                    string entryExtension = Encoding.ASCII.GetString(sector, offset + 8, 3).Trim();
                    if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(entryExtension, extension, StringComparison.OrdinalIgnoreCase))
                    {
                        found = index;
                        return;
                    }
                }
            }
        }

        // Sets every granule of the chain that starts at firstGranule to free. The walk is bounded so a damaged
        // (looping) chain cannot run for ever.
        private static void FreeChain(byte[] fat, byte firstGranule)
        {
            int granule = firstGranule;
            int steps = 0;
            while (granule < MAX_GRANULES && steps <= MAX_GRANULES)
            {
                byte next = fat[granule];
                fat[granule] = 0xFF;
                if (next >= 0xC0 || next == 0xFF)
                {
                    break;
                }

                granule = next;
                steps++;
            }
        }

        // Port of Toolshed find_free_granule, which says it matches the DECB ROM: start at the first granule of the
        // track of nextTo, then alternate outward over the tracks. Returns -1 when no granule is free.
        private static int FindFreeGranule(byte[] fat, int nextTo)
        {
            int counter = 0;
            int direction = 0;
            int granule = nextTo & ~1;

            while (counter < MAX_GRANULES)
            {
                if (granule >= 0 && granule < MAX_GRANULES && fat[granule] == 0xFF)
                {
                    return granule;
                }

                counter++;
                granule++;

                if ((granule & 1) != 0)
                {
                    continue;
                }

                int save = granule;
                granule -= 2;
                direction = ~direction;

                if (direction != 0)
                {
                    granule -= counter;
                    if (granule < 0)
                    {
                        granule = save;
                        direction = ~direction;
                    }
                }
                else
                {
                    granule += counter;
                    if (granule >= MAX_GRANULES)
                    {
                        granule = save;
                        granule -= 4;
                        direction = ~direction;
                    }
                }
            }

            return -1;
        }

        private void CommitFat(byte[] fat)
        {
            uint lsn = CalculateLSN(FAT_TRACK, FAT_SECTOR);
            _device!.WriteBlock(BlockAddress.FromLBA(lsn), fat);
            _fat = fat;
        }

        private static void DefaultTypeFor(string extension, out byte fileType, out byte asciiFlag)
        {
            fileType = 2;
            asciiFlag = 0x00;
            if (extension == "BAS")
            {
                fileType = 0;
            }
            else if (extension == "DAT")
            {
                fileType = 1;
                asciiFlag = 0xFF;
            }
            else if (extension == "TXT")
            {
                fileType = 3;
                asciiFlag = 0xFF;
            }
        }

        private void WriteFileCore(string name, string extension, byte[] data, byte fileType, byte asciiFlag, bool typeGiven)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            EnsureWritable();
            ScanDirectory(name, extension, out int found, out int firstEmpty);
            byte[] fat = (byte[])_fat!.Clone();

            int slot = firstEmpty;
            if (found >= 0)
            {
                byte[] old = ReadDirEntry(found);
                if (!typeGiven)
                {
                    fileType = old[11];
                    asciiFlag = old[12];
                }

                // Toolshed overwrites by killing the old file and creating a new one, so its granules are free again
                // for the new data and the first empty entry is used (which can be the old one).
                FreeChain(fat, old[13]);
                if (slot < 0 || found < slot)
                {
                    slot = found;
                }
            }
            else if (!typeGiven)
            {
                DefaultTypeFor(extension, out fileType, out asciiFlag);
            }

            if (slot < 0)
            {
                throw new IOException("The DECB directory is full (72 entries)");
            }

            int granulesNeeded = Math.Max(1, (data.Length + GRANULE_SIZE - 1) / GRANULE_SIZE);
            int[] chain = new int[granulesNeeded];
            int previous = 34;
            for (int i = 0; i < granulesNeeded; i++)
            {
                int granule = FindFreeGranule(fat, previous);
                if (granule < 0)
                {
                    throw new IOException("The disk is full: not enough free granules");
                }

                fat[granule] = 0xC1; // taken for now; the real value is set below
                chain[i] = granule;
                previous = granule;
            }

            // Sectors used in the last granule and bytes used in the last sector.
            int remaining = data.Length - ((granulesNeeded - 1) * GRANULE_SIZE);
            int sectorsInLast = Math.Max(1, (remaining + SECTOR_SIZE - 1) / SECTOR_SIZE);
            int lastSectorBytes = data.Length == 0 ? 0 : (remaining % SECTOR_SIZE == 0 ? SECTOR_SIZE : remaining % SECTOR_SIZE);

            for (int i = 0; i < granulesNeeded - 1; i++)
            {
                fat[chain[i]] = (byte)chain[i + 1];
            }

            fat[chain[granulesNeeded - 1]] = (byte)(0xC0 + sectorsInLast);

            // Data first, then FAT, then the directory entry, so a failure leaves the old directory entry valid.
            byte[] buffer = new byte[SECTOR_SIZE];
            int position = 0;
            for (int i = 0; i < granulesNeeded; i++)
            {
                int track = chain[i] / 2;
                if (track >= FAT_TRACK)
                {
                    track++;
                }

                int startSector = ((chain[i] % 2) * SECTORS_PER_GRANULE) + 1;
                int sectors = i == granulesNeeded - 1 ? sectorsInLast : SECTORS_PER_GRANULE;
                for (int s = 0; s < sectors; s++)
                {
                    Array.Clear(buffer, 0, buffer.Length);
                    int count = Math.Min(SECTOR_SIZE, data.Length - position);
                    if (count > 0)
                    {
                        Array.Copy(data, position, buffer, 0, count);
                        position += count;
                    }

                    _device!.WriteBlock(BlockAddress.FromLBA(CalculateLSN(track, startSector + s)), buffer);
                }
            }

            CommitFat(fat);

            byte[] entry = new byte[DIR_ENTRY_SIZE];
            PutName(entry, name, extension);
            entry[11] = fileType;
            entry[12] = asciiFlag;
            entry[13] = (byte)chain[0];
            entry[14] = (byte)(lastSectorBytes >> 8);
            entry[15] = (byte)(lastSectorBytes & 0xFF);
            WriteDirEntry(slot, entry);

            // The new entry went into an earlier empty slot: the old entry of the same name is deleted as Toolshed kill does.
            if (found >= 0 && found != slot)
            {
                byte[] oldEntry = ReadDirEntry(found);
                oldEntry[0] = 0x00;
                WriteDirEntry(found, oldEntry);
            }

            _device!.Flush();
        }

        private void EnsureInitialized()
        {
            if (_device == null || _fat == null)
            {
                throw new InvalidOperationException("Filesystem not initialized");
            }
        }

        // ===================================================================
        // HELPER CLASSES
        // ===================================================================

        private sealed class DecbEntry
        {
            public string Filename { get; set; } = string.Empty;
            public string Extension { get; set; } = string.Empty;
            public byte FileType { get; set; }
            public byte AsciiFlag { get; set; }
            public byte FirstGranule { get; set; }
            public ushort LastSectorBytes { get; set; }
            public int FileSize { get; set; }
            public bool IsDeleted { get; set; }
            public bool IsEndOfDirectory { get; set; }
        }
    }
}
