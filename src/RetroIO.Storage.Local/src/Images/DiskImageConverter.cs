//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers;
using System.IO;

namespace RetroIO.Storage.Local.Images
{
    /// <summary>
    /// Converts a PC hard-disk image between the raw format (<c>.img</c>, <c>.hdd</c>: every byte a disk
    /// byte) and the fixed VHD format (the same bytes followed by a 512-byte footer).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raw to VHD copies the disk bytes and appends a footer built per the VHD spec (see
    /// <see cref="FixedVhd"/>): the size, the geometry, disk type fixed, the checksum and a new unique
    /// id. VHD to raw copies the disk bytes and drops the footer. The disk bytes themselves are never
    /// changed, so raw to VHD to raw gives back the same file byte for byte.
    /// </para>
    /// <para>
    /// The conversion always writes a new file and never changes the source. It refuses, with the
    /// reason in the message, when the target exists, when target and source are the same file, or
    /// when the source is not a usable image. A target that was started and then failed is deleted.
    /// </para>
    /// </remarks>
    public static class DiskImageConverter
    {
        // Copy in 1 MB pieces.
        private const int CopyChunkBytes = 1024 * 1024;

        // Where the MBR partition table starts in sector 0, and the size of one entry.
        private const int MbrTableOffset = 446;
        private const int MbrEntryBytes = 16;

        /// <summary>
        /// Says what the image at <paramref name="path"/> is: a fixed VHD or a raw image.
        /// </summary>
        /// <param name="path">
        /// The image file.
        /// </param>
        /// <returns>
        /// <c>true</c> for a usable fixed VHD, <c>false</c> for a raw image.
        /// </returns>
        /// <exception cref="InvalidDataException">
        /// Thrown when the file ends in a VHD footer that cannot be used.
        /// </exception>
        public static bool IsFixedVhd(string path)
        {
            using FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            VhdProbeResult kind = FixedVhd.Probe(file, out _, out string? problem);
            if (kind == VhdProbeResult.NotUsable)
            {
                throw new InvalidDataException($"{path}: {problem}.");
            }
            return kind == VhdProbeResult.Fixed;
        }

        /// <summary>
        /// Works out a disk's cylinders, heads and sectors per track from what is written on it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For a fixed VHD the geometry in its footer is used. Otherwise, in this order: a FAT boot
        /// sector in sector 0 (a floppy, or a disk without a partition table) gives sectors per track
        /// and heads in its parameter block (offsets 24 and 26); then a used partition table entry whose
        /// end position (cylinder, head, sector) agrees with its end sector number gives heads (end head
        /// + 1) and sectors per track (end sector); then the FAT boot sector of the first partition.
        /// Cylinders are the disk sectors divided by heads x sectors, at most 65535.
        /// </para>
        /// <para>
        /// A partition table entry that does not agree with its own sector numbers is not used: many
        /// tools write the filler FE FF FF ("use the sector numbers") there, which read as a position
        /// would claim 255 heads and 63 sectors on any disk. An end position at cylinder 1023 is
        /// accepted when the partition ends further out than 1024 cylinders reach (a large disk, where
        /// the position is capped). A disk with none of these gives <c>false</c>, and the caller must
        /// ask for the geometry.
        /// </para>
        /// </remarks>
        /// <param name="path">
        /// The image file, raw or fixed VHD.
        /// </param>
        /// <param name="geometry">
        /// The geometry, when found.
        /// </param>
        /// <returns>
        /// <c>true</c> when the geometry was found.
        /// </returns>
        public static bool TryDetectGeometry(string path, out DiskGeometry geometry)
        {
            geometry = default;
            using FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            VhdProbeResult kind = FixedVhd.Probe(file, out FixedVhdFooter footer, out _);
            if (kind == VhdProbeResult.Fixed && footer.Geometry.Cylinders > 0 && footer.Geometry.Heads > 0 && footer.Geometry.SectorsPerTrack > 0)
            {
                geometry = footer.Geometry;
                return true;
            }
            long diskBytes = kind == VhdProbeResult.Fixed ? footer.DiskBytes : file.Length;
            if (diskBytes < FixedVhd.SectorBytes)
            {
                return false;
            }

            byte[] sector0 = new byte[FixedVhd.SectorBytes];
            file.Position = 0;
            file.ReadExactly(sector0, 0, sector0.Length);

            // The boot sector of the first used partition, when there is one inside the disk.
            byte[]? partitionBoot = null;
            for (int i = 0; i < 4 && partitionBoot == null; i++)
            {
                int e = MbrTableOffset + i * MbrEntryBytes;
                if (sector0[e + 4] == 0)
                {
                    continue;
                }
                long start = ReadUInt32LE(sector0, e + 8);
                if (start > 0 && (start + 1) * FixedVhd.SectorBytes <= diskBytes)
                {
                    partitionBoot = new byte[FixedVhd.SectorBytes];
                    file.Position = start * FixedVhd.SectorBytes;
                    file.ReadExactly(partitionBoot, 0, partitionBoot.Length);
                }
            }

            return TryDetectGeometry(sector0, partitionBoot, diskBytes / FixedVhd.SectorBytes, out geometry);
        }

