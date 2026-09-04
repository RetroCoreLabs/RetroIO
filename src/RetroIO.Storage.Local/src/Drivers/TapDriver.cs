//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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

        public string FileSystemType => "TAP";
        public string VolumeLabel => "TAPE";
        public string ExtensionSeparator => "";

        public DriverCapabilities Capabilities =>
            DriverCapabilities.Read | DriverCapabilities.ListDirectory | DriverCapabilities.VolumeLabel;

        public bool CanRead => true;
        public bool CanWrite => false;
        public bool HasBlockAccess => false;
        public IFileSystemMetadataDescriptor? GetMetadataDescriptor() => null;

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

        public DirectoryEntry[] ListDirectory(string path)
        {
            return new[] { new DirectoryEntry { Name = EntryName, Metadata = BuildMetadata() } };
        }

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

        public void WriteFile(string path, byte[] data) => throw new NotSupportedException("TAP is read-only");
        public void DeleteFile(string path) => throw new NotSupportedException("TAP is read-only");
        public int ReadBlock(BlockAddress address, Span<byte> buffer) => throw new NotSupportedException("TAP is a container, not a block device");
        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data) => throw new NotSupportedException("TAP is a container, not a block device");
        public BlockDeviceInfo? GetBlockDeviceInfo() => null;

        public void Dispose() => _pulses = Array.Empty<byte>();

        private static string Normalize(string path)
            => path != null && path.StartsWith("/") ? path.Substring(1) : (path ?? string.Empty);
    }
}
