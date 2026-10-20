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

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Filesystem driver for T64 tape archive images (Commodore tape format).
    /// T64 is a container format storing multiple files without track/sector structure.
    /// Format: Header + Directory entries + Raw file data.
    /// </summary>
    public sealed class T64Driver : IFileSystemDriver
    {
        private Stream? _imageStream;
        private byte[]? _imageData;
        private bool _readOnly;
        private string _volumeLabel = string.Empty;
        private T64Header? _header;
        private List<T64DirectoryEntry>? _entries;

        private const int HeaderSize = 64;
        private const int DirectoryEntrySize = 32;

        /// <summary>
        /// Gets the text T64.
        /// </summary>
        public string FileSystemType => "T64";
        /// <summary>
        /// Gets the container name stored in the T64 header.
        /// </summary>
        public string VolumeLabel => _volumeLabel;
        /// <summary>
        /// Gets an empty string, because T64 entries carry a file type and no extension.
        /// </summary>
        public string ExtensionSeparator => "";  // T64 uses Commodore-style FileType metadata, not extensions

        /// <summary>
        /// T64 driver capabilities (read-only).
        /// T64 is a simple container format with no directory structure.
        /// </summary>
        public DriverCapabilities Capabilities
        {
            get
            {
                return DriverCapabilities.Read |
                       DriverCapabilities.ListDirectory |
                       DriverCapabilities.FileTypes |
                       DriverCapabilities.PetsciiEncoding |
                       DriverCapabilities.VolumeLabel;
                // No block access - T64 is a container, not a block device
            }
        }

        /// <summary>
        /// Gets <c>true</c>, because this driver reads the files in the T64 archive.
        /// </summary>
        public bool CanRead => true;
        /// <summary>
        /// Gets false, because the driver does not write yet.
        /// </summary>
        public bool CanWrite => false; // TODO: Implement write operations
        /// <summary>
        /// Gets false, because T64 is a container and not a block device.
        /// </summary>
        public bool HasBlockAccess => false; // T64 is not a block device

        /// <summary>
        /// Returns null, because this driver has no metadata descriptor yet.
        /// </summary>
        /// <returns>
        /// Always null.
        /// </returns>
        public IFileSystemMetadataDescriptor? GetMetadataDescriptor()
        {
            // TODO: Implement T64 metadata descriptor
            return null;
        }

        /// <summary>
        /// Reads the whole image into memory and parses the header and the directory.
        /// </summary>
        /// <param name="imageStream">
        /// The stream holding the T64 image.
        /// </param>
        /// <param name="readOnly">
        /// True to mount read-only.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="imageStream"/> is null.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// Thrown when the image is too small or the signature does not match.
        /// </exception>
        public void Mount(Stream imageStream, bool readOnly)
        {
            if (imageStream == null)
            {
                throw new ArgumentNullException(nameof(imageStream));
            }

            _imageStream = imageStream;
            _readOnly = readOnly;

            // Read entire image into memory
            long length = imageStream.Length;
            _imageData = new byte[length];
            imageStream.Position = 0;
            imageStream.ReadExactly(_imageData, 0, (int)length);

            // Parse header and directory
            ParseHeader();
            ParseDirectory();
        }

        /// <summary>
        /// Returns the data of one entry. An empty or deleted entry gives an empty array.
        /// </summary>
        /// <param name="path">
        /// The entry name, with or without a leading slash.
        /// </param>
        /// <returns>
        /// The entry data.
        /// </returns>
        /// <exception cref="FileNotFoundException">
        /// Thrown when no entry has that name.
        /// </exception>
        public byte[] ReadFile(string path)
        {
            EnsureMounted();

            if (path.StartsWith("/"))
            {
                path = path.Substring(1);
            }

            T64DirectoryEntry? entry = FindEntry(path);
            if (entry == null)
            {
                throw new FileNotFoundException($"File not found: {path}");
            }

            if (entry.DataOffset == 0 || entry.DataSize == 0)
            {
                return Array.Empty<byte>(); // Empty or deleted file
            }

            // Read file data directly from offset
            byte[] fileData = new byte[entry.DataSize];
            Array.Copy(_imageData!, entry.DataOffset, fileData, 0, entry.DataSize);
            return fileData;
        }

        /// <summary>
        /// Always throws UnauthorizedAccessException: the T64 driver has no write support, so the volume is read-only.
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
            throw new UnauthorizedAccessException("The T64 driver has no write support, so the volume is read-only");
        }

        /// <summary>
        /// Always throws UnauthorizedAccessException: the T64 driver has no write support, so the volume is read-only.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <exception cref="UnauthorizedAccessException">
        /// Always thrown.
        /// </exception>
        public void DeleteFile(string path)
        {
            throw new UnauthorizedAccessException("The T64 driver has no write support, so the volume is read-only");
        }

        /// <summary>
        /// Lists the entries of the tape. Empty and deleted entries are skipped. The path is ignored.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <returns>
        /// One entry per file, with start and end address in the metadata.
        /// </returns>
        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureMounted();

            List<DirectoryEntry> entries = new List<DirectoryEntry>();

            for (int i = 0; i < _entries!.Count; i++)
            {
                T64DirectoryEntry entry = _entries[i];

                // Skip deleted or empty entries
                if (entry.DataOffset == 0 || entry.DataSize == 0)
                {
                    continue;
                }

                FileMetadata metadata = new FileMetadata
                {
                    Name = entry.FileName,
                    Path = "/" + entry.FileName,
                    IsDirectory = false,
                    Size = entry.DataSize,
                    FileType = entry.FileType,
                    Attributes = "",
                    UrnIdentifier = UrnIdentifier.Encode(entry.FileName)
                };

                metadata.SetProperty("StartAddress", entry.StartAddress);
                metadata.SetProperty("EndAddress", entry.EndAddress);
                metadata.SetProperty("DataOffset", entry.DataOffset);
                metadata.SetProperty("DataSize", entry.DataSize);
                metadata.SetProperty("DirectoryIndex", entries.Count);

                entries.Add(new DirectoryEntry { Name = entry.FileName, Metadata = metadata });
            }

            return entries.ToArray();
        }

        /// <summary>
        /// Returns the metadata of one entry, including start address, end address, data offset and data size.
        /// </summary>
        /// <param name="path">
        /// The entry name, with or without a leading slash.
        /// </param>
        /// <returns>
        /// The metadata, or null when no entry has that name.
        /// </returns>
        public FileMetadata? GetMetadata(string path)
        {
            EnsureMounted();

            if (path.StartsWith("/"))
            {
                path = path.Substring(1);
            }

            T64DirectoryEntry? entry = FindEntry(path);
            if (entry == null)
            {
                return null;
            }

            FileMetadata metadata = new FileMetadata
            {
                Name = entry.FileName,
                Path = "/" + entry.FileName,
                IsDirectory = false,
                Size = entry.DataSize,
                FileType = entry.FileType,
                UrnIdentifier = UrnIdentifier.Encode(entry.FileName)
            };

            metadata.SetProperty("StartAddress", entry.StartAddress);
            metadata.SetProperty("EndAddress", entry.EndAddress);
            metadata.SetProperty("DataOffset", entry.DataOffset);
            metadata.SetProperty("DataSize", entry.DataSize);

            return metadata;
        }

        /// <summary>
        /// Not supported. Always throws.
        /// </summary>
        /// <param name="address">
        /// Not used.
        /// </param>
        /// <param name="buffer">
        /// Not used.
        /// </param>
        /// <returns>
        /// Never returns.
        /// </returns>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            throw new NotSupportedException("T64 is a container format, not a block device.");
        }

        /// <summary>
        /// Not supported. Always throws.
        /// </summary>
        /// <param name="address">
        /// Not used.
        /// </param>
        /// <param name="data">
        /// Not used.
        /// </param>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            throw new NotSupportedException("T64 is a container format, not a block device.");
        }

        /// <summary>
        /// Returns null, because there is no block device.
        /// </summary>
        /// <returns>
        /// Always null.
        /// </returns>
        public BlockDeviceInfo? GetBlockDeviceInfo()
        {
            return null; // T64 is not a block device
        }

        /// <summary>
        /// Drops the image data and the entry list.
        /// </summary>
        public void Dispose()
        {
            _imageData = null;
            _entries = null;
        }

        private void ParseHeader()
        {
            if (_imageData == null || _imageData.Length < HeaderSize)
            {
                throw new InvalidDataException("Invalid T64 file: too small for header.");
            }

            // T64 signature: "C64 tape image file" or "C64S tape file"
            string signature = Encoding.ASCII.GetString(_imageData, 0, 32).TrimEnd('\0');
            if (!signature.StartsWith("C64") && !signature.Contains("tape"))
            {
                throw new InvalidDataException("Invalid T64 file: signature mismatch.");
            }

            _header = new T64Header
            {
                Version = BitConverter.ToUInt16(_imageData, 32),
                MaxEntries = BitConverter.ToUInt16(_imageData, 34),
                UsedEntries = BitConverter.ToUInt16(_imageData, 36),
                Reserved = BitConverter.ToUInt16(_imageData, 38)
            };

            // Container name at offset 40 (24 bytes)
            _volumeLabel = PetsciiToAscii(_imageData, 40, 24);
        }

        private void ParseDirectory()
        {
            if (_header == null)
            {
                throw new InvalidOperationException("Header not parsed.");
            }

            _entries = new List<T64DirectoryEntry>();

            int directoryOffset = HeaderSize;
            int maxEntries = _header.MaxEntries;

            for (int i = 0; i < maxEntries; i++)
            {
                int entryOffset = directoryOffset + (i * DirectoryEntrySize);

                if (entryOffset + DirectoryEntrySize > _imageData!.Length)
                {
                    break; // Beyond file bounds
                }

                byte entryType = _imageData[entryOffset];

                // Entry type: 0 = free/deleted, 1 = normal tape file, 3 = memory snapshot
                if (entryType == 0)
                {
                    continue; // Skip free entries
                }

                ushort c64FileType = _imageData[entryOffset + 1];
                ushort startAddress = BitConverter.ToUInt16(_imageData, entryOffset + 2);
                ushort endAddress = BitConverter.ToUInt16(_imageData, entryOffset + 4);
                uint dataOffset = BitConverter.ToUInt32(_imageData, entryOffset + 8);
                string fileName = PetsciiToAscii(_imageData, entryOffset + 16, 16);

                int dataSize = endAddress - startAddress;
                if (dataSize < 0)
                {
                    dataSize = 0;
                }

                string fileTypeStr = c64FileType switch
                {
                    0x82 => "PRG", // Most common
                    0x81 => "SEQ",
                    _ => "DAT"
                };

                _entries.Add(new T64DirectoryEntry
                {
                    EntryType = entryType,
                    FileName = fileName,
                    FileType = fileTypeStr,
                    StartAddress = startAddress,
                    EndAddress = endAddress,
                    DataOffset = (int)dataOffset,
                    DataSize = dataSize
                });
            }
        }

        private T64DirectoryEntry? FindEntry(string fileName)
        {
            if (_entries == null)
            {
                return null;
            }

            for (int i = 0; i < _entries.Count; i++)
            {
                T64DirectoryEntry entry = _entries[i];
                if (string.Equals(entry.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            return null;
        }

        private static string PetsciiToAscii(byte[] data, int offset, int length)
        {
            StringBuilder sb = new StringBuilder(length);

            for (int i = 0; i < length; i++)
            {
                byte b = data[offset + i];

                if (b == 0xA0 || b == 0x00)
                {
                    break; // Padding
                }

                if (b >= 0x41 && b <= 0x5A)
                {
                    sb.Append((char)b); // A-Z
                }
                else if (b >= 0xC1 && b <= 0xDA)
                {
                    sb.Append((char)(b - 0x80)); // Shifted lowercase -> uppercase
                }
                else if (b >= 0x20 && b <= 0x3F)
                {
                    sb.Append((char)b); // Numbers and symbols
                }
                else
                {
                    sb.Append('?');
                }
            }

            return sb.ToString().TrimEnd();
        }

        private void EnsureMounted()
        {
            if (_imageData == null || _entries == null)
            {
                throw new InvalidOperationException("T64 image not mounted.");
            }
        }

        private sealed class T64Header
        {
            public ushort Version { get; set; }
            public ushort MaxEntries { get; set; }
            public ushort UsedEntries { get; set; }
            public ushort Reserved { get; set; }
        }

        private sealed class T64DirectoryEntry
        {
            public byte EntryType { get; set; }
            public string FileName { get; set; } = string.Empty;
            public string FileType { get; set; } = string.Empty;
            public ushort StartAddress { get; set; }
            public ushort EndAddress { get; set; }
            public int DataOffset { get; set; }
            public int DataSize { get; set; }
        }
    }
}
