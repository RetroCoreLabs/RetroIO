//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Text;
using RetroIO.Storage.Local.V7.Structures;

namespace RetroIO.Storage.Local.BSD.Structures
{
    /// <summary>
    /// BSD Disk Label structure (512 bytes, at sector 1).
    ///
    /// IMPORTANT NAMING NOTE:
    /// This is a PARTITION TABLE FORMAT (like MBR or GPT), NOT a filesystem!
    ///
    /// BSD Disk Label divides a disk into partitions labeled 'a' through 'h',
    /// similar to how MBR creates partitions 0-3. Each partition can contain
    /// different filesystems (V7, FFS/UFS, swap, etc.)
    ///
    /// Structure:
    /// - Disk geometry (cylinders, heads, sectors)
    /// - Up to 8 partition entries (a-h) with offsets, sizes, and filesystem type codes
    /// - Magic number: 0x82564557 (validates structure)
    /// - Checksum for integrity verification
    ///
    /// This structure is used by: FreeBSD, NetBSD, OpenBSD, BSD 2.11
    /// Related: See BsdPartitionManager for partition table management
    /// </summary>
    public class BsdDiskLabel
    {
        // =================================================================
        // HEADER FIELDS (0-37 bytes)
        // =================================================================

        /// <summary>
        /// Magic number (must be 0x82564557).
        /// </summary>
        public uint Magic { get; set; }

        /// <summary>
        /// Drive type (SMD, MSCP, SCSI, etc.).
        /// </summary>
        public byte DriveType { get; set; }

        /// <summary>
        /// Controller/type specific subtype.
        /// </summary>
        public byte SubType { get; set; }

        /// <summary>
        /// Type name (e.g., "RA81"), 16 bytes.
        /// </summary>
        public string TypeName { get; set; } = string.Empty;

        /// <summary>
        /// Pack identifier, 16 bytes.
        /// </summary>
        public string PackName { get; set; } = string.Empty;

        // =================================================================
        // GEOMETRY FIELDS (38-57 bytes)
        // =================================================================

        /// <summary>
        /// Bytes per sector (usually 512).
        /// </summary>
        public ushort BytesPerSector { get; set; }

        /// <summary>
        /// Sectors per track.
        /// </summary>
        public ushort SectorsPerTrack { get; set; }

        /// <summary>
        /// Tracks (heads) per cylinder.
        /// </summary>
        public ushort TracksPerCylinder { get; set; }

        /// <summary>
        /// Cylinders per unit.
        /// </summary>
        public ushort CylindersPerUnit { get; set; }

        /// <summary>
        /// Sectors per cylinder (SectorsPerTrack × TracksPerCylinder).
        /// </summary>
        public ushort SectorsPerCylinder { get; set; }

        /// <summary>
        /// Total sectors on disk.
        /// </summary>
        public uint SectorsPerUnit { get; set; }

        // =================================================================
        // SPARE/ALTERNATE FIELDS (52-57 bytes)
        // =================================================================

        /// <summary>
        /// Spare sectors per track.
        /// </summary>
        public ushort SparesPerTrack { get; set; }

        /// <summary>
        /// Spare sectors per cylinder.
        /// </summary>
        public ushort SparesPerCylinder { get; set; }

        /// <summary>
        /// Alternate cylinders.
        /// </summary>
        public ushort AlternateCylinders { get; set; }

        // =================================================================
        // HARDWARE CHARACTERISTICS (58-69 bytes)
        // =================================================================

        /// <summary>
        /// Rotational speed (RPM).
        /// </summary>
        public ushort RPM { get; set; }

        /// <summary>
        /// Sector interleave.
        /// </summary>
        public byte Interleave { get; set; }

        /// <summary>
        /// Track skew.
        /// </summary>
        public byte TrackSkew { get; set; }

        /// <summary>
        /// Cylinder skew.
        /// </summary>
        public byte CylinderSkew { get; set; }

        /// <summary>
        /// Head switch time (microseconds).
        /// </summary>
        public byte HeadSwitch { get; set; }

        /// <summary>
        /// Track-to-track seek time (milliseconds).
        /// </summary>
        public ushort TrackSeek { get; set; }

        /// <summary>
        /// Flags (removable, ECC, bad sector forwarding, etc.).
        /// </summary>
        public ushort Flags { get; set; }

        // =================================================================
        // DRIVE DATA AND VALIDATION (68-121 bytes)
        // =================================================================

        /// <summary>
        /// Drive-specific data (5 × 4 bytes = 20 bytes).
        /// </summary>
        public uint[] DriveData { get; set; } = new uint[5];

