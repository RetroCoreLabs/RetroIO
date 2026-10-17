//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
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

        public string FileSystemType => "DECB";
        public string VolumeLabel => _volumeLabel;
        public string ExtensionSeparator => ".";  // DECB uses name.ext format

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

        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureInitialized();

            // DECB only supports root directory
            if (!string.IsNullOrEmpty(path) && path != "/")
            {
                throw new NotImplementedException("DECB does not support subdirectories");
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
            DecbEntry? entry = FindFileInDirectory(filename, extension);
            if (entry == null)
            {
                throw new System.IO.FileNotFoundException($"File not found: {path}");
            }

            // Read file data using granule chain
            return ReadFileData(entry);
        }

        public void WriteFile(string path, byte[] data)
        {
            throw new NotImplementedException("DECB write operations not yet implemented");
        }

        public void DeleteFile(string path)
        {
            throw new NotImplementedException("DECB delete operations not yet implemented");
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

        public void CreateDirectory(string path)
        {
            throw new NotSupportedException("DECB does not support subdirectories");
        }

        public void DeleteDirectory(string path)
        {
            throw new NotSupportedException("DECB does not support subdirectories");
        }

        public void Rename(string oldPath, string newPath)
        {
            throw new NotImplementedException("File rename not yet implemented for DECB");
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
            byte fileType = data[offset + 12];
            byte asciiFlag = data[offset + 13];
            byte firstGranule = data[offset + 14];
            ushort lastSectorBytes = (ushort)(data[offset + 15] | (data[offset + 16] << 8));

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

            int fileSize = ((totalSectors - 1) * SECTOR_SIZE) + (lastSectorBytes == 0 ? SECTOR_SIZE : lastSectorBytes);
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
