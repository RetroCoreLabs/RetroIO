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
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FileSystems;
using RetroIO.Storage.Local.MBR.Structures;
using RetroIO.Storage.Local.Streams;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Driver for PC floppy disk images and PC hard disk images (MBR-partitioned) with
    /// FAT12/FAT16/FAT32 filesystems. Supports 360KB, 720KB, 1.2MB, 1.44MB, 2.88MB floppy formats,
    /// plus arbitrary-size FAT partitions carved out of a larger hard disk image via
    /// <see cref="IPartitionProvider"/> — mirrors how <see cref="V7Driver"/> already handles BSD
    /// disk labels.
    /// Thin wrapper that composes GenericBlockDevice + Fat12FileSystem/Fat16FileSystem/Fat32FileSystem.
    /// </summary>
    public sealed class PcFloppyDriver : IFileSystemDriver, IPartitionProvider
    {
        private GenericBlockDevice? _blockDevice;
        private IBlockFileSystem? _filesystem;
        private BlockDeviceDescriptor? _descriptor;

        // MBR partition state — set via IPartitionProvider before Mount(), same pattern as
        // BsdDriver's _partitionLetter/_useDiskLabel. null = whole image is the filesystem
        // (unchanged default behavior for plain floppy images, no MBR involved at all).
        private int? _selectedMbrPartitionIndex;

        // Kept so ListPartitions()/SupportsPartitions() can re-read the MBR after Mount() has
        // already substituted _blockDevice for a partition-scoped sub-view.
        private Stream? _rawStream;

        public string FileSystemType => _filesystem?.FileSystemType ?? "FAT";
        public string VolumeLabel => _filesystem?.VolumeLabel ?? string.Empty;
        public string ExtensionSeparator => _filesystem?.ExtensionSeparator ?? ".";

        public DriverCapabilities Capabilities
        {
            get
            {
                return DriverCapabilities.Read |
                       DriverCapabilities.Write |
                       DriverCapabilities.Delete |
                       DriverCapabilities.Rename |
                       DriverCapabilities.ListDirectory |
                       DriverCapabilities.Subdirectories |
                       DriverCapabilities.CreateDirectory |
                       DriverCapabilities.FileTypes |
                       DriverCapabilities.VolumeLabel |
                       DriverCapabilities.BlockRead |
                       DriverCapabilities.BlockWrite;
                // Note: DeleteDirectory is deliberately not advertised — IFileSystemDriver has no
                // DeleteDirectory member at all (verified via grep; same gap exists for every
                // other driver in this codebase, e.g. NdfsDriver only exposes DeleteFile). The
                // underlying Fat12/16/32FileSystem.DeleteDirectory is real and directly testable,
                // it's just not reachable through this driver yet, matching existing convention.
            }
        }

        public bool CanRead => true;
        public bool CanWrite => true;
        public bool HasBlockAccess => true;

        public IFileSystemMetadataDescriptor? GetMetadataDescriptor()
        {
            // TODO: Implement PC FAT12 metadata descriptor
            return null;
        }

        public void Mount(Stream imageStream, bool readOnly)
        {
            if (imageStream == null)
            {
                throw new ArgumentNullException(nameof(imageStream));
            }

            _rawStream = imageStream;
            Stream fsStream = imageStream;

            if (_selectedMbrPartitionIndex.HasValue)
            {
                MbrPartitionEntry partition = ReadSelectedMbrPartition(imageStream, _selectedMbrPartitionIndex.Value);
                long offsetBytes = (long)partition.LbaStart * 512;
                long lengthBytes = (long)partition.NumSectors * 512;
                fsStream = new SubStream(imageStream, offsetBytes, lengthBytes);
            }

            // 1. Detect descriptor from image size
            _descriptor = DetectDescriptor(fsStream.Length);

            // 2. Mount block device with descriptor
            _blockDevice = new GenericBlockDevice(_descriptor);
            _blockDevice.Mount(fsStream, readOnly);

            // 3. Auto-detect and mount filesystem (FAT12 vs FAT16 vs FAT32)
            _filesystem = DetectFatFilesystem(_blockDevice);
            _filesystem.Initialize(_blockDevice);
        }

        /// <summary>
        /// Reads the MBR from the start of <paramref name="stream"/> and returns the requested
        /// partition entry, throwing a clear error if the MBR is invalid or the index doesn't
        /// exist — SelectPartition itself can't fully validate this since it's called before the
        /// stream is available (same constraint BsdDriver.SelectPartition documents).
        /// </summary>
        private static MbrPartitionEntry ReadSelectedMbrPartition(Stream stream, int partitionIndex)
        {
            long originalPosition = stream.Position;
            byte[] mbrBytes = new byte[512];
            stream.Position = 0;
            stream.ReadExactly(mbrBytes, 0, 512);
            stream.Position = originalPosition;

            MbrPartitionTable table = MbrPartitionTable.Parse(mbrBytes);
            if (!table.IsValid())
            {
                throw new InvalidDataException("No valid MBR partition table found on this image.");
            }

            if (partitionIndex < 0 || partitionIndex >= table.Partitions.Length || table.Partitions[partitionIndex].IsUnused)
            {
                throw new ArgumentException($"MBR partition {partitionIndex} does not exist on this image.");
            }

            return table.Partitions[partitionIndex];
        }

        /// <summary>
        /// Detects the correct descriptor based on image size. Known floppy sizes get their exact
        /// CHS geometry; anything else (a FAT partition carved out of a larger PC hard disk image
        /// via VolumeProviderFactory's MBR support, for example) falls back to a generic LBA-only
        /// descriptor — the FAT filesystem layer only ever addresses blocks via
        /// BlockAddress.FromLBA, so CHS geometry was never actually required for correctness, only
        /// for the fixed floppy presets that happened to use it.
        /// </summary>
        private BlockDeviceDescriptor DetectDescriptor(long imageSize)
        {
            return imageSize switch
            {
                368640 => BlockDeviceDescriptor.PC_360K,   // 40*2*9*512
                737280 => BlockDeviceDescriptor.PC_720K,   // 80*2*9*512
                1228800 => BlockDeviceDescriptor.PC_1_2M,  // 80*2*15*512
                1474560 => BlockDeviceDescriptor.PC_1_44M, // 80*2*18*512
                2949120 => BlockDeviceDescriptor.PC_2_88M, // 80*2*36*512
                _ when imageSize > 0 && imageSize % 512 == 0 => new BlockDeviceDescriptor
                {
                    Name = $"Generic PC FAT volume ({imageSize} bytes)",
                    BlockSize = 512,
                    AddressMode = BlockAddressMode.LBA,
                    TotalBlocks = (uint)(imageSize / 512)
                },
                _ => throw new InvalidDataException(
                    $"Unknown PC floppy image size: {imageSize} bytes. " +
                    "Supported floppy formats: 360KB, 720KB, 1.2MB, 1.44MB, 2.88MB; " +
                    "any other size must be a whole number of 512-byte sectors.")
            };
        }

        /// <summary>
        /// Detects filesystem type by reading boot sector.
        /// </summary>
        private IBlockFileSystem DetectFatFilesystem(IBlockDevice device)
        {
            // Read boot sector
            byte[] bootSector = new byte[512];
            device.ReadBlock(BlockAddress.FromLBA(0), bootSector);

            // Check filesystem type at offset 0x36 (FAT12/FAT16)
            string fsType = Encoding.ASCII.GetString(bootSector, 0x36, 8).Trim();

            if (fsType.StartsWith("FAT12"))
            {
                return new Fat12FileSystem();
            }

            if (fsType.StartsWith("FAT16"))
            {
                return new Fat16FileSystem();
            }

            if (fsType.StartsWith("FAT32"))
            {
                return new Fat32FileSystem();
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

            // Determine FAT type by cluster count
            // FAT12: < 4085 clusters
            // FAT16: 4085 - 65524 clusters
            // FAT32: >= 65525 clusters
            if (clusterCount < 4085)
            {
                return new Fat12FileSystem();
            }
            else if (clusterCount < 65525)
            {
                return new Fat16FileSystem();
            }
            else
            {
                return new Fat32FileSystem();
            }
        }

        // ===================================================================
        // FILE OPERATIONS - Delegate to Filesystem
        // ===================================================================

        public byte[] ReadFile(string path)
        {
            EnsureMounted();
            return _filesystem!.ReadFile(path);
        }

        public void WriteFile(string path, byte[] data)
        {
            EnsureMounted();
            _filesystem!.WriteFile(path, data);
        }

        public void DeleteFile(string path)
        {
            EnsureMounted();
            _filesystem!.DeleteFile(path);
        }

        public void CreateDirectory(string path)
        {
            EnsureMounted();
            _filesystem!.CreateDirectory(path);
        }

        public void RenameFile(string oldPath, string newPath)
        {
            EnsureMounted();
            _filesystem!.Rename(oldPath, newPath);
        }

        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureMounted();
            return _filesystem!.ListDirectory(path);
        }

        public FileMetadata? GetMetadata(string path)
        {
            EnsureMounted();
            return _filesystem!.GetMetadata(path);
        }

        // ===================================================================
        // BLOCK OPERATIONS - Delegate to Block Device
        // ===================================================================

        public int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            EnsureMounted();
            return _blockDevice!.ReadBlock(address, buffer);
        }

        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            EnsureMounted();
            _blockDevice!.WriteBlock(address, data);
        }

        public BlockDeviceInfo? GetBlockDeviceInfo()
        {
            EnsureMounted();
            return _blockDevice!.GetBlockDeviceInfo();
        }

        // ===================================================================
        // MBR PARTITION SUPPORT (IPartitionProvider)
        // ===================================================================

        public bool SupportsPartitions()
        {
            if (_rawStream == null)
            {
                return false;
            }

            return TryReadMbrTable(_rawStream, out MbrPartitionTable? table) && table!.IsValid();
        }

        public IReadOnlyList<PartitionMetadata> ListPartitions()
        {
            var result = new List<PartitionMetadata>();

            if (_rawStream == null || !TryReadMbrTable(_rawStream, out MbrPartitionTable? table) || !table!.IsValid())
            {
                return result;
            }

            for (int i = 0; i < table.Partitions.Length; i++)
            {
                MbrPartitionEntry entry = table.Partitions[i];
                if (entry.IsUnused)
                {
                    continue;
                }

                double sizeMB = (entry.NumSectors * 512.0) / (1024.0 * 1024.0);
                string bootableFlag = entry.IsBootable ? " [Bootable]" : "";

                result.Add(new PartitionMetadata
                {
                    Id = i.ToString(),
                    DisplayText = $"{i} - {entry.TypeName} ({sizeMB:F1} MB){bootableFlag}",
                    OffsetSectors = entry.LbaStart,
                    SizeSectors = entry.NumSectors,
                    FileSystemTypeCode = entry.PartitionType,
                    RecommendedUrn = string.Empty, // path unknown at this layer; caller fills it in
                    IsBootable = entry.IsBootable,
                    TableType = PartitionTableType.Mbr
                });
            }

            return result;
        }

        /// <summary>
        /// Selects an MBR partition by numeric index (e.g. "0"). Must be called before Mount() —
        /// mirrors BsdDriver.SelectPartition's contract exactly. If the stream isn't available
        /// yet (the normal case — VolumeProviderFactory always selects before opening the image),
        /// this only validates that the string is a plausible index and defers real existence
        /// checking to Mount(), which throws a clear error if the partition turns out not to exist.
        /// </summary>
        public bool SelectPartition(string partitionId)
        {
            if (!int.TryParse(partitionId, out int index) || index < 0 || index > 3)
            {
                return false;
            }

            if (_rawStream != null)
            {
                if (!TryReadMbrTable(_rawStream, out MbrPartitionTable? table) || !table!.IsValid() ||
                    index >= table.Partitions.Length || table.Partitions[index].IsUnused)
                {
                    return false;
                }
            }

            _selectedMbrPartitionIndex = index;
            return true;
        }

        public string? CurrentPartition => _selectedMbrPartitionIndex?.ToString();

        public void SetRawMode()
        {
            _selectedMbrPartitionIndex = null;
        }

        private static bool TryReadMbrTable(Stream stream, out MbrPartitionTable? table)
        {
            try
            {
                long originalPosition = stream.Position;
                byte[] mbrBytes = new byte[512];
                stream.Position = 0;
                stream.ReadExactly(mbrBytes, 0, 512);
                stream.Position = originalPosition;

                table = MbrPartitionTable.Parse(mbrBytes);
                return true;
            }
            catch
            {
                table = null;
                return false;
            }
        }

        // ===================================================================
        // LIFECYCLE
        // ===================================================================

        public void Dispose()
        {
            _filesystem = null;
            _blockDevice?.Dispose();
            _blockDevice = null;
        }

        private void EnsureMounted()
        {
            if (_blockDevice == null || _filesystem == null)
            {
                throw new InvalidOperationException("Floppy image not mounted");
            }
        }
    }
}
