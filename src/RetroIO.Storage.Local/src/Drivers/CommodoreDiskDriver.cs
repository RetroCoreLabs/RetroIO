//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Unified driver for Commodore disk images (D64, D71, D81).
    /// Thin wrapper that composes GenericBlockDevice + CbmDosFileSystem.
    /// Replaces separate D64Driver, D71Driver, and D81Driver classes.
    /// </summary>
    public sealed class CommodoreDiskDriver : IFileSystemDriver
    {
        private GenericBlockDevice? _blockDevice;
        private CbmDosFileSystem? _filesystem;
        private BlockDeviceDescriptor? _descriptor;

        /// <inheritdoc/>
        public string FileSystemType => _filesystem?.FileSystemType ?? "CBM-DOS";
        /// <inheritdoc/>
        public string VolumeLabel => _filesystem?.VolumeLabel ?? string.Empty;
        /// <inheritdoc/>
        public string ExtensionSeparator => _filesystem?.ExtensionSeparator ?? "";

        /// <inheritdoc/>
        public DriverCapabilities Capabilities
        {
            get
            {
                DriverCapabilities caps = DriverCapabilities.Read |
                                          DriverCapabilities.ListDirectory |
                                          DriverCapabilities.FileTypes |
                                          DriverCapabilities.PetsciiEncoding |
                                          DriverCapabilities.FileLocking |
                                          DriverCapabilities.VolumeLabel |
                                          DriverCapabilities.BlockRead |
                                          DriverCapabilities.BlockAllocationMap;

                // D81 supports subdirectories
                if (_descriptor == BlockDeviceDescriptor.D81)
                {
                    caps |= DriverCapabilities.Subdirectories;
                }

                // Write is available for the implemented formats (D64/D81) on a read-write mount.
                if (CanWrite)
                {
                    caps |= DriverCapabilities.Write;
                }

                return caps;
            }
        }

        /// <inheritdoc/>
        public bool CanRead => true;

        // Writable when mounted read-write. D64, D71 and D81 write paths are all implemented and
        // validated against c1541.
        /// <summary>
        /// Gets true when the image is mounted read-write.
        /// </summary>
        public bool CanWrite => _blockDevice != null && !_blockDevice.IsReadOnly && _descriptor != null;

        /// <inheritdoc/>
        public bool HasBlockAccess => true;

        /// <summary>
        /// Returns null, because this driver has no metadata descriptor yet.
        /// </summary>
        /// <returns>
        /// Always null.
        /// </returns>
        public IFileSystemMetadataDescriptor? GetMetadataDescriptor()
        {
            // TODO: Implement Commodore metadata descriptor (file type, record length, locked flag)
            return null;
        }

        /// <summary>
        /// Picks the disk format from the image size, mounts the block device and initializes the CBM DOS file system.
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
        /// Thrown when the image size matches no D64, D71 or D81 size.
        /// </exception>
        public void Mount(Stream imageStream, bool readOnly)
        {
            if (imageStream == null)
            {
                throw new ArgumentNullException(nameof(imageStream));
            }

            // 1. Detect descriptor from image size
            _descriptor = DetectDescriptor(imageStream.Length);

            // 2. Mount block device with descriptor
            _blockDevice = new GenericBlockDevice(_descriptor);
            _blockDevice.Mount(imageStream, readOnly);

            // 3. Determine directory/BAM track based on format
            // Note: Compare by name since BlockDeviceDescriptor properties return new instances
            int directoryTrack;
            int bamTrack;

            if (_descriptor.Name.Contains("D81"))
            {
                directoryTrack = 40;
                bamTrack = 40;
            }
            else
            {
                directoryTrack = 18;
                bamTrack = 18;
            }

            // 4. Mount filesystem
            _filesystem = new CbmDosFileSystem(directoryTrack, bamTrack);
            _filesystem.Initialize(_blockDevice);
        }

        /// <summary>
        /// Detects the correct descriptor based on image size.
        /// </summary>
        private BlockDeviceDescriptor DetectDescriptor(long imageSize)
        {
            // Check for exact match or match with error table
            return imageSize switch
            {
                174848 => BlockDeviceDescriptor.D64_35Track,
                175531 => BlockDeviceDescriptor.D64_35Track, // With error table (683 bytes)
                196608 => BlockDeviceDescriptor.D64_40Track,
                197376 => BlockDeviceDescriptor.D64_40Track, // With error table (768 bytes)
                205312 => BlockDeviceDescriptor.D64_42Track,
                206114 => BlockDeviceDescriptor.D64_42Track, // With error table (802 bytes)
                349696 => BlockDeviceDescriptor.D71,
                351062 => BlockDeviceDescriptor.D71, // With error table (1366 bytes)
                819200 => BlockDeviceDescriptor.D81,
                822400 => BlockDeviceDescriptor.D81, // With error table (3200 bytes)
                _ => throw new InvalidDataException(
                    $"Unknown Commodore disk image size: {imageSize} bytes. " +
                    "Supported formats: D64 (35/40/42 tracks), D71 (70 tracks), D81 (80 tracks).")
            };
        }

        // ===================================================================
        // FILE OPERATIONS - Delegate to Filesystem
        // ===================================================================

        /// <inheritdoc/>
        public byte[] ReadFile(string path)
        {
            EnsureMounted();
            return _filesystem!.ReadFile(path);
        }

        /// <inheritdoc/>
        public void WriteFile(string path, byte[] data)
        {
            EnsureMounted();
            _filesystem!.WriteFile(path, data);
        }

        /// <inheritdoc/>
        public void DeleteFile(string path)
        {
            EnsureMounted();
            _filesystem!.DeleteFile(path);
        }

        /// <inheritdoc/>
        public void RenameFile(string oldPath, string newPath)
        {
            EnsureMounted();
            _filesystem!.Rename(oldPath, newPath);
        }

        /// <inheritdoc/>
        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureMounted();
            return _filesystem!.ListDirectory(path);
        }

        /// <inheritdoc/>
        public FileMetadata? GetMetadata(string path)
        {
            EnsureMounted();
            return _filesystem!.GetMetadata(path);
        }

        // ===================================================================
        // BLOCK OPERATIONS - Delegate to Block Device
        // ===================================================================

        /// <inheritdoc/>
        public int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            EnsureMounted();
            return _blockDevice!.ReadBlock(address, buffer);
        }

        /// <inheritdoc/>
        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            EnsureMounted();
            _blockDevice!.WriteBlock(address, data);
        }

        /// <inheritdoc/>
        public BlockDeviceInfo? GetBlockDeviceInfo()
        {
            EnsureMounted();
            return _blockDevice!.GetBlockDeviceInfo();
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
                throw new InvalidOperationException("Disk image not mounted");
            }
        }
    }
}
