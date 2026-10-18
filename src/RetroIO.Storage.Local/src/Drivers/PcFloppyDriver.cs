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

        /// <summary>
        /// Gets the type of the mounted FAT file system (<c>FAT12</c>, <c>FAT16</c> or <c>FAT32</c>), or <c>FAT</c> when nothing is mounted.
        /// </summary>
        public string FileSystemType => _filesystem?.FileSystemType ?? "FAT";
        /// <summary>
        /// Gets the volume label of the mounted FAT file system, or an empty string when nothing is mounted.
        /// </summary>
        public string VolumeLabel => _filesystem?.VolumeLabel ?? string.Empty;
        /// <summary>
        /// Gets <c>.</c>, the separator between file name and extension in FAT.
        /// </summary>
        public string ExtensionSeparator => _filesystem?.ExtensionSeparator ?? ".";

        /// <summary>
        /// Gets the capability flags of this driver.
        /// </summary>
        /// <remarks>
        /// The flags are read, write, delete, rename, directory listing, subdirectories, create directory,
        /// file types, volume label, block read and block write. The flags do not change with a read-only mount.
        /// </remarks>
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

        /// <summary>
        /// Gets <c>true</c>, because this driver reads files.
        /// </summary>
        public bool CanRead => true;
        /// <summary>
        /// Gets true, because the driver writes through the FAT file system.
        /// </summary>
        public bool CanWrite => true;
        /// <summary>
        /// Gets <c>true</c>, because this driver can read and write raw blocks of the mounted image or partition.
        /// </summary>
        public bool HasBlockAccess => true;

        /// <summary>
        /// Returns null, because this driver has no metadata descriptor yet.
        /// </summary>
        /// <returns>
        /// Always null.
        /// </returns>
        public IFileSystemMetadataDescriptor? GetMetadataDescriptor()
        {
            // TODO: Implement PC FAT12 metadata descriptor
            return null;
        }

        /// <summary>
        /// Mounts the image, or the selected MBR partition, as a block device and initializes a FAT12, FAT16 or FAT32 file system, chosen from the cluster count in the boot sector.
        /// </summary>
        /// <param name="imageStream">
        /// The stream holding the disk image.
        /// </param>
        /// <param name="readOnly">
        /// True to mount read-only.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="imageStream"/> is null.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// Thrown when a partition is selected and the image has no valid MBR.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when the selected MBR partition does not exist.
        /// </exception>
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

        /// <summary>
        /// Reads a whole file from the mounted FAT file system.
        /// </summary>
        /// <param name="path">
        /// Path of the file.
        /// </param>
        /// <returns>
        /// The file contents.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public byte[] ReadFile(string path)
        {
            EnsureMounted();
            return _filesystem!.ReadFile(path);
        }

        /// <summary>
        /// Writes a file to the mounted FAT file system.
        /// </summary>
        /// <param name="path">
        /// Path of the file.
        /// </param>
        /// <param name="data">
        /// The file contents.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public void WriteFile(string path, byte[] data)
        {
            EnsureMounted();
            _filesystem!.WriteFile(path, data);
        }

        /// <summary>
        /// Deletes a file from the mounted FAT file system.
        /// </summary>
        /// <param name="path">
        /// Path of the file.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public void DeleteFile(string path)
        {
            EnsureMounted();
            _filesystem!.DeleteFile(path);
        }

        /// <summary>
        /// Creates a directory in the mounted FAT file system.
        /// </summary>
        /// <param name="path">
        /// Path of the directory to create.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public void CreateDirectory(string path)
        {
            EnsureMounted();
            _filesystem!.CreateDirectory(path);
        }

        /// <summary>
        /// Renames a file or directory in the mounted FAT file system.
        /// </summary>
        /// <param name="oldPath">
        /// Current path.
        /// </param>
        /// <param name="newPath">
        /// New path.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public void RenameFile(string oldPath, string newPath)
        {
            EnsureMounted();
            _filesystem!.Rename(oldPath, newPath);
        }

        /// <summary>
        /// Lists the contents of a directory in the mounted FAT file system.
        /// </summary>
        /// <param name="path">
        /// Path of the directory.
        /// </param>
        /// <returns>
        /// The directory entries.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureMounted();
            return _filesystem!.ListDirectory(path);
        }

        /// <summary>
        /// Gets metadata of a file or directory in the mounted FAT file system.
        /// </summary>
        /// <param name="path">
        /// Path of the file or directory.
        /// </param>
        /// <returns>
        /// The metadata, or <c>null</c> when the file system does not find <paramref name="path"/>.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public FileMetadata? GetMetadata(string path)
        {
            EnsureMounted();
            return _filesystem!.GetMetadata(path);
        }

        // ===================================================================
        // BLOCK OPERATIONS - Delegate to Block Device
        // ===================================================================

        /// <summary>
        /// Reads one 512-byte block from the block device.
        /// </summary>
        /// <remarks>
        /// When an MBR partition is mounted, the address counts from the start of that partition.
        /// </remarks>
        /// <param name="address">
        /// The block to read.
        /// </param>
        /// <param name="buffer">
        /// Receives the block data.
        /// </param>
        /// <returns>
        /// The number of bytes read.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            EnsureMounted();
            return _blockDevice!.ReadBlock(address, buffer);
        }

        /// <summary>
        /// Writes one 512-byte block through the block device.
        /// </summary>
        /// <remarks>
        /// When an MBR partition is mounted, the address counts from the start of that partition.
        /// </remarks>
        /// <param name="address">
        /// The block to write.
        /// </param>
        /// <param name="data">
        /// The block data.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            EnsureMounted();
            _blockDevice!.WriteBlock(address, data);
        }

        /// <summary>
        /// Gets the information of the block device the image or partition is mounted on.
        /// </summary>
        /// <returns>
        /// The block device information; never <c>null</c> once mounted.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public BlockDeviceInfo? GetBlockDeviceInfo()
        {
            EnsureMounted();
            return _blockDevice!.GetBlockDeviceInfo();
        }

        // ===================================================================
        // MBR PARTITION SUPPORT (IPartitionProvider)
        // ===================================================================

        /// <summary>
        /// Returns true when the first sector of the image holds a valid MBR partition table.
        /// </summary>
        /// <returns>
        /// True when a valid MBR is found, false otherwise or when no image is mounted yet.
        /// </returns>
        public bool SupportsPartitions()
        {
            if (_rawStream == null)
            {
                return false;
            }

            return TryReadMbrTable(_rawStream, out MbrPartitionTable? table) && table!.IsValid();
        }

        /// <summary>
        /// Lists the used partitions of the MBR partition table.
        /// </summary>
        /// <returns>
        /// One entry per used partition, or an empty list when there is no valid MBR.
        /// </returns>
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

        /// <summary>
        /// Gets the selected MBR partition number as text, or null when no partition is selected.
        /// </summary>
        public string? CurrentPartition => _selectedMbrPartitionIndex?.ToString();

        /// <summary>
        /// Clears the partition selection so the whole image is used as one file system.
        /// </summary>
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

        /// <summary>
        /// Releases the file system and disposes the block device.
        /// </summary>
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
