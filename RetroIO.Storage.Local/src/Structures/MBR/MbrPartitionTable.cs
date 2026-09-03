//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;

namespace RetroIO.Storage.Local.MBR.Structures
{
    /// <summary>
    /// Master Boot Record (MBR) partition table.
    /// </summary>
    public class MbrPartitionTable
    {
        /// <summary>
        /// Boot code (first 446 bytes).
        /// </summary>
        public byte[] BootCode { get; set; } = new byte[446];

        /// <summary>
        /// Four primary partition entries.
        /// </summary>
        public MbrPartitionEntry[] Partitions { get; set; } = new MbrPartitionEntry[4];

        /// <summary>
        /// MBR signature (should be 0x55AA).
        /// </summary>
        public ushort Signature { get; set; }

        /// <summary>
        /// Parses an MBR from 512 bytes.
        /// </summary>
        /// <param name="mbrBytes">512-byte MBR sector</param>
        /// <returns>Parsed MBR partition table</returns>
        public static MbrPartitionTable Parse(byte[] mbrBytes)
        {
            if (mbrBytes == null || mbrBytes.Length < 512)
                throw new ArgumentException("MBR must be exactly 512 bytes");

            var mbr = new MbrPartitionTable();

            // Copy boot code
            Array.Copy(mbrBytes, 0, mbr.BootCode, 0, 446);

            // Parse 4 partition entries (16 bytes each, starting at offset 446)
            for (int i = 0; i < 4; i++)
            {
                int offset = 446 + (i * 16);
                mbr.Partitions[i] = MbrPartitionEntry.Parse(mbrBytes, offset);
            }

            // Read signature (little-endian)
            mbr.Signature = BitConverter.ToUInt16(mbrBytes, 510);

            return mbr;
        }

        /// <summary>
        /// Validates the MBR structure.
        /// </summary>
        /// <returns>True if the boot signature bytes at offset 510-511 are 0x55, 0xAA.</returns>
        /// <remarks>
        /// Real, pre-existing bug fixed here (found via a real MBR-partitioned disk image, not
        /// assumed): <see cref="Signature"/> is parsed via <c>BitConverter.ToUInt16(mbrBytes, 510)</c>,
        /// which reads bytes 510-511 as little-endian — for the standard MBR boot signature bytes
        /// (0x55 at 510, 0xAA at 511), that produces the ushort value 0xAA55, NOT 0x55AA. This
        /// method used to compare against 0x55AA and would therefore reject every genuinely valid
        /// MBR. A separate, independent MBR parser elsewhere in this codebase
        /// (PartitionDetectionService.DetectMbrPartitions) checks the two bytes directly rather
        /// than through a little-endian ushort, which is why it never had this bug.
        /// </remarks>
        public bool IsValid()
        {
            return Signature == 0xAA55;
        }

