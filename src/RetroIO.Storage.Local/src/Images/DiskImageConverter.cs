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
        /// For a fixed VHD the geometry in its footer is used. Otherwise sector 0 is read: a FAT boot
        /// sector gives heads and sectors per track in its parameter block (offsets 24 and 26); a
        /// partition table gives them as the end head and end sector of a used partition (head + 1,
        /// sector bits 0-5). Cylinders are then the disk sectors divided by heads x sectors, at most
        /// 65535. A disk with neither gives <c>false</c>, and the caller must ask for the geometry.
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

            byte[] sector = new byte[FixedVhd.SectorBytes];
            file.Position = 0;
            file.ReadExactly(sector, 0, sector.Length);
            return TryDetectGeometry(sector, diskBytes / FixedVhd.SectorBytes, out geometry);
        }

        /// <summary>
        /// Works out a disk's geometry from its sector 0 (see <see cref="TryDetectGeometry(string, out DiskGeometry)"/>).
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
            geometry = default;
            if (sector0.Length < FixedVhd.SectorBytes || sector0[510] != 0x55 || sector0[511] != 0xAA)
            {
                return false;
            }

            int heads = 0;
            int spt = 0;

            // A FAT boot sector starts with a jump (EB xx or E9 xx xx) and says 512 bytes per sector.
            bool jump = sector0[0] == 0xEB || sector0[0] == 0xE9;
            int bytesPerSector = sector0[11] | (sector0[12] << 8);
            if (jump && bytesPerSector == 512)
            {
                spt = sector0[24] | (sector0[25] << 8);
                heads = sector0[26] | (sector0[27] << 8);
            }

            if (heads < 1 || heads > 255 || spt < 1 || spt > 63)
            {
                heads = 0;
                spt = 0;
                // A partition table: the end of a used partition sits at the last head and sector.
                for (int i = 0; i < 4; i++)
                {
                    int e = MbrTableOffset + i * MbrEntryBytes;
                    byte type = sector0[e + 4];
                    if (type == 0)
                    {
                        continue;
                    }
                    int endHead = sector0[e + 5];
                    int endSector = sector0[e + 6] & 0x3F;
                    if (endSector >= 1 && endHead + 1 > heads)
                    {
                        heads = endHead + 1;
                        spt = endSector;
                    }
                }
            }

            if (heads < 1 || heads > 255 || spt < 1 || spt > 63)
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
            using FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
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
                byte[] sector = new byte[FixedVhd.SectorBytes];
                input.Position = 0;
                input.ReadExactly(sector, 0, sector.Length);
                if (!TryDetectGeometry(sector, diskBytes / FixedVhd.SectorBytes, out g))
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
            using FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
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
