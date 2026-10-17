//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.BlockDevices
{
    /// <summary>
    /// Block device wrapper that provides access to a specific partition within a larger disk image.
    /// Transparently offsets all LBA read/write operations by the partition's starting sector.
    /// </summary>
    public sealed class PartitionBlockDevice : IBlockDevice
    {
        private readonly IBlockDevice _physicalDevice;
        private readonly uint _partitionStartLBA;
        private readonly uint _partitionSizeLBA;
        private readonly string _partitionId;

        /// <summary>
        /// Creates a partition block device wrapper.
        /// </summary>
        /// <param name="physicalDevice">
        /// The underlying physical block device containing the partition.
        /// </param>
        /// <param name="partitionStartLBA">
        /// Starting LBA sector of the partition (in 512-byte sectors).
        /// </param>
        /// <param name="partitionSizeLBA">
        /// Size of the partition in 512-byte sectors.
        /// </param>
        /// <param name="partitionId">
        /// Partition identifier (e.g., "0", "1", "a", "b") for error messages.
        /// </param>
        public PartitionBlockDevice(
            IBlockDevice physicalDevice,
            uint partitionStartLBA,
            uint partitionSizeLBA,
            string partitionId)
        {
            _physicalDevice = physicalDevice ?? throw new ArgumentNullException(nameof(physicalDevice));
            _partitionStartLBA = partitionStartLBA;
            _partitionSizeLBA = partitionSizeLBA;
            _partitionId = partitionId ?? throw new ArgumentNullException(nameof(partitionId));

            if (partitionSizeLBA == 0)
            {
                throw new ArgumentException("Partition size cannot be zero", nameof(partitionSizeLBA));
            }
        }

        /// <summary>
        /// Block size of the underlying physical device.
        /// </summary>
        public int BlockSize => _physicalDevice.BlockSize;

        /// <summary>
        /// Total number of blocks in this partition.
        /// </summary>
        public uint TotalBlocks => _partitionSizeLBA;

        /// <summary>
        /// True if the underlying physical device is mounted read-only.
        /// </summary>
        public bool IsReadOnly => _physicalDevice.IsReadOnly;

        /// <summary>
        /// True if the underlying physical device is mounted.
        /// </summary>
        public bool IsMounted => _physicalDevice.IsMounted;

        /// <summary>
        /// Always false - partitions have fixed geometry.
        /// </summary>
        public bool SupportsVariableGeometry => false;

        public int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            // Validate partition bounds
            uint lba = ConvertToLBA(address);
            if (lba >= _partitionSizeLBA)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(address),
                    $"Block address {lba} exceeds partition '{_partitionId}' size ({_partitionSizeLBA} sectors)");
            }

            // Offset by partition start and read from physical device
            uint physicalLBA = _partitionStartLBA + lba;
            return _physicalDevice.ReadBlock(BlockAddress.FromLBA(physicalLBA), buffer);
        }

        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            // Validate partition bounds
            uint lba = ConvertToLBA(address);
            if (lba >= _partitionSizeLBA)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(address),
                    $"Block address {lba} exceeds partition '{_partitionId}' size ({_partitionSizeLBA} sectors)");
            }

            // Offset by partition start and write to physical device
            uint physicalLBA = _partitionStartLBA + lba;
            _physicalDevice.WriteBlock(BlockAddress.FromLBA(physicalLBA), data);
        }

        public BlockDeviceInfo GetBlockDeviceInfo()
        {
            var physicalInfo = _physicalDevice.GetBlockDeviceInfo();

            // Return partition-specific info
            return new BlockDeviceInfo
            {
                AddressMode = BlockAddressMode.LBA, // Partitions always use LBA
                BlockSize = BlockSize,
                TrackCount = 0, // Not applicable for partitions
                CylinderCount = 0, // Not applicable for partitions
                SectorsPerTrack = 0, // Not applicable for partitions
                TotalSize = (long)_partitionSizeLBA * BlockSize,
                TotalBlocks = _partitionSizeLBA,
                GeometryDescription = $"Partition '{_partitionId}' ({_partitionSizeLBA} sectors, {(_partitionSizeLBA * BlockSize) / 1048576.0:F1} MB)"
            };
        }

        public int GetBlocksPerTrack(int track)
        {
            throw new NotSupportedException("Track-based geometry not supported for partitions. Use LBA addressing.");
        }

        public void Mount(Stream imageStream, bool readOnly)
        {
            // Mounting handled by underlying physical device
            // Partition wrappers are created after the physical device is already mounted
            // This method should not be called directly on partition devices
            throw new InvalidOperationException(
                "Cannot mount a partition device directly. " +
                "The underlying physical device must be mounted first, then create the partition wrapper.");
        }

        public void Flush()
        {
            // Delegate flush to the physical device
            _physicalDevice.Flush();
        }

        public void Dispose()
        {
            // Do NOT dispose the physical device - it may be shared by multiple partition wrappers
            // Physical device lifecycle managed externally
        }

        /// <summary>
        /// Converts any block address to LBA for partition boundary checking.
        /// </summary>
        private uint ConvertToLBA(BlockAddress address)
        {
            return address.Mode switch
            {
                BlockAddressMode.LBA => address.LBA,
                BlockAddressMode.TrackSector => throw new NotSupportedException(
                    "Track/Sector addressing not supported for partitions. Use LBA addressing."),
                BlockAddressMode.CylinderHeadSector => throw new NotSupportedException(
                    "CHS addressing not supported for partitions. Use LBA addressing."),
                _ => throw new ArgumentException($"Unsupported addressing mode: {address.Mode}", nameof(address))
            };
        }
    }
}
