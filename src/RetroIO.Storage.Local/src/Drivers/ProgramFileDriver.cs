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
    /// Read-only driver for single-program Commodore containers:
    ///   * PRG — raw program: 2-byte little-endian load address followed by the program bytes.
    ///   * P00 — PC64 wrapper: "C64File" + embedded 16-byte CBM filename, then the PRG payload.
    /// Presents the container as a one-entry directory whose file is the program (load address +
    /// data), with the load address exposed as metadata.
    /// </summary>
    public sealed class ProgramFileDriver : IFileSystemDriver
    {
        // P00 layout (per VICE/MAME): "C64File"\0 | 16-byte name\0 | \0 | recsize | payload@0x1A
        private static readonly byte[] P00Magic = { 0x43, 0x36, 0x34, 0x46, 0x69, 0x6C, 0x65 }; // "C64File"
        private const int P00PayloadOffset = 0x1A;

        private byte[] _program = Array.Empty<byte>(); // load address (2) + program bytes
        private string _name = "PROGRAM";
        private bool _isP00;
        private ushort _loadAddress;

        public string FileSystemType => _isP00 ? "P00" : "PRG";
        public string VolumeLabel => _name;
        public string ExtensionSeparator => "";

        public DriverCapabilities Capabilities =>
            DriverCapabilities.Read |
            DriverCapabilities.ListDirectory |
            DriverCapabilities.FileTypes |
            DriverCapabilities.PetsciiEncoding;

        public bool CanRead => true;
        public bool CanWrite => false;
        public bool HasBlockAccess => false;

        public IFileSystemMetadataDescriptor? GetMetadataDescriptor() => null;

        public static bool IsP00(byte[] data)
        {
            if (data == null || data.Length < P00PayloadOffset)
            {
                return false;
            }
            for (int i = 0; i < P00Magic.Length; i++)
            {
                if (data[i] != P00Magic[i])
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

            if (IsP00(all))
            {
                _isP00 = true;
                string embedded = PetsciiToAscii(all, 8, 16);
                _name = embedded.Length > 0 ? embedded : "PROGRAM";

                int payloadLen = all.Length - P00PayloadOffset;
                if (payloadLen < 0)
                {
                    payloadLen = 0;
                }
                _program = new byte[payloadLen];
                Array.Copy(all, P00PayloadOffset, _program, 0, payloadLen);
            }
            else
            {
                _isP00 = false;
                _name = "PROGRAM";
                _program = all;
            }

            if (_program.Length >= 2)
            {
                _loadAddress = (ushort)(_program[0] | (_program[1] << 8));
            }
        }

        public byte[] ReadFile(string path)
        {
            EnsureMounted();
            string name = Normalize(path);
            if (!string.Equals(name, _name, StringComparison.OrdinalIgnoreCase))
            {
                throw new FileNotFoundException($"File not found: {path}");
            }
            byte[] copy = new byte[_program.Length];
            Array.Copy(_program, copy, _program.Length);
            return copy;
        }

        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureMounted();
            FileMetadata metadata = BuildMetadata();
            return new[] { new DirectoryEntry { Name = _name, Metadata = metadata } };
        }

        public FileMetadata? GetMetadata(string path)
        {
            EnsureMounted();
            string name = Normalize(path);
            return string.Equals(name, _name, StringComparison.OrdinalIgnoreCase) ? BuildMetadata() : null;
        }

        private FileMetadata BuildMetadata()
        {
            FileMetadata metadata = new FileMetadata
            {
                Name = _name,
                Path = "/" + _name,
                IsDirectory = false,
                Size = _program.Length,
                FileType = "PRG",
                Attributes = string.Empty,
                UrnIdentifier = UrnIdentifier.Encode(_name)
            };
            metadata.SetProperty("LoadAddress", _loadAddress);
            metadata.SetProperty("Container", _isP00 ? "P00" : "PRG");
            return metadata;
        }

        public void WriteFile(string path, byte[] data) => throw new NotSupportedException("PRG/P00 is read-only");
        public void DeleteFile(string path) => throw new NotSupportedException("PRG/P00 is read-only");
        public int ReadBlock(BlockAddress address, Span<byte> buffer) => throw new NotSupportedException("PRG/P00 is a container, not a block device");
        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data) => throw new NotSupportedException("PRG/P00 is a container, not a block device");
        public BlockDeviceInfo? GetBlockDeviceInfo() => null;

        public void Dispose()
        {
            _program = Array.Empty<byte>();
        }

        private static string Normalize(string path)
        {
            return path != null && path.StartsWith("/") ? path.Substring(1) : (path ?? string.Empty);
        }

        private void EnsureMounted()
        {
            if (_program == null)
            {
                throw new InvalidOperationException("Program file not mounted");
            }
        }

        // Decodes a PETSCII name (0x00/0xA0 terminated) to ASCII.
        private static string PetsciiToAscii(byte[] data, int offset, int length)
        {
            StringBuilder sb = new StringBuilder(length);
            for (int i = 0; i < length && offset + i < data.Length; i++)
            {
                byte b = data[offset + i];
                if (b == 0x00 || b == 0xA0)
                {
                    break;
                }
                if (b >= 0x41 && b <= 0x5A)
                {
                    sb.Append((char)b);
                }
                else if (b >= 0xC1 && b <= 0xDA)
                {
                    sb.Append((char)(b - 0x80));
                }
                else if (b >= 0x20 && b <= 0x3F)
                {
                    sb.Append((char)b);
                }
                else
                {
                    sb.Append('?');
                }
            }
            return sb.ToString().TrimEnd();
        }
    }
}