        /// <summary>
        /// Works out a disk's geometry from its sector 0 alone (see
        /// <see cref="TryDetectGeometry(string, out DiskGeometry)"/>).
        /// </summary>
        /// <param name="sector0">
        /// The disk's first 512 bytes.
        /// </param>
        /// <param name="totalSectors">
        /// The disk size in sectors.
        /// </param>
        /// <param name="geometry">
        /// The geometry, when found.
        /// </param>
        /// <returns>
        /// <c>true</c> when the geometry was found.
        /// </returns>
        public static bool TryDetectGeometry(ReadOnlySpan<byte> sector0, long totalSectors, out DiskGeometry geometry)
        {
            return TryDetectGeometry(sector0, ReadOnlySpan<byte>.Empty, totalSectors, out geometry);
        }

        /// <summary>
        /// Works out a disk's geometry from its sector 0 and the boot sector of its first partition
        /// (see <see cref="TryDetectGeometry(string, out DiskGeometry)"/>).
        /// </summary>
        /// <param name="sector0">
        /// The disk's first 512 bytes.
        /// </param>
        /// <param name="partitionBootSector">
        /// The first 512 bytes of the first used partition, or empty when there is none.
        /// </param>
        /// <param name="totalSectors">
        /// The disk size in sectors.
        /// </param>
        /// <param name="geometry">
        /// The geometry, when found.
        /// </param>
        /// <returns>
        /// <c>true</c> when the geometry was found.
        /// </returns>
        public static bool TryDetectGeometry(ReadOnlySpan<byte> sector0, ReadOnlySpan<byte> partitionBootSector, long totalSectors, out DiskGeometry geometry)
        {
            geometry = default;
            if (sector0.Length < FixedVhd.SectorBytes || sector0[510] != 0x55 || sector0[511] != 0xAA)
            {
                return false;
            }

            int heads;
            int spt;
            if (!TryBootSectorGeometry(sector0, out heads, out spt)
                && !TryPartitionTableGeometry(sector0, out heads, out spt)
                && !TryBootSectorGeometry(partitionBootSector, out heads, out spt))
            {
                return false;
            }

            long cylinders = totalSectors / ((long)heads * spt);
            if (cylinders < 1)
            {
                return false;
            }
            if (cylinders > ushort.MaxValue)
            {
                cylinders = ushort.MaxValue;
            }
            geometry = new DiskGeometry((int)cylinders, heads, spt);
            return true;
        }
        /// <summary>
        /// Converts in the direction the source asks for: a fixed VHD becomes raw, a raw image becomes a
        /// fixed VHD.
        /// </summary>
        /// <param name="source">
        /// The image to convert. It is not changed.
        /// </param>
        /// <param name="target">
        /// The new file. It must not exist.
        /// </param>
        /// <param name="geometry">
        /// For raw to VHD: the geometry to record, or <c>null</c> to read it from the disk.
        /// Not used for VHD to raw.
        /// </param>
        /// <returns>
        /// <c>true</c> when a VHD was written, <c>false</c> when a raw image was written.
        /// </returns>
        public static bool Convert(string source, string target, DiskGeometry? geometry)
        {
            if (IsFixedVhd(source))
            {
                FixedVhdToRaw(source, target);
                return false;
            }
            RawToFixedVhd(source, target, geometry);
            return true;
        }

