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
    /// Read-only implementation (write operations to be added later).
    /// </summary>
    public class DragonDosFileSystem : IBlockFileSystem
    {
        private IBlockDevice? _device;
        private string _volumeLabel = string.Empty;
        private byte[]? _bam;
        private int _sectorsPerTrack;
        private int _trackCount;
        private bool _doubleSided;

        private const int PRIMARY_DIR_TRACK = 20;
        private const int BACKUP_DIR_TRACK = 16;
        private const int DIR_ENTRY_SIZE = 25;
        private const int ENTRIES_PER_SECTOR = 10;
        private const int MAX_FILES = 160;
        private const int SECTOR_SIZE = 256;

        /// <summary>
        /// Gets the file system type identifier: <c>DragonDOS</c>.
        /// </summary>
        public string FileSystemType => "DragonDOS";
        /// <summary>
        /// Gets the volume label set by <see cref="Initialize"/>, or an empty string before it.
        /// </summary>
        /// <remarks>
        /// The label is the name stored in the BAM, <c>DRAGON DISK</c> when that name is blank, or
        /// <c>VDK DISK</c> when the BAM geometry checks fail.
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
        /// The sectors per track and track count come from the BAM when their complement bytes check out;
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
                _sectorsPerTrack = info.SectorsPerTrack;
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

            // Extract disk geometry from BAM
            _sectorsPerTrack = bamSector1[0xA2];
            byte sptCheck = bamSector1[0xA3];
            _trackCount = bamSector1[0xA4];
            byte trackCheck = bamSector1[0xA5];

            // Verify complement checks
            // Note: Some VDK images (especially game images) may not have valid DragonDOS BAM
            bool validSptComplement = (_sectorsPerTrack == (~sptCheck & 0xFF));
            bool validTrackComplement = (_trackCount == (~trackCheck & 0xFF));

            if (!validSptComplement || !validTrackComplement)
            {
                // This may not be a DragonDOS disk, try to use VDK header geometry instead
                _sectorsPerTrack = info != null && info.SectorsPerTrack > 0 ? info.SectorsPerTrack : 18;
                _trackCount = info != null && info.CylinderCount > 0 ? info.CylinderCount : 40;
                _doubleSided = (_sectorsPerTrack == 36);
                _volumeLabel = "VDK DISK";
            }
            else
            {
                _doubleSided = (_sectorsPerTrack == 36);

                // Extract volume label (offset 0x90, 18 bytes)
                _volumeLabel = Encoding.ASCII.GetString(bamSector1, 0x90, 18).Trim('\0', ' ');
                if (string.IsNullOrWhiteSpace(_volumeLabel))
                {
                    _volumeLabel = "DRAGON DISK";
                }
            }
        }

        /// <summary>
        /// Checks whether the device looks like a DragonDOS disk.
        /// </summary>
        /// <remarks>
        /// Reads the BAM sector at LBA 360 (track 20, sector 1 on a single-sided disk) and checks that it gives
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

                // Assume single-sided for detection
                uint lsn = 20 * 18; // Track 20, Sector 1 (SS)
                device.ReadBlock(BlockAddress.FromLBA(lsn), bamSector);

                // Check sectors per track (should be 18 or 36)
                byte spt = bamSector[0xA2];
                byte sptCheck = bamSector[0xA3];

                if (spt != 18 && spt != 36)
                {
                    return false;
                }

                // Verify complement
                if (spt != (~sptCheck & 0xFF))
                {
                    return false;
                }

                // Check track count (should be 40 or 80)
                byte tracks = bamSector[0xA4];
                byte trackCheck = bamSector[0xA5];

                if (tracks != 40 && tracks != 80)
                {
                    return false;
                }

                // Verify complement
                if (tracks != (~trackCheck & 0xFF))
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
        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureInitialized();

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
            DragonDosEntry? entry = FindFileInDirectory(filename, extension);
            if (entry == null)
            {
                throw new System.IO.FileNotFoundException($"File not found: {path}");
            }

            // Read file data using allocation blocks
            return ReadFileData(entry);
        }

        /// <summary>
        /// Not implemented. Always throws.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <param name="data">
        /// Not used.
        /// </param>
        /// <exception cref="NotImplementedException">
        /// Always thrown.
        /// </exception>
        public void WriteFile(string path, byte[] data)
        {
            throw new NotImplementedException("DragonDOS write operations not yet implemented");
        }

        /// <summary>
        /// Not implemented. Always throws.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <exception cref="NotImplementedException">
        /// Always thrown.
        /// </exception>
        public void DeleteFile(string path)
        {
            throw new NotImplementedException("DragonDOS delete operations not yet implemented");
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
        /// first 180 bytes of each of the two BAM sectors are counted.
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
            // per byte). _bam holds the two sectors one after the other, 256 bytes each.
            const int BamEntriesPerSector = 180;
            int freeSectors = 0;

            for (int sector = 0; sector < 2; sector++)
            {
                int start = sector * SECTOR_SIZE;
                for (int i = 0; i < BamEntriesPerSector; i++)
                {
                    freeSectors += System.Numerics.BitOperations.PopCount(_bam![start + i]);
                }
            }

            return freeSectors;
        }

        /// <summary>
        /// Not implemented. Always throws.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <exception cref="NotImplementedException">
        /// Always thrown.
        /// </exception>
        public void CreateDirectory(string path)
        {
            throw new NotImplementedException("CreateDirectory not yet implemented");
        }

        /// <summary>
        /// Not implemented. Always throws.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <exception cref="NotImplementedException">
        /// Always thrown.
        /// </exception>
        public void DeleteDirectory(string path)
        {
            throw new NotImplementedException("DeleteDirectory not yet implemented");
        }

        /// <summary>
        /// Not implemented. Always throws.
        /// </summary>
        /// <param name="oldPath">
        /// Not used.
        /// </param>
        /// <param name="newPath">
        /// Not used.
        /// </param>
        /// <exception cref="NotImplementedException">
        /// Always thrown.
        /// </exception>
        public void Rename(string oldPath, string newPath)
        {
            throw new NotImplementedException("Rename not yet implemented");
        }

        // ===================================================================
        // PRIVATE METHODS
        // ===================================================================

        private DirectoryEntry[] ListRootDirectory()
        {
            List<DirectoryEntry> entries = new List<DirectoryEntry>();

            // Read directory sectors (Track 20, Sectors 3-18)
            for (int sector = 3; sector <= 18; sector++)
            {
                uint lsn = CalculateLSN(PRIMARY_DIR_TRACK, 0, sector);
                byte[] sectorData = new byte[SECTOR_SIZE];
                _device!.ReadBlock(BlockAddress.FromLBA(lsn), sectorData);

                // Parse 10 entries per sector
                for (int entryNum = 0; entryNum < ENTRIES_PER_SECTOR; entryNum++)
                {
                    int offset = entryNum * DIR_ENTRY_SIZE;
                    DragonDosEntry? entry = ParseDirectoryEntry(sectorData, offset);

                    if (entry != null && !entry.IsDeleted && !entry.IsEmpty)
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
                            Attributes = entry.IsContinuation ? "Continuation" : "File",
                            UrnIdentifier = UrnIdentifier.Encode(fullName)
                        };

                        entries.Add(new DirectoryEntry { Name = fullName, Metadata = metadata });
                    }
                }
            }

            return entries.ToArray();
        }

        private DragonDosEntry? ParseDirectoryEntry(byte[] data, int offset)
        {
            byte flag = data[offset];

            // Check if deleted (bit 7)
            bool isDeleted = (flag & 0x80) != 0;

            // Check if all zeros (empty entry)
            bool isEmpty = true;
            for (int i = 0; i < DIR_ENTRY_SIZE; i++)
            {
                if (data[offset + i] != 0x00)
                {
                    isEmpty = false;
                    break;
                }
            }

            if (isEmpty)
            {
                return new DragonDosEntry { IsEmpty = true };
            }

            // Check if continuation block (bit 0)
            bool isContinuation = (flag & 0x01) != 0;

            if (isContinuation)
            {
                // Continuation block - skip for directory listing
                return new DragonDosEntry { IsContinuation = true, IsDeleted = isDeleted };
            }

            // Parse file header
            string filename = Encoding.ASCII.GetString(data, offset + 1, 8).Trim('\0', ' ');
            string extension = Encoding.ASCII.GetString(data, offset + 9, 3).Trim('\0', ' ');

            // Parse allocation blocks
            List<AllocationBlock> blocks = new List<AllocationBlock>();
            for (int i = 0; i < 4; i++)
            {
                int blockOffset = offset + 12 + (i * 3);
                ushort lsn = (ushort)((data[blockOffset] << 8) | data[blockOffset + 1]);
                byte count = data[blockOffset + 2];

                if (count > 0)
                {
                    blocks.Add(new AllocationBlock { LSN = lsn, Count = count });
                }
            }

            byte lastSectorBytes = data[offset + 24];

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
                Blocks = blocks.ToArray(),
                LastSectorBytes = lastSectorBytes,
                FileSize = fileSize,
                IsDeleted = isDeleted,
                IsContinuation = false,
                IsEmpty = false
            };
        }

        private DragonDosEntry? FindFileInDirectory(string filename, string extension)
        {
            for (int sector = 3; sector <= 18; sector++)
            {
                uint lsn = CalculateLSN(PRIMARY_DIR_TRACK, 0, sector);
                byte[] sectorData = new byte[SECTOR_SIZE];
                _device!.ReadBlock(BlockAddress.FromLBA(lsn), sectorData);

                for (int entryNum = 0; entryNum < ENTRIES_PER_SECTOR; entryNum++)
                {
                    int offset = entryNum * DIR_ENTRY_SIZE;
                    DragonDosEntry? entry = ParseDirectoryEntry(sectorData, offset);

                    if (entry != null && !entry.IsDeleted && !entry.IsEmpty && !entry.IsContinuation)
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

        private byte[] ReadFileData(DragonDosEntry entry)
        {
            List<byte> fileData = new List<byte>(entry.FileSize);

            // Read all allocation blocks
            for (int i = 0; i < entry.Blocks.Length; i++)
            {
                AllocationBlock block = entry.Blocks[i];

                for (int s = 0; s < block.Count; s++)
                {
                    byte[] sectorData = new byte[SECTOR_SIZE];
                    uint lsn = (uint)(block.LSN + s);
                    _device!.ReadBlock(BlockAddress.FromLBA(lsn), sectorData);

                    // For last sector, only add used bytes (0 means full sector = 256 bytes)
                    bool isLastSector = (i == entry.Blocks.Length - 1) && (s == block.Count - 1);
                    if (isLastSector && entry.LastSectorBytes > 0)
                    {
                        for (int j = 0; j < entry.LastSectorBytes; j++)
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
            }

            return fileData.ToArray();
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
            public AllocationBlock[] Blocks { get; set; } = Array.Empty<AllocationBlock>();
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
