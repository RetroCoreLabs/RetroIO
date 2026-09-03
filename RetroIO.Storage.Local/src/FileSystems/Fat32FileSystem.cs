//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.FileSystems
{
    /// <summary>
    /// FAT32 filesystem implementation for hard disks and large volumes.
    /// Supports volumes up to 2TB. Full read/write, including arbitrary-depth subdirectories.
    /// Unlike FAT12/16, the root directory is itself just a regular cluster chain — no
    /// fixed-size special case is needed here.
    /// </summary>
    public class Fat32FileSystem : IBlockFileSystem
    {
        private const int DirEntrySize = 32;
        private const int EndOfDirectoryMarker = 0x00;
        private const int DeletedEntryMarker = 0xE5;
        private const int VolumeLabelAttribute = 0x08;
        private const int DirectoryAttribute = 0x10;
        private const int ArchiveAttribute = 0x20;
        private const int LfnAttributeMask = 0x0F;
        private const uint Fat32EndOfChain = 0x0FFFFFFF;
        private const uint Fat32FirstDataCluster = 2;
        private const uint Fat32ReservedThreshold = 0x0FFFFFF8; // clusters >= this are EOC/reserved/bad
        private const uint Fat32EntryMask = 0x0FFFFFFF; // only the low 28 bits are meaningful

        private IBlockDevice? _device;
        private string _volumeLabel = string.Empty;
        private Fat32BootSector? _bootSector;
        private byte[]? _fatTable;
        private uint _dataStart;
        private uint _rootDirCluster;

        public string FileSystemType => "FAT32";
        public string VolumeLabel => _volumeLabel;
        public string ExtensionSeparator => ".";  // FAT uses name.ext format

        public void Initialize(IBlockDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));

            byte[] bootSectorData = new byte[512];
            device.ReadBlock(BlockAddress.FromLBA(0), bootSectorData);

            _bootSector = ParseBootSector(bootSectorData);

            ReadFatTable();

            _dataStart = _bootSector.ReservedSectors + (_bootSector.NumberOfFATs * _bootSector.SectorsPerFAT32);
            _rootDirCluster = _bootSector.RootCluster;
        }

        public bool CanHandle(IBlockDevice device)
        {
            try
            {
                byte[] bootSector = new byte[512];
                device.ReadBlock(BlockAddress.FromLBA(0), bootSector);

                if (bootSector[510] != 0x55 || bootSector[511] != 0xAA)
                {
                    return false;
                }

                string fsType = Encoding.ASCII.GetString(bootSector, 0x52, 8).Trim();
                if (fsType.StartsWith("FAT32"))
                {
                    return true;
                }

                ushort bytesPerSector = (ushort)(bootSector[11] | (bootSector[12] << 8));
                byte sectorsPerCluster = bootSector[13];
                ushort reservedSectors = (ushort)(bootSector[14] | (bootSector[15] << 8));
                byte numberOfFATs = bootSector[16];
                ushort rootEntryCount = (ushort)(bootSector[17] | (bootSector[18] << 8));
                uint totalSectors32 = (uint)(bootSector[32] | (bootSector[33] << 8) | (bootSector[34] << 16) | (bootSector[35] << 24));
                uint sectorsPerFAT32 = (uint)(bootSector[36] | (bootSector[37] << 8) | (bootSector[38] << 16) | (bootSector[39] << 24));

                if (rootEntryCount != 0)
                {
                    return false;
                }

                if (sectorsPerFAT32 == 0)
                {
                    return false;
                }

                uint dataSectors = totalSectors32 - (reservedSectors + (numberOfFATs * sectorsPerFAT32));
                uint clusterCount = dataSectors / sectorsPerCluster;

                return clusterCount >= 65525;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Formats a blank FAT32 filesystem onto <paramref name="device"/> — see
        /// <see cref="Fat12FileSystem.Format"/> for the shared design notes. Unlike FAT12/16,
        /// FAT32 has no fixed root directory area: root cluster 2 is preallocated and marked
        /// end-of-chain in both FAT copies, exactly like any other directory (this codebase does
        /// not write an FSInfo sector — it's an optional performance hint per the FAT32 spec, not
        /// required for a volume to be valid, and this reader never consults one).
        /// </summary>
        public static void Format(IBlockDevice device, uint totalSectors, byte sectorsPerCluster = 8,
            uint reservedSectors = 32, byte numberOfFats = 2, byte mediaDescriptor = 0xF8, string volumeLabel = "")
        {
            if (device == null)
            {
                throw new ArgumentNullException(nameof(device));
            }

            const int bytesPerSector = 512;
            const uint rootCluster = 2;
            uint sectorsPerFat32 = ComputeSectorsPerFat32(totalSectors, sectorsPerCluster, reservedSectors, numberOfFats, bytesPerSector);

            byte[] boot = new byte[bytesPerSector];
            boot[0] = 0xEB; boot[1] = 0x3C; boot[2] = 0x90;
            WriteAsciiField(boot, 3, "RETROFS ", 8);
            boot[11] = (byte)(bytesPerSector & 0xFF);
            boot[12] = (byte)((bytesPerSector >> 8) & 0xFF);
            boot[13] = sectorsPerCluster;
            boot[14] = (byte)(reservedSectors & 0xFF);
            boot[15] = (byte)((reservedSectors >> 8) & 0xFF);
            boot[16] = numberOfFats;
            // RootEntryCount (17-18) and TotalSectors16 (19-20) stay 0 — FAT32 always uses the 32-bit fields.
            boot[21] = mediaDescriptor;
            // SectorsPerFAT16 (22-23) stays 0 — FAT32 uses SectorsPerFAT32 at offset 36 instead.
            boot[32] = (byte)(totalSectors & 0xFF);
            boot[33] = (byte)((totalSectors >> 8) & 0xFF);
            boot[34] = (byte)((totalSectors >> 16) & 0xFF);
            boot[35] = (byte)((totalSectors >> 24) & 0xFF);
            boot[36] = (byte)(sectorsPerFat32 & 0xFF);
            boot[37] = (byte)((sectorsPerFat32 >> 8) & 0xFF);
            boot[38] = (byte)((sectorsPerFat32 >> 16) & 0xFF);
            boot[39] = (byte)((sectorsPerFat32 >> 24) & 0xFF);
            boot[44] = (byte)(rootCluster & 0xFF);
            boot[45] = (byte)((rootCluster >> 8) & 0xFF);
            boot[46] = (byte)((rootCluster >> 16) & 0xFF);
            boot[47] = (byte)((rootCluster >> 24) & 0xFF);
            WriteAsciiField(boot, 0x52, "FAT32   ", 8);
            boot[510] = 0x55;
            boot[511] = 0xAA;

            device.WriteBlock(BlockAddress.FromLBA(0), boot);

            byte[] zero = new byte[bytesPerSector];
            uint fatStart = reservedSectors;
            for (int copy = 0; copy < numberOfFats; copy++)
            {
                uint copyStart = fatStart + (uint)copy * sectorsPerFat32;
                for (uint i = 0; i < sectorsPerFat32; i++)
                {
                    device.WriteBlock(BlockAddress.FromLBA(copyStart + i), zero);
                }

                // Cluster 0/1 reserved header entries, plus the root directory's own cluster
                // (2) preallocated and marked end-of-chain — the root must exist as a real,
                // allocated cluster from the moment the volume is formatted.
                byte[] fatHeader = new byte[bytesPerSector];
                fatHeader[0] = mediaDescriptor;
                fatHeader[1] = 0xFF; fatHeader[2] = 0xFF; fatHeader[3] = 0x0F;
                fatHeader[4] = 0xFF; fatHeader[5] = 0xFF; fatHeader[6] = 0xFF; fatHeader[7] = 0x0F;
                fatHeader[8] = 0xFF; fatHeader[9] = 0xFF; fatHeader[10] = 0xFF; fatHeader[11] = 0x0F;
                device.WriteBlock(BlockAddress.FromLBA(copyStart), fatHeader);
            }

            uint dataStart = fatStart + (uint)numberOfFats * sectorsPerFat32;
            uint rootLba = dataStart; // cluster 2 is always the first data cluster
            for (int s = 0; s < sectorsPerCluster; s++)
            {
                device.WriteBlock(BlockAddress.FromLBA(rootLba + (uint)s), zero);
            }

            if (!string.IsNullOrEmpty(volumeLabel))
            {
                byte[] rootFirstSector = new byte[bytesPerSector];
                WriteAsciiField(rootFirstSector, 0, volumeLabel.ToUpperInvariant(), 11);
                rootFirstSector[11] = VolumeLabelAttribute;
                device.WriteBlock(BlockAddress.FromLBA(rootLba), rootFirstSector);
            }
        }

        private static uint ComputeSectorsPerFat32(uint totalSectors, byte sectorsPerCluster,
            uint reservedSectors, byte numberOfFats, int bytesPerSector)
        {
            uint sectorsPerFat = 1;
            for (int iteration = 0; iteration < 10; iteration++)
            {
                uint dataStart = reservedSectors + (uint)numberOfFats * sectorsPerFat;
                uint dataSectors = totalSectors > dataStart ? totalSectors - dataStart : 0;
                uint clusterCount = dataSectors / sectorsPerCluster;
                double fatBytesNeeded = (clusterCount + 2) * 4.0 + 16; // +16 padding for rounding
                uint needed = (uint)Math.Max(1, Math.Ceiling(fatBytesNeeded / bytesPerSector));

                if (needed == sectorsPerFat)
                {
                    break;
                }

                sectorsPerFat = needed;
            }

            return sectorsPerFat;
        }

        private static void WriteAsciiField(byte[] buffer, int offset, string text, int fieldLength)
        {
            for (int i = 0; i < fieldLength; i++)
            {
                buffer[offset + i] = i < text.Length ? (byte)text[i] : (byte)' ';
            }
        }

        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureInitialized();
            uint cluster = ResolveDirectory(path);
            return ListDirectoryCluster(cluster);
        }

        public byte[] ReadFile(string path)
        {
            EnsureInitialized();
            RawEntry raw = FindEntryByPath(path) ?? throw new FileNotFoundException($"File not found: {path}");
            if (raw.Entry.IsDirectory)
            {
                throw new FileNotFoundException($"Path is a directory, not a file: {path}");
            }

            return ReadFileData(raw.Entry.StartCluster, raw.Entry.FileSize);
        }

        public void WriteFile(string path, byte[] data)
        {
            EnsureInitialized();
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            (uint parentCluster, string name) = ResolveParentAndName(path);
            ValidateShortName(name);

            RawEntry? existing = FindEntryInDirectory(parentCluster, name);
            if (existing.HasValue && existing.Value.Entry.IsDirectory)
            {
                throw new IOException($"Cannot overwrite directory '{name}' with a file");
            }

            if (existing.HasValue)
            {
                if (existing.Value.Entry.StartCluster >= Fat32FirstDataCluster)
                {
                    FreeChain(existing.Value.Entry.StartCluster);
                }

                uint newStartCluster = data.Length > 0 ? AllocateAndWriteChain(data) : 0;
                WriteEntryFields(existing.Value.Lba, existing.Value.Offset, newStartCluster, (uint)data.Length, ArchiveAttribute);
                return;
            }

            uint startCluster = data.Length > 0 ? AllocateAndWriteChain(data) : 0;
            EntrySlot slot = FindFreeSlot(parentCluster);
            WriteNewEntry(slot, name, ArchiveAttribute, startCluster, (uint)data.Length);
        }

        public void DeleteFile(string path)
        {
            EnsureInitialized();
            (uint parentCluster, string name) = ResolveParentAndName(path);
            RawEntry raw = FindEntryInDirectory(parentCluster, name) ?? throw new FileNotFoundException($"File not found: {path}");

            if (raw.Entry.IsDirectory)
            {
                throw new IOException($"'{path}' is a directory — use DeleteDirectory instead");
            }

            if (raw.Entry.StartCluster >= Fat32FirstDataCluster)
            {
                FreeChain(raw.Entry.StartCluster);
            }

            MarkSlotDeleted(raw.Lba, raw.Offset);
        }

        public FileMetadata? GetMetadata(string path)
        {
            EnsureInitialized();
            RawEntry? raw = FindEntryByPath(path);
            return raw == null ? null : ToMetadata(raw.Value.Entry);
        }

        public int GetBlocksFree()
        {
            EnsureInitialized();

            int freeClusters = 0;
            uint totalClusters = (_bootSector!.TotalSectors - _dataStart) / _bootSector.SectorsPerCluster;

            for (uint cluster = Fat32FirstDataCluster; cluster < totalClusters + Fat32FirstDataCluster; cluster++)
            {
                if (ReadFatEntry(cluster) == 0)
                {
                    freeClusters++;
                }
            }

            return freeClusters * _bootSector.SectorsPerCluster;
        }

        public void CreateDirectory(string path)
        {
            EnsureInitialized();
            (uint parentCluster, string name) = ResolveParentAndName(path);
            ValidateShortName(name);

            if (FindEntryInDirectory(parentCluster, name).HasValue)
            {
                throw new IOException($"'{name}' already exists");
            }

            uint newCluster = AllocateSingleZeroedCluster();
            WriteDotEntries(newCluster, parentCluster);

            EntrySlot slot = FindFreeSlot(parentCluster);
            WriteNewEntry(slot, name, DirectoryAttribute, newCluster, 0);
        }

        public void DeleteDirectory(string path)
        {
            EnsureInitialized();
            (uint parentCluster, string name) = ResolveParentAndName(path);
            RawEntry raw = FindEntryInDirectory(parentCluster, name) ?? throw new DirectoryNotFoundException($"Directory not found: {path}");

            if (!raw.Entry.IsDirectory)
            {
                throw new IOException($"'{path}' is a file — use DeleteFile instead");
            }

            if (ListDirectoryCluster(raw.Entry.StartCluster).Length > 0)
            {
                throw new IOException($"Directory '{path}' is not empty");
            }

            FreeChain(raw.Entry.StartCluster);
            MarkSlotDeleted(raw.Lba, raw.Offset);
        }

        public void Rename(string oldPath, string newPath)
        {
            EnsureInitialized();
            (uint oldParentCluster, string oldName) = ResolveParentAndName(oldPath);
            (uint newParentCluster, string newName) = ResolveParentAndName(newPath);
            ValidateShortName(newName);

            RawEntry raw = FindEntryInDirectory(oldParentCluster, oldName) ?? throw new FileNotFoundException($"Path not found: {oldPath}");

            if (FindEntryInDirectory(newParentCluster, newName).HasValue)
            {
                throw new IOException($"'{newPath}' already exists");
            }

            if (oldParentCluster == newParentCluster)
            {
                WriteEntryName(raw.Lba, raw.Offset, newName);
                return;
            }

            MarkSlotDeleted(raw.Lba, raw.Offset);
            EntrySlot slot = FindFreeSlot(newParentCluster);
            WriteNewEntry(slot, newName, raw.Entry.Attributes, raw.Entry.StartCluster, raw.Entry.FileSize);
        }

        // ===================================================================
        // PATH RESOLUTION
        // ===================================================================

        private uint ResolveDirectory(string path)
        {
            string[] segments = SplitPath(path);
            uint cluster = _rootDirCluster;

            for (int i = 0; i < segments.Length; i++)
            {
                RawEntry entry = FindEntryInDirectory(cluster, segments[i])
                    ?? throw new DirectoryNotFoundException($"Directory not found: {segments[i]}");

                if (!entry.Entry.IsDirectory)
                {
                    throw new DirectoryNotFoundException($"'{segments[i]}' is not a directory");
                }

                cluster = entry.Entry.StartCluster;
            }

            return cluster;
        }

        private (uint ParentCluster, string Name) ResolveParentAndName(string path)
        {
            string[] segments = SplitPath(path);
            if (segments.Length == 0)
            {
                throw new ArgumentException("Path must reference a file or directory name, not root", nameof(path));
            }

            uint cluster = _rootDirCluster;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                RawEntry entry = FindEntryInDirectory(cluster, segments[i])
                    ?? throw new DirectoryNotFoundException($"Directory not found: {segments[i]}");

                if (!entry.Entry.IsDirectory)
                {
                    throw new DirectoryNotFoundException($"'{segments[i]}' is not a directory");
                }

                cluster = entry.Entry.StartCluster;
            }

            return (cluster, segments[segments.Length - 1]);
        }

        private RawEntry? FindEntryByPath(string path)
        {
            (uint parentCluster, string name) = ResolveParentAndName(path);
            return FindEntryInDirectory(parentCluster, name);
        }

        private static string[] SplitPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
            {
                return Array.Empty<string>();
            }

            string trimmed = path.Trim('/');
            return trimmed.Length == 0 ? Array.Empty<string>() : trimmed.Split('/');
        }

        // ===================================================================
        // BOOT SECTOR / FAT TABLE
        // ===================================================================

        private Fat32BootSector ParseBootSector(byte[] data)
        {
            Fat32BootSector boot = new Fat32BootSector
            {
                BytesPerSector = (ushort)(data[11] | (data[12] << 8)),
                SectorsPerCluster = data[13],
                ReservedSectors = (ushort)(data[14] | (data[15] << 8)),
                NumberOfFATs = data[16],
                RootEntryCount = (ushort)(data[17] | (data[18] << 8)),
                TotalSectors16 = (ushort)(data[19] | (data[20] << 8)),
                MediaDescriptor = data[21],
                SectorsPerFAT16 = (ushort)(data[22] | (data[23] << 8)),
                TotalSectors = (uint)(data[32] | (data[33] << 8) | (data[34] << 16) | (data[35] << 24)),
                SectorsPerFAT32 = (uint)(data[36] | (data[37] << 8) | (data[38] << 16) | (data[39] << 24)),
                RootCluster = (uint)(data[44] | (data[45] << 8) | (data[46] << 16) | (data[47] << 24))
            };

            if (boot.BytesPerSector != 512 && boot.BytesPerSector != 1024 && boot.BytesPerSector != 2048 && boot.BytesPerSector != 4096)
            {
                throw new InvalidDataException($"Invalid bytes per sector: {boot.BytesPerSector}");
            }

            if (boot.SectorsPerCluster == 0 || boot.SectorsPerCluster > 128)
            {
                throw new InvalidDataException($"Invalid sectors per cluster: {boot.SectorsPerCluster}");
            }

            if (boot.NumberOfFATs == 0 || boot.NumberOfFATs > 2)
            {
                throw new InvalidDataException($"Invalid number of FATs: {boot.NumberOfFATs}");
            }

            if (boot.SectorsPerFAT32 == 0)
            {
                throw new InvalidDataException("Invalid sectors per FAT: 0");
            }

            if (boot.RootCluster < 2)
            {
                throw new InvalidDataException($"Invalid root cluster: {boot.RootCluster}");
            }

            return boot;
        }

        private void ReadFatTable()
        {
            uint fatSizeBytes = _bootSector!.SectorsPerFAT32 * _bootSector.BytesPerSector;
            _fatTable = new byte[fatSizeBytes];

            uint fatStart = _bootSector.ReservedSectors;

            for (int i = 0; i < _bootSector.SectorsPerFAT32; i++)
            {
                byte[] sectorData = new byte[_bootSector.BytesPerSector];
                _device!.ReadBlock(BlockAddress.FromLBA(fatStart + (uint)i), sectorData);
                Array.Copy(sectorData, 0, _fatTable, i * _bootSector.BytesPerSector, _bootSector.BytesPerSector);
            }
        }

        private void FlushFatTable()
        {
            uint fatStart = _bootSector!.ReservedSectors;
            WriteFatCopy(fatStart);
            if (_bootSector.NumberOfFATs > 1)
            {
                WriteFatCopy(fatStart + _bootSector.SectorsPerFAT32);
            }
        }

        private void WriteFatCopy(uint startSector)
        {
            for (int i = 0; i < _bootSector!.SectorsPerFAT32; i++)
            {
                byte[] sectorData = new byte[_bootSector.BytesPerSector];
                Array.Copy(_fatTable!, i * _bootSector.BytesPerSector, sectorData, 0, _bootSector.BytesPerSector);
                _device!.WriteBlock(BlockAddress.FromLBA(startSector + (uint)i), sectorData);
            }
        }

        private uint ReadFatEntry(uint cluster)
        {
            // FAT32: 4 bytes per entry (but only 28 bits used)
            long offset = cluster * 4;
            if (offset >= _fatTable!.Length - 3)
            {
                return Fat32EndOfChain;
            }

            uint entry = (uint)(_fatTable[offset] | (_fatTable[offset + 1] << 8) |
                               (_fatTable[offset + 2] << 16) | (_fatTable[offset + 3] << 24));

            return entry & Fat32EntryMask;
        }

        /// <summary>
        /// Writes a FAT32 entry. Only the low 28 bits are meaningful — the top 4 bits are
        /// reserved by the spec and must be preserved exactly as read, never zeroed.
        /// </summary>
        private void WriteFatEntry(uint cluster, uint value)
        {
            long offset = cluster * 4;
            uint existing = (uint)(_fatTable![offset] | (_fatTable[offset + 1] << 8) |
                                   (_fatTable[offset + 2] << 16) | (_fatTable[offset + 3] << 24));
            uint reservedTopBits = existing & ~Fat32EntryMask;
            uint newValue = reservedTopBits | (value & Fat32EntryMask);

            _fatTable[offset] = (byte)(newValue & 0xFF);
            _fatTable[offset + 1] = (byte)((newValue >> 8) & 0xFF);
            _fatTable[offset + 2] = (byte)((newValue >> 16) & 0xFF);
            _fatTable[offset + 3] = (byte)((newValue >> 24) & 0xFF);

            FlushFatTable();
        }

        private uint FindFreeCluster()
        {
            uint totalClusters = (_bootSector!.TotalSectors - _dataStart) / _bootSector.SectorsPerCluster;
            for (uint cluster = Fat32FirstDataCluster; cluster < totalClusters + Fat32FirstDataCluster; cluster++)
            {
                if (ReadFatEntry(cluster) == 0)
                {
                    return cluster;
                }
            }

            throw new IOException("No free space left on disk");
        }

        private void FreeChain(uint startCluster)
        {
            uint cluster = startCluster;
            while (cluster >= Fat32FirstDataCluster && cluster < Fat32ReservedThreshold)
            {
                uint next = ReadFatEntry(cluster);
                WriteFatEntry(cluster, 0);
                cluster = next;
            }
        }

        // ===================================================================
        // CLUSTER ALLOCATION AND FILE DATA
        // ===================================================================

        private uint AllocateAndWriteChain(byte[] data)
        {
            int clusterSizeBytes = _bootSector!.SectorsPerCluster * _bootSector.BytesPerSector;
            int clustersNeeded = (data.Length + clusterSizeBytes - 1) / clusterSizeBytes;

            uint firstCluster = 0;
            uint previousCluster = 0;
            int bytesWritten = 0;

            for (int c = 0; c < clustersNeeded; c++)
            {
                uint cluster = FindFreeCluster();
                WriteFatEntry(cluster, Fat32EndOfChain);

                if (c == 0)
                {
                    firstCluster = cluster;
                }
                else
                {
                    WriteFatEntry(previousCluster, cluster);
                }

                uint lba = ClusterToLBA(cluster);
                for (int s = 0; s < _bootSector.SectorsPerCluster; s++)
                {
                    byte[] sectorData = new byte[_bootSector.BytesPerSector];
                    int remaining = data.Length - bytesWritten;
                    if (remaining > 0)
                    {
                        int toCopy = Math.Min(remaining, _bootSector.BytesPerSector);
                        Array.Copy(data, bytesWritten, sectorData, 0, toCopy);
                        bytesWritten += toCopy;
                    }

                    _device!.WriteBlock(BlockAddress.FromLBA(lba + (uint)s), sectorData);
                }

                previousCluster = cluster;
            }

            return firstCluster;
        }

        private uint AllocateSingleZeroedCluster()
        {
            uint cluster = FindFreeCluster();
            WriteFatEntry(cluster, Fat32EndOfChain);

            uint lba = ClusterToLBA(cluster);
            byte[] zeroSector = new byte[_bootSector!.BytesPerSector];
            for (int s = 0; s < _bootSector.SectorsPerCluster; s++)
            {
                _device!.WriteBlock(BlockAddress.FromLBA(lba + (uint)s), zeroSector);
            }

            return cluster;
        }

        private byte[] ReadFileData(uint startCluster, uint fileSize)
        {
            byte[] fileData = new byte[fileSize];
            uint cluster = startCluster;
            uint bytesRemaining = fileSize;
            int written = 0;

            while (cluster >= Fat32FirstDataCluster && cluster < Fat32ReservedThreshold && bytesRemaining > 0)
            {
                uint lba = ClusterToLBA(cluster);

                for (int i = 0; i < _bootSector!.SectorsPerCluster && bytesRemaining > 0; i++)
                {
                    byte[] sectorData = new byte[_bootSector.BytesPerSector];
                    _device!.ReadBlock(BlockAddress.FromLBA(lba + (uint)i), sectorData);

                    uint bytesToCopy = Math.Min(bytesRemaining, (uint)_bootSector.BytesPerSector);
                    Array.Copy(sectorData, 0, fileData, written, (int)bytesToCopy);
                    written += (int)bytesToCopy;
                    bytesRemaining -= bytesToCopy;
                }

                cluster = ReadFatEntry(cluster);
            }

            return fileData;
        }

        private uint ClusterToLBA(uint cluster)
        {
            return _dataStart + ((cluster - Fat32FirstDataCluster) * _bootSector!.SectorsPerCluster);
        }

        // ===================================================================
        // DIRECTORY SCANNING (root is just a regular cluster chain in FAT32)
        // ===================================================================

        private uint[] GetDirLbas(uint startCluster)
        {
            List<uint> result = new List<uint>();
            uint cluster = startCluster;
            while (cluster >= Fat32FirstDataCluster && cluster < Fat32ReservedThreshold)
            {
                uint lba = ClusterToLBA(cluster);
                for (int s = 0; s < _bootSector!.SectorsPerCluster; s++)
                {
                    result.Add(lba + (uint)s);
                }

                cluster = ReadFatEntry(cluster);
            }

            return result.ToArray();
        }

        private DirectoryEntry[] ListDirectoryCluster(uint startCluster)
        {
            List<DirectoryEntry> entries = new List<DirectoryEntry>();
            uint[] lbas = GetDirLbas(startCluster);

            for (int s = 0; s < lbas.Length; s++)
            {
                byte[] sectorData = new byte[_bootSector!.BytesPerSector];
                _device!.ReadBlock(BlockAddress.FromLBA(lbas[s]), sectorData);

                int entriesPerSector = _bootSector.BytesPerSector / DirEntrySize;
                for (int j = 0; j < entriesPerSector; j++)
                {
                    int offset = j * DirEntrySize;

                    byte firstByte = sectorData[offset];
                    if (firstByte == EndOfDirectoryMarker)
                    {
                        return entries.ToArray();
                    }

                    if (firstByte == DeletedEntryMarker)
                    {
                        continue;
                    }

                    byte attributes = sectorData[offset + 11];
                    if ((attributes & VolumeLabelAttribute) != 0 || (attributes & LfnAttributeMask) == LfnAttributeMask)
                    {
                        continue;
                    }

                    FatDirectoryEntry entry = ParseDirectoryEntry(sectorData, offset);
                    if (entry.FileName == "." || entry.FileName == "..")
                    {
                        continue;
                    }

                    entries.Add(new DirectoryEntry { Name = entry.FileName, Metadata = ToMetadata(entry) });
                }
            }

            return entries.ToArray();
        }

        private RawEntry? FindEntryInDirectory(uint startCluster, string fileName)
        {
            uint[] lbas = GetDirLbas(startCluster);

            for (int s = 0; s < lbas.Length; s++)
            {
                byte[] sectorData = new byte[_bootSector!.BytesPerSector];
                _device!.ReadBlock(BlockAddress.FromLBA(lbas[s]), sectorData);

                int entriesPerSector = _bootSector.BytesPerSector / DirEntrySize;
                for (int j = 0; j < entriesPerSector; j++)
                {
                    int offset = j * DirEntrySize;

                    byte firstByte = sectorData[offset];
                    if (firstByte == EndOfDirectoryMarker)
                    {
                        return null;
                    }

                    if (firstByte == DeletedEntryMarker)
                    {
                        continue;
                    }

                    byte attributes = sectorData[offset + 11];
                    if ((attributes & VolumeLabelAttribute) != 0 || (attributes & LfnAttributeMask) == LfnAttributeMask)
                    {
                        continue;
                    }

                    FatDirectoryEntry entry = ParseDirectoryEntry(sectorData, offset);
                    if (string.Equals(entry.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return new RawEntry { Lba = lbas[s], Offset = offset, Entry = entry };
                    }
                }
            }

            return null;
        }

        private EntrySlot FindFreeSlot(uint dirStartCluster)
        {
            uint[] lbas = GetDirLbas(dirStartCluster);

            for (int s = 0; s < lbas.Length; s++)
            {
                byte[] sectorData = new byte[_bootSector!.BytesPerSector];
                _device!.ReadBlock(BlockAddress.FromLBA(lbas[s]), sectorData);

                for (int i = 0; i < _bootSector.BytesPerSector; i += DirEntrySize)
                {
                    if (sectorData[i] == EndOfDirectoryMarker || sectorData[i] == DeletedEntryMarker)
                    {
                        return new EntrySlot { Lba = lbas[s], Offset = i };
                    }
                }
            }

            // FAT32 directories (including root) are always regular cluster chains, so they can
            // always grow — unlike FAT12/16's fixed-size root.
            uint lastCluster = FindLastCluster(dirStartCluster);
            uint newCluster = FindFreeCluster();
            WriteFatEntry(lastCluster, newCluster);
            WriteFatEntry(newCluster, Fat32EndOfChain);

            uint newLba = ClusterToLBA(newCluster);
            byte[] zeroSector = new byte[_bootSector!.BytesPerSector];
            for (int s = 0; s < _bootSector.SectorsPerCluster; s++)
            {
                _device!.WriteBlock(BlockAddress.FromLBA(newLba + (uint)s), zeroSector);
            }

            return new EntrySlot { Lba = newLba, Offset = 0 };
        }

        private uint FindLastCluster(uint startCluster)
        {
            uint cluster = startCluster;
            uint next = ReadFatEntry(cluster);
            while (next >= Fat32FirstDataCluster && next < Fat32ReservedThreshold)
            {
                cluster = next;
                next = ReadFatEntry(cluster);
            }

            return cluster;
        }

        private void WriteDotEntries(uint newCluster, uint parentCluster)
        {
            uint lba = ClusterToLBA(newCluster);
            byte[] sector = new byte[_bootSector!.BytesPerSector];

            WriteRawEntryBytes(sector, 0, ".", DirectoryAttribute, newCluster, 0);
            // FAT32's convention: ".." pointing at the root uses cluster 0, not the real root cluster.
            uint dotDotCluster = parentCluster == _rootDirCluster ? 0u : parentCluster;
            WriteRawEntryBytes(sector, DirEntrySize, "..", DirectoryAttribute, dotDotCluster, 0);

            _device!.WriteBlock(BlockAddress.FromLBA(lba), sector);
        }

        // ===================================================================
        // DIRECTORY ENTRY BYTE LAYOUT
        // ===================================================================

        private FatDirectoryEntry ParseDirectoryEntry(byte[] data, int offset)
        {
            string name = Encoding.ASCII.GetString(data, offset, 8).TrimEnd();
            string ext = Encoding.ASCII.GetString(data, offset + 8, 3).TrimEnd();
            string fileName = string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";

            byte attributes = data[offset + 11];
            ushort startClusterHigh = (ushort)(data[offset + 20] | (data[offset + 21] << 8));
            ushort startClusterLow = (ushort)(data[offset + 26] | (data[offset + 27] << 8));
            uint startCluster = (uint)((startClusterHigh << 16) | startClusterLow);
            uint fileSize = (uint)(data[offset + 28] | (data[offset + 29] << 8) | (data[offset + 30] << 16) | (data[offset + 31] << 24));

            return new FatDirectoryEntry
            {
                FileName = fileName,
                Attributes = attributes,
                StartCluster = startCluster,
                FileSize = fileSize,
                IsDirectory = (attributes & DirectoryAttribute) != 0
            };
        }

        private void WriteNewEntry(EntrySlot slot, string name, byte attributes, uint startCluster, uint fileSize)
        {
            byte[] sector = new byte[_bootSector!.BytesPerSector];
            _device!.ReadBlock(BlockAddress.FromLBA(slot.Lba), sector);
            WriteRawEntryBytes(sector, slot.Offset, name, attributes, startCluster, fileSize);
            _device.WriteBlock(BlockAddress.FromLBA(slot.Lba), sector);
        }

        private void WriteEntryFields(uint lba, int offset, uint startCluster, uint fileSize, byte attributes)
        {
            byte[] sector = new byte[_bootSector!.BytesPerSector];
            _device!.ReadBlock(BlockAddress.FromLBA(lba), sector);

            sector[offset + 11] = attributes;
            sector[offset + 20] = (byte)((startCluster >> 16) & 0xFF);
            sector[offset + 21] = (byte)((startCluster >> 24) & 0xFF);
            sector[offset + 26] = (byte)(startCluster & 0xFF);
            sector[offset + 27] = (byte)((startCluster >> 8) & 0xFF);
            sector[offset + 28] = (byte)(fileSize & 0xFF);
            sector[offset + 29] = (byte)((fileSize >> 8) & 0xFF);
            sector[offset + 30] = (byte)((fileSize >> 16) & 0xFF);
            sector[offset + 31] = (byte)((fileSize >> 24) & 0xFF);

            _device.WriteBlock(BlockAddress.FromLBA(lba), sector);
        }

        private void WriteEntryName(uint lba, int offset, string name)
        {
            byte[] sector = new byte[_bootSector!.BytesPerSector];
            _device!.ReadBlock(BlockAddress.FromLBA(lba), sector);
            WriteNameBytes(sector, offset, name);
            _device.WriteBlock(BlockAddress.FromLBA(lba), sector);
        }

        private void MarkSlotDeleted(uint lba, int offset)
        {
            byte[] sector = new byte[_bootSector!.BytesPerSector];
            _device!.ReadBlock(BlockAddress.FromLBA(lba), sector);
            sector[offset] = DeletedEntryMarker;
            _device.WriteBlock(BlockAddress.FromLBA(lba), sector);
        }

        private static void WriteRawEntryBytes(byte[] sector, int offset, string name, byte attributes, uint startCluster, uint fileSize)
        {
            WriteNameBytes(sector, offset, name);
            sector[offset + 11] = attributes;
            sector[offset + 20] = (byte)((startCluster >> 16) & 0xFF);
            sector[offset + 21] = (byte)((startCluster >> 24) & 0xFF);
            sector[offset + 26] = (byte)(startCluster & 0xFF);
            sector[offset + 27] = (byte)((startCluster >> 8) & 0xFF);
            sector[offset + 28] = (byte)(fileSize & 0xFF);
            sector[offset + 29] = (byte)((fileSize >> 8) & 0xFF);
            sector[offset + 30] = (byte)((fileSize >> 16) & 0xFF);
            sector[offset + 31] = (byte)((fileSize >> 24) & 0xFF);
        }

        private static void WriteNameBytes(byte[] sector, int offset, string name)
        {
            for (int i = 0; i < 11; i++)
            {
                sector[offset + i] = (byte)' ';
            }

            if (name == "." || name == "..")
            {
                for (int i = 0; i < name.Length; i++)
                {
                    sector[offset + i] = (byte)name[i];
                }

                return;
            }

            int dot = name.LastIndexOf('.');
            string baseName = dot >= 0 ? name.Substring(0, dot) : name;
            string ext = dot >= 0 ? name.Substring(dot + 1) : string.Empty;

            baseName = baseName.ToUpperInvariant();
            ext = ext.ToUpperInvariant();

            for (int i = 0; i < baseName.Length && i < 8; i++)
            {
                sector[offset + i] = (byte)baseName[i];
            }

            for (int i = 0; i < ext.Length && i < 3; i++)
            {
                sector[offset + 8 + i] = (byte)ext[i];
            }
        }

        private static void ValidateShortName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("Name cannot be empty", nameof(name));
            }

            int dot = name.LastIndexOf('.');
            string baseName = dot >= 0 ? name.Substring(0, dot) : name;
            string ext = dot >= 0 ? name.Substring(dot + 1) : string.Empty;

            if (baseName.Length == 0 || baseName.Length > 8 || ext.Length > 3)
            {
                throw new ArgumentException($"'{name}' is not a valid 8.3 short name (long filenames are not supported for write)", nameof(name));
            }
        }

        private static FileMetadata ToMetadata(FatDirectoryEntry entry)
        {
            return new FileMetadata
            {
                Name = entry.FileName,
                Path = "/" + entry.FileName,
                IsDirectory = entry.IsDirectory,
                Size = entry.FileSize,
                FileType = entry.IsDirectory ? "DIR" : "FILE",
                Attributes = entry.Attributes.ToString("X2"),
                UrnIdentifier = UrnIdentifier.Encode(entry.FileName)
            };
        }

        private void EnsureInitialized()
        {
            if (_device == null || _bootSector == null)
            {
                throw new InvalidOperationException("FAT32 filesystem not initialized");
            }
        }

        // ===================================================================
        // INTERNAL STRUCTURES
        // ===================================================================

        private class FatDirectoryEntry
        {
            public string FileName { get; set; } = string.Empty;
            public byte Attributes { get; set; }
            public uint StartCluster { get; set; }
            public uint FileSize { get; set; }
            public bool IsDirectory { get; set; }
        }

        private class Fat32BootSector
        {
            public ushort BytesPerSector { get; set; }
            public byte SectorsPerCluster { get; set; }
            public ushort ReservedSectors { get; set; }
            public byte NumberOfFATs { get; set; }
            public ushort RootEntryCount { get; set; }
            public ushort TotalSectors16 { get; set; }
            public byte MediaDescriptor { get; set; }
            public ushort SectorsPerFAT16 { get; set; }
            public uint TotalSectors { get; set; }
            public uint SectorsPerFAT32 { get; set; }
            public uint RootCluster { get; set; }
        }

        private readonly struct RawEntry
        {
            public uint Lba { get; init; }
            public int Offset { get; init; }
            public FatDirectoryEntry Entry { get; init; }
        }

        private readonly struct EntrySlot
        {
            public uint Lba { get; init; }
            public int Offset { get; init; }
        }
    }
}
