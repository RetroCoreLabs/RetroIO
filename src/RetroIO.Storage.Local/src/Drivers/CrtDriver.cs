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
    /// Read-only driver for Commodore CRT cartridge images. Presents each "CHIP" packet (a ROM/RAM
    /// bank) as a directory entry, exposing its bank number, load address and chip type as metadata,
    /// so individual ROM banks can be listed and extracted. Big-endian fields per the CRT spec.
    /// </summary>
    public sealed class CrtDriver : IFileSystemDriver
    {
        private static readonly byte[] CrtMagic = Encoding.ASCII.GetBytes("C64 CARTRIDGE   ");
        private static readonly byte[] ChipMagic = Encoding.ASCII.GetBytes("CHIP");

        private byte[] _data = Array.Empty<byte>();
        private string _name = "CARTRIDGE";
        private readonly List<ChipEntry> _chips = new();

        /// <summary>
        /// Gets the text CRT.
        /// </summary>
        public string FileSystemType => "CRT";
        /// <summary>
        /// Gets the cartridge name from the header, or CARTRIDGE when the header name is empty.
        /// </summary>
        public string VolumeLabel => _name;
        /// <summary>
        /// Gets an empty string, because the chip names have no extension.
        /// </summary>
        public string ExtensionSeparator => "";

        /// <summary>
        /// Gets the capability flags of this driver: read, directory listing, file types and volume label.
        /// </summary>
        public DriverCapabilities Capabilities =>
            DriverCapabilities.Read | DriverCapabilities.ListDirectory | DriverCapabilities.FileTypes | DriverCapabilities.VolumeLabel;

        /// <summary>
        /// Gets <c>true</c>, because this driver reads the chip entries of the cartridge.
        /// </summary>
        public bool CanRead => true;
        /// <summary>
        /// Gets false, because CRT images are read-only.
        /// </summary>
        public bool CanWrite => false;
        /// <summary>
        /// Gets false, because a CRT image is a container and not a block device.
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
        /// Checks whether the data starts with the CRT cartridge signature.
        /// </summary>
        /// <param name="data">
        /// The first bytes of the file.
        /// </param>
        /// <returns>
        /// True when the data starts with the 16-byte signature text, false otherwise or when the data is too short.
        /// </returns>
        public static bool HasMagic(byte[] data)
        {
            if (data == null || data.Length < CrtMagic.Length)
            {
                return false;
            }
            for (int i = 0; i < CrtMagic.Length; i++)
            {
                if (data[i] != CrtMagic[i])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Reads the whole image, checks the signature, reads the cartridge name and builds the list of CHIP packets.
        /// </summary>
        /// <param name="imageStream">
        /// The stream holding the cartridge image.
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
            // Read and check into a local first. A stream that is not a cartridge must not replace the
            // data of the cartridge that is already mounted, and a second Mount must not add its chips
            // to the first one's.
            byte[] image = new byte[imageStream.Length];
            imageStream.Position = 0;
            imageStream.ReadExactly(image, 0, image.Length);

            if (!HasMagic(image))
            {
                throw new InvalidDataException("Not a CRT cartridge image");
            }

            _data = image;
            _chips.Clear();

            // Cartridge name: 32 bytes at 0x20 (ASCII, null-padded).
            _name = AsciiTrim(_data, 0x20, 32);
            if (_name.Length == 0)
            {
                _name = "CARTRIDGE";
            }

            // Walk CHIP packets starting just after the (big-endian) header length.
            int offset = (int)ReadBe32(_data, 0x10);
            if (offset < 0x40 || offset > _data.Length)
            {
                offset = 0x40;
            }

            int index = 0;
            while (offset + 0x10 <= _data.Length && MatchAt(_data, offset, ChipMagic))
            {
                uint packetLen = ReadBe32(_data, offset + 0x04);
                ushort chipType = ReadBe16(_data, offset + 0x08);
                ushort bank = ReadBe16(_data, offset + 0x0A);
                ushort loadAddr = ReadBe16(_data, offset + 0x0C);
                ushort romSize = ReadBe16(_data, offset + 0x0E);

                int dataOffset = offset + 0x10;
                int dataSize = romSize;
                if (dataOffset + dataSize > _data.Length)
                {
                    dataSize = Math.Max(0, _data.Length - dataOffset);
                }

                _chips.Add(new ChipEntry
                {
                    Name = $"CHIP{index:D2}_B{bank:D2}_{loadAddr:X4}",
                    Bank = bank,
                    LoadAddress = loadAddr,
                    ChipType = chipType,
                    DataOffset = dataOffset,
                    DataSize = dataSize
                });

                index++;
                if (packetLen < 0x10)
                {
                    break; // malformed packet length; stop to avoid an infinite loop
                }
                offset += (int)packetLen;
            }
        }

        /// <summary>
        /// Returns the data of one CHIP packet.
        /// </summary>
        /// <param name="path">
        /// The chip name as shown by <see cref="ListDirectory"/>.
        /// </param>
        /// <returns>
        /// The chip data.
        /// </returns>
        /// <exception cref="FileNotFoundException">
        /// Thrown when no chip has that name.
        /// </exception>
        public byte[] ReadFile(string path)
        {
            string name = Normalize(path);
            ChipEntry? chip = Find(name);
            if (chip == null)
            {
                throw new FileNotFoundException($"File not found: {path}");
            }
            byte[] result = new byte[chip.DataSize];
            Array.Copy(_data, chip.DataOffset, result, 0, chip.DataSize);
            return result;
        }

        /// <summary>
        /// Lists all CHIP packets of the cartridge. The path is ignored.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <returns>
        /// One entry per CHIP packet.
        /// </returns>
        public DirectoryEntry[] ListDirectory(string path)
        {
            DirectoryEntry[] entries = new DirectoryEntry[_chips.Count];
            for (int i = 0; i < _chips.Count; i++)
            {
                entries[i] = new DirectoryEntry { Name = _chips[i].Name, Metadata = BuildMetadata(_chips[i]) };
            }
            return entries;
        }

        /// <summary>
        /// Returns the metadata of one CHIP packet.
        /// </summary>
        /// <param name="path">
        /// The chip name.
        /// </param>
        /// <returns>
        /// The metadata, including bank, load address and chip type, or null when no chip has that name.
        /// </returns>
        public FileMetadata? GetMetadata(string path)
        {
            ChipEntry? chip = Find(Normalize(path));
            return chip == null ? null : BuildMetadata(chip);
        }

        private FileMetadata BuildMetadata(ChipEntry chip)
        {
            FileMetadata metadata = new FileMetadata
            {
                Name = chip.Name,
                Path = "/" + chip.Name,
                IsDirectory = false,
                Size = chip.DataSize,
                FileType = "ROM",
                Attributes = string.Empty,
                UrnIdentifier = UrnIdentifier.Encode(chip.Name)
            };
            metadata.SetProperty("Bank", chip.Bank);
            metadata.SetProperty("LoadAddress", chip.LoadAddress);
            metadata.SetProperty("ChipType", chip.ChipType);
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
        public void WriteFile(string path, byte[] data) => throw new NotSupportedException("CRT is read-only");
        /// <summary>
        /// Not supported. Always throws.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public void DeleteFile(string path) => throw new NotSupportedException("CRT is read-only");
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
        public int ReadBlock(BlockAddress address, Span<byte> buffer) => throw new NotSupportedException("CRT is a container, not a block device");
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
        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data) => throw new NotSupportedException("CRT is a container, not a block device");
        /// <summary>
        /// Returns null, because there is no block device.
        /// </summary>
        /// <returns>
        /// Always null.
        /// </returns>
        public BlockDeviceInfo? GetBlockDeviceInfo() => null;

        /// <summary>
        /// Drops the image data.
        /// </summary>
        public void Dispose() => _data = Array.Empty<byte>();

        private ChipEntry? Find(string name)
        {
            for (int i = 0; i < _chips.Count; i++)
            {
                if (string.Equals(_chips[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return _chips[i];
                }
            }
            return null;
        }

        private static string Normalize(string path)
            => path != null && path.StartsWith("/") ? path.Substring(1) : (path ?? string.Empty);

        private static bool MatchAt(byte[] data, int offset, byte[] magic)
        {
            if (offset + magic.Length > data.Length)
            {
                return false;
            }
            for (int i = 0; i < magic.Length; i++)
            {
                if (data[offset + i] != magic[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static uint ReadBe32(byte[] data, int offset)
            => (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

        private static ushort ReadBe16(byte[] data, int offset)
            => (ushort)((data[offset] << 8) | data[offset + 1]);

        private static string AsciiTrim(byte[] data, int offset, int length)
        {
            StringBuilder sb = new StringBuilder(length);
            for (int i = 0; i < length && offset + i < data.Length; i++)
            {
                byte b = data[offset + i];
                if (b == 0)
                {
                    break;
                }
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : ' ');
            }
            return sb.ToString().Trim();
        }

        private sealed class ChipEntry
        {
            public string Name { get; set; } = string.Empty;
            public ushort Bank { get; set; }
            public ushort LoadAddress { get; set; }
            public ushort ChipType { get; set; }
            public int DataOffset { get; set; }
            public int DataSize { get; set; }
        }
    }
}
