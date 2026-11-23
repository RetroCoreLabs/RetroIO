//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.FileSystems.Cpm;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Reads and writes CP/M disks: Commodore 128 CP/M 3 disks on 1541 (D64), 1571 (D71) and 1581
    /// (D81) images, Commodore 64 CP/M 2.2 disks (D64), Kaypro II and Kaypro 4 raw images, and Amstrad CPC
    /// system and data format disks in DSK files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Files of user 0 are in the root; files of user 1 to 15 are in folders <c>USER1</c> to
    /// <c>USER15</c>, which appear only when they hold files.
    /// </para>
    /// <para>
    /// A C128 CP/M disk is recognised the way the C128 BIOS does it at log-in (CXDISK,
    /// <c>login$154X</c>, and the Z80 ROM routine at 046Bh): track 1 sector 0 starts with "CBM",
    /// and its last byte is FFh on a double-sided disk. The layouts are the BIOS's own:
    ///  - single-sided: <c>dpb 1024,1,170,1024,64,0</c> (1 KB blocks, 64 directory entries);
    ///  - double-sided: <c>dpb 1024,1,340,2048,128,0</c> (2 KB blocks, 128 directory entries).
    /// Where each record lies is <see cref="C128CpmRecordMap"/>.
    /// </para>
    /// <para>
    /// A 1581 disk carries no CP/M mark: in CP/M mode the C128 BIOS picks its layout from the
    /// drive's sector size alone (CXDISK, <c>commodore$type</c>). Here a D81 image is read as CP/M
    /// when its first 4 KB, the CP/M directory, passes <see cref="CpmFileSystem.LooksLikeDirectory"/>.
    /// The layout is <c>dpb 1024,5,159,2048,128,0</c>, placed by <see cref="C1581CpmRecordMap"/>.
    /// </para>
    /// <para>
    /// A 1541 disk without the "CBM" mark is, to the C128 BIOS, a C64 CP/M 2.2 disk (CXDISK
    /// <c>login$154X</c> keeps the C64 layout when <c>FR$check$CBM</c> fails). Here it is read so
    /// only when its directory on track 3 passes <see cref="CpmFileSystem.LooksLikeDirectory"/>.
    /// The layout is <c>dpb 256,17,34,1024,64,3</c>, placed by <see cref="C64CpmRecordMap"/>.
    /// </para>
    /// <para>
    /// Files can be written, deleted and renamed; each change is saved back to the image stream
    /// straight away. CP/M 3 time stamps are not written yet.
    /// </para>
    /// </remarks>
    public sealed class CpmDriver : IFileSystemDriver
    {
        // Kaypro ROM 81-232, 0165h: Kaypro II SSDD DPB (SPT 40, BSH 3, BLM 7, EXM 0, DSM 194, DRM 63,
        // AL0 F0h, AL1 0, CKS 16, OFF 1).
        private static readonly byte[] KayproIiDpb = { 0x28, 0x00, 0x03, 0x07, 0x00, 0xC2, 0x00, 0x3F, 0x00, 0xF0, 0x00, 0x10, 0x00, 0x01, 0x00 };

        // Kaypro ROM 81-232, 0186h: Kaypro 4 DSDD DPB (SPT 40, BSH 4, BLM 15, EXM 1, DSM 196, DRM 63,
        // AL0 C0h, AL1 0, CKS 16, OFF 1).
        private static readonly byte[] Kaypro4Dpb = { 0x28, 0x00, 0x04, 0x0F, 0x01, 0xC4, 0x00, 0x3F, 0x00, 0xC0, 0x00, 0x10, 0x00, 0x01, 0x00 };

        // TIKO 4.01 kernel (boot disk 1736h): drive A DPB for the 90 KB disk (SPT 18, BSH 3, BLM 7,
        // EXM 0, DSM 82, DRM 31, AL0 80h, AL1 0, CKS 8, OFF 3).
        private static readonly byte[] Tiki90Dpb = { 0x12, 0x00, 0x03, 0x07, 0x00, 0x52, 0x00, 0x1F, 0x00, 0x80, 0x00, 0x08, 0x00, 0x03, 0x00 };

        // TIKO 4.01 kernel (boot disk 1708h): the drive A skew table, logical to physical sector.
        private static readonly byte[] Tiki90Skew = { 0x01, 0x06, 0x0B, 0x10, 0x03, 0x08, 0x0D, 0x12, 0x05, 0x0A, 0x0F, 0x02, 0x07, 0x0C, 0x11, 0x04, 0x09, 0x0E };

        // TIKO 4.01 kernel (boot disk 1754h): drive C DPB for the 400 KB disk (SPT 80, BSH 4, BLM 15,
        // EXM 1, DSM 194, DRM 127, AL0 C0h, AL1 0, CKS 32, OFF 1).
        // TIKO kernel on the TIKI-100 hard disk (its system cylinder, 1745h): the 8 MB hard disk DPB
        // (SPT 128, BSH 6, BLM 63, EXM 3, DSM 1021, DRM 2047, AL0 FFh, AL1 0, CKS 0, OFF 1).
        private static readonly byte[] TikiHardDiskDpb = { 0x80, 0x00, 0x06, 0x3F, 0x03, 0xFD, 0x03, 0xFF, 0x07, 0xFF, 0x00, 0x00, 0x00, 0x01, 0x00 };

        private static readonly byte[] Tiki400Dpb = { 0x50, 0x00, 0x04, 0x0F, 0x01, 0xC2, 0x00, 0x7F, 0x00, 0xC0, 0x00, 0x20, 0x00, 0x01, 0x00 };

        private CpmFileSystem? _fileSystem;
        private Stream? _stream;
        private ImdDiskImage? _imd;
        private bool _readOnly = true;

        private const string UserFolderPrefix = "USER";

        /// <summary>
        /// Gets the filesystem type name, "CPM".
        /// </summary>
        public string FileSystemType => "CPM";

        /// <summary>
        /// Gets the separator between name and type, a dot.
        /// </summary>
        public string ExtensionSeparator => ".";

        /// <summary>
        /// Gets the CP/M 3 disk label, or an empty string.
        /// </summary>
        public string VolumeLabel => _fileSystem?.Label ?? string.Empty;

        /// <summary>
        /// Gets what this driver can do: read and list, and write, delete and rename on a writable mount.
        /// </summary>
        public DriverCapabilities Capabilities => _readOnly
            ? DriverCapabilities.Read | DriverCapabilities.ListDirectory
            : DriverCapabilities.Read | DriverCapabilities.ListDirectory | DriverCapabilities.Write | DriverCapabilities.Delete | DriverCapabilities.Rename;

        /// <summary>
        /// Gets a value indicating whether files can be read; always <c>true</c>.
        /// </summary>
        public bool CanRead => true;

        /// <summary>
        /// Gets a value indicating whether files can be written: the disk was mounted writable on a
        /// writable stream.
        /// </summary>
        public bool CanWrite => _fileSystem != null && !_readOnly;

        /// <summary>
        /// Gets a value indicating whether raw blocks can be read; <c>false</c>.
        /// </summary>
        public bool HasBlockAccess => false;

        /// <summary>
        /// Tells whether an image's first sector carries the C128 CP/M mark, and how many sides it has.
        /// </summary>
        /// <remarks>
        /// The test of the C128 Z80 ROM routine at 046Bh: bytes 0-2 are "CBM"; the routine then
        /// returns byte 255, which is FFh on a double-sided disk.
        /// </remarks>
        /// <param name="firstSector">
        /// Track 1 sector 0: the first 256 bytes of the image.
        /// </param>
        /// <param name="doubleSided">
        /// Set to <c>true</c> when byte 255 is FFh.
        /// </param>
        /// <returns>
        /// <c>true</c> when the sector starts with "CBM".
        /// </returns>
        public static bool IsC128CpmBootSector(ReadOnlySpan<byte> firstSector, out bool doubleSided)
        {
            doubleSided = false;
            if (firstSector.Length < 256 || firstSector[0] != (byte)'C' || firstSector[1] != (byte)'B' || firstSector[2] != (byte)'M')
            {
                return false;
            }

            doubleSided = firstSector[255] == 0xFF;
            return true;
        }

        /// <summary>
        /// Tells whether an image length is a D81 image: 3200 sectors of 256 bytes, with or without
        /// one error byte per sector.
        /// </summary>
        /// <param name="length">
        /// Image length in bytes.
        /// </param>
        /// <returns>
        /// <c>true</c> for 819200 or 822400 bytes.
        /// </returns>
        public static bool IsD81Size(long length) => length == 3200 * 256 || length == 3200 * 257;

        /// <summary>
        /// Gives the CP/M disk parameters of an Amstrad CPC system or data format disk.
        /// </summary>
        /// <remarks>
        /// All three formats have 1 KB blocks and 64 directory entries (two blocks): the data area is
        /// the 40 tracks (CPC data format), the 38 after the two system tracks (CPC system format,
        /// DSM 170) or the 39 after one reserved track (PCW, DSM 174, the last half block unused).
        /// PCW: measured on real disks, sixteen blocks to a full 16 KB entry. The directory was found at the start
        /// of the first data track on real system-format disks.
        /// </remarks>
        /// <param name="recordCount">
        /// Number of 128-byte records in the data area.
        /// </param>
        /// <param name="firstSectorId">
        /// 41h (CPC system format), C1h (CPC data format) or 01h (PCW).
        /// </param>
        /// <returns>
        /// The parameters.
        /// </returns>
        public static CpmDiskParameters AmstradParameters(int recordCount, byte firstSectorId)
        {
            return new CpmDiskParameters(1024, recordCount / 8, 64, 0, AmstradCpmRecordMap.ReservedTracks(firstSectorId));
        }

        /// <summary>
        /// Gives the CP/M-86 layout of a 160 KB or 320 KB IBM PC floppy image, if the image is one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One side, 40 tracks of eight 512-byte sectors, stored in order. Measured on the IBM PC
        /// CP/M-86 1.00 system disk: one reserved track (the loader), the directory at 1000h, 1 KB
        /// blocks, 64 entries in blocks 0 and 1, so DSM is 155. Every text file on that disk reads
        /// back clean with this layout.
        /// </para>
        /// <para>
        /// The 320 KB IBM PC-XT disk (CP/M-86 1.10, two sides): measured the same way: one reserved
        /// track, 2 KB blocks, EXM 1, 64 entries in block 0, DSM 157, with the tracks running down side
        /// 0 and back up side 1 (<see cref="SerpentineCpmRecordMap"/>).
        /// </para>
        /// <para>
        /// A DOS disk of the same size has its FAT at 200h, starting with the media byte (F0h-FFh, FEh
        /// for 160 KB, FFh for 320 KB) and FFh FFh; an image that starts that way is not taken as
        /// CP/M-86.
        /// </para>
        /// </remarks>
        /// <param name="length">
        /// Image length in bytes.
        /// </param>
        /// <param name="image">
        /// The image, or at least its first 1 KB.
        /// </param>
        /// <param name="parameters">
        /// The disk parameters, or <c>null</c>.
        /// </param>
        /// <param name="map">
        /// The record map, or <c>null</c>.
        /// </param>
        /// <returns>
        /// <c>true</c> when the image has the size of a 160 KB or 320 KB PC floppy and no DOS FAT.
        /// </returns>
        public static bool TryIbmPcCpm86(long length, ReadOnlySpan<byte> image, out CpmDiskParameters? parameters, out ICpmRecordMap? map)
        {
            const int Track = 8 * 512;
            parameters = null;
            map = null;
            if ((length != 163840 && length != 327680) || image.Length < 0x203 || (image[0x201] == 0xFF && image[0x202] == 0xFF && image[0x200] >= 0xF0))
            {
                return false;
            }

            if (length == 163840)
            {
                parameters = new CpmDiskParameters(1024, 156, 64, 0, 1);
                map = new LinearCpmRecordMap(Track, 156 * 8);
                return true;
            }

            parameters = new CpmDiskParameters(2048, 158, 64, 1, 1);
            map = new SerpentineCpmRecordMap(40, Track, 1, 158 * 16);
            return true;
        }

        /// <summary>
        /// Tells whether an ImageDisk file holds a CP/M disk of a layout this driver knows.
        /// </summary>
        /// <remarks>
        /// The file is decoded and its flat image tried against the layouts that come as ImageDisk
        /// files (see <see cref="TrySelectFlatLayout"/>). Only a disk with at least one file counts, as
        /// an empty directory gives no sign of CP/M.
        /// </remarks>
        /// <param name="path">
        /// Path of the .IMD file.
        /// </param>
        /// <returns>
        /// <c>true</c> when one of the layouts fits.
        /// </returns>
        public static bool ImdLooksLikeCpm(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }

            ImdDiskImage? imd = ImdDiskImage.TryDecode(File.ReadAllBytes(path), out _);
            if (imd == null)
            {
                return false;
            }

            return HasFilesFor(imd.Flat);
        }

        /// <summary>
        /// Picks the CP/M layout of a raw image (or an ImageDisk image laid out flat) of a size that
        /// several machines share.
        /// </summary>
        /// <remarks>
        /// The candidates for the size are tried in this order: CP/M-86 160 KB, Kaypro, Osborne 1,
        /// TIKI-100. Each puts its directory somewhere else (1000h, 1400h, 3C00h, 2800h), so the first
        /// whose directory passes the check and holds a file is taken. When none holds a file (an empty
        /// disk), the first whose directory passes the check is taken: an empty disk does not tell
        /// which machine formatted it.
        /// </remarks>
        /// <param name="length">
        /// Length of the whole flat image in bytes.
        /// </param>
        /// <param name="image">
        /// The flat image, or at least its first 80 KB (every candidate directory lies in it; the TIKI-100
        /// hard disk directory ends at 14000h).
        /// </param>
        /// <param name="parameters">
        /// The chosen disk parameters, or <c>null</c>.
        /// </param>
        /// <param name="map">
        /// The chosen record map, or <c>null</c>.
        /// </param>
        /// <returns>
        /// <c>true</c> when a layout was found.
        /// </returns>
        public static bool TrySelectFlatLayout(long length, byte[] image, out CpmDiskParameters? parameters, out ICpmRecordMap? map)
        {
            parameters = null;
            map = null;
            for (int candidate = 0; candidate < 4; candidate++)
            {
                CpmDiskParameters? p;
                ICpmRecordMap? m;
                bool fits = candidate switch
                {
                    0 => TryIbmPcCpm86(length, image, out p, out m),
                    1 => TryKaypro(length, out p, out m),
                    2 => TryOsborne(length, out p, out m),
                    _ => TryTiki100(length, out p, out m),
                };
                if (!fits || !CpmFileSystem.LooksLikeDirectory(CpmFileSystem.DirectoryBytes(image, p!, m!), p!, out int files))
                {
                    continue;
                }

                if (files > 0)
                {
                    parameters = p;
                    map = m;
                    return true;
                }

                if (parameters == null)
                {
                    // Remember the first empty one, in case no candidate holds a file.
                    parameters = p;
                    map = m;
                }
            }

            return parameters != null;
        }

        /// <summary>
        /// Gives the TIKI-100 (TIKO) layout for a raw image of this length, if there is one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The image is the sectors in order, both sides of a cylinder before the next. The layouts:
        ///  - 90 KB, 40 tracks of 18 sectors of 128 bytes: the DPB and skew table of drive A in the
        ///    TIKO 4.01 kernel (on its boot disk at 1736h and 1708h): SPT 18, 1 KB blocks, DSM 82,
        ///    32 entries in one block, three reserved tracks. Only an empty disk was at hand to check.
        ///  - 200 KB, one side of 40 tracks of ten 512-byte sectors: measured on a real games disk:
        ///    two reserved tracks (directory at 2800h), 1 KB blocks (sixteen to a 16 KB entry),
        ///    64 entries in blocks 0 and 1, so DSM 189.
        ///  - 400 KB, two sides: the DPB of drive C in the TIKO 4.01 kernel (1754h): SPT 80 (one CP/M
        ///    track is both sides of a cylinder), 2 KB blocks, EXM 1, DSM 194, 128 entries, one
        ///    reserved track. The kernel and games disks read back with it.
        /// </para>
        /// <para>
        /// The 8 MB hard disk (256 cylinders, 2 heads, 16 sectors of 512 bytes): the DPB in the TIKO
        /// kernel on the hard disk's own system cylinder (1745h): SPT 128 (one CP/M track is a 16 KB
        /// cylinder), 8 KB blocks, EXM 3, DSM 1021, 2048 entries in eight blocks, one reserved
        /// cylinder. Two real hard disks read back with it, no block in two files.
        /// </para>
        /// <para>
        /// The 800 KB disk (two sides, 80 cylinders) is not in the kernel's tables, as the kernel sets the
        /// drive table when a disk goes in. Measured on a real library disk (FD0054): one reserved
        /// cylinder (directory at 2800h), 2 KB blocks with two-byte block numbers, 128 entries in
        /// blocks 0 and 1, EXM 0; every text file reads back clean. DSM is 394, all of the disk after
        /// the reserved cylinder, the same rule as in the kernel's 400 KB table.
        /// </para>
        /// </remarks>
        /// <param name="length">
        /// Image length in bytes.
        /// </param>
        /// <param name="parameters">
        /// The disk parameters, or <c>null</c>.
        /// </param>
        /// <param name="map">
        /// The record map, or <c>null</c>.
        /// </param>
        /// <returns>
        /// <c>true</c> when the length is a TIKI-100 floppy of a known layout.
        /// </returns>
        public static bool TryTiki100(long length, out CpmDiskParameters? parameters, out ICpmRecordMap? map)
        {
            switch (length)
            {
                case 92160:
                    parameters = CpmDiskParameters.FromDpbBytes(Tiki90Dpb);
                    map = new SkewedCpmRecordMap(3 * 18 * 128, parameters.BlockCount * 8, (byte[])Tiki90Skew.Clone());
                    return true;
                case 204800:
                    parameters = new CpmDiskParameters(1024, 190, 64, 0, 2);
                    map = new LinearCpmRecordMap(2 * 5120, 190 * 8);
                    return true;
                case 8388608:
                    parameters = CpmDiskParameters.FromDpbBytes(TikiHardDiskDpb);
                    map = new LinearCpmRecordMap(16384, parameters.BlockCount * 64);
                    return true;
                case 819200:
                    parameters = new CpmDiskParameters(2048, 395, 128, 0, 1);
                    map = new LinearCpmRecordMap(2 * 5120, 395 * 16);
                    return true;
                case 409600:
                    parameters = CpmDiskParameters.FromDpbBytes(Tiki400Dpb);
                    map = new LinearCpmRecordMap(2 * 5120, parameters.BlockCount * 16);
                    return true;
                default:
                    parameters = null;
                    map = null;
                    return false;
            }
        }

        /// <summary>
        /// Gives the Osborne 1 double-density layout for a flat image of this length, if there is one.
        /// </summary>
        /// <remarks>
        /// Single-sided, 40 tracks of five 1024-byte sectors (200 KB, the same size as a Kaypro II disk).
        /// Measured on three Osborne user group disks (ImageDisk images): three reserved tracks, the
        /// directory at 3C00h, 1 KB blocks and 64 entries, and block 184 in use, so DSM is 184. Every
        /// text file on those disks reads back clean with this layout.
        /// </remarks>
        /// <param name="length">
        /// Flat image length in bytes.
        /// </param>
        /// <param name="parameters">
        /// The disk parameters, or <c>null</c>.
        /// </param>
        /// <param name="map">
        /// The record map, or <c>null</c>.
        /// </param>
        /// <returns>
        /// <c>true</c> when the length is 204800 bytes.
        /// </returns>
        public static bool TryOsborne(long length, out CpmDiskParameters? parameters, out ICpmRecordMap? map)
        {
            if (length != 204800)
            {
                parameters = null;
                map = null;
                return false;
            }

            parameters = new CpmDiskParameters(1024, 185, 64, 0, 3);
            map = new LinearCpmRecordMap(3 * 5120, 185 * 8);
            return true;
        }

        /// <summary>
        /// Gives the Kaypro layout for a raw Kaypro disk image of this length, if there is one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The Disk Parameter Blocks are copied byte for byte from the Kaypro ROM 81-232 (Kaypro 4),
        /// which holds them at 0165h and 0186h; the Kaypro II ROM 81-149c has the same SSDD table at
        /// 0165h:
        ///  - Kaypro II, single-sided double density, 40 tracks of 10 sectors of 512 bytes:
        ///    SPT 40, 1 KB blocks, DSM 194, 64 entries, AL0 F0h (four directory blocks), one
        ///    reserved track;
        ///  - Kaypro 4, double-sided: SPT 40, 2 KB blocks, EXM 1, DSM 196, 64 entries, AL0 C0h, one
        ///    reserved track.
        /// In both, the data area is the image from the second 5120-byte track on, in order
        /// (<see cref="LinearCpmRecordMap"/>).
        /// </para>
        /// <para>
        /// A raw image is 204800 bytes (Kaypro II) or 409600 bytes (Kaypro 4). Images found online carry
        /// a few bytes more after the last track (1024 and 128 have been seen); up to one track more is
        /// accepted, and those bytes are neither read nor changed.
        /// </para>
        /// </remarks>
        /// <param name="length">
        /// Image length in bytes.
        /// </param>
        /// <param name="parameters">
        /// The Kaypro disk parameters, or <c>null</c>.
        /// </param>
        /// <param name="map">
        /// The record map, or <c>null</c>.
        /// </param>
        /// <returns>
        /// <c>true</c> when the length is a Kaypro II or Kaypro 4 raw image.
        /// </returns>
        public static bool TryKaypro(long length, out CpmDiskParameters? parameters, out ICpmRecordMap? map)
        {
            const int Track = 5120;
            ReadOnlySpan<byte> dpb;
            if (length >= 204800 && length < 204800 + Track)
            {
                dpb = KayproIiDpb;
            }
            else if (length >= 409600 && length < 409600 + Track)
            {
                dpb = Kaypro4Dpb;
            }
            else
            {
                parameters = null;
                map = null;
                return false;
            }

            parameters = CpmDiskParameters.FromDpbBytes(dpb);
            map = new LinearCpmRecordMap(Track, parameters.BlockCount * (parameters.BlockSize / 128));
            return true;
        }

        /// <summary>
        /// Tells whether an image length is a D64 image: 683 sectors of 256 bytes, with or without
        /// one error byte per sector.
        /// </summary>
        /// <param name="length">
        /// Image length in bytes.
        /// </param>
        /// <returns>
        /// <c>true</c> for 174848 or 175531 bytes.
        /// </returns>
        public static bool IsD64Size(long length) => length == 683 * 256 || length == 683 * 257;

        /// <summary>
        /// Gets the C128 BIOS layout for a C64 CP/M 2.2 disk, <c>dpb 256,17,34,1024,64,3</c>.
        /// </summary>
        /// <returns>
        /// 131 blocks of 1 KB with 64 directory entries and three reserved tracks.
        /// </returns>
        public static CpmDiskParameters C64Parameters() => CpmDiskParameters.FromCpm3Macro(256, 17, 34, 1024, 64, 3);

        /// <summary>
        /// Gets the C128 BIOS layout for a 1581 disk, <c>dpb 1024,5,159,2048,128,0</c>.
        /// </summary>
        /// <returns>
        /// 397 blocks of 2 KB with 128 directory entries; the directory is the first 4 KB of the image.
        /// </returns>
        public static CpmDiskParameters C1581Parameters() => CpmDiskParameters.FromCpm3Macro(1024, 5, 159, 2048, 128, 0);

        /// <summary>
        /// Returns <c>null</c>: there is no metadata descriptor for CP/M yet.
        /// </summary>
        /// <returns>
        /// Always <c>null</c>.
        /// </returns>
        public IFileSystemMetadataDescriptor? GetMetadataDescriptor() => null;

        /// <summary>
        /// Mounts a raw image with a given layout, without detecting one.
        /// </summary>
        /// <remarks>
        /// Used where several machines' disks are the same bytes, such as a freshly formatted disk
        /// that is all E5h: the image is opened as the machine the user picked.
        /// </remarks>
        /// <param name="imageStream">
        /// The image, laid out flat.
        /// </param>
        /// <param name="readOnly">
        /// Whether the mount is read-only.
        /// </param>
        /// <param name="parameters">
        /// The disk parameters.
        /// </param>
        /// <param name="map">
        /// Where each record lies in the image.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="imageStream"/>, <paramref name="parameters"/> or <paramref name="map"/> is null.
        /// </exception>
        public void MountAs(Stream imageStream, bool readOnly, CpmDiskParameters parameters, ICpmRecordMap map)
        {
            if (imageStream == null)
            {
                throw new ArgumentNullException(nameof(imageStream));
            }

            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            byte[] image = new byte[imageStream.Length];
            imageStream.Position = 0;
            imageStream.ReadExactly(image, 0, image.Length);
            _stream = imageStream;
            _readOnly = readOnly || !imageStream.CanWrite;
            _imd = null;
            _fileSystem = new CpmFileSystem(image, parameters, map);
        }

        /// <summary>
        /// Reads the whole image and its CP/M directory.
        /// </summary>
        /// <param name="imageStream">
        /// The disk image.
        /// </param>
        /// <param name="readOnly">
        /// <c>true</c> to refuse every change; otherwise changes are saved back to
        /// <paramref name="imageStream"/>, which must then be writable.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="imageStream"/> is null.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// Thrown when the image is not a C128 CP/M disk of a known size.
        /// </exception>
        public void Mount(Stream imageStream, bool readOnly)
        {
            if (imageStream == null)
            {
                throw new ArgumentNullException(nameof(imageStream));
            }

            byte[] image = new byte[imageStream.Length];
            imageStream.Position = 0;
            imageStream.ReadExactly(image, 0, image.Length);
            _stream = imageStream;
            _readOnly = readOnly || !imageStream.CanWrite;
            _imd = null;
            if (ImdDiskImage.IsImd(image))
            {
                // An ImageDisk file: the CP/M layouts below work on its sectors laid out flat, and
                // a change is encoded back to ImageDisk form when it is saved.
                _imd = ImdDiskImage.Decode(image);
                image = _imd.Flat;
            }

            AmstradCpmRecordMap? amstrad = AmstradCpmRecordMap.TryRead(image, out byte firstSectorId, out _);
            if (amstrad != null)
            {
                CpmDiskParameters amstradParameters = AmstradParameters(amstrad.RecordCount, firstSectorId);
                if (!CpmFileSystem.LooksLikeDirectory(CpmFileSystem.DirectoryBytes(image, amstradParameters, amstrad), amstradParameters, out _))
                {
                    throw new InvalidDataException("Not an Amstrad CP/M disk: the first data track does not hold a CP/M directory.");
                }

                _fileSystem = new CpmFileSystem(image, amstradParameters, amstrad);
                return;
            }

            if (HasFlatLayouts(image.Length) && !IsC1581CpmWithFiles(image.Length, image))
            {
                if (!TrySelectFlatLayout(image.Length, image, out CpmDiskParameters? flat, out ICpmRecordMap? flatMap))
                {
                    throw new InvalidDataException("Not a CP/M disk: no CP/M-86, Kaypro, Osborne or TIKI-100 layout of this size has a CP/M directory where it should be.");
                }

                _fileSystem = new CpmFileSystem(image, flat!, flatMap!);
                return;
            }

            if (IsD81Size(image.Length))
            {
                CpmDiskParameters p1581 = C1581Parameters();
                if (!CpmFileSystem.LooksLikeDirectory(image.AsSpan(0, p1581.DirectoryEntries * 32), p1581, out _))
                {
                    throw new InvalidDataException("Not a C128 CP/M 1581 disk: track 1 does not hold a CP/M directory.");
                }

                _fileSystem = new CpmFileSystem(image, p1581, new C1581CpmRecordMap(p1581.BlockCount * (p1581.BlockSize / 256)));
                return;
            }

            if (!IsC128CpmBootSector(image, out bool doubleSided))
            {
                // No "CBM" mark: the C128 BIOS reads a 1541 disk like this as a C64 CP/M 2.2 disk.
                if (IsD64Size(image.Length))
                {
                    CpmDiskParameters c64 = C64Parameters();
                    C64CpmRecordMap c64Map = new C64CpmRecordMap(c64.BlockCount * (c64.BlockSize / 256));
                    if (CpmFileSystem.LooksLikeDirectory(CpmFileSystem.DirectoryBytes(image, c64, c64Map), c64, out _))
                    {
                        _fileSystem = new CpmFileSystem(image, c64, c64Map);
                        return;
                    }
                }

                throw new InvalidDataException("Not a C128 or C64 CP/M disk: no \"CBM\" mark on track 1 sector 0 and no CP/M directory on track 3.");
            }

            // 683 or 1366 sectors of 256 bytes; an image may carry one error byte per sector after them.
            int sectors = doubleSided ? 1366 : 683;
            if (image.Length != sectors * 256 && image.Length != sectors * 257)
            {
                throw new InvalidDataException(
                    "A " + (doubleSided ? "double" : "single") + "-sided C128 CP/M disk image is " + sectors * 256
                    + " bytes (or " + sectors * 257 + " with error bytes), not " + image.Length + ".");
            }

            CpmDiskParameters parameters = doubleSided
                ? CpmDiskParameters.FromCpm3Macro(1024, 1, 340, 2048, 128, 0)
                : CpmDiskParameters.FromCpm3Macro(1024, 1, 170, 1024, 64, 0);
            int records = parameters.BlockCount * (parameters.BlockSize / 256);
            _fileSystem = new CpmFileSystem(image, parameters, new C128CpmRecordMap(doubleSided, records));
        }

        /// <summary>
        /// Lists the root (user 0 files and the user folders) or a <c>USERn</c> folder.
        /// </summary>
        /// <param name="path">
        /// "/" or "/USERn".
        /// </param>
        /// <returns>
        /// The entries of that folder.
        /// </returns>
        /// <exception cref="DirectoryNotFoundException">
        /// Thrown when <paramref name="path"/> is not the root or a user folder.
        /// </exception>
        public DirectoryEntry[] ListDirectory(string path)
        {
            CpmFileSystem fs = EnsureMounted();
            int user = UserOfFolder(path);
            List<DirectoryEntry> entries = new List<DirectoryEntry>();
            bool[] usersWithFiles = new bool[16];
            IReadOnlyList<CpmFile> files = fs.Files;
            for (int i = 0; i < files.Count; i++)
            {
                usersWithFiles[files[i].User] = true;
                if (files[i].User == user)
                {
                    entries.Add(new DirectoryEntry { Name = files[i].Name, Metadata = Metadata(files[i]) });
                }
            }

            if (user == 0)
            {
                for (int u = 1; u < 16; u++)
                {
                    if (usersWithFiles[u])
                    {
                        string name = UserFolderPrefix + u;
                        entries.Add(new DirectoryEntry
                        {
                            Name = name,
                            Metadata = new FileMetadata { Name = name, Path = "/" + name, IsDirectory = true, UrnIdentifier = UrnIdentifier.Encode(name) },
                        });
                    }
                }
            }

            return entries.ToArray();
        }

        /// <summary>
        /// Reads a file.
        /// </summary>
        /// <param name="path">
        /// "/NAME.TYP" or "/USERn/NAME.TYP".
        /// </param>
        /// <returns>
        /// The file's bytes, a whole number of 128-byte records.
        /// </returns>
        /// <exception cref="FileNotFoundException">
        /// Thrown when there is no file at <paramref name="path"/>.
        /// </exception>
        public byte[] ReadFile(string path)
        {
            CpmFileSystem fs = EnsureMounted();
            CpmFile file = Find(fs, path) ?? throw new FileNotFoundException("File not found: " + path);
            return fs.Read(file);
        }

        /// <summary>
        /// Describes a file, or returns <c>null</c> when there is none at the path.
        /// </summary>
        /// <param name="path">
        /// "/NAME.TYP" or "/USERn/NAME.TYP".
        /// </param>
        /// <returns>
        /// The file's metadata, or <c>null</c>.
        /// </returns>
        public FileMetadata? GetMetadata(string path)
        {
            CpmFile? file = Find(EnsureMounted(), path);
            return file == null ? null : Metadata(file);
        }

        /// <summary>
        /// Writes a file and saves the image back to the stream.
        /// </summary>
        /// <param name="path">
        /// "/NAME.TYP" or "/USERn/NAME.TYP".
        /// </param>
        /// <param name="data">
        /// The file's bytes; a last partial 128-byte record is filled with Ctrl-Z.
        /// </param>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when the disk was mounted read-only.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the file there is read-only, or the disk or its directory is full.
        /// </exception>
        public void WriteFile(string path, byte[] data)
        {
            CpmFileSystem fs = EnsureWritable();
            ParsePath(path, out int user, out string name);
            fs.Write(user, name, data);
            Save(fs);
        }

        /// <summary>
        /// Deletes a file and saves the image back to the stream.
        /// </summary>
        /// <param name="path">
        /// "/NAME.TYP" or "/USERn/NAME.TYP".
        /// </param>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when the disk was mounted read-only.
        /// </exception>
        /// <exception cref="FileNotFoundException">
        /// Thrown when there is no file at <paramref name="path"/>.
        /// </exception>
        public void DeleteFile(string path)
        {
            CpmFileSystem fs = EnsureWritable();
            ParsePath(path, out int user, out string name);
            fs.Delete(user, name);
            Save(fs);
        }

        /// <summary>
        /// Renames a file within its user area and saves the image back to the stream.
        /// </summary>
        /// <param name="oldPath">
        /// "/NAME.TYP" or "/USERn/NAME.TYP".
        /// </param>
        /// <param name="newPath">
        /// The new path, in the same user area.
        /// </param>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when the disk was mounted read-only.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when <paramref name="newPath"/> is in another user area or already exists.
        /// </exception>
        public void RenameFile(string oldPath, string newPath)
        {
            CpmFileSystem fs = EnsureWritable();
            ParsePath(oldPath, out int user, out string oldName);
            ParsePath(newPath, out int newUser, out string newName);
            if (newUser != user)
            {
                throw new IOException("A CP/M rename stays in one user area: " + oldPath + " to " + newPath);
            }

            fs.Rename(user, oldName, newName);
            Save(fs);
        }

        /// <summary>
        /// Not supported: the driver gives files, not raw blocks.
        /// </summary>
        /// <param name="address">
        /// Unused.
        /// </param>
        /// <param name="buffer">
        /// Unused.
        /// </param>
        /// <returns>
        /// Never returns.
        /// </returns>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public int ReadBlock(BlockAddress address, Span<byte> buffer)
            => throw new NotSupportedException("The CP/M driver has no block access.");

        /// <summary>
        /// Not supported: the driver gives files, not raw blocks.
        /// </summary>
        /// <param name="address">
        /// Unused.
        /// </param>
        /// <param name="data">
        /// Unused.
        /// </param>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
            => throw new NotSupportedException("The CP/M driver has no block access.");

        /// <summary>
        /// Returns <c>null</c>: the driver has no block access.
        /// </summary>
        /// <returns>
        /// Always <c>null</c>.
        /// </returns>
        public BlockDeviceInfo? GetBlockDeviceInfo() => null;

        /// <summary>
        /// Lets go of the image and closes the stream it was mounted from; the driver owns that stream.
        /// </summary>
        public void Dispose()
        {
            _fileSystem = null;
            _stream?.Dispose();
            _stream = null;
        }

        private CpmFileSystem EnsureMounted()
            => _fileSystem ?? throw new InvalidOperationException("The CP/M disk is not mounted.");

        private CpmFileSystem EnsureWritable()
        {
            CpmFileSystem fs = EnsureMounted();
            if (_readOnly)
            {
                throw new UnauthorizedAccessException("The CP/M disk is mounted read-only.");
            }

            return fs;
        }

        // The whole image goes back to the stream after each change (at most 822400 bytes).
        private void Save(CpmFileSystem fs)
        {
            byte[] bytes = fs.Image;
            if (_imd != null)
            {
                // fs.Image is the ImageDisk image's own flat array, so the change is already in it.
                bytes = _imd.Encode();
                _stream!.SetLength(bytes.Length);
            }

            _stream!.Position = 0;
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }

        // A size one of the raw layouts in TrySelectFlatLayout uses.
        internal static bool HasFlatLayouts(long length)
        {
            return length == 163840 || length == 327680 || length == 92160 || length == 819200 || length == 8388608 || TryKaypro(length, out _, out _);
        }

        private static bool HasFilesFor(byte[] image)
        {
            return TrySelectFlatLayout(image.Length, image, out CpmDiskParameters? p, out ICpmRecordMap? m) && HasFiles(image, p!, m!);
        }

        // 819200 bytes is both a Commodore 1581 (D81) image and a TIKI-100 800 KB disk. A 1581 CP/M
        // disk has its directory in the first 4 KB of the image; when that holds files, it is a 1581.
        internal static bool IsC1581CpmWithFiles(long length, byte[] image)
        {
            return IsD81Size(length) && CpmFileSystem.LooksLikeDirectory(image, C1581Parameters(), out int files) && files > 0;
        }

        // A freshly formatted CP/M disk: every byte of the directory is E5h, as the format program
        // leaves it. Zeros or any other data there are not taken as an empty CP/M directory.
        internal static bool IsFreshlyFormatted(byte[] image, CpmDiskParameters parameters, ICpmRecordMap map)
        {
            byte[] directory = CpmFileSystem.DirectoryBytes(image, parameters, map);
            for (int i = 0; i < directory.Length; i++)
            {
                if (directory[i] != 0xE5)
                {
                    return false;
                }
            }

            return directory.Length > 0;
        }

        internal static bool HasFiles(byte[] image, CpmDiskParameters parameters, ICpmRecordMap map)
        {
            return CpmFileSystem.LooksLikeDirectory(CpmFileSystem.DirectoryBytes(image, parameters, map), parameters, out int files) && files > 0;
        }

        // "/NAME.TYP" is user 0; "/USERn/NAME.TYP" is user n.
        private static void ParsePath(string path, out int user, out string name)
        {
            string trimmed = path.Trim('/');
            int slash = trimmed.IndexOf('/');
            user = 0;
            name = trimmed;
            if (slash >= 0)
            {
                user = UserOfFolder(trimmed.Substring(0, slash));
                name = trimmed.Substring(slash + 1);
            }
        }

        // 0 for the root, n for "/USERn"; anything else is not a folder.
        private static int UserOfFolder(string path)
        {
            string trimmed = path.Trim('/');
            if (trimmed.Length == 0)
            {
                return 0;
            }

            if (trimmed.StartsWith(UserFolderPrefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(trimmed.AsSpan(UserFolderPrefix.Length), out int user) && user >= 1 && user <= 15)
            {
                return user;
            }

            throw new DirectoryNotFoundException("A CP/M disk has the root and USER1 to USER15 only: " + path);
        }

        // "/NAME.TYP" is user 0; "/USERn/NAME.TYP" is user n.
        private static CpmFile? Find(CpmFileSystem fs, string path)
        {
            ParsePath(path, out int user, out string name);
            return fs.Find(user, name);
        }

        private static FileMetadata Metadata(CpmFile file)
        {
            string path = file.User == 0 ? "/" + file.Name : "/" + UserFolderPrefix + file.User + "/" + file.Name;
            FileMetadata metadata = new FileMetadata
            {
                Name = file.Name,
                Path = path,
                IsDirectory = false,
                Size = file.Size,
                Attributes = (file.ReadOnly ? "R" : string.Empty) + (file.System ? "S" : string.Empty),
                UrnIdentifier = UrnIdentifier.Encode(file.Name),
            };
            metadata.SetProperty("User", file.User);
            metadata.SetProperty("Blocks", file.Blocks.Count);
            return metadata;
        }
    }
}