        /// <summary>
        /// Gets count of non-empty partitions.
        /// </summary>
        public int GetPartitionCount()
        {
            int count = 0;
            for (int i = 0; i < 4; i++)
            {
                if (!Partitions[i].IsUnused)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Builds a fresh 512-byte MBR sector containing a single partition (entry 0) spanning
        /// from <paramref name="partitionStartSector"/> to the end of the disk — used for
        /// auto-creating a "100% of the disk" partition when generating a new blank PC hard disk
        /// image. Entries 1-3 are left unused. Boot code is left zeroed (this is not a bootable
        /// MBR, just a valid partition table).
        /// </summary>
        /// <param name="totalSectors">Total size of the disk, in 512-byte sectors.</param>
        /// <param name="partitionTypeCode">MBR partition type byte (e.g. 0x01=FAT12, 0x06=FAT16, 0x0B=FAT32).</param>
        /// <param name="partitionStartSector">
        /// First sector of the partition. Defaults to 1 (immediately after the MBR sector).
        /// Classic DOS FDISK instead aligned to the start of head 1 (skipping the rest of
        /// cylinder 0), typically sector-per-track sectors in; callers that care about that
        /// convention can pass the real geometry's sectors-per-track here.
        /// </param>
        /// <param name="bootable">Sets the partition's boot-indicator flag (0x80) if true.</param>
        /// <remarks>
        /// CHS start/end fields are written as the standard 0xFE/0xFF/0xFF "use LBA instead"
        /// sentinel bytes (what modern fdisk/Windows already write for LBA-addressed partitions)
        /// rather than computed real CHS values — this codebase's own MBR reader
        /// (<see cref="MbrPartitionEntry.Parse"/> and <see cref="RetroIO.Storage.Local.Drivers.PcFloppyDriver"/>) only ever
        /// consults <see cref="MbrPartitionEntry.LbaStart"/>/<see cref="MbrPartitionEntry.NumSectors"/>,
        /// never the CHS fields, so this is safe for round-tripping through this codebase and still
        /// spec-valid for any real tool that reads it.
        /// </remarks>
        public static byte[] CreateWithSinglePartition(uint totalSectors, byte partitionTypeCode,
            uint partitionStartSector = 1, bool bootable = false)
        {
            if (partitionStartSector == 0 || partitionStartSector >= totalSectors)
            {
                throw new ArgumentException(
                    $"partitionStartSector ({partitionStartSector}) must be > 0 and < totalSectors ({totalSectors})",
                    nameof(partitionStartSector));
            }

            byte[] mbr = new byte[512];
            uint numSectors = totalSectors - partitionStartSector;

            const int entryOffset = 446; // partition entry 0
            mbr[entryOffset] = (byte)(bootable ? 0x80 : 0x00);
            mbr[entryOffset + 1] = 0xFE; // start CHS: LBA sentinel
            mbr[entryOffset + 2] = 0xFF;
            mbr[entryOffset + 3] = 0xFF;
            mbr[entryOffset + 4] = partitionTypeCode;
            mbr[entryOffset + 5] = 0xFE; // end CHS: LBA sentinel
            mbr[entryOffset + 6] = 0xFF;
            mbr[entryOffset + 7] = 0xFF;
            WriteUInt32LE(mbr, entryOffset + 8, partitionStartSector);
            WriteUInt32LE(mbr, entryOffset + 12, numSectors);

            mbr[510] = 0x55;
            mbr[511] = 0xAA;

            return mbr;
        }

        private static void WriteUInt32LE(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }

    /// <summary>
    /// MBR partition entry (16 bytes).
    /// </summary>
    public struct MbrPartitionEntry
    {
        /// <summary>
        /// Boot indicator (0x80 = bootable, 0x00 = not bootable).
        /// </summary>
        public byte Status;

        /// <summary>
        /// Partition type code (0x07 = NTFS, 0x0B = FAT32, etc.).
        /// </summary>
        public byte PartitionType;

        /// <summary>
        /// Logical Block Address of partition start (512-byte sectors).
        /// </summary>
        public uint LbaStart;

        /// <summary>
        /// Number of sectors in partition.
        /// </summary>
        public uint NumSectors;

        // CHS values (mostly unused on modern systems)
        public byte StartHead;
        public byte StartSector;
        public byte StartCylinder;
        public byte EndHead;
        public byte EndSector;
        public byte EndCylinder;

        /// <summary>
        /// Checks if partition is bootable.
        /// </summary>
        public readonly bool IsBootable => (Status & 0x80) != 0;

        /// <summary>
        /// Checks if partition entry is unused.
        /// </summary>
        public readonly bool IsUnused => PartitionType == 0 || NumSectors == 0;

        /// <summary>
        /// Gets friendly name for partition type.
        /// </summary>
        public readonly string TypeName => GetPartitionTypeName(PartitionType);

        /// <summary>
        /// Parses a partition entry from 16 bytes.
        /// </summary>
        /// <param name="mbrBytes">MBR sector data</param>
        /// <param name="offset">Offset to partition entry (446, 462, 478, or 494)</param>
        /// <returns>Parsed partition entry</returns>
        public static MbrPartitionEntry Parse(byte[] mbrBytes, int offset)
        {
            return new MbrPartitionEntry
            {
                Status = mbrBytes[offset + 0],
                StartHead = mbrBytes[offset + 1],
                StartSector = mbrBytes[offset + 2],
                StartCylinder = mbrBytes[offset + 3],
                PartitionType = mbrBytes[offset + 4],
                EndHead = mbrBytes[offset + 5],
                EndSector = mbrBytes[offset + 6],
                EndCylinder = mbrBytes[offset + 7],
                LbaStart = BitConverter.ToUInt32(mbrBytes, offset + 8),
                NumSectors = BitConverter.ToUInt32(mbrBytes, offset + 12)
            };
        }

        /// <summary>
        /// Gets friendly name for MBR partition type code.
        /// </summary>
        public static string GetPartitionTypeName(byte type)
        {
            return type switch
            {
                0x00 => "Empty",
                0x01 => "FAT12",
                0x04 => "FAT16 <32M",
                0x05 => "Extended",
                0x06 => "FAT16",
                0x07 => "NTFS/exFAT",
                0x0B => "FAT32",
                0x0C => "FAT32 LBA",
                0x0E => "FAT16 LBA",
                0x0F => "Extended LBA",
                0x11 => "Hidden FAT12",
                0x14 => "Hidden FAT16 <32M",
                0x16 => "Hidden FAT16",
                0x1B => "Hidden FAT32",
                0x1C => "Hidden FAT32 LBA",
                0x1E => "Hidden FAT16 LBA",
                0x82 => "Linux Swap",
                0x83 => "Linux",
                0x85 => "Linux Extended",
                0x8E => "Linux LVM",
                0xA5 => "FreeBSD",
                0xA6 => "OpenBSD",
                0xA8 => "macOS UFS",
                0xA9 => "NetBSD",
                0xAB => "macOS Boot",
                0xEE => "GPT Protective",
                0xEF => "EFI System",
                _ => $"0x{type:X2}"
            };
        }
    }
}
