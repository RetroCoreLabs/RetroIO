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
    /// FAT16 filesystem implementation for hard disks and large floppy disks.
    /// Supports volumes up to 2GB. Full read/write, including arbitrary-depth subdirectories.
    /// </summary>
    public class Fat16FileSystem : IBlockFileSystem
    {
        private const int DirEntrySize = 32;
        private const int EndOfDirectoryMarker = 0x00;
        private const int DeletedEntryMarker = 0xE5;
        private const int VolumeLabelAttribute = 0x08;
        private const int DirectoryAttribute = 0x10;
        private const int ArchiveAttribute = 0x20;
        private const int LfnAttributeMask = 0x0F;
        private const uint Fat16EndOfChain = 0xFFFF;
        private const uint Fat16FirstDataCluster = 2;
        private const uint Fat16ReservedThreshold = 0xFFF8; // clusters >= this are EOC/reserved/bad

        private IBlockDevice? _device;
        private string _volumeLabel = string.Empty;
        private FatBootSector? _bootSector;
        private byte[]? _fatTable;
        private uint _rootDirStart;
        private uint _rootDirSectors;
        private uint _dataStart;

        /// <inheritdoc/>
        public string FileSystemType => "FAT16";
        /// <inheritdoc/>
        public string VolumeLabel => _volumeLabel;
        /// <inheritdoc/>
        public string ExtensionSeparator => ".";  // FAT uses name.ext format

        /// <inheritdoc/>
        public void Initialize(IBlockDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));

            // Read boot sector
            byte[] bootSectorData = new byte[512];
            device.ReadBlock(BlockAddress.FromLBA(0), bootSectorData);

            _bootSector = ParseBootSector(bootSectorData);

            // Read FAT table
            ReadFatTable();

            // Calculate directory and data area locations
            _rootDirStart = (uint)(_bootSector.ReservedSectors + (_bootSector.NumberOfFATs * _bootSector.SectorsPerFAT));
            _rootDirSectors = ((uint)_bootSector.RootEntryCount * DirEntrySize + _bootSector.BytesPerSector - 1) / (uint)_bootSector.BytesPerSector;
            _dataStart = _rootDirStart + _rootDirSectors;

            ReadVolumeLabel();
        }

        /// <inheritdoc/>
        public bool CanHandle(IBlockDevice device)
        {
            try
            {
                byte[] bootSector = new byte[512];
                device.ReadBlock(BlockAddress.FromLBA(0), bootSector);

                // Check boot signature
                if (bootSector[510] != 0x55 || bootSector[511] != 0xAA)
                {
                    return false;
                }

                // Check for FAT16 signature at offset 0x36
                string fsType = Encoding.ASCII.GetString(bootSector, 0x36, 8).Trim();
                if (fsType.StartsWith("FAT16"))
                {
                    return true;
                }

                // Heuristic: Calculate cluster count to determine FAT type
                ushort bytesPerSector = (ushort)(bootSector[11] | (bootSector[12] << 8));
                byte sectorsPerCluster = bootSector[13];
                ushort reservedSectors = (ushort)(bootSector[14] | (bootSector[15] << 8));
                byte numberOfFATs = bootSector[16];
                ushort rootEntryCount = (ushort)(bootSector[17] | (bootSector[18] << 8));
                ushort totalSectors16 = (ushort)(bootSector[19] | (bootSector[20] << 8));
                ushort sectorsPerFAT = (ushort)(bootSector[22] | (bootSector[23] << 8));
                uint totalSectors32 = (uint)(bootSector[32] | (bootSector[33] << 8) | (bootSector[34] << 16) | (bootSector[35] << 24));

                uint totalSectors = totalSectors16 != 0 ? (uint)totalSectors16 : totalSectors32;
                uint rootDirSectors = ((uint)rootEntryCount * 32u + (uint)bytesPerSector - 1) / (uint)bytesPerSector;
                uint dataSectors = totalSectors - ((uint)reservedSectors + ((uint)numberOfFATs * (uint)sectorsPerFAT) + rootDirSectors);
                uint clusterCount = dataSectors / sectorsPerCluster;

                // FAT16: 4085 to 65524 clusters
                return clusterCount >= 4085 && clusterCount < 65525;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Formats a blank FAT16 filesystem onto <paramref name="device"/> — see
        /// <see cref="Fat12FileSystem.Format"/> for the shared design notes (this must not be
        /// called on an already-Initialize'd instance; construct a fresh instance afterwards).
        /// </summary>
        public static void Format(IBlockDevice device, uint totalSectors, byte sectorsPerCluster = 4,
            ushort reservedSectors = 1, byte numberOfFats = 2, ushort rootEntryCount = 512,
            byte mediaDescriptor = 0xF8, string volumeLabel = "")
        {
            if (device == null)
            {
                throw new ArgumentNullException(nameof(device));
            }

            const int bytesPerSector = 512;
            uint rootDirSectors = ((uint)rootEntryCount * DirEntrySize + bytesPerSector - 1) / (uint)bytesPerSector;
            ushort sectorsPerFat = ComputeSectorsPerFat(totalSectors, sectorsPerCluster, reservedSectors,
                numberOfFats, rootDirSectors, bytesPerSector, bytesPerFatEntry: 2.0);

            bool needs32BitTotalSectors = totalSectors > 0xFFFF;

            byte[] boot = new byte[bytesPerSector];
            boot[0] = 0xEB; boot[1] = 0x3C; boot[2] = 0x90;
            WriteAsciiField(boot, 3, "RETROFS ", 8);
            boot[11] = (byte)(bytesPerSector & 0xFF);
            boot[12] = (byte)((bytesPerSector >> 8) & 0xFF);
            boot[13] = sectorsPerCluster;
            boot[14] = (byte)(reservedSectors & 0xFF);
            boot[15] = (byte)((reservedSectors >> 8) & 0xFF);
            boot[16] = numberOfFats;
            boot[17] = (byte)(rootEntryCount & 0xFF);
            boot[18] = (byte)((rootEntryCount >> 8) & 0xFF);
            if (!needs32BitTotalSectors)
            {
                boot[19] = (byte)(totalSectors & 0xFF);
                boot[20] = (byte)((totalSectors >> 8) & 0xFF);
            }
            boot[21] = mediaDescriptor;
            boot[22] = (byte)(sectorsPerFat & 0xFF);
            boot[23] = (byte)((sectorsPerFat >> 8) & 0xFF);
            if (needs32BitTotalSectors)
            {
                boot[32] = (byte)(totalSectors & 0xFF);
                boot[33] = (byte)((totalSectors >> 8) & 0xFF);
                boot[34] = (byte)((totalSectors >> 16) & 0xFF);
                boot[35] = (byte)((totalSectors >> 24) & 0xFF);
            }
            WriteAsciiField(boot, 0x36, "FAT16   ", 8);
            boot[510] = 0x55;
            boot[511] = 0xAA;

            device.WriteBlock(BlockAddress.FromLBA(0), boot);

            byte[] zero = new byte[bytesPerSector];
            uint fatStart = reservedSectors;
            for (int copy = 0; copy < numberOfFats; copy++)
            {
                uint copyStart = fatStart + (uint)copy * sectorsPerFat;
                for (uint i = 0; i < sectorsPerFat; i++)
                {
                    device.WriteBlock(BlockAddress.FromLBA(copyStart + i), zero);
                }

                // FAT16 clusters 0/1 are reserved: entry 0 = 0xFF00 | media descriptor, entry 1 = end-of-chain marker.
                byte[] fatHeader = new byte[bytesPerSector];
                fatHeader[0] = mediaDescriptor;
                fatHeader[1] = 0xFF;
                fatHeader[2] = 0xFF;
                fatHeader[3] = 0xFF;
                device.WriteBlock(BlockAddress.FromLBA(copyStart), fatHeader);
            }

            uint rootDirStart = fatStart + (uint)numberOfFats * sectorsPerFat;
            for (uint i = 0; i < rootDirSectors; i++)
            {
                device.WriteBlock(BlockAddress.FromLBA(rootDirStart + i), zero);
            }

            if (!string.IsNullOrEmpty(volumeLabel))
            {
                byte[] rootFirstSector = new byte[bytesPerSector];
                WriteAsciiField(rootFirstSector, 0, volumeLabel.ToUpperInvariant(), 11);
                rootFirstSector[11] = VolumeLabelAttribute;
                device.WriteBlock(BlockAddress.FromLBA(rootDirStart), rootFirstSector);
            }
        }

        private static ushort ComputeSectorsPerFat(uint totalSectors, byte sectorsPerCluster,
            ushort reservedSectors, byte numberOfFats, uint rootDirSectors, int bytesPerSector, double bytesPerFatEntry)
        {
            ushort sectorsPerFat = 1;
            for (int iteration = 0; iteration < 10; iteration++)
            {
                uint dataStart = reservedSectors + (uint)numberOfFats * sectorsPerFat + rootDirSectors;
                uint dataSectors = totalSectors > dataStart ? totalSectors - dataStart : 0;
                uint clusterCount = dataSectors / sectorsPerCluster;
                double fatBytesNeeded = (clusterCount + 2) * bytesPerFatEntry + 8;
                ushort needed = (ushort)Math.Max(1, (int)Math.Ceiling(fatBytesNeeded / bytesPerSector));

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

        /// <inheritdoc/>
        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureInitialized();
            DirCursor dir = ResolveDirectory(path);
            return ListDirectoryEntries(dir);
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public void WriteFile(string path, byte[] data)
        {
            EnsureInitialized();
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            (DirCursor parent, string name) = ResolveParentAndName(path);
            ValidateShortName(name);

            RawEntry? existing = FindEntryInDirectory(parent, name);
            if (existing.HasValue && existing.Value.Entry.IsDirectory)
            {
                throw new IOException($"Cannot overwrite directory '{name}' with a file");
            }

            if (existing.HasValue)
            {
                if (existing.Value.Entry.StartCluster >= Fat16FirstDataCluster)
                {
                    FreeChain(existing.Value.Entry.StartCluster);
                }

                uint newStartCluster = data.Length > 0 ? AllocateAndWriteChain(data) : 0;
                WriteEntryFields(existing.Value.Lba, existing.Value.Offset, newStartCluster, (uint)data.Length, ArchiveAttribute);
                return;
            }

            uint startCluster = data.Length > 0 ? AllocateAndWriteChain(data) : 0;
            EntrySlot slot = FindFreeSlot(parent, allowGrow: true);
            WriteNewEntry(slot, name, ArchiveAttribute, startCluster, (uint)data.Length);
        }

        /// <inheritdoc/>
        public void DeleteFile(string path)
        {
            EnsureInitialized();
            (DirCursor parent, string name) = ResolveParentAndName(path);
            RawEntry raw = FindEntryInDirectory(parent, name) ?? throw new FileNotFoundException($"File not found: {path}");

            if (raw.Entry.IsDirectory)
            {
                throw new IOException($"'{path}' is a directory — use DeleteDirectory instead");
            }

            if (raw.Entry.StartCluster >= Fat16FirstDataCluster)
            {
                FreeChain(raw.Entry.StartCluster);
            }

            MarkSlotDeleted(raw.Lba, raw.Offset);
        }

        /// <inheritdoc/>
        public FileMetadata? GetMetadata(string path)
        {
            EnsureInitialized();
            RawEntry? raw = FindEntryByPath(path);
            return raw == null ? null : ToMetadata(raw.Value.Entry);
        }

        /// <inheritdoc/>
        public int GetBlocksFree()
        {
            EnsureInitialized();

            int freeClusters = 0;
            uint totalClusters = GetTotalDataClusters();

            for (uint cluster = Fat16FirstDataCluster; cluster < totalClusters + Fat16FirstDataCluster; cluster++)
            {
                if (ReadFatEntry(cluster) == 0)
                {
                    freeClusters++;
                }
            }

            return freeClusters * _bootSector!.SectorsPerCluster;
        }

        /// <inheritdoc/>
        public void CreateDirectory(string path)
        {
            EnsureInitialized();
            (DirCursor parent, string name) = ResolveParentAndName(path);
            ValidateShortName(name);

            if (FindEntryInDirectory(parent, name).HasValue)
            {
                throw new IOException($"'{name}' already exists");
            }

            uint newCluster = AllocateSingleZeroedCluster();
            WriteDotEntries(newCluster, parent);

            EntrySlot slot = FindFreeSlot(parent, allowGrow: true);
            WriteNewEntry(slot, name, DirectoryAttribute, newCluster, 0);
        }

        /// <inheritdoc/>
        public void DeleteDirectory(string path)
        {
            EnsureInitialized();
            (DirCursor parent, string name) = ResolveParentAndName(path);
            RawEntry raw = FindEntryInDirectory(parent, name) ?? throw new DirectoryNotFoundException($"Directory not found: {path}");

            if (!raw.Entry.IsDirectory)
            {
                throw new IOException($"'{path}' is a file — use DeleteFile instead");
            }

            DirCursor target = DirCursor.Sub(raw.Entry.StartCluster);
            if (ListDirectoryEntries(target).Length > 0)
            {
                throw new IOException($"Directory '{path}' is not empty");
            }

            FreeChain(raw.Entry.StartCluster);
            MarkSlotDeleted(raw.Lba, raw.Offset);
        }

        /// <inheritdoc/>
        public void Rename(string oldPath, string newPath)
        {
            EnsureInitialized();
            (DirCursor oldParent, string oldName) = ResolveParentAndName(oldPath);
            (DirCursor newParent, string newName) = ResolveParentAndName(newPath);
            ValidateShortName(newName);

            RawEntry raw = FindEntryInDirectory(oldParent, oldName) ?? throw new FileNotFoundException($"Path not found: {oldPath}");

            if (FindEntryInDirectory(newParent, newName).HasValue)
            {
                throw new IOException($"'{newPath}' already exists");
            }

            if (DirCursorsEqual(oldParent, newParent))
            {
                WriteEntryName(raw.Lba, raw.Offset, newName);
                return;
            }

            MarkSlotDeleted(raw.Lba, raw.Offset);
            EntrySlot slot = FindFreeSlot(newParent, allowGrow: true);
            WriteNewEntry(slot, newName, raw.Entry.Attributes, raw.Entry.StartCluster, raw.Entry.FileSize);
        }

        // ===================================================================
        // PATH RESOLUTION
        // ===================================================================

        private DirCursor ResolveDirectory(string path)
        {
            string[] segments = SplitPath(path);
            DirCursor cursor = DirCursor.Root;

            for (int i = 0; i < segments.Length; i++)
            {
                RawEntry entry = FindEntryInDirectory(cursor, segments[i])
                    ?? throw new DirectoryNotFoundException($"Directory not found: {segments[i]}");

                if (!entry.Entry.IsDirectory)
                {
                    throw new DirectoryNotFoundException($"'{segments[i]}' is not a directory");
                }

                cursor = DirCursor.Sub(entry.Entry.StartCluster);
            }

            return cursor;
        }

        private (DirCursor Parent, string Name) ResolveParentAndName(string path)
        {
            string[] segments = SplitPath(path);
            if (segments.Length == 0)
            {
                throw new ArgumentException("Path must reference a file or directory name, not root", nameof(path));
            }

            DirCursor cursor = DirCursor.Root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                RawEntry entry = FindEntryInDirectory(cursor, segments[i])
                    ?? throw new DirectoryNotFoundException($"Directory not found: {segments[i]}");

                if (!entry.Entry.IsDirectory)
                {
                    throw new DirectoryNotFoundException($"'{segments[i]}' is not a directory");
                }

                cursor = DirCursor.Sub(entry.Entry.StartCluster);
            }

            return (cursor, segments[segments.Length - 1]);
        }

        private RawEntry? FindEntryByPath(string path)
        {
            (DirCursor parent, string name) = ResolveParentAndName(path);
            return FindEntryInDirectory(parent, name);
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

        private static bool DirCursorsEqual(DirCursor a, DirCursor b)
        {
            if (a.IsRoot != b.IsRoot)
            {
                return false;
            }

            return a.IsRoot || a.StartCluster == b.StartCluster;
        }

        // ===================================================================
        // BOOT SECTOR / FAT TABLE
        // ===================================================================

        private FatBootSector ParseBootSector(byte[] data)
        {
            FatBootSector boot = new FatBootSector
            {
                BytesPerSector = (ushort)(data[11] | (data[12] << 8)),
                SectorsPerCluster = data[13],
                ReservedSectors = (ushort)(data[14] | (data[15] << 8)),
                NumberOfFATs = data[16],
                RootEntryCount = (ushort)(data[17] | (data[18] << 8)),
                TotalSectors = (ushort)(data[19] | (data[20] << 8)),
                MediaDescriptor = data[21],
                SectorsPerFAT = (ushort)(data[22] | (data[23] << 8))
            };

            if (boot.TotalSectors == 0)
            {
                boot.TotalSectors = (uint)(data[32] | (data[33] << 8) | (data[34] << 16) | (data[35] << 24));
            }

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

            if (boot.SectorsPerFAT == 0)
            {
                throw new InvalidDataException("Invalid sectors per FAT: 0");
            }

            return boot;
        }

        private void ReadFatTable()
        {
            uint fatSizeBytes = (uint)(_bootSector!.SectorsPerFAT * _bootSector.BytesPerSector);
            _fatTable = new byte[fatSizeBytes];

            uint fatStart = _bootSector.ReservedSectors;

            for (int i = 0; i < _bootSector.SectorsPerFAT; i++)
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
                WriteFatCopy(fatStart + _bootSector.SectorsPerFAT);
            }
        }

        private void WriteFatCopy(uint startSector)
        {
            for (int i = 0; i < _bootSector!.SectorsPerFAT; i++)
            {
                byte[] sectorData = new byte[_bootSector.BytesPerSector];
                Array.Copy(_fatTable!, i * _bootSector.BytesPerSector, sectorData, 0, _bootSector.BytesPerSector);
                _device!.WriteBlock(BlockAddress.FromLBA(startSector + (uint)i), sectorData);
            }
        }

        private uint ReadFatEntry(uint cluster)
        {
            // FAT16: 2 bytes per entry
            long offset = cluster * 2;
            if (offset >= _fatTable!.Length - 1)
            {
                return Fat16EndOfChain;
            }

            return (uint)(_fatTable[offset] | (_fatTable[offset + 1] << 8));
        }

        private void WriteFatEntry(uint cluster, uint value)
        {
            long offset = cluster * 2;
            _fatTable![offset] = (byte)(value & 0xFF);
            _fatTable[offset + 1] = (byte)((value >> 8) & 0xFF);
            FlushFatTable();
        }

        private uint GetTotalDataClusters()
        {
            uint dataSectors = _bootSector!.TotalSectors - _dataStart;
            return dataSectors / _bootSector.SectorsPerCluster;
        }

        private uint FindFreeCluster()
        {
            uint totalClusters = GetTotalDataClusters();
            for (uint cluster = Fat16FirstDataCluster; cluster < totalClusters + Fat16FirstDataCluster; cluster++)
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
            while (cluster >= Fat16FirstDataCluster && cluster < Fat16ReservedThreshold)
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
                WriteFatEntry(cluster, Fat16EndOfChain);

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
            WriteFatEntry(cluster, Fat16EndOfChain);

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

            while (cluster >= Fat16FirstDataCluster && cluster < Fat16ReservedThreshold && bytesRemaining > 0)
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
            return _dataStart + ((cluster - Fat16FirstDataCluster) * _bootSector!.SectorsPerCluster);
        }

        // ===================================================================
        // DIRECTORY SCANNING (root: fixed sector range; subdir: cluster chain)
        // ===================================================================

        private uint[] GetDirLbas(DirCursor dir)
        {
            if (dir.IsRoot)
            {
                uint[] lbas = new uint[_rootDirSectors];
                for (int i = 0; i < lbas.Length; i++)
                {
                    lbas[i] = _rootDirStart + (uint)i;
                }

                return lbas;
            }

            List<uint> result = new List<uint>();
            uint cluster = dir.StartCluster;
            while (cluster >= Fat16FirstDataCluster && cluster < Fat16ReservedThreshold)
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

        private DirectoryEntry[] ListDirectoryEntries(DirCursor dir)
        {
            List<DirectoryEntry> entries = new List<DirectoryEntry>();
            uint[] lbas = GetDirLbas(dir);

            for (int s = 0; s < lbas.Length; s++)
            {
                byte[] sectorData = new byte[_bootSector!.BytesPerSector];
                _device!.ReadBlock(BlockAddress.FromLBA(lbas[s]), sectorData);

                for (int i = 0; i < _bootSector.BytesPerSector; i += DirEntrySize)
                {
                    if (sectorData[i] == EndOfDirectoryMarker)
                    {
                        return entries.ToArray();
                    }

                    if (sectorData[i] == DeletedEntryMarker)
                    {
                        continue;
                    }

                    byte attributes = sectorData[i + 11];
                    if ((attributes & VolumeLabelAttribute) != 0 || (attributes & LfnAttributeMask) == LfnAttributeMask)
                    {
                        continue;
                    }

                    FatDirectoryEntry entry = ParseDirectoryEntry(sectorData, i);
                    if (entry.FileName == "." || entry.FileName == "..")
                    {
                        continue;
                    }

                    entries.Add(new DirectoryEntry { Name = entry.FileName, Metadata = ToMetadata(entry) });
                }
            }

            return entries.ToArray();
        }

        private RawEntry? FindEntryInDirectory(DirCursor dir, string name)
        {
            uint[] lbas = GetDirLbas(dir);

            for (int s = 0; s < lbas.Length; s++)
            {
                byte[] sectorData = new byte[_bootSector!.BytesPerSector];
                _device!.ReadBlock(BlockAddress.FromLBA(lbas[s]), sectorData);

                for (int i = 0; i < _bootSector.BytesPerSector; i += DirEntrySize)
                {
                    if (sectorData[i] == EndOfDirectoryMarker)
                    {
                        return null;
                    }

                    if (sectorData[i] == DeletedEntryMarker)
                    {
                        continue;
                    }

                    byte attributes = sectorData[i + 11];
                    if ((attributes & VolumeLabelAttribute) != 0 || (attributes & LfnAttributeMask) == LfnAttributeMask)
                    {
                        continue;
                    }

                    FatDirectoryEntry entry = ParseDirectoryEntry(sectorData, i);
                    if (string.Equals(entry.FileName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return new RawEntry { Lba = lbas[s], Offset = i, Entry = entry };
                    }
                }
            }

            return null;
        }

        private EntrySlot FindFreeSlot(DirCursor dir, bool allowGrow)
        {
            uint[] lbas = GetDirLbas(dir);

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

            if (dir.IsRoot)
            {
                throw new IOException("Root directory is full and cannot grow (FAT12/16 fixed-size root)");
            }

            if (!allowGrow)
            {
                throw new IOException("Directory is full");
            }

            uint lastCluster = FindLastCluster(dir.StartCluster);
            uint newCluster = FindFreeCluster();
            WriteFatEntry(lastCluster, newCluster);
            WriteFatEntry(newCluster, Fat16EndOfChain);

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
            while (next >= Fat16FirstDataCluster && next < Fat16ReservedThreshold)
            {
                cluster = next;
                next = ReadFatEntry(cluster);
            }

            return cluster;
        }

        private void WriteDotEntries(uint newCluster, DirCursor parent)
        {
            uint lba = ClusterToLBA(newCluster);
            byte[] sector = new byte[_bootSector!.BytesPerSector];

            uint parentCluster = parent.IsRoot ? 0u : parent.StartCluster;
            WriteRawEntryBytes(sector, 0, ".", DirectoryAttribute, newCluster, 0);
            WriteRawEntryBytes(sector, DirEntrySize, "..", DirectoryAttribute, parentCluster, 0);

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
            ushort startClusterLow = (ushort)(data[offset + 26] | (data[offset + 27] << 8));
            uint fileSize = (uint)(data[offset + 28] | (data[offset + 29] << 8) | (data[offset + 30] << 16) | (data[offset + 31] << 24));

            return new FatDirectoryEntry
            {
                FileName = fileName,
                Attributes = attributes,
                StartCluster = startClusterLow,
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

        private void ReadVolumeLabel()
        {
            byte[] rootDir = new byte[_bootSector!.BytesPerSector];
            _device!.ReadBlock(BlockAddress.FromLBA(_rootDirStart), rootDir);

            for (int i = 0; i < rootDir.Length; i += DirEntrySize)
            {
                if (rootDir[i] == EndOfDirectoryMarker || rootDir[i] == DeletedEntryMarker)
                {
                    continue;
                }

                byte attributes = rootDir[i + 11];
                if ((attributes & VolumeLabelAttribute) != 0)
                {
                    _volumeLabel = Encoding.ASCII.GetString(rootDir, i, 11).Trim();
                    return;
                }
            }

            _volumeLabel = "NO NAME";
        }

        private void EnsureInitialized()
        {
            if (_device == null || _bootSector == null)
            {
                throw new InvalidOperationException("FAT16 filesystem not initialized");
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

        private class FatBootSector
        {
            public ushort BytesPerSector { get; set; }
            public byte SectorsPerCluster { get; set; }
            public ushort ReservedSectors { get; set; }
            public byte NumberOfFATs { get; set; }
            public ushort RootEntryCount { get; set; }
            public uint TotalSectors { get; set; }
            public byte MediaDescriptor { get; set; }
            public ushort SectorsPerFAT { get; set; }
        }

        private readonly struct DirCursor
        {
            public bool IsRoot { get; }
            public uint StartCluster { get; }

            private DirCursor(bool isRoot, uint startCluster)
            {
                IsRoot = isRoot;
                StartCluster = startCluster;
            }

            public static DirCursor Root => new DirCursor(true, 0);
            public static DirCursor Sub(uint startCluster) => new DirCursor(false, startCluster);
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