        /// <summary>
        /// Reserved for future use (5 × 4 bytes = 20 bytes).
        /// </summary>
        public uint[] Spare { get; set; } = new uint[5];

        /// <summary>
        /// Magic number again (must be 0x82564557).
        /// </summary>
        public uint Magic2 { get; set; }

        /// <summary>
        /// XOR checksum of label.
        /// </summary>
        public ushort Checksum { get; set; }

        // =================================================================
        // FILESYSTEM METADATA (114-119 bytes)
        // =================================================================

        /// <summary>
        /// Number of partitions (1-8).
        /// </summary>
        public ushort NumPartitions { get; set; }

        /// <summary>
        /// Boot block size (usually 512).
        /// </summary>
        public ushort BootBlockSize { get; set; }

        /// <summary>
        /// Superblock size (usually 1024).
        /// </summary>
        public ushort SuperBlockSize { get; set; }

        // =================================================================
        // PARTITION TABLE (120-215 bytes for 8 partitions)
        // =================================================================

        /// <summary>
        /// Partition table (8 partitions × 12 bytes = 96 bytes).
        /// </summary>
        public BsdPartition[] Partitions { get; set; } = new BsdPartition[V7Constants.MAX_PARTITIONS];

        // =================================================================
        // VALIDATION AND PARSING
        // =================================================================

        /// <summary>
        /// Validates magic numbers.
        /// </summary>
        public bool IsValid()
        {
            return Magic == V7Constants.DISK_MAGIC &&
                   Magic2 == V7Constants.DISK_MAGIC &&
                   NumPartitions > 0 &&
                   NumPartitions <= V7Constants.MAX_PARTITIONS;
        }

        /// <summary>
        /// Calculates checksum for label.
        /// XOR of all 16-bit words up to end of last partition.
        /// </summary>
        public ushort CalculateChecksum()
        {
            byte[] buffer = new byte[V7Constants.LABEL_SIZE];
            WriteTo(buffer, 0);

            // Clear checksum field before calculating
            buffer[112] = 0;
            buffer[113] = 0;

            ushort sum = 0;
            int endByte = 120 + (NumPartitions * V7Constants.PARTITION_SIZE);

            for (int i = 0; i < endByte; i += 2)
            {
                if (i + 1 < buffer.Length)
                {
                    ushort word = BitConverter.ToUInt16(buffer, i);
                    sum ^= word;
                }
            }

            return sum;
        }

        /// <summary>
        /// Validates checksum.
        /// </summary>
        public bool ValidateChecksum()
        {
            return Checksum == CalculateChecksum();
        }

        /// <summary>
        /// Parses disk label from byte array.
        /// </summary>
        public static BsdDiskLabel Parse(byte[] buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (buffer.Length < V7Constants.LABEL_SIZE)
                throw new ArgumentException($"Buffer too small. Expected {V7Constants.LABEL_SIZE} bytes.");

            var label = new BsdDiskLabel();

            // Header (0-37)
            // Use PDP-endian for 32-bit magic number
            label.Magic = V7EndianHelper.ReadUInt32PDP(buffer, 0);

            label.DriveType = buffer[4];
            label.SubType = buffer[5];
            label.TypeName = ReadString(buffer, 6, 16);
            label.PackName = ReadString(buffer, 22, 16);

            // Geometry (38-57)
            label.BytesPerSector = BitConverter.ToUInt16(buffer, 38);
            label.SectorsPerTrack = BitConverter.ToUInt16(buffer, 40);
            label.TracksPerCylinder = BitConverter.ToUInt16(buffer, 42);
            label.CylindersPerUnit = BitConverter.ToUInt16(buffer, 44);
            label.SectorsPerCylinder = BitConverter.ToUInt16(buffer, 46);
            // Use PDP-endian for 32-bit SectorsPerUnit
            label.SectorsPerUnit = V7EndianHelper.ReadUInt32PDP(buffer, 48);

            // Spares (52-57)
            label.SparesPerTrack = BitConverter.ToUInt16(buffer, 52);
            label.SparesPerCylinder = BitConverter.ToUInt16(buffer, 54);
            label.AlternateCylinders = BitConverter.ToUInt16(buffer, 56);

            // Hardware (58-69)
            label.RPM = BitConverter.ToUInt16(buffer, 58);
            label.Interleave = buffer[60];
            label.TrackSkew = buffer[61];
            label.CylinderSkew = buffer[62];
            label.HeadSwitch = buffer[63];
            label.TrackSeek = BitConverter.ToUInt16(buffer, 64);
            label.Flags = BitConverter.ToUInt16(buffer, 66);

            // Drive data and spare (68-107)
            // Use PDP-endian for all 32-bit fields
            for (int i = 0; i < 5; i++)
            {
                label.DriveData[i] = V7EndianHelper.ReadUInt32PDP(buffer, 68 + (i * 4));
                label.Spare[i] = V7EndianHelper.ReadUInt32PDP(buffer, 88 + (i * 4));
            }

            // Validation (108-113)
            // Use PDP-endian for 32-bit magic2
            label.Magic2 = V7EndianHelper.ReadUInt32PDP(buffer, 108);

            label.Checksum = BitConverter.ToUInt16(buffer, 112);

            // Filesystem metadata (114-119)
            label.NumPartitions = BitConverter.ToUInt16(buffer, 114);
            label.BootBlockSize = BitConverter.ToUInt16(buffer, 116);
            label.SuperBlockSize = BitConverter.ToUInt16(buffer, 118);

            // Partition table (120-215)
            for (int i = 0; i < V7Constants.MAX_PARTITIONS; i++)
            {
                label.Partitions[i] = BsdPartition.Parse(buffer, 120 + (i * V7Constants.PARTITION_SIZE));
            }

            return label;
        }

