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
    /// Generic block device implementation that uses a descriptor for configuration.
    /// Replaces separate D64/D71/D81/PC floppy device classes.
    /// Handles all geometry types: Track/Sector (ZBR and uniform), CHS, and LBA.
    /// </summary>
    public sealed class GenericBlockDevice : BlockDeviceBase
    {
        private readonly BlockDeviceDescriptor _descriptor;

        /// <summary>
        /// Creates a generic block device with the specified descriptor.
        /// </summary>
        /// <param name="descriptor">
        /// Device geometry configuration
        /// </param>
        public GenericBlockDevice(BlockDeviceDescriptor descriptor)
        {
            _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        }

        /// <summary>
        /// Block size in bytes (from descriptor).
        /// </summary>
        public override int BlockSize => _descriptor.BlockSize;

        /// <summary>
        /// Total number of blocks on this device (from descriptor).
        /// </summary>
        public override uint TotalBlocks => _descriptor.TotalBlocks;

        /// <summary>
        /// True if this device supports variable geometry (from descriptor).
        /// </summary>
        public override bool SupportsVariableGeometry => _descriptor.SupportsVariableGeometry;

        /// <summary>
        /// Checks that the image size equals the size the descriptor expects, or that size plus one byte per block (an error table).
        /// </summary>
        /// <param name="imageSize">
        /// Size of the disk image in bytes.
        /// </param>
        /// <exception cref="InvalidDataException">
        /// Thrown when the size matches neither value.
        /// </exception>
        protected override void ValidateGeometry(long imageSize)
        {
            // Allow for error table variants (some D64 images have extra error bytes)
            long expectedSize = _descriptor.ExpectedImageSize;

            // Auto-calculate expected size if not explicitly set
            if (expectedSize == 0)
            {
                expectedSize = (long)_descriptor.TotalBlocks * _descriptor.BlockSize;
            }

            long expectedWithErrors = expectedSize + (_descriptor.TotalBlocks);

            if (imageSize != expectedSize && imageSize != expectedWithErrors)
            {
                throw new InvalidDataException(
                    $"Invalid image size for {_descriptor.Name}: {imageSize} bytes. " +
                    $"Expected {expectedSize} or {expectedWithErrors} bytes (with error table).");
            }
        }

        /// <summary>
        /// Copies one block from the in-memory image into the buffer.
        /// </summary>
        /// <param name="address">
        /// The block to read.
        /// </param>
        /// <param name="buffer">
        /// Receives the block data. It must hold at least one block.
        /// </param>
        /// <returns>
        /// The block size in bytes.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="buffer"/> is smaller than one block.
        /// </exception>
        public override int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            EnsureMounted();

            if (buffer.Length < BlockSize)
            {
                throw new ArgumentException(
                    $"Buffer too small. Required: {BlockSize}, Provided: {buffer.Length}",
                    nameof(buffer));
            }

            int offset = CalculateOffset(address);

            // Copy data to buffer
            _imageData.AsSpan(offset, BlockSize).CopyTo(buffer);

            return BlockSize;
        }

        /// <summary>
        /// Copies one block into the in-memory image.
        /// </summary>
        /// <param name="address">
        /// The block to write.
        /// </param>
        /// <param name="data">
        /// The block data. It must be exactly one block.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the device is read-only.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="data"/> is not exactly one block.
        /// </exception>
        public override void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            EnsureMounted();

            if (_readOnly)
            {
                throw new InvalidOperationException("Cannot write to read-only device");
            }

            if (data.Length != BlockSize)
            {
                throw new ArgumentException(
                    $"Invalid data size. Required: {BlockSize}, Provided: {data.Length}",
                    nameof(data));
            }

            int offset = CalculateOffset(address);

            // Write data
            data.CopyTo(_imageData.AsSpan(offset, BlockSize));
        }

        /// <summary>
        /// Returns the geometry of the device, taken from the descriptor.
        /// </summary>
        /// <returns>
        /// The device information.
        /// </returns>
        public override BlockDeviceInfo GetBlockDeviceInfo()
        {
            EnsureMounted();

            return new BlockDeviceInfo
            {
                AddressMode = _descriptor.AddressMode,
                BlockSize = BlockSize,
                TrackCount = (byte)(_descriptor.TotalTracks > 0 ? _descriptor.TotalTracks : _descriptor.Cylinders),
                CylinderCount = (ushort)_descriptor.Cylinders,
                SectorsPerTrack = (byte)(_descriptor.UniformSectorsPerTrack > 0
                    ? _descriptor.UniformSectorsPerTrack
                    : GetMaxSectorsPerTrack()),
                TotalSize = GetImageSize(),
                TotalBlocks = TotalBlocks,
                GeometryDescription = _descriptor.Name
            };
        }

        /// <summary>
        /// Returns the number of sectors on a track.
        /// </summary>
        /// <param name="track">
        /// The track number.
        /// </param>
        /// <returns>
        /// The sectors on that track, or the uniform sectors per track for a cylinder, head, sector device.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the track is outside the tracks of the device.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// Thrown when the descriptor uses another addressing mode.
        /// </exception>
        public override int GetBlocksPerTrack(int track)
        {
            EnsureMounted();

            if (_descriptor.AddressMode == BlockAddressMode.TrackSector)
            {
                if (track < 1 || track > _descriptor.TotalTracks)
                {
                    throw new ArgumentOutOfRangeException(nameof(track), $"Invalid track: {track}");
                }

                return _descriptor.GetSectorsPerTrack(track);
            }
            else if (_descriptor.AddressMode == BlockAddressMode.CylinderHeadSector)
            {
                return _descriptor.UniformSectorsPerTrack;
            }

            throw new NotSupportedException($"GetBlocksPerTrack not supported for addressing mode: {_descriptor.AddressMode}");
        }

        /// <summary>
        /// Calculates byte offset for the given block address based on descriptor's addressing mode.
        /// Automatically converts LBA to native mode if needed.
        /// </summary>
        private int CalculateOffset(BlockAddress address)
        {
            // If address mode matches descriptor mode, use directly
            if (address.Mode == _descriptor.AddressMode)
            {
                return _descriptor.AddressMode switch
                {
                    BlockAddressMode.TrackSector => CalculateTrackSectorOffset(address),
                    BlockAddressMode.CylinderHeadSector => CalculateCHSOffset(address),
                    BlockAddressMode.LBA => CalculateLBAOffset(address),
                    _ => throw new ArgumentException($"Unsupported addressing mode: {_descriptor.AddressMode}", nameof(address))
                };
            }

            // Auto-convert LBA to native mode
            if (address.Mode == BlockAddressMode.LBA)
            {
                uint lba = address.LBA;

                switch (_descriptor.AddressMode)
                {
                    case BlockAddressMode.CylinderHeadSector:
                        // Convert LBA to CHS
                        var (cylinder, head, sector) = LBAToCHS(lba, (byte)_descriptor.Heads, (byte)_descriptor.UniformSectorsPerTrack);
                        return CalculateCHSOffset(BlockAddress.FromCHS(cylinder, head, sector));

                    case BlockAddressMode.TrackSector:
                        // Convert LBA to Track/Sector (for uniform geometry)
                        int sectorsPerTrack = _descriptor.UniformSectorsPerTrack;
                        byte track = (byte)((lba / sectorsPerTrack) + 1); // Tracks start at 1
                        byte sect = (byte)(lba % sectorsPerTrack);
                        return CalculateTrackSectorOffset(BlockAddress.FromTrackSector(track, sect));

                    case BlockAddressMode.LBA:
                        return CalculateLBAOffset(address);

                    default:
                        throw new ArgumentException($"Cannot convert LBA to {_descriptor.AddressMode}", nameof(address));
                }
            }

            throw new ArgumentException(
                $"Address mode mismatch. Device expects {_descriptor.AddressMode}, got {address.Mode}. " +
                "Only LBA addresses can be auto-converted.", nameof(address));
        }

        /// <summary>
        /// Calculates offset for Track/Sector addressing (Commodore D64/D71/D81).
        /// </summary>
        private int CalculateTrackSectorOffset(BlockAddress address)
        {
            if (address.Mode != BlockAddressMode.TrackSector)
            {
                throw new ArgumentException(
                    $"Address mode mismatch. Expected TrackSector, got {address.Mode}",
                    nameof(address));
            }

            byte track = address.Track;
            byte sector = address.Sector;

            // Validate track
            if (track < 1 || track > _descriptor.TotalTracks)
            {
                throw new ArgumentException($"Invalid track: {track}");
            }

            // Validate sector
            int sectorsOnTrack = _descriptor.GetSectorsPerTrack(track);
            if (sector >= sectorsOnTrack)
            {
                throw new ArgumentException($"Invalid sector {sector} for track {track}");
            }

            // Calculate offset using base class helper (handles ZBR)
            if (_descriptor.SectorsPerTrackTable != null)
            {
                return GetTrackSectorOffset(track, sector, _descriptor.SectorsPerTrackTable, BlockSize);
            }
            else
            {
                // Uniform geometry - simple calculation
                return ((track - 1) * _descriptor.UniformSectorsPerTrack + sector) * BlockSize;
            }
        }

        /// <summary>
        /// Calculates offset for CHS addressing (PC floppies, Amiga ADF).
        /// </summary>
        private int CalculateCHSOffset(BlockAddress address)
        {
            if (address.Mode != BlockAddressMode.CylinderHeadSector)
            {
                throw new ArgumentException(
                    $"Address mode mismatch. Expected CylinderHeadSector, got {address.Mode}",
                    nameof(address));
            }

            ushort cylinder = address.Cylinder;
            byte head = address.Head;
            byte sector = address.Sector;

            // Validate CHS
            if (cylinder >= _descriptor.Cylinders)
            {
                throw new ArgumentException($"Invalid cylinder: {cylinder}");
            }

            if (head >= _descriptor.Heads)
            {
                throw new ArgumentException($"Invalid head: {head}");
            }

            if (sector >= _descriptor.UniformSectorsPerTrack)
            {
                throw new ArgumentException($"Invalid sector: {sector}");
            }

            // Use base class helper
            return GetCHSOffset(
                cylinder,
                head,
                sector,
                (byte)_descriptor.Heads,
                (byte)_descriptor.UniformSectorsPerTrack,
                BlockSize);
        }

        /// <summary>
        /// Calculates offset for LBA addressing.
        /// </summary>
        private int CalculateLBAOffset(BlockAddress address)
        {
            if (address.Mode != BlockAddressMode.LBA)
            {
                throw new ArgumentException(
                    $"Address mode mismatch. Expected LBA, got {address.Mode}",
                    nameof(address));
            }

            if (address.LBA >= TotalBlocks)
            {
                throw new ArgumentException($"Invalid LBA: {address.LBA}. Max LBA is {TotalBlocks - 1} (TotalBlocks: {TotalBlocks}, BlockSize: {BlockSize})");
            }

            return (int)(address.LBA * BlockSize);
        }

        /// <summary>
        /// Gets the maximum sectors per track (for variable geometry).
        /// </summary>
        private byte GetMaxSectorsPerTrack()
        {
            if (_descriptor.SectorsPerTrackTable == null)
            {
                return (byte)_descriptor.UniformSectorsPerTrack;
            }

            byte max = 0;
            for (int i = 1; i < _descriptor.SectorsPerTrackTable.Length; i++)
            {
                if (_descriptor.SectorsPerTrackTable[i] > max)
                {
                    max = (byte)_descriptor.SectorsPerTrackTable[i];
                }
            }
            return max;
        }
    }
}
