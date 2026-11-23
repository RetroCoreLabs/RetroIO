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
    /// Finds CP/M disks for <see cref="CpmDriver"/>: Commodore CP/M disks on D64, D71 and D81 images, and
    /// Kaypro II and Kaypro 4 raw images.
    /// </summary>
    /// <remarks>
    /// A C128 CP/M disk is also a readable CBM DOS disk (it has a Commodore header), so it matches
    /// the Commodore provider too. It is reported here with a higher confidence because the mark it
    /// is found by, "CBM" at the start of track 1 sector 0, is the C128 BIOS's own test for a CP/M
    /// disk; the C128 itself treats such a disk as CP/M.
    /// A 1581 disk has no such mark: the C128 BIOS treats every 1581 disk as CP/M in CP/M mode. A
    /// D81 image is claimed only when all 128 entries of its first 4 KB pass
    /// <see cref="CpmFileSystem.LooksLikeDirectory"/> and at
    /// least one is a file; an empty CP/M directory gives no sign of CP/M, so it is left to the
    /// Commodore provider.
    /// </remarks>
    public sealed class CpmDriverProvider : FileSystemDriverProviderBase, IBlankImageProvider, ITransferConversionProvider
    {
        // The machines this format's disks come from, shown to users.
        private static readonly string[] MachineList = new[] { "Commodore 128", "Commodore 64", "Kaypro II", "Kaypro 4", "Osborne 1", "Amstrad CPC", "Amstrad PCW", "IBM PC (CP/M-86)", "IBM PC-XT (CP/M-86)", "TIKI-100" };

        // Blank CP/M disks: raw images filled with E5h throughout, as measured on real disks of
        // each machine (the free space is E5h on every Kaypro, Osborne and TIKI-100 disk tested, and
        // the reserved tracks are E5h on the data disks that never had the system written to them:
        // Kaypro MBasic and TIKI-100 hello). Not offered yet, because what a freshly formatted disk
        // holds was not measured: C128 and C64 CP/M (the boot sector of the C128 format program),
        // CP/M-86 (its free space is not E5h), Amstrad CPC and PCW (their reserved tracks).
        private static readonly BlankImageLayout[] Layouts =
        {
            new BlankImageLayout { Id = "kaypro2", Machine = "Kaypro II", Name = "200 KB floppy - 40 tracks, 1 side", Extension = ".img", SizeBytes = 204800, SameBytesAs = new[] { "osborne1", "tiki100-200k" } },
            new BlankImageLayout { Id = "kaypro4", Machine = "Kaypro 4", Name = "400 KB floppy - 40 tracks, 2 sides", Extension = ".img", SizeBytes = 409600, SameBytesAs = new[] { "tiki100-400k" } },
            new BlankImageLayout { Id = "osborne1", Machine = "Osborne 1", Name = "200 KB double-density floppy - 40 tracks, 1 side", Extension = ".img", SizeBytes = 204800, SameBytesAs = new[] { "kaypro2", "tiki100-200k" } },
            new BlankImageLayout { Id = "tiki100-90k", Machine = "TIKI-100", Name = "90 KB floppy - 40 tracks, 1 side, single density", Extension = ".dsk", SizeBytes = 92160 },
            new BlankImageLayout { Id = "tiki100-200k", Machine = "TIKI-100", Name = "200 KB floppy - 40 tracks, 1 side, 10 x 512", Extension = ".dsk", SizeBytes = 204800, SameBytesAs = new[] { "kaypro2", "osborne1" } },
            new BlankImageLayout { Id = "tiki100-400k", Machine = "TIKI-100", Name = "400 KB floppy - 40 tracks, 2 sides, 10 x 512", Extension = ".dsk", SizeBytes = 409600, SameBytesAs = new[] { "kaypro4" } },
            new BlankImageLayout { Id = "tiki100-800k", Machine = "TIKI-100", Name = "800 KB floppy - 80 tracks, 2 sides, 10 x 512", Extension = ".dsk", SizeBytes = 819200 },
            new BlankImageLayout { Id = "tiki100-hd", Machine = "TIKI-100", Name = "8 MB hard disk", Extension = ".dsk", SizeBytes = 8388608 },
        };

        private static readonly string[] KnownExtensions = { ".d64", ".d71", ".d81", ".img", ".dsk", ".imd" };

        /// <summary>
        /// Gets the filesystem type name, "CPM".
        /// </summary>
        public override string FileSystemType => "CPM";

        /// <summary>
        /// Gets the file extensions CP/M images are found under.
        /// </summary>
        public override IReadOnlyList<string> FileExtensions => KnownExtensions;

        /// <summary>
        /// Gets a short description of the format.
        /// </summary>
        public override string Description => "CP/M disk (Commodore 128 and 64 CP/M on D64, D71 and D81 images; Kaypro II and 4 and Osborne 1 raw or ImageDisk images; Amstrad CPC DSK; Amstrad PCW DSK; CP/M-86 on 160 KB IBM PC and 320 KB IBM PC-XT floppies; TIKI-100 floppies and 8 MB hard disk)";

        /// <inheritdoc/>
        public override System.Collections.Generic.IReadOnlyList<string> Machines => MachineList;

        /// <inheritdoc/>
        public override bool SupportsWrite => true;

        /// <summary>
        /// Gets the number of first bytes detection needs: 80 KB, which holds every directory detection
        /// looks at; the largest is the TIKI-100 hard disk's, 64 KB from 4000h.
        /// </summary>
        public override int RequiredHeaderBytes => 81920;

        /// <summary>
        /// Says how sure the provider is that an image is a C128 CP/M disk.
        /// </summary>
        /// <param name="hints">
        /// Size and first bytes of the image.
        /// </param>
        /// <returns>
        /// 0.95 for a Kaypro II or Kaypro 4 sized raw image whose second track is a CP/M directory with
        /// files, a D64 or D71 sized image whose track 1 sector 0 starts with "CBM", or a D81 sized
        /// image whose first 4 KB is a CP/M directory with files, or a D64 sized image without the mark
        /// whose track 3 holds a C64 CP/M directory with files; otherwise 0.
        /// </returns>
        public override float DetectConfidence(DetectionHints hints)
        {
            if (ImdDiskImage.IsImd(hints.HeaderBytes))
            {
                // The whole file is needed to lay an ImageDisk image out flat; Mount does the full
                // check. Here: the CP/M layouts for the flat size, tried on the first tracks.
                return CpmDriver.ImdLooksLikeCpm(hints.FilePath) ? 0.95f : 0.0f;
            }

            if (IsAmstradDsk(hints.HeaderBytes))
            {
                return 0.95f;
            }

            if (CpmDriver.HasFlatLayouts(hints.FileSize) && !CpmDriver.IsC1581CpmWithFiles(hints.FileSize, hints.HeaderBytes))
            {
                // CP/M-86, Kaypro, Osborne and TIKI-100 raw images: every candidate directory lies in
                // the header bytes.
                byte[] start = hints.HeaderBytes;
                if (!CpmDriver.TrySelectFlatLayout(hints.FileSize, start, out CpmDiskParameters? flat, out ICpmRecordMap? flatMap))
                {
                    return 0.0f;
                }

                if (CpmDriver.HasFiles(start, flat!, flatMap!))
                {
                    return 0.95f;
                }

                // An empty disk counts only when its directory is all E5h, which is what a CP/M
                // format program writes: positive evidence, but weaker than files, and stronger
                // than a format that is only guessed from the file size.
                return CpmDriver.IsFreshlyFormatted(start, flat!, flatMap!) ? 0.7f : 0.0f;
            }

            if (CpmDriver.IsD81Size(hints.FileSize))
            {
                CpmDiskParameters p1581 = CpmDriver.C1581Parameters();
                bool isDirectory = CpmFileSystem.LooksLikeDirectory(hints.HeaderBytes, p1581, out int files);
                return isDirectory && files > 0 ? 0.95f : 0.0f;
            }

            if (!CpmDriver.IsC128CpmBootSector(hints.HeaderBytes, out bool doubleSided))
            {
                if (!CpmDriver.IsD64Size(hints.FileSize))
                {
                    return 0.0f;
                }

                // A C64 CP/M 2.2 disk: its directory is on Commodore track 3, inside the first 12800 bytes.
                CpmDiskParameters c64 = CpmDriver.C64Parameters();
                byte[] directory = CpmFileSystem.DirectoryBytes(hints.HeaderBytes, c64, new C64CpmRecordMap(c64.BlockCount * 4));
                return CpmFileSystem.LooksLikeDirectory(directory, c64, out int c64Files) && c64Files > 0 ? 0.95f : 0.0f;
            }

            long sectors = doubleSided ? 1366 : 683;
            return hints.FileSize == sectors * 256 || hints.FileSize == sectors * 257 ? 0.95f : 0.0f;
        }

        /// <summary>
        /// Creates a CP/M driver.
        /// </summary>
        /// <returns>
        /// A new <see cref="CpmDriver"/>.
        /// </returns>
        public override IFileSystemDriver CreateDriver() => new CpmDriver();

        /// <inheritdoc/>
        public IReadOnlyList<BlankImageLayout> BlankLayouts => Layouts;

        /// <inheritdoc/>
        public byte[] CreateBlank(string layoutId, IReadOnlyDictionary<string, string> options)
        {
            LayoutOf(layoutId, out _, out _);
            byte[] image = new byte[SizeOf(layoutId)];
            image.AsSpan().Fill(0xE5);
            return image;
        }

        /// <inheritdoc/>
        public IFileSystemDriver Mount(Stream stream, bool readOnly, string layoutId)
        {
            LayoutOf(layoutId, out CpmDiskParameters parameters, out ICpmRecordMap map);
            CpmDriver driver = new CpmDriver();
            driver.MountAs(stream, readOnly, parameters, map);
            return driver;
        }

        private static long SizeOf(string layoutId)
        {
            for (int i = 0; i < Layouts.Length; i++)
            {
                if (Layouts[i].Id == layoutId)
                {
                    return Layouts[i].SizeBytes;
                }
            }

            throw new ArgumentException("not a CP/M blank-image layout: " + layoutId, nameof(layoutId));
        }

        // The layout of each blank disk, from the same tables detection uses.
        private static void LayoutOf(string layoutId, out CpmDiskParameters parameters, out ICpmRecordMap map)
        {
            long size = SizeOf(layoutId);
            CpmDiskParameters? p;
            ICpmRecordMap? m;
            bool found = layoutId switch
            {
                "kaypro2" or "kaypro4" => CpmDriver.TryKaypro(size, out p, out m),
                "osborne1" => CpmDriver.TryOsborne(size, out p, out m),
                _ => CpmDriver.TryTiki100(size, out p, out m),
            };
            if (!found || p == null || m == null)
            {
                throw new InvalidOperationException("no CP/M layout for " + layoutId);
            }

            parameters = p;
            map = m;
        }

        // A CPC DSK file, one side, whose first track has the Amstrad sector IDs 41h-49h (system
        // format), C1h-C9h (data format) or 01h-09h (PCW). The full check of every track is done at mount.
        private static bool IsAmstradDsk(ReadOnlySpan<byte> header)
        {
            if (header.Length < 512 || header[0x31] != 1 || !StartsWithText(header, "EXTENDED CPC DSK") && !StartsWithText(header, "MV - CPC"))
            {
                return false;
            }

            ReadOnlySpan<byte> track = header.Slice(256);
            if (!StartsWithText(track, "Track-Info") || track[0x15] != 9)
            {
                return false;
            }

            byte lowest = 0xFF;
            for (int s = 0; s < 9; s++)
            {
                byte id = track[0x18 + s * 8 + 2];
                if (id < lowest)
                {
                    lowest = id;
                }
            }

            return lowest == 0x41 || lowest == 0xC1 || lowest == 0x01;
        }

        private static bool StartsWithText(ReadOnlySpan<byte> data, string text)
        {
            if (data.Length < text.Length)
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (data[i] != text[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Gets the copy conversions of this file system; see <see cref="CpmTextConversion"/>.
        /// </summary>
        public IReadOnlyList<TransferConversion> Conversions => CpmTextConversion.Conversions;

        /// <summary>
        /// Converts a file's stored bytes for the host.
        /// </summary>
        /// <param name="conversionId">
        /// The conversion's id.
        /// </param>
        /// <param name="stored">
        /// The bytes as read from the image.
        /// </param>
        /// <param name="options">
        /// Option values, or <c>null</c>.
        /// </param>
        /// <returns>
        /// The bytes for the host file.
        /// </returns>
        public byte[] ToHost(string conversionId, ReadOnlySpan<byte> stored, IReadOnlyDictionary<string, string>? options)
        {
            return CpmTextConversion.ToHost(conversionId, stored, options);
        }

        /// <summary>
        /// Converts a host file's bytes for the image.
        /// </summary>
        /// <param name="conversionId">
        /// The conversion's id.
        /// </param>
        /// <param name="host">
        /// The bytes of the host file.
        /// </param>
        /// <param name="options">
        /// Option values, or <c>null</c>.
        /// </param>
        /// <returns>
        /// The bytes to write to the image.
        /// </returns>
        public byte[] FromHost(string conversionId, ReadOnlySpan<byte> host, IReadOnlyDictionary<string, string>? options)
        {
            return CpmTextConversion.FromHost(conversionId, host, options);
        }
    }
}