        /// <summary>
        /// Writes disk label to byte array.
        /// </summary>
        public void WriteTo(byte[] buffer, int offset)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || offset + V7Constants.LABEL_SIZE > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            // Clear buffer first
            Array.Fill(buffer, (byte)0, offset, V7Constants.LABEL_SIZE);

            // Header (0-37)
            // Use PDP-endian for 32-bit magic
            V7EndianHelper.WriteUInt32PDP(buffer, offset, Magic);
            buffer[offset + 4] = DriveType;
            buffer[offset + 5] = SubType;
            WriteString(buffer, offset + 6, TypeName, 16);
            WriteString(buffer, offset + 22, PackName, 16);

            // Geometry (38-57)
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 38), BytesPerSector);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 40), SectorsPerTrack);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 42), TracksPerCylinder);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 44), CylindersPerUnit);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 46), SectorsPerCylinder);
            // Use PDP-endian for 32-bit SectorsPerUnit
            V7EndianHelper.WriteUInt32PDP(buffer, offset + 48, SectorsPerUnit);

            // Spares (52-57)
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 52), SparesPerTrack);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 54), SparesPerCylinder);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 56), AlternateCylinders);

            // Hardware (58-69)
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 58), RPM);
            buffer[offset + 60] = Interleave;
            buffer[offset + 61] = TrackSkew;
            buffer[offset + 62] = CylinderSkew;
            buffer[offset + 63] = HeadSwitch;
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 64), TrackSeek);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 66), Flags);

            // Drive data and spare (68-107)
            // Use PDP-endian for all 32-bit fields
            for (int i = 0; i < 5; i++)
            {
                V7EndianHelper.WriteUInt32PDP(buffer, offset + 68 + (i * 4), DriveData[i]);
                V7EndianHelper.WriteUInt32PDP(buffer, offset + 88 + (i * 4), Spare[i]);
            }

            // Validation (108-113)
            // Use PDP-endian for 32-bit magic2
            V7EndianHelper.WriteUInt32PDP(buffer, offset + 108, Magic2);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 112), Checksum);

            // Filesystem metadata (114-119)
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 114), NumPartitions);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 116), BootBlockSize);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 118), SuperBlockSize);

            // Partition table (120-215)
            for (int i = 0; i < V7Constants.MAX_PARTITIONS; i++)
            {
                Partitions[i].WriteTo(buffer, offset + 120 + (i * V7Constants.PARTITION_SIZE));
            }
        }

        // =================================================================
        // HELPER METHODS
        // =================================================================

        private static string ReadString(byte[] buffer, int offset, int maxLength)
        {
            int length = 0;
            while (length < maxLength && buffer[offset + length] != 0)
            {
                length++;
            }

            if (length == 0)
                return string.Empty;

            return Encoding.ASCII.GetString(buffer, offset, length);
        }

        private static void WriteString(byte[] buffer, int offset, string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
                return;

            int length = Math.Min(value.Length, maxLength - 1);  // Leave room for null terminator
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            Array.Copy(bytes, 0, buffer, offset, Math.Min(length, bytes.Length));
            buffer[offset + length] = 0;  // Null terminator
        }

        public override string ToString()
        {
            return $"BSD Disk Label: {TypeName} ({PackName}), " +
                   $"{CylindersPerUnit}C/{TracksPerCylinder}H/{SectorsPerTrack}S, " +
                   $"{NumPartitions} partitions, " +
                   $"Valid={IsValid()}, ChecksumOK={ValidateChecksum()}";
        }
    }
}