        /// <summary>
        /// Writes a fixed VHD: the raw image's bytes, then a new footer.
        /// </summary>
        /// <param name="source">
        /// The raw image. It is not changed.
        /// </param>
        /// <param name="target">
        /// The new VHD file. It must not exist.
        /// </param>
        /// <param name="geometry">
        /// The geometry to record, or <c>null</c> to read it from the disk.
        /// </param>
        /// <exception cref="IOException">
        /// Thrown when the target exists or is the source.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// Thrown when the source is not a raw image (it is already a VHD, is empty, or is not a whole
        /// number of 512-byte sectors), or its geometry cannot be read and none was given.
        /// </exception>
        public static void RawToFixedVhd(string source, string target, DiskGeometry? geometry)
        {
            CheckPaths(source, target);
            // Shared for writing too: an emulator that is switched off may still hold the disk open
            // for writing (RetroCore keeps a mounted disk open that way). Only read here.
            using FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            VhdProbeResult kind = FixedVhd.Probe(input, out _, out string? problem);
            if (kind == VhdProbeResult.Fixed)
            {
                throw new InvalidDataException($"{source} is already a fixed VHD.");
            }
            if (kind == VhdProbeResult.NotUsable)
            {
                throw new InvalidDataException($"{source}: {problem}.");
            }
            long diskBytes = input.Length;
            if (diskBytes <= 0 || diskBytes % FixedVhd.SectorBytes != 0)
            {
                throw new InvalidDataException($"{source} is not a disk image: its size ({diskBytes} bytes) is not a whole number of 512-byte sectors.");
            }

            DiskGeometry g;
            if (geometry.HasValue)
            {
                g = geometry.Value;
            }
            else
            {
                if (!TryDetectGeometry(source, out g))
                {
                    throw new InvalidDataException($"The disk shape of {source} cannot be read from its partition table or boot sector; give the cylinders, heads and sectors per track.");
                }
            }

            byte[] footer = FixedVhd.BuildFooter(diskBytes, g.Cylinders, g.Heads, g.SectorsPerTrack, Guid.NewGuid(), DateTime.UtcNow);
            WriteNew(target, input, diskBytes, footer);
        }

        /// <summary>
        /// Writes a raw image: the fixed VHD's disk bytes, without the footer.
        /// </summary>
        /// <param name="source">
        /// The fixed VHD. It is not changed.
        /// </param>
        /// <param name="target">
        /// The new raw file. It must not exist.
        /// </param>
        /// <exception cref="IOException">
        /// Thrown when the target exists or is the source.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// Thrown when the source is not a usable fixed VHD.
        /// </exception>
        public static void FixedVhdToRaw(string source, string target)
        {
            CheckPaths(source, target);
            // Shared for writing too: an emulator that is switched off may still hold the disk open
            // for writing (RetroCore keeps a mounted disk open that way). Only read here.
            using FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            VhdProbeResult kind = FixedVhd.Probe(input, out FixedVhdFooter footer, out string? problem);
            if (kind == VhdProbeResult.NotVhd)
            {
                throw new InvalidDataException($"{source} is not a VHD: it has no VHD footer at its end.");
            }
            if (kind == VhdProbeResult.NotUsable)
            {
                throw new InvalidDataException($"{source}: {problem}.");
            }
            WriteNew(target, input, footer.DiskBytes, null);
        }

