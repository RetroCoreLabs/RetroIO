//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers.Binary;
using System.IO;

namespace RetroIO.Storage.Local.Images
{
    /// <summary>
    /// Reads and writes the fixed VHD hard-disk image format: the raw disk sectors, followed by one
    /// 512-byte footer that records the disk size and its cylinders, heads and sectors per track.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The authority is Microsoft's "Virtual Hard Disk Image Format Specification", version 1.0
    /// (published under the Open Specification Promise). Every value in the footer is big-endian
    /// (the spec says "network byte order"). The footer fields, in order, as offset and size in bytes:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>0, 8: cookie, the ASCII text <c>conectix</c>.</description></item>
    ///   <item><description>8, 4: features. Bit 1 (0x00000002) is reserved and "must always be set to 1"; bit 0 marks a temporary disk.</description></item>
    ///   <item><description>12, 4: file format version, 0x00010000.</description></item>
    ///   <item><description>16, 8: data offset. Unused by fixed disks; the spec says it "should be set to 0xFFFFFFFF", and this class writes all eight bytes as 0xFF.</description></item>
    ///   <item><description>24, 4: creation time, seconds since 2000-01-01 00:00:00 UTC.</description></item>
    ///   <item><description>28, 4: creator application, four ASCII characters.</description></item>
    ///   <item><description>32, 4: creator version.</description></item>
    ///   <item><description>36, 4: creator host OS, 0x5769326B ("Wi2k") or 0x4D616320 ("Mac ").</description></item>
    ///   <item><description>40, 8: original size in bytes. 48, 8: current size in bytes.</description></item>
    ///   <item><description>56, 4: geometry: cylinders (2 bytes), heads (1), sectors per track (1).</description></item>
    ///   <item><description>60, 4: disk type: 2 fixed, 3 dynamic, 4 differencing.</description></item>
    ///   <item><description>64, 4: checksum, the one's complement of the sum of every footer byte except these four.</description></item>
    ///   <item><description>68, 16: unique id. 84, 1: saved state. 85, 427: reserved, zero.</description></item>
    /// </list>
    /// <para>
    /// The same field layout and values as RetroCore's PC/AT hard-disk code writes, so a VHD made there
    /// opens here. The spec also says that Virtual PC versions before 2004 wrote a 511-byte footer, so
    /// the footer "can exist in the last 511 or 512 bytes" of the file; <see cref="Probe"/> accepts both.
    /// </para>
    /// <para>
    /// Only fixed disks are read. A dynamic or differencing VHD keeps its sectors in blocks scattered
    /// through the file; reading one as if it were raw sectors would give garbage, so <see cref="Probe"/>
    /// reports it as not usable, with the reason.
    /// </para>
    /// </remarks>
    public static class FixedVhd
    {
        /// <summary>
        /// Size of the footer this class writes, and that every VHD since Virtual PC 2004 has.
        /// </summary>
        public const int FooterBytes = 512;

        /// <summary>
        /// Size of the footer written by Virtual PC versions before 2004.
        /// </summary>
        public const int OldFooterBytes = 511;

        /// <summary>
        /// The footer's disk type value for a fixed disk.
        /// </summary>
        public const uint DiskTypeFixed = 2;

        /// <summary>
        /// The footer's disk type value for a dynamic disk.
        /// </summary>
        public const uint DiskTypeDynamic = 3;

        /// <summary>
        /// The footer's disk type value for a differencing disk.
        /// </summary>
        public const uint DiskTypeDifferencing = 4;

        /// <summary>
        /// Bytes in one disk sector.
        /// </summary>
        public const int SectorBytes = 512;

        // Where each field sits in the footer (see the class remarks).
        private const int CookieOffset = 0;
        private const int FeaturesOffset = 8;
        private const int VersionOffset = 12;
        private const int DataOffsetOffset = 16;
        private const int TimeStampOffset = 24;
        private const int CreatorAppOffset = 28;
        private const int CreatorVersionOffset = 32;
        private const int CreatorHostOffset = 36;
        private const int OriginalSizeOffset = 40;
        private const int CurrentSizeOffset = 48;
        private const int GeometryOffset = 56;
        private const int DiskTypeOffset = 60;
        private const int ChecksumOffset = 64;
        private const int UniqueIdOffset = 68;

        private const uint FeatureReservedBit = 0x00000002;
        private const uint FormatVersion = 0x00010000;
        private const uint HostWindows = 0x5769326B;   // "Wi2k"

        // "conectix", the cookie every VHD footer starts with.
        private static ReadOnlySpan<byte> Cookie => new byte[] { 0x63, 0x6F, 0x6E, 0x65, 0x63, 0x74, 0x69, 0x78 };

