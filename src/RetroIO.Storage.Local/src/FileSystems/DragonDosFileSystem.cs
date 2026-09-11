//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
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

        public string FileSystemType => "DragonDOS";
        public string VolumeLabel => _volumeLabel;
        public string ExtensionSeparator => ".";  // DragonDOS uses name.ext format

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

        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureInitialized();

            // DragonDOS only supports root directory
            if (!string.IsNullOrEmpty(path) && path != "/")
            {
                throw new NotImplementedException("DragonDOS does not support subdirectories");
            }

            return ListRootDirectory();
        }

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

        public void WriteFile(string path, byte[] data)
        {
            throw new NotImplementedException("DragonDOS write operations not yet implemented");
        }

        public void DeleteFile(string path)
        {
            throw new NotImplementedException("DragonDOS delete operations not yet implemented");
        }

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

        public int GetBlocksFree()
        {
            EnsureInitialized();

            int freeSectors = 0;

            // Count free sectors in BAM (0xFF = free)
            int totalSectors = _trackCount * _sectorsPerTrack;
            int bamBytes = Math.Min(totalSectors, _bam!.Length);

            for (int i = 0; i < bamBytes; i++)
            {
                if (_bam[i] == 0xFF)
                {
                    freeSectors++;
                }
            }

            return freeSectors;
        }

        public void CreateDirectory(string path)
        {
            throw new NotImplementedException("CreateDirectory not yet implemented");
        }

        public void DeleteDirectory(string path)
        {
            throw new NotImplementedException("DeleteDirectory not yet implemented");
        }

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