        // A FAT boot sector: a jump (EB xx or E9 xx xx), 512 bytes per sector, the 55h AAh
        // signature, and believable sectors per track (offset 24) and heads (offset 26).
        private static bool TryBootSectorGeometry(ReadOnlySpan<byte> s, out int heads, out int spt)
        {
            heads = 0;
            spt = 0;
            if (s.Length < FixedVhd.SectorBytes || s[510] != 0x55 || s[511] != 0xAA)
            {
                return false;
            }
            bool jump = s[0] == 0xEB || s[0] == 0xE9;
            int bytesPerSector = s[11] | (s[12] << 8);
            int t = s[24] | (s[25] << 8);
            int h = s[26] | (s[27] << 8);
            if (!jump || bytesPerSector != 512 || t < 1 || t > 63 || h < 1 || h > 255)
            {
                return false;
            }
            heads = h;
            spt = t;
            return true;
        }

        // The used partition table entry with the most heads whose end position (cylinder, head,
        // sector) is the same place as its end sector number, or is capped at cylinder 1023 on a
        // partition that ends beyond what 1024 cylinders reach.
        private static bool TryPartitionTableGeometry(ReadOnlySpan<byte> sector0, out int heads, out int spt)
        {
            heads = 0;
            spt = 0;
            for (int i = 0; i < 4; i++)
            {
                int e = MbrTableOffset + i * MbrEntryBytes;
                if (sector0[e + 4] == 0)
                {
                    continue;
                }
                int endHead = sector0[e + 5];
                int endSector = sector0[e + 6] & 0x3F;
                int endCylinder = ((sector0[e + 6] & 0xC0) << 2) | sector0[e + 7];
                long start = ReadUInt32LE(sector0, e + 8);
                long count = ReadUInt32LE(sector0, e + 12);
                if (endSector < 1 || count == 0)
                {
                    continue;
                }
                int h = endHead + 1;
                long endLba = start + count - 1;
                long chsLba = ((long)endCylinder * h + endHead) * endSector + endSector - 1;
                bool agrees = chsLba == endLba || (endCylinder == 1023 && endLba > chsLba);
                if (agrees && h > heads)
                {
                    heads = h;
                    spt = endSector;
                }
            }
            return heads > 0;
        }

        private static long ReadUInt32LE(ReadOnlySpan<byte> b, int at)
        {
            return (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));
        }

        private static void CheckPaths(string source, string target)
        {
            if (string.IsNullOrEmpty(source)) throw new ArgumentException("No source file given.", nameof(source));
            if (string.IsNullOrEmpty(target)) throw new ArgumentException("No target file given.", nameof(target));
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The new file must not be the file being converted.");
            }
            if (!File.Exists(source))
            {
                throw new FileNotFoundException($"{source} does not exist.", source);
            }
            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new IOException($"{target} already exists; the conversion always writes a new file.");
            }
        }

        // Copies the first diskBytes of input into a new file, then the footer if there is one.
        // A target that fails part way is deleted, so no half-written image is left behind.
        private static void WriteNew(string target, Stream input, long diskBytes, byte[]? footer)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyChunkBytes);
            bool done = false;
            FileStream? output = null;
            try
            {
                output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.Position = 0;
                long left = diskBytes;
                while (left > 0)
                {
                    int want = (int)Math.Min(left, CopyChunkBytes);
                    input.ReadExactly(buffer, 0, want);
                    output.Write(buffer, 0, want);
                    left -= want;
                }
                if (footer != null)
                {
                    output.Write(footer, 0, footer.Length);
                }
                output.Flush(true);
                done = true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                if (output != null)
                {
                    output.Dispose();
                    if (!done)
                    {
                        File.Delete(target);
                    }
                }
            }
        }
    }
}