        // "rio ", the creator application RetroIO writes. The spec asks every application to use its own.
        private static ReadOnlySpan<byte> CreatorApp => new byte[] { 0x72, 0x69, 0x6F, 0x20 };

        // The VHD clock starts at 2000-01-01 00:00:00 UTC.
        private static readonly DateTime Epoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Looks at the end of an image to see whether it is a fixed VHD, and if so reads its footer.
        /// </summary>
        /// <remarks>
        /// The file name plays no part: only the bytes at the end decide. The stream position is left
        /// where it was. A file with no <c>conectix</c> cookie at its end is a raw image and reported as
        /// <see cref="VhdProbeResult.NotVhd"/>. A footer with a bad checksum, a disk type other than fixed,
        /// or a size that does not match the file is reported as <see cref="VhdProbeResult.NotUsable"/>
        /// with the reason in plain words.
        /// </remarks>
        /// <param name="image">
        /// The image, readable and seekable.
        /// </param>
        /// <param name="footer">
        /// The footer's contents when the result is <see cref="VhdProbeResult.Fixed"/>.
        /// </param>
        /// <param name="problem">
        /// Why the VHD cannot be used, when the result is <see cref="VhdProbeResult.NotUsable"/>.
        /// </param>
        /// <returns>
        /// What the image is.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="image"/> is null.
        /// </exception>
        public static VhdProbeResult Probe(Stream image, out FixedVhdFooter footer, out string? problem)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            footer = default;
            problem = null;

            long length = image.Length;
            Span<byte> tail = stackalloc byte[FooterBytes];
            long saved = image.Position;
            try
            {
                // The 512-byte footer first, then the old 511-byte one.
                int size = FooterBytes;
                while (size >= OldFooterBytes)
                {
                    if (length >= size)
                    {
                        Span<byte> t = tail.Slice(0, size);
                        image.Position = length - size;
                        ReadExactly(image, t);
                        if (t.Slice(CookieOffset, 8).SequenceEqual(Cookie))
                        {
                            return Judge(t, length - size, out footer, out problem);
                        }
                    }
                    size--;
                }
                return VhdProbeResult.NotVhd;
            }
            finally
            {
                image.Position = saved;
            }
        }

        /// <summary>
        /// Builds the 512-byte footer of a fixed disk.
        /// </summary>
        /// <remarks>
        /// The original and current size are <paramref name="diskBytes"/>, which need not equal
        /// cylinders x heads x sectors x 512: the spec's own geometry rule rounds down, so a disk often
        /// holds a few more sectors than its geometry describes.
        /// </remarks>
        /// <param name="diskBytes">
        /// Size of the disk data in bytes, a whole number of 512-byte sectors.
        /// </param>
        /// <param name="cylinders">
        /// Cylinders, 1 to 65535.
        /// </param>
        /// <param name="heads">
        /// Heads, 1 to 255.
        /// </param>
        /// <param name="sectorsPerTrack">
        /// Sectors per track, 1 to 255.
        /// </param>
        /// <param name="uniqueId">
        /// The disk's unique id.
        /// </param>
        /// <param name="createdUtc">
        /// The creation time, in UTC, not before 2000-01-01.
        /// </param>
        /// <returns>
        /// The footer bytes.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when a value does not fit its footer field.
        /// </exception>
        public static byte[] BuildFooter(long diskBytes, int cylinders, int heads, int sectorsPerTrack, Guid uniqueId, DateTime createdUtc)
        {
            CheckGeometry(cylinders, heads, sectorsPerTrack);
            if (diskBytes <= 0 || diskBytes % SectorBytes != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(diskBytes), "A disk holds a whole number of 512-byte sectors, at least one.");
            }

