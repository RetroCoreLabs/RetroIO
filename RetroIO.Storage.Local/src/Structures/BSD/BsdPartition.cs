//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Runtime.InteropServices;
using RetroIO.Storage.Local.V7.Structures;

namespace RetroIO.Storage.Local.BSD.Structures
{
    /// <summary>
    /// BSD partition table entry (12 bytes).
    /// Each partition entry describes a region of the disk.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BsdPartition
    {
        /// <summary>
        /// Number of 512-byte sectors in partition.
        /// </summary>
        public uint Size;

        /// <summary>
        /// Starting sector number (from beginning of disk).
        /// </summary>
        public uint Offset;

        /// <summary>
        /// Filesystem fragment size in bytes (usually 1024).
        /// </summary>
        public ushort FragmentSize;

        /// <summary>
        /// Filesystem type (FS_UNUSED, FS_SWAP, FS_V71K, etc.).
        /// </summary>
        public byte FsType;

        /// <summary>
        /// Fragments per filesystem block.
        /// </summary>
        public byte FragmentsPerBlock;

        // =================================================================
        // HELPER PROPERTIES
        // =================================================================

        /// <summary>
        /// True if partition is unused.
        /// </summary>
        public readonly bool IsUnused => FsType == V7Constants.FS_UNUSED || Size == 0;

        /// <summary>
        /// True if partition is swap space.
        /// </summary>
        public readonly bool IsSwap => FsType == V7Constants.FS_SWAP;

        /// <summary>
        /// True if partition contains a V7/2.11BSD filesystem.
        /// </summary>
        public readonly bool IsV7Filesystem => FsType == V7Constants.FS_V71K;

        /// <summary>
        /// True if partition contains a 4.2BSD FFS/UFS filesystem.
        /// </summary>
        public readonly bool IsFfsFilesystem => FsType == V7Constants.FS_BSDFFS;

        /// <summary>
        /// Ending sector number (exclusive).
        /// </summary>
        public readonly uint EndSector => Offset + Size;

        /// <summary>
        /// Gets filesystem type name as string.
        /// </summary>
        public readonly string FileSystemTypeName => GetFileSystemTypeName(FsType);

        // =================================================================
        // PARSING AND SERIALIZATION
        // =================================================================

        /// <summary>
        /// Parses a partition entry from byte array.
        /// </summary>
        /// <param name="buffer">Buffer containing partition data (at least 12 bytes).</param>
        /// <param name="offset">Offset in buffer where partition data starts.</param>
        /// <returns>Parsed partition structure.</returns>
        public static BsdPartition Parse(byte[] buffer, int offset)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || offset + V7Constants.PARTITION_SIZE > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            BsdPartition partition;
            // Use PDP-endian (middle-endian) byte order for 32-bit fields
            partition.Size = V7EndianHelper.ReadUInt32PDP(buffer, offset);
            partition.Offset = V7EndianHelper.ReadUInt32PDP(buffer, offset + 4);
            partition.FragmentSize = BitConverter.ToUInt16(buffer, offset + 8);
            partition.FsType = buffer[offset + 10];
            partition.FragmentsPerBlock = buffer[offset + 11];

            return partition;
        }

        /// <summary>
        /// Writes partition to byte array.
        /// </summary>
        /// <param name="buffer">Buffer to write to (at least 12 bytes).</param>
        /// <param name="offset">Offset in buffer where partition data should be written.</param>
        public readonly void WriteTo(byte[] buffer, int offset)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || offset + V7Constants.PARTITION_SIZE > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            // Use PDP-endian (middle-endian) byte order for 32-bit fields
            V7EndianHelper.WriteUInt32PDP(buffer, offset, Size);
            V7EndianHelper.WriteUInt32PDP(buffer, offset + 4, Offset);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 8), FragmentSize);
            buffer[offset + 10] = FsType;
            buffer[offset + 11] = FragmentsPerBlock;
        }

        /// <summary>
        /// Writes partition to span.
        /// </summary>
        public readonly void WriteTo(Span<byte> buffer)
        {
            if (buffer.Length < V7Constants.PARTITION_SIZE)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            // Use PDP-endian (middle-endian) byte order for 32-bit fields
            byte[] temp = new byte[12];
            V7EndianHelper.WriteUInt32PDP(temp, 0, Size);
            V7EndianHelper.WriteUInt32PDP(temp, 4, Offset);
            BitConverter.TryWriteBytes(temp.AsSpan(8), FragmentSize);
            temp[10] = FsType;
            temp[11] = FragmentsPerBlock;
            temp.AsSpan().CopyTo(buffer);
        }

        // =================================================================
        // VALIDATION
        // =================================================================

        /// <summary>
        /// Validates partition entry.
        /// </summary>
        /// <param name="diskSize">Total disk size in sectors.</param>
        /// <returns>True if partition is valid.</returns>
        public readonly bool Validate(uint diskSize)
        {
            // Empty partition is valid
            if (IsUnused)
                return true;

            // Check offset + size doesn't exceed disk
            if (Offset + Size > diskSize)
                return false;

            // Check for reasonable fragment size
            if (FragmentSize != 0 && FragmentSize != 512 && FragmentSize != 1024)
                return false;

            return true;
        }

        // =================================================================
        // HELPER METHODS
        // =================================================================

        /// <summary>
        /// Gets filesystem type name from type code.
        /// </summary>
        private static string GetFileSystemTypeName(byte fsType)
        {
            return fsType switch
            {
                V7Constants.FS_UNUSED => "unused",
                V7Constants.FS_SWAP => "swap",
                V7Constants.FS_V6 => "V6",
                V7Constants.FS_V7 => "V7",
                V7Constants.FS_SYSV => "SYSV",
                V7Constants.FS_V71K => "V7-1K (2.11BSD)",
                V7Constants.FS_V8 => "V8",
                V7Constants.FS_BSDFFS => "4.2BSD FFS",
                V7Constants.FS_MSDOS => "MS-DOS",
                V7Constants.FS_BSDLFS => "4.4BSD LFS",
                V7Constants.FS_OTHER => "other",
                V7Constants.FS_HPFS => "HPFS",
                V7Constants.FS_ISO9660 => "ISO9660",
                _ => $"unknown({fsType})"
            };
        }

        /// <summary>
        /// Gets partition letter ('a' through 'h').
        /// </summary>
        public static char GetPartitionLetter(int index)
        {
            if (index < 0 || index >= V7Constants.MAX_PARTITIONS)
                throw new ArgumentOutOfRangeException(nameof(index));

            return (char)('a' + index);
        }

        /// <summary>
        /// Gets partition index from letter ('a' = 0, 'b' = 1, etc.).
        /// </summary>
        public static int GetPartitionIndex(char letter)
        {
            letter = char.ToLowerInvariant(letter);

            if (letter < 'a' || letter > 'h')
                throw new ArgumentException($"Invalid partition letter: {letter}", nameof(letter));

            return letter - 'a';
        }

        public override readonly string ToString()
        {
            if (IsUnused)
                return $"Partition (unused)";

            return $"Partition: offset={Offset}, size={Size} sectors ({Size * 512 / 1024}KB), type={FileSystemTypeName}";
        }
    }
}

