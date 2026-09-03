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
    /// FAT12 filesystem implementation for PC floppy disks.
    /// Supports 360KB, 720KB, 1.2MB, 1.44MB, 2.88MB formats.
    /// Full read/write, including arbitrary-depth subdirectories.
    /// </summary>
    public class Fat12FileSystem : IBlockFileSystem
    {
        private const int DirEntrySize = 32;
        private const int EndOfDirectoryMarker = 0x00;
        private const int DeletedEntryMarker = 0xE5;
        private const int VolumeLabelAttribute = 0x08;
        private const int DirectoryAttribute = 0x10;
        private const int ArchiveAttribute = 0x20;
        private const int LfnAttributeMask = 0x0F;
        private const ushort Fat12EndOfChain = 0xFFF;
        private const ushort Fat12FirstDataCluster = 2;
        private const ushort Fat12ReservedThreshold = 0xFF8; // clusters >= this are EOC/reserved/bad

        private IBlockDevice? _device;
        private string _volumeLabel = string.Empty;
        private FatBootSector? _bootSector;
        private byte[]? _fatTable;
        private uint _rootDirStart;
        private uint _rootDirSectors;
        private uint _dataStart;

        public string FileSystemType => "FAT12";
        public string VolumeLabel => _volumeLabel;
        public string ExtensionSeparator => ".";  // FAT uses name.ext format

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
            _rootDirSectors = (uint)((_bootSector.RootEntryCount * DirEntrySize) + _bootSector.BytesPerSector - 1) / _bootSector.BytesPerSector;
            _dataStart = _rootDirStart + _rootDirSectors;

            // Read volume label from root directory
            ReadVolumeLabel();
        }

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

                // Check for FAT12 signature at offset 0x36
                string fsType = Encoding.ASCII.GetString(bootSector, 0x36, 8).Trim();
                if (fsType.StartsWith("FAT12"))
                {
                    return true;
                }

                // Alternative: Check bytes per sector and cluster size (FAT12 heuristic)
                ushort bytesPerSector = (ushort)(bootSector[11] | (bootSector[12] << 8));
                byte sectorsPerCluster = bootSector[13];

                if (bytesPerSector == 512 && sectorsPerCluster >= 1 && sectorsPerCluster <= 8)
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Formats a blank FAT12 filesystem onto <paramref name="device"/> — writes the boot
        /// sector, both zeroed FAT copies (with the reserved cluster-0/1 header entries set per
        /// spec), and a zeroed root directory. The device must already be mounted (writable) at
        /// the correct total size; this does not call <see cref="Initialize"/> on the resulting
        /// filesystem — construct a fresh instance and call Initialize afterwards to mount it.
        /// </summary>
        public static void Format(IBlockDevice device, uint totalSectors, byte sectorsPerCluster = 1,
            ushort reservedSectors = 1, byte numberOfFats = 2, ushort rootEntryCount = 224,
            byte mediaDescriptor = 0xF8, string volumeLabel = "")
        {
            if (device == null)
            {
                throw new ArgumentNullException(nameof(device));
            }

            if (totalSectors > 0xFFFF)
            {
                throw new ArgumentException(
                    $"FAT12 volumes must be 65535 sectors or fewer ({totalSectors} given) — use FAT16/32 for larger disks",
                    nameof(totalSectors));
            }

            const int bytesPerSector = 512;
            uint rootDirSectors = ((uint)rootEntryCount * DirEntrySize + bytesPerSector - 1) / bytesPerSector;
            ushort sectorsPerFat = ComputeSectorsPerFat(totalSectors, sectorsPerCluster, reservedSectors,
                numberOfFats, rootDirSectors, bytesPerSector, bytesPerFatEntry: 1.5);

            byte[] boot = new byte[bytesPerSector];
            boot[0] = 0xEB; boot[1] = 0x3C; boot[2] = 0x90; // JMP short + NOP (real DOS boot sectors start this way)
            WriteAsciiField(boot, 3, "RETROFS ", 8);
            boot[11] = (byte)(bytesPerSector & 0xFF);
            boot[12] = (byte)((bytesPerSector >> 8) & 0xFF);
            boot[13] = sectorsPerCluster;
            boot[14] = (byte)(reservedSectors & 0xFF);
            boot[15] = (byte)((reservedSectors >> 8) & 0xFF);
            boot[16] = numberOfFats;
            boot[17] = (byte)(rootEntryCount & 0xFF);
            boot[18] = (byte)((rootEntryCount >> 8) & 0xFF);
            boot[19] = (byte)(totalSectors & 0xFF);
            boot[20] = (byte)((totalSectors >> 8) & 0xFF);
            boot[21] = mediaDescriptor;
            boot[22] = (byte)(sectorsPerFat & 0xFF);
            boot[23] = (byte)((sectorsPerFat >> 8) & 0xFF);
            WriteAsciiField(boot, 0x36, "FAT12   ", 8);
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

                // FAT12 clusters 0/1 are reserved: entry 0 = 0xF00 | media descriptor, entry 1 = end-of-chain marker.
                byte[] fatHeader = new byte[bytesPerSector];
                fatHeader[0] = mediaDescriptor;
                fatHeader[1] = 0xFF;
                fatHeader[2] = 0xFF;
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
                double fatBytesNeeded = (clusterCount + 2) * bytesPerFatEntry + 8; // +8 padding for rounding
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

        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureInitialized();
            DirCursor dir = ResolveDirectory(path);
            return ListDirectoryEntries(dir);
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

            (DirCursor parent, string name) = ResolveParentAndName(path);
            ValidateShortName(name);

            RawEntry? existing = FindEntryInDirectory(parent, name);
            if (existing.HasValue && existing.Value.Entry.IsDirectory)
            {
                throw new IOException($"Cannot overwrite directory '{name}' with a file");
            }

            if (existing.HasValue)
            {
                // Overwrite: free the old chain, reuse the existing directory slot.
                if (existing.Value.Entry.StartCluster >= Fat12FirstDataCluster)
                {
                    FreeChain(existing.Value.Entry.StartCluster);
                }

                ushort newStartCluster = data.Length > 0 ? AllocateAndWriteChain(data) : (ushort)0;
                WriteEntryFields(existing.Value.Lba, existing.Value.Offset, newStartCluster, (uint)data.Length, ArchiveAttribute);
                return;
            }

            ushort startCluster = data.Length > 0 ? AllocateAndWriteChain(data) : (ushort)0;
            EntrySlot slot = FindFreeSlot(parent, allowGrow: true);
            WriteNewEntry(slot, name, ArchiveAttribute, startCluster, (uint)data.Length);
        }

        public void DeleteFile(string path)
        {
            EnsureInitialized();
            (DirCursor parent, string name) = ResolveParentAndName(path);
            RawEntry raw = FindEntryInDirectory(parent, name) ?? throw new FileNotFoundException($"File not found: {path}");

            if (raw.Entry.IsDirectory)
            {
                throw new IOException($"'{path}' is a directory — use DeleteDirectory instead");
            }

            if (raw.Entry.StartCluster >= Fat12FirstDataCluster)
            {
                FreeChain(raw.Entry.StartCluster);
            }

            MarkSlotDeleted(raw.Lba, raw.Offset);
        }

        public FileMetadata? GetMetadata(string path)
        {
            EnsureInitialized();
            RawEntry? raw = FindEntryByPath(path);
            if (raw == null)
            {
                return null;
            }

            return ToMetadata(raw.Value.Entry);
        }

        public int GetBlocksFree()
        {
            EnsureInitialized();

            int freeClusters = 0;
            int totalClusters = GetTotalDataClusters();

            for (int cluster = Fat12FirstDataCluster; cluster < totalClusters + Fat12FirstDataCluster; cluster++)
            {
                ushort fatEntry = ReadFatEntry(cluster);
                if (fatEntry == 0)
                {
                    freeClusters++;
                }
            }

            return freeClusters * _bootSector!.SectorsPerCluster;
        }

        public void CreateDirectory(string path)
        {
            EnsureInitialized();
            (DirCursor parent, string name) = ResolveParentAndName(path);
            ValidateShortName(name);

            if (FindEntryInDirectory(parent, name).HasValue)
            {
                throw new IOException($"'{name}' already exists");
            }

            ushort newCluster = AllocateSingleZeroedCluster();
            WriteDotEntries(newCluster, parent);

            EntrySlot slot = FindFreeSlot(parent, allowGrow: true);
            WriteNewEntry(slot, name, DirectoryAttribute, newCluster, 0);
        }

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
                // Same directory: just rewrite the name bytes in place.
                WriteEntryName(raw.Lba, raw.Offset, newName);
                return;
            }

            // Cross-directory move: relocate the directory entry, but the data cluster chain
            // is untouched — no need to copy file contents just to change which directory
            // entry points at them.
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
            if (trimmed.Length == 0)
            {
                return Array.Empty<string>();
            }

            return trimmed.Split('/');
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

            // Validate boot sector values
            if (boot.BytesPerSector != 512)
            {
                throw new InvalidDataException($"Invalid bytes per sector: {boot.BytesPerSector} (expected 512)");
            }

            if (boot.SectorsPerCluster == 0 || boot.SectorsPerCluster > 128)
            {
                throw new InvalidDataException($"Invalid sectors per cluster: {boot.SectorsPerCluster}");
            }

            if (boot.NumberOfFATs == 0 || boot.NumberOfFATs > 2)
            {
                throw new InvalidDataException($"Invalid number of FATs: {boot.NumberOfFATs} (expected 1 or 2)");
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

        private ushort ReadFatEntry(int cluster)
        {
            // FAT12: 12-bit entries, packed 2 entries per 3 bytes
            int fatOffset = cluster + (cluster / 2); // cluster * 1.5
            ushort fatEntry = (ushort)(_fatTable![fatOffset] | (_fatTable[fatOffset + 1] << 8));

            if ((cluster & 1) != 0)
            {
                fatEntry >>= 4; // Odd cluster, use high 12 bits
            }
            else
            {
                fatEntry &= 0x0FFF; // Even cluster, use low 12 bits
            }

            return fatEntry;
        }

        /// <summary>
        /// Writes a 12-bit FAT entry, reversing the odd/even nibble packing of
        /// <see cref="ReadFatEntry"/> exactly — a FAT12 entry shares a byte with its
        /// neighboring cluster's entry, so the neighbor's nibble must be preserved.
        /// </summary>
        private void WriteFatEntry(int cluster, ushort value)
        {
            int fatOffset = cluster + (cluster / 2);
            value &= 0x0FFF;

            if ((cluster & 1) != 0)
            {
                // Odd cluster: value occupies bits 4-15 of the 16-bit word at fatOffset.
                // Bits 0-3 (low nibble of the first byte) belong to the even cluster below.
                byte preservedLowNibble = (byte)(_fatTable![fatOffset] & 0x0F);
                ushort shifted = (ushort)(value << 4);
                _fatTable[fatOffset] = (byte)(preservedLowNibble | (shifted & 0xF0));
                _fatTable[fatOffset + 1] = (byte)(shifted >> 8);
            }
            else
            {
                // Even cluster: value occupies bits 0-11. Bits 12-15 (high nibble of the
                // second byte) belong to the odd cluster above.
                byte preservedHighNibble = (byte)(_fatTable![fatOffset + 1] & 0xF0);
                _fatTable[fatOffset] = (byte)(value & 0xFF);
                _fatTable[fatOffset + 1] = (byte)(preservedHighNibble | ((value >> 8) & 0x0F));
            }

            FlushFatTable();
        }

        private int GetTotalDataClusters()
        {
            uint dataSectors = (uint)_bootSector!.TotalSectors - _dataStart;
            return (int)(dataSectors / _bootSector.SectorsPerCluster);
        }

        private ushort FindFreeCluster()
        {
            int totalClusters = GetTotalDataClusters();
            for (int cluster = Fat12FirstDataCluster; cluster < totalClusters + Fat12FirstDataCluster; cluster++)
            {
                if (ReadFatEntry(cluster) == 0)
                {
                    return (ushort)cluster;
                }
            }

            throw new IOException("No free space left on disk");
        }

        private void FreeChain(ushort startCluster)
        {
            int cluster = startCluster;
            while (cluster >= Fat12FirstDataCluster && cluster < Fat12ReservedThreshold)
            {
                int next = ReadFatEntry(cluster);
                WriteFatEntry(cluster, 0);
                cluster = next;
            }
        }

        // ===================================================================
        // CLUSTER ALLOCATION AND FILE DATA
        // ===================================================================

        private ushort AllocateAndWriteChain(byte[] data)
        {
            int clusterSizeBytes = _bootSector!.SectorsPerCluster * _bootSector.BytesPerSector;
            int clustersNeeded = (data.Length + clusterSizeBytes - 1) / clusterSizeBytes;

            ushort firstCluster = 0;
            ushort previousCluster = 0;
            int bytesWritten = 0;

            for (int c = 0; c < clustersNeeded; c++)
            {
                ushort cluster = FindFreeCluster();
                WriteFatEntry(cluster, Fat12EndOfChain); // provisionally mark used/EOC so the next FindFreeCluster call skips it

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

        private ushort AllocateSingleZeroedCluster()
        {
            ushort cluster = FindFreeCluster();
            WriteFatEntry(cluster, Fat12EndOfChain);

            uint lba = ClusterToLBA(cluster);
            byte[] zeroSector = new byte[_bootSector!.BytesPerSector];
            for (int s = 0; s < _bootSector.SectorsPerCluster; s++)
            {
                _device!.WriteBlock(BlockAddress.FromLBA(lba + (uint)s), zeroSector);
            }

            return cluster;
        }

        private byte[] ReadFileData(ushort startCluster, uint fileSize)
        {
            byte[] fileData = new byte[fileSize];
            int cluster = startCluster;
            uint bytesRemaining = fileSize;
            int written = 0;

            while (cluster >= Fat12FirstDataCluster && cluster < Fat12ReservedThreshold && bytesRemaining > 0)
            {
                uint lba = ClusterToLBA((ushort)cluster);

                for (int i = 0; i < _bootSector!.SectorsPerCluster && bytesRemaining > 0; i++)
                {
                    byte[] sectorData = new byte[_bootSector.BytesPerSector];
                    _device!.ReadBlock(BlockAddress.FromLBA(lba + (uint)i), sectorData);

                    int bytesToCopy = (int)Math.Min(bytesRemaining, (uint)_bootSector.BytesPerSector);
                    Array.Copy(sectorData, 0, fileData, written, bytesToCopy);
                    written += bytesToCopy;
                    bytesRemaining -= (uint)bytesToCopy;
                }

                cluster = ReadFatEntry(cluster);
            }

            return fileData;
        }

        private uint ClusterToLBA(ushort cluster)
        {
            return _dataStart + (uint)((cluster - Fat12FirstDataCluster) * _bootSector!.SectorsPerCluster);
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
            int cluster = dir.StartCluster;
            while (cluster >= Fat12FirstDataCluster && cluster < Fat12ReservedThreshold)
            {
                uint lba = ClusterToLBA((ushort)cluster);
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

                    Fat12DirectoryEntry entry = ParseDirectoryEntry(sectorData, i);
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

                    Fat12DirectoryEntry entry = ParseDirectoryEntry(sectorData, i);
                    if (string.Equals(entry.FileName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return new RawEntry { Lba = lbas[s], Offset = i, Entry = entry };
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Finds a reusable deleted (0xE5) or end-of-directory (0x00) slot for a new entry.
        /// For a subdirectory that's completely full, <paramref name="allowGrow"/> allocates and
        /// links a new cluster (only the fixed-size FAT12/16 root cannot grow this way).
        /// </summary>
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

            // Extend the subdirectory's cluster chain by one cluster.
            uint lastCluster = FindLastCluster(dir.StartCluster);
            ushort newCluster = FindFreeCluster();
            WriteFatEntry((int)lastCluster, newCluster);
            WriteFatEntry(newCluster, Fat12EndOfChain);

            uint newLba = ClusterToLBA(newCluster);
            byte[] zeroSector = new byte[_bootSector!.BytesPerSector];
            for (int s = 0; s < _bootSector.SectorsPerCluster; s++)
            {
                _device!.WriteBlock(BlockAddress.FromLBA(newLba + (uint)s), zeroSector);
            }

            return new EntrySlot { Lba = newLba, Offset = 0 };
        }

        private uint FindLastCluster(ushort startCluster)
        {
            int cluster = startCluster;
            int next = ReadFatEntry(cluster);
            while (next >= Fat12FirstDataCluster && next < Fat12ReservedThreshold)
            {
                cluster = next;
                next = ReadFatEntry(cluster);
            }

            return (uint)cluster;
        }

        private void WriteDotEntries(ushort newCluster, DirCursor parent)
        {
            uint lba = ClusterToLBA(newCluster);
            byte[] sector = new byte[_bootSector!.BytesPerSector];

            uint parentCluster = parent.IsRoot ? 0u : parent.StartCluster;
            WriteRawEntryBytes(sector, 0, ".", DirectoryAttribute, newCluster, 0);
            WriteRawEntryBytes(sector, DirEntrySize, "..", DirectoryAttribute, (ushort)parentCluster, 0);

            _device!.WriteBlock(BlockAddress.FromLBA(lba), sector);
        }

        // ===================================================================
        // DIRECTORY ENTRY BYTE LAYOUT
        // ===================================================================

        private Fat12DirectoryEntry ParseDirectoryEntry(byte[] data, int offset)
        {
            string filename = Encoding.ASCII.GetString(data, offset, 8).Trim();
            string extension = Encoding.ASCII.GetString(data, offset + 8, 3).Trim();
            string fullName = string.IsNullOrEmpty(extension) ? filename : $"{filename}.{extension}";

            byte attributes = data[offset + 11];
            ushort startCluster = (ushort)(data[offset + 26] | (data[offset + 27] << 8));
            uint fileSize = (uint)(data[offset + 28] | (data[offset + 29] << 8) | (data[offset + 30] << 16) | (data[offset + 31] << 24));

            return new Fat12DirectoryEntry
            {
                FileName = fullName,
                Attributes = attributes,
                IsDirectory = (attributes & DirectoryAttribute) != 0,
                StartCluster = startCluster,
                FileSize = fileSize
            };
        }

        private void WriteNewEntry(EntrySlot slot, string name, byte attributes, ushort startCluster, uint fileSize)
        {
            byte[] sector = new byte[_bootSector!.BytesPerSector];
            _device!.ReadBlock(BlockAddress.FromLBA(slot.Lba), sector);
            WriteRawEntryBytes(sector, slot.Offset, name, attributes, startCluster, fileSize);
            _device.WriteBlock(BlockAddress.FromLBA(slot.Lba), sector);
        }

        private void WriteEntryFields(uint lba, int offset, ushort startCluster, uint fileSize, byte attributes)
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

        private static void WriteRawEntryBytes(byte[] sector, int offset, string name, byte attributes, ushort startCluster, uint fileSize)
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

        /// <summary>
        /// Validates that <paramref name="name"/> fits an 8.3 short name. This implementation
        /// deliberately does not support VFAT/LFN write (only LFN read-skip) — see
        /// ai-docs/implement-fat.md §5 non-goals.
        /// </summary>
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

        private static FileMetadata ToMetadata(Fat12DirectoryEntry entry)
        {
            return new FileMetadata
            {
                Name = entry.FileName,
                Path = "/" + entry.FileName,
                IsDirectory = entry.IsDirectory,
                Size = entry.FileSize,
                FileType = entry.IsDirectory ? "DIR" : "FILE",
                Attributes = entry.Attributes.ToString(),
                UrnIdentifier = UrnIdentifier.Encode(entry.FileName)
            };
        }

        private void ReadVolumeLabel()
        {
            // Read first sector of root directory
            byte[] rootDir = new byte[512];
            _device!.ReadBlock(BlockAddress.FromLBA(_rootDirStart), rootDir);

            // Look for volume label entry (attribute = 0x08)
            for (int i = 0; i < 512; i += DirEntrySize)
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
                throw new InvalidOperationException("Filesystem not initialized");
            }
        }

        // ===================================================================
        // HELPER TYPES
        // ===================================================================

        private sealed class FatBootSector
        {
            public ushort BytesPerSector { get; set; }
            public byte SectorsPerCluster { get; set; }
            public ushort ReservedSectors { get; set; }
            public byte NumberOfFATs { get; set; }
            public ushort RootEntryCount { get; set; }
            public ushort TotalSectors { get; set; }
            public byte MediaDescriptor { get; set; }
            public ushort SectorsPerFAT { get; set; }
        }

        private sealed class Fat12DirectoryEntry
        {
            public string FileName { get; set; } = string.Empty;
            public byte Attributes { get; set; }
            public bool IsDirectory { get; set; }
            public ushort StartCluster { get; set; }
            public uint FileSize { get; set; }
        }

        /// <summary>Identifies a directory to scan: either the fixed-size root, or a subdirectory's starting cluster.</summary>
        private readonly struct DirCursor
        {
            public bool IsRoot { get; }
            public ushort StartCluster { get; }

            private DirCursor(bool isRoot, ushort startCluster)
            {
                IsRoot = isRoot;
                StartCluster = startCluster;
            }

            public static DirCursor Root => new DirCursor(true, 0);
            public static DirCursor Sub(ushort startCluster) => new DirCursor(false, startCluster);
        }

        /// <summary>The on-disk location and parsed content of a directory entry, for in-place patching.</summary>
        private readonly struct RawEntry
        {
            public uint Lba { get; init; }
            public int Offset { get; init; }
            public Fat12DirectoryEntry Entry { get; init; }
        }

        private readonly struct EntrySlot
        {
            public uint Lba { get; init; }
            public int Offset { get; init; }
        }
    }
}
