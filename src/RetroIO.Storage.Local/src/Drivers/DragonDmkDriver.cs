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
    /// <para><b>Writing</b></para>
    /// A change made through the DSK driver lands in the decoded sectors. After every write, delete, rename or block write this driver gives the
    /// decoded sectors back to <see cref="DmkDiskImage.SetSectorImage"/>, which rebuilds the sector headers, data and both CRCs of the changed
    /// sectors, and writes the DMK file back to the stream from its start. An image whose header marks it write protected is mounted read-only.
    /// </remarks>
    public sealed class DragonDmkDriver : IFileSystemDriver
    {
        private readonly DragonDskDriver _inner = new DragonDskDriver();
        private MemoryStream? _sectors;
        private DmkDiskImage? _image;
        private Stream? _fileStream;
        private bool _writable;

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
        /// Gets <c>true</c> when the image is mounted read-write, is not marked write protected and holds a volume that can be written.
        /// </summary>
        public bool CanWrite => _writable && _inner.CanWrite;

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

            // A disk the header marks write protected is never written, and a stream that cannot be written cannot take the changes back.
            _writable = !readOnly && !image.WriteProtected && imageStream.CanWrite && imageStream.CanSeek;
            _sectors = new MemoryStream(image.ToJvcImage(), _writable);
            _inner.Mount(_sectors, !_writable);
            _image = image;
            _fileStream = imageStream;
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
        /// Creates or replaces a file on the mounted volume and writes the DMK file back.
        /// </summary>
        /// <param name="path">
        /// The file.
        /// </param>
        /// <param name="data">
        /// The file contents.
        /// </param>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when nothing is mounted for writing or the volume cannot be written.
        /// </exception>
        public void WriteFile(string path, byte[] data)
        {
            _inner.WriteFile(path, data);
            SaveToFile();
        }

        /// <summary>
        /// Deletes a file from the mounted volume and writes the DMK file back.
        /// </summary>
        /// <param name="path">
        /// The file.
        /// </param>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when nothing is mounted for writing or the volume cannot be written.
        /// </exception>
        public void DeleteFile(string path)
        {
            _inner.DeleteFile(path);
            SaveToFile();
        }

        /// <summary>
        /// Renames a file on the mounted volume and writes the DMK file back.
        /// </summary>
        /// <param name="oldPath">
        /// Current name of the file.
        /// </param>
        /// <param name="newPath">
        /// New name of the file.
        /// </param>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when nothing is mounted for writing or the volume cannot be written.
        /// </exception>
        public void RenameFile(string oldPath, string newPath)
        {
            _inner.RenameFile(oldPath, newPath);
            SaveToFile();
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
        /// Writes one decoded sector and writes the DMK file back.
        /// </summary>
        /// <param name="address">
        /// The block to write.
        /// </param>
        /// <param name="data">
        /// The 256 bytes of the sector.
        /// </param>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when nothing is mounted for writing.
        /// </exception>
        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            _inner.WriteBlock(address, data);
            SaveToFile();
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
            _image = null;
            _fileStream = null;
        }

        // Gives the decoded sectors to the DMK image, which rebuilds the changed ones (headers, data, CRCs), and writes the whole file back.
        // The sectors in the memory stream are up to date because the DSK driver flushes its block device after every write.
        private void SaveToFile()
        {
            if (_image == null || _sectors == null || _fileStream == null)
            {
                return;
            }

            byte[] jvc = _sectors.ToArray();
            int changed = _image.SetSectorImage(new ReadOnlySpan<byte>(jvc, 5, jvc.Length - 5));
            if (changed == 0)
            {
                return;
            }

            byte[] file = _image.ToFileBytes();
            _fileStream.Position = 0;
            _fileStream.Write(file, 0, file.Length);
            _fileStream.Flush();
        }
    }
}
