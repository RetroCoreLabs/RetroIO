//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Text;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Read-only driver for Commodore TAP tape images (raw cassette pulse data). A TAP is a single
    /// signal blob, not a multi-file archive, so it is presented as one entry ("TAPE.RAW") holding
    /// the raw pulse data, with the format version exposed as metadata.
    /// </summary>
    public sealed class TapDriver : IFileSystemDriver
    {
        private static readonly byte[] TapMagic = Encoding.ASCII.GetBytes("C64-TAPE-RAW");
        private const int PulseDataOffset = 0x14;
        private const string EntryName = "TAPE.RAW";

        private byte[] _pulses = Array.Empty<byte>();
        private byte _version;

        /// <summary>
        /// Gets the text TAP.
        /// </summary>
        public string FileSystemType => "TAP";
        /// <summary>
        /// Gets the text TAPE.
        /// </summary>
        public string VolumeLabel => "TAPE";
        /// <summary>
        /// Gets an empty string, because the single entry has a fixed name.
        /// </summary>
        public string ExtensionSeparator => "";

        /// <inheritdoc/>
        public DriverCapabilities Capabilities =>
            DriverCapabilities.Read | DriverCapabilities.ListDirectory | DriverCapabilities.VolumeLabel;

        /// <inheritdoc/>
        public bool CanRead => true;
        /// <summary>
        /// Gets false, because TAP images are read-only.
        /// </summary>
        public bool CanWrite => false;
        /// <summary>
        /// Gets false, because a TAP image is a container and not a block device.
        /// </summary>
        public bool HasBlockAccess => false;
        /// <summary>
        /// Returns null, because this driver has no metadata descriptor.
        /// </summary>
        /// <returns>
        /// Always null.
        /// </returns>
        public IFileSystemMetadataDescriptor? GetMetadataDescriptor() => null;

        /// <summary>
        /// Checks whether the data starts with the TAP signature and is long enough to hold the header.
        /// </summary>
        /// <param name="data">
        /// The first bytes of the file.
        /// </param>
        /// <returns>
        /// True for a TAP header, false otherwise.
        /// </returns>
        public static bool HasMagic(byte[] data)
        {
            if (data == null || data.Length < PulseDataOffset)
            {
                return false;
            }
            for (int i = 0; i < TapMagic.Length; i++)
            {
                if (data[i] != TapMagic[i])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Reads the whole image, checks the signature, reads the format version and keeps the pulse data.
        /// </summary>
        /// <param name="imageStream">
        /// The stream holding the TAP image.
        /// </param>
        /// <param name="readOnly">
        /// Not used. The driver is always read-only.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="imageStream"/> is null.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// Thrown when the signature is missing.
        /// </exception>
        public void Mount(Stream imageStream, bool readOnly)
        {
            if (imageStream == null)
            {
                throw new ArgumentNullException(nameof(imageStream));
            }
            byte[] all = new byte[imageStream.Length];
            imageStream.Position = 0;
            imageStream.ReadExactly(all, 0, all.Length);

            if (!HasMagic(all))
            {
                throw new InvalidDataException("Not a TAP tape image");
            }

            _version = all[0x0C];

            // Pulse-data length is a little-endian 32-bit field at 0x10; clamp to the actual file.
            long declared = (uint)(all[0x10] | (all[0x11] << 8) | (all[0x12] << 16) | (all[0x13] << 24));
            int available = all.Length - PulseDataOffset;
            int len = available < 0 ? 0 : (int)Math.Min(declared, available);

            _pulses = new byte[len];
            Array.Copy(all, PulseDataOffset, _pulses, 0, len);
        }

        /// <summary>
        /// Returns a copy of the pulse data.
        /// </summary>
        /// <param name="path">
        /// The entry name TAPE.RAW, with or without a leading slash.
        /// </param>
        /// <returns>
        /// The pulse data.
        /// </returns>
        /// <exception cref="FileNotFoundException">
        /// Thrown when the name is not TAPE.RAW.
        /// </exception>
        public byte[] ReadFile(string path)
        {
            if (!string.Equals(Normalize(path), EntryName, StringComparison.OrdinalIgnoreCase))
            {
                throw new FileNotFoundException($"File not found: {path}");
            }
            byte[] copy = new byte[_pulses.Length];
            Array.Copy(_pulses, copy, _pulses.Length);
            return copy;
        }

        /// <summary>
        /// Lists the single entry TAPE.RAW. The path is ignored.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <returns>
        /// An array with one entry.
        /// </returns>
        public DirectoryEntry[] ListDirectory(string path)
        {
            return new[] { new DirectoryEntry { Name = EntryName, Metadata = BuildMetadata() } };
        }

        /// <summary>
        /// Returns the metadata of the entry TAPE.RAW, including the TAP version.
        /// </summary>
        /// <param name="path">
        /// The entry name.
        /// </param>
        /// <returns>
        /// The metadata, or null when the name is not TAPE.RAW.
        /// </returns>
        public FileMetadata? GetMetadata(string path)
        {
            return string.Equals(Normalize(path), EntryName, StringComparison.OrdinalIgnoreCase) ? BuildMetadata() : null;
        }

        private FileMetadata BuildMetadata()
        {
            FileMetadata metadata = new FileMetadata
            {
                Name = EntryName,
                Path = "/" + EntryName,
                IsDirectory = false,
                Size = _pulses.Length,
                FileType = "TAP",
                Attributes = string.Empty,
                UrnIdentifier = UrnIdentifier.Encode(EntryName)
            };
            metadata.SetProperty("TapVersion", _version);
            return metadata;
        }

        /// <summary>
        /// Not supported. Always throws.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <param name="data">
        /// Not used.
        /// </param>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public void WriteFile(string path, byte[] data) => throw new NotSupportedException("TAP is read-only");
        /// <summary>
        /// Not supported. Always throws.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public void DeleteFile(string path) => throw new NotSupportedException("TAP is read-only");
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
        public int ReadBlock(BlockAddress address, Span<byte> buffer) => throw new NotSupportedException("TAP is a container, not a block device");
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
        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data) => throw new NotSupportedException("TAP is a container, not a block device");
        /// <summary>
        /// Returns null, because there is no block device.
        /// </summary>
        /// <returns>
        /// Always null.
        /// </returns>
        public BlockDeviceInfo? GetBlockDeviceInfo() => null;

        /// <summary>
        /// Drops the pulse data.
        /// </summary>
        public void Dispose() => _pulses = Array.Empty<byte>();

        private static string Normalize(string path)
            => path != null && path.StartsWith("/") ? path.Substring(1) : (path ?? string.Empty);
    }
}
