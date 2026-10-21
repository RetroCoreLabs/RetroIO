//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Driver for Dragon and Color Computer DMK disk images (DragonDOS, DECB and OS-9 volumes).
    /// </summary>
    /// <remarks>
    /// The DMK file is decoded by <see cref="DmkDiskImage"/>, which also describes the format. The decoded sectors are handed to a
    /// <see cref="DragonDskDriver"/> behind a JVC header, so the choice of file system and everything after it is the DSK driver's.
    /// </remarks>
    public sealed class DragonDmkDriver : IFileSystemDriver
    {
        private readonly DragonDskDriver _inner = new DragonDskDriver();
        private MemoryStream? _sectors;

        /// <summary>
        /// Gets the file system type of the mounted volume.
        /// </summary>
        public string FileSystemType => _inner.FileSystemType;

        /// <summary>
        /// Gets the separator between a file name and its extension.
        /// </summary>
        public string ExtensionSeparator => _inner.ExtensionSeparator;

        /// <summary>
        /// Gets the volume label of the mounted volume.
        /// </summary>
        public string VolumeLabel => _inner.VolumeLabel;

        /// <summary>
        /// Gets the capability flags, the same as the DSK driver.
        /// </summary>
        public DriverCapabilities Capabilities => _inner.Capabilities;

        /// <summary>
        /// Gets <c>true</c>, because this driver reads files.
        /// </summary>
        public bool CanRead => _inner.CanRead;

        /// <summary>
        /// Gets <c>false</c>, because this driver has no write support.
        /// </summary>
        public bool CanWrite => _inner.CanWrite;

        /// <summary>
        /// Gets <c>true</c>, because the decoded sectors can be read as blocks.
        /// </summary>
        public bool HasBlockAccess => _inner.HasBlockAccess;

        /// <summary>
        /// Gets no metadata descriptor.
        /// </summary>
        /// <returns>
        /// Always null.
        /// </returns>
        public IFileSystemMetadataDescriptor? GetMetadataDescriptor()
        {
            return _inner.GetMetadataDescriptor();
        }

        /// <summary>
        /// Decodes the DMK image and mounts the volume on it.
        /// </summary>
        /// <param name="imageStream">
        /// The DMK file.
        /// </param>
        /// <param name="readOnly">
        /// Whether the image is mounted read-only.
        /// </param>
        /// <exception cref="InvalidDataException">
        /// Thrown when the image is damaged. See <see cref="DmkDiskImage.Parse(Stream)"/>.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// Thrown for a DMK disk that is not decoded. See <see cref="DmkDiskImage.Parse(Stream)"/>.
        /// </exception>
        public void Mount(Stream imageStream, bool readOnly)
        {
            DmkDiskImage image = DmkDiskImage.Parse(imageStream);
            _sectors = new MemoryStream(image.ToJvcImage(), false);
            _inner.Mount(_sectors, readOnly);
        }

        /// <summary>
        /// Reads a whole file from the mounted volume.
        /// </summary>
        /// <param name="path">
        /// The file.
        /// </param>
        /// <returns>
        /// The bytes of the file.
        /// </returns>
        public byte[] ReadFile(string path)
        {
            return _inner.ReadFile(path);
        }

        /// <summary>
        /// Always throws UnauthorizedAccessException: the driver has no write support, so the volume is read-only.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <param name="data">
        /// Not used.
        /// </param>
        /// <exception cref="UnauthorizedAccessException">
        /// Always thrown.
        /// </exception>
        public void WriteFile(string path, byte[] data)
        {
            _inner.WriteFile(path, data);
        }

        /// <summary>
        /// Always throws UnauthorizedAccessException: the driver has no write support, so the volume is read-only.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <exception cref="UnauthorizedAccessException">
        /// Always thrown.
        /// </exception>
        public void DeleteFile(string path)
        {
            _inner.DeleteFile(path);
        }

        /// <summary>
        /// Lists a directory of the mounted volume.
        /// </summary>
        /// <param name="path">
        /// The directory.
        /// </param>
        /// <returns>
        /// The entries of the directory.
        /// </returns>
        public DirectoryEntry[] ListDirectory(string path)
        {
            return _inner.ListDirectory(path);
        }

        /// <summary>
        /// Gets the details of a file or directory of the mounted volume.
        /// </summary>
        /// <param name="path">
        /// The file or directory.
        /// </param>
        /// <returns>
        /// The metadata, or null when the path does not exist.
        /// </returns>
        public FileMetadata? GetMetadata(string path)
        {
            return _inner.GetMetadata(path);
        }

        /// <summary>
        /// Gets the number of free sectors of the mounted volume.
        /// </summary>
        /// <returns>
        /// The free sectors.
        /// </returns>
        public int GetBlocksFree()
        {
            return _inner.GetBlocksFree();
        }

        /// <summary>
        /// Reads one decoded sector.
        /// </summary>
        /// <param name="address">
        /// The logical block address of the sector.
        /// </param>
        /// <param name="buffer">
        /// Receives the 256 bytes of the sector.
        /// </param>
        /// <returns>
        /// The number of bytes read.
        /// </returns>
        public int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            return _inner.ReadBlock(address, buffer);
        }

        /// <summary>
        /// Always throws: the driver has no write support, so the volume is read-only.
        /// </summary>
        /// <param name="address">
        /// Not used.
        /// </param>
        /// <param name="data">
        /// Not used.
        /// </param>
        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            _inner.WriteBlock(address, data);
        }

        /// <summary>
        /// Gets the geometry of the decoded disk.
        /// </summary>
        /// <returns>
        /// The block device information.
        /// </returns>
        public BlockDeviceInfo? GetBlockDeviceInfo()
        {
            return _inner.GetBlockDeviceInfo();
        }

        /// <summary>
        /// Releases the volume and the decoded sectors.
        /// </summary>
        public void Dispose()
        {
            _inner.Dispose();
            _sectors?.Dispose();
            _sectors = null;
        }
    }
}