            double seconds = (createdUtc.ToUniversalTime() - Epoch).TotalSeconds;
            if (seconds < 0 || seconds > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(createdUtc), "A VHD time stamp counts seconds from 2000-01-01 in 32 bits.");
            }

            byte[] f = new byte[FooterBytes];
            Span<byte> s = f;
            Cookie.CopyTo(s.Slice(CookieOffset));
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(FeaturesOffset), FeatureReservedBit);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(VersionOffset), FormatVersion);
            BinaryPrimitives.WriteUInt64BigEndian(s.Slice(DataOffsetOffset), ulong.MaxValue);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(TimeStampOffset), (uint)seconds);
            CreatorApp.CopyTo(s.Slice(CreatorAppOffset));
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(CreatorVersionOffset), 0x00010000);
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(CreatorHostOffset), HostWindows);
            BinaryPrimitives.WriteUInt64BigEndian(s.Slice(OriginalSizeOffset), (ulong)diskBytes);
            BinaryPrimitives.WriteUInt64BigEndian(s.Slice(CurrentSizeOffset), (ulong)diskBytes);
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(GeometryOffset), (ushort)cylinders);
            s[GeometryOffset + 2] = (byte)heads;
            s[GeometryOffset + 3] = (byte)sectorsPerTrack;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(DiskTypeOffset), DiskTypeFixed);
            // A UUID in its standard (big-endian) byte order, like every other field of the footer.
            uniqueId.TryWriteBytes(s.Slice(UniqueIdOffset, 16), bigEndian: true, out _);
            // Saved state (offset 84) and the reserved bytes stay zero.
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(ChecksumOffset), Checksum(s));
            return f;
        }

        /// <summary>
        /// Computes the spec's footer checksum: the sum of every footer byte except the four checksum
        /// bytes, inverted (one's complement).
        /// </summary>
        /// <param name="footer">
        /// The footer, 511 or 512 bytes.
        /// </param>
        /// <returns>
        /// The checksum.
        /// </returns>
        public static uint Checksum(ReadOnlySpan<byte> footer)
        {
            uint sum = 0;
            for (int i = 0; i < footer.Length; i++)
            {
                if (i >= ChecksumOffset && i < ChecksumOffset + 4) continue;
                sum += footer[i];
            }
            return ~sum;
        }

        /// <summary>
        /// The cylinders, heads and sectors per track the spec's appendix gives for a disk of
        /// <paramref name="totalSectors"/> sectors.
        /// </summary>
        /// <remarks>
        /// The pseudo-code of the spec's appendix "CHS Calculation", line for line. The product is
        /// rounded down, so the geometry can hold fewer sectors than asked for; the spec says so too.
        /// </remarks>
        /// <param name="totalSectors">
        /// Disk size in 512-byte sectors.
        /// </param>
        /// <returns>
        /// The geometry.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="totalSectors"/> is negative.
        /// </exception>
        public static DiskGeometry ComputeGeometry(long totalSectors)
        {
            if (totalSectors < 0) throw new ArgumentOutOfRangeException(nameof(totalSectors));
            long cylinderTimesHeads;
            long h;
            long spt;

            if (totalSectors > 65535L * 16 * 255)
            {
                totalSectors = 65535L * 16 * 255;
            }

            if (totalSectors >= 65535L * 16 * 63)
            {
                spt = 255;
                h = 16;
                cylinderTimesHeads = totalSectors / spt;
            }
            else
            {
                spt = 17;
                cylinderTimesHeads = totalSectors / spt;
                h = (cylinderTimesHeads + 1023) / 1024;
                if (h < 4)
                {
                    h = 4;
                }
                if (cylinderTimesHeads >= (h * 1024) || h > 16)
                {
                    spt = 31;
                    h = 16;
                    cylinderTimesHeads = totalSectors / spt;
                }
                if (cylinderTimesHeads >= (h * 1024))
                {
                    spt = 63;
                    h = 16;
                    cylinderTimesHeads = totalSectors / spt;
                }
            }

            return new DiskGeometry((int)(cylinderTimesHeads / h), (int)h, (int)spt);
        }

        // Checks a footer that has the cookie, and says whether it describes a usable fixed disk.
        private static VhdProbeResult Judge(ReadOnlySpan<byte> t, long dataBytes, out FixedVhdFooter footer, out string? problem)
        {
            footer = default;
            problem = null;

            uint stored = BinaryPrimitives.ReadUInt32BigEndian(t.Slice(ChecksumOffset));
            uint computed = Checksum(t);
            if (stored != computed)
            {
                // The spec lets a reader fall back to the copy at the front of the file, which only
                // dynamic and differencing disks have. A fixed disk has no second copy.
                problem = $"the VHD footer's checksum is wrong (stored 0x{stored:X8}, computed 0x{computed:X8}), so the file is damaged";
                return VhdProbeResult.NotUsable;
            }

            uint diskType = BinaryPrimitives.ReadUInt32BigEndian(t.Slice(DiskTypeOffset));
            if (diskType != DiskTypeFixed)
            {
                string kind = diskType == DiskTypeDynamic ? "a dynamic (growing)"
                    : diskType == DiskTypeDifferencing ? "a differencing"
                    : "an unknown kind of (type " + diskType + ")";
                problem = $"this is {kind} VHD; only fixed VHD files can be used. Convert it to a fixed VHD first";
                return VhdProbeResult.NotUsable;
            }

            ulong currentSize = BinaryPrimitives.ReadUInt64BigEndian(t.Slice(CurrentSizeOffset));
            if (currentSize != (ulong)dataBytes)
            {
                problem = $"the VHD footer says the disk holds {currentSize} bytes, but the file holds {dataBytes} bytes before the footer";
                return VhdProbeResult.NotUsable;
            }

            ushort cylinders = BinaryPrimitives.ReadUInt16BigEndian(t.Slice(GeometryOffset));
            byte heads = t[GeometryOffset + 2];
            byte spt = t[GeometryOffset + 3];
            footer = new FixedVhdFooter(dataBytes, t.Length, new DiskGeometry(cylinders, heads, spt),
                new Guid(t.Slice(UniqueIdOffset, 16), bigEndian: true));
            return VhdProbeResult.Fixed;
        }

        private static void CheckGeometry(int cylinders, int heads, int sectorsPerTrack)
        {
            if (cylinders < 1 || cylinders > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(cylinders), "A VHD stores 1 to 65535 cylinders.");
            if (heads < 1 || heads > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(heads), "A VHD stores 1 to 255 heads.");
            if (sectorsPerTrack < 1 || sectorsPerTrack > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(sectorsPerTrack), "A VHD stores 1 to 255 sectors per track.");
        }

        private static void ReadExactly(Stream s, Span<byte> buffer)
        {
            int done = 0;
            while (done < buffer.Length)
            {
                int n = s.Read(buffer.Slice(done));
                if (n <= 0) throw new EndOfStreamException("The image ended inside its VHD footer.");
                done += n;
            }
        }
    }

    /// <summary>
    /// What <see cref="FixedVhd.Probe"/> found at the end of an image.
    /// </summary>
    public enum VhdProbeResult
    {
        /// <summary>No VHD footer: a raw image, every byte a disk byte.</summary>
        NotVhd,

        /// <summary>A fixed VHD: the disk bytes, then the footer.</summary>
        Fixed,

        /// <summary>A VHD footer that cannot be used (damaged, or a dynamic or differencing VHD).</summary>
        NotUsable,
    }

    /// <summary>
    /// A disk's cylinders, heads and sectors per track.
    /// </summary>
    public readonly struct DiskGeometry
    {
        /// <summary>
        /// Creates a geometry.
        /// </summary>
        /// <param name="cylinders">
        /// Cylinders.
        /// </param>
        /// <param name="heads">
        /// Heads.
        /// </param>
        /// <param name="sectorsPerTrack">
        /// Sectors per track.
        /// </param>
        public DiskGeometry(int cylinders, int heads, int sectorsPerTrack)
        {
            Cylinders = cylinders;
            Heads = heads;
            SectorsPerTrack = sectorsPerTrack;
        }

        /// <summary>
        /// Gets the cylinders.
        /// </summary>
        public int Cylinders { get; }

        /// <summary>
        /// Gets the heads.
        /// </summary>
        public int Heads { get; }

        /// <summary>
        /// Gets the sectors per track.
        /// </summary>
        public int SectorsPerTrack { get; }

        /// <summary>
        /// Returns the geometry as "cylinders/heads/sectors", for example <c>615/4/17</c>.
        /// </summary>
        /// <returns>
        /// The text.
        /// </returns>
        public override string ToString() => Cylinders + "/" + Heads + "/" + SectorsPerTrack;
    }

    /// <summary>
    /// The contents of a fixed VHD's footer that a reader needs.
    /// </summary>
    public readonly struct FixedVhdFooter
    {
        /// <summary>
        /// Creates the footer contents.
        /// </summary>
        /// <param name="diskBytes">
        /// Bytes of disk data before the footer.
        /// </param>
        /// <param name="footerBytes">
        /// Size of the footer, 512 or 511.
        /// </param>
        /// <param name="geometry">
        /// The geometry recorded in the footer.
        /// </param>
        /// <param name="uniqueId">
        /// The disk's unique id.
        /// </param>
        public FixedVhdFooter(long diskBytes, int footerBytes, DiskGeometry geometry, Guid uniqueId)
        {
            DiskBytes = diskBytes;
            FooterBytes = footerBytes;
            Geometry = geometry;
            UniqueId = uniqueId;
        }

        /// <summary>
        /// Gets the bytes of disk data before the footer. A reader sees exactly this many bytes.
        /// </summary>
        public long DiskBytes { get; }

        /// <summary>
        /// Gets the size of the footer: 512, or 511 for a file from Virtual PC before 2004.
        /// </summary>
        public int FooterBytes { get; }

        /// <summary>
        /// Gets the geometry recorded in the footer.
        /// </summary>
        public DiskGeometry Geometry { get; }

        /// <summary>
        /// Gets the disk's unique id.
        /// </summary>
        public Guid UniqueId { get; }
    }
}
