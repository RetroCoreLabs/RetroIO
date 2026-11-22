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
using RetroIO.Storage.Local.BlockDevices;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Provider for PC floppy disk drivers (FAT12/FAT16).
    /// Handles format detection and driver instantiation.
    /// </summary>
    public sealed class PcFloppyDriverProvider : FileSystemDriverProviderBase, IBlankImageProvider
    {
        // The machines this format's disks come from, shown to users.
        private static readonly string[] MachineList = new[] { "IBM PC" };

        // The volume label of the boot sector and root directory: 11 characters, as FORMAT /V takes it.
        private static readonly BlankImageOption VolumeLabel = new BlankImageOption { Id = "volumeLabel", Label = "Volume label", Kind = BlankImageOptionKind.Text, Default = string.Empty, MaxLength = 11, PrintableAsciiOnly = true };
        private static readonly BlankImageOption[] LabelOnly = { VolumeLabel };

        // FAT12 floppies with the cluster size and root directory size MS-DOS used (PcImageFactory).
        private static readonly BlankImageLayout[] Layouts =
        {
            new BlankImageLayout { Id = "pc-360k", Machine = "IBM PC", Name = "360 KB 5.25\" floppy - 40 tracks, 2 sides, 9 x 512", Extension = ".img", SizeBytes = 368640, Options = LabelOnly },
            new BlankImageLayout { Id = "pc-720k", Machine = "IBM PC", Name = "720 KB 3.5\" floppy - 80 tracks, 2 sides, 9 x 512", Extension = ".img", SizeBytes = 737280, Options = LabelOnly },
            new BlankImageLayout { Id = "pc-1200k", Machine = "IBM PC", Name = "1.2 MB 5.25\" floppy - 80 tracks, 2 sides, 15 x 512", Extension = ".img", SizeBytes = 1228800, Options = LabelOnly },
            new BlankImageLayout { Id = "pc-1440k", Machine = "IBM PC", Name = "1.44 MB 3.5\" floppy - 80 tracks, 2 sides, 18 x 512", Extension = ".img", SizeBytes = 1474560, Options = LabelOnly },
            new BlankImageLayout { Id = "pc-2880k", Machine = "IBM PC", Name = "2.88 MB 3.5\" floppy - 80 tracks, 2 sides, 36 x 512", Extension = ".img", SizeBytes = 2949120, Options = LabelOnly },
            new BlankImageLayout { Id = "pc-hd-xt-5m", Machine = "IBM PC/XT", Name = "5 MB hard disk - 306 cylinders, 2 heads, 17 x 512", Extension = ".img", SizeBytes = BlockDeviceDescriptor.PcXt_5MB.TotalBlocks * 512L, Options = LabelOnly, Details = "IBM XT BIOS drive type 0.\nOne FAT12 partition over the whole disk, from the first sector after cylinder 0 head 0." },
            new BlankImageLayout { Id = "pc-hd-xt-10m", Machine = "IBM PC/XT", Name = "10 MB hard disk - 306 cylinders, 4 heads, 17 x 512", Extension = ".img", SizeBytes = BlockDeviceDescriptor.PcXt_10MB.TotalBlocks * 512L, Options = LabelOnly, Details = "the usual XT drive, also IBM AT type 1.\nOne FAT12 partition over the whole disk, from the first sector after cylinder 0 head 0." },
            new BlankImageLayout { Id = "pc-hd-xt-15m", Machine = "IBM PC/XT", Name = "15 MB hard disk - 306 cylinders, 6 heads, 17 x 512", Extension = ".img", SizeBytes = BlockDeviceDescriptor.PcXt_15MB.TotalBlocks * 512L, Options = LabelOnly, Details = "IBM XT BIOS drive type 2.\nOne FAT12 partition over the whole disk, from the first sector after cylinder 0 head 0." },
            new BlankImageLayout { Id = "pc-hd-xt-26m", Machine = "IBM PC/XT", Name = "26 MB hard disk - 375 cylinders, 8 heads, 17 x 512", Extension = ".img", SizeBytes = BlockDeviceDescriptor.PcXt_26MB.TotalBlocks * 512L, Options = LabelOnly, Details = "IBM XT BIOS drive type 1.\nOne FAT16 partition over the whole disk, from the first sector after cylinder 0 head 0." },
            new BlankImageLayout { Id = "pc-hd-at-20m", Machine = "IBM PC/AT", Name = "20 MB hard disk - 615 cylinders, 4 heads, 17 x 512", Extension = ".img", SizeBytes = BlockDeviceDescriptor.PcAt_20MB.TotalBlocks * 512L, Options = LabelOnly, Details = "IBM AT BIOS drive type 2.\nOne FAT16 partition over the whole disk, from the first sector after cylinder 0 head 0." },
            new BlankImageLayout { Id = "pc-hd-at-30m", Machine = "IBM PC/AT", Name = "30 MB hard disk - 615 cylinders, 6 heads, 17 x 512", Extension = ".img", SizeBytes = BlockDeviceDescriptor.PcAt_30MB.TotalBlocks * 512L, Options = LabelOnly, Details = "IBM AT BIOS drive type 3.\nOne FAT16 partition over the whole disk, from the first sector after cylinder 0 head 0." },
            new BlankImageLayout { Id = "pc-hd-at-47m", Machine = "IBM PC/AT", Name = "47 MB hard disk - 940 cylinders, 6 heads, 17 x 512", Extension = ".img", SizeBytes = BlockDeviceDescriptor.PcAt_47MB.TotalBlocks * 512L, Options = LabelOnly, Details = "IBM AT BIOS drive type 5.\nOne FAT16 partition over the whole disk, from the first sector after cylinder 0 head 0." },
            new BlankImageLayout { Id = "pc-hd-at-62m", Machine = "IBM PC/AT", Name = "62 MB hard disk - 940 cylinders, 8 heads, 17 x 512", Extension = ".img", SizeBytes = BlockDeviceDescriptor.PcAt_62MB.TotalBlocks * 512L, Options = LabelOnly, Details = "IBM AT BIOS drive type 4.\nOne FAT16 partition over the whole disk, from the first sector after cylinder 0 head 0." },
        };

        // The one extension list for this format: detection below uses it, and programs read it
        // through FileExtensions (for example to link these files to themselves).
        // .hdd is a raw PC hard-disk image, the same bytes as .img. .vhd is a fixed VHD: the raw disk
        // bytes plus a 512-byte footer, which DiskImageFile strips (by its contents, not this name)
        // before any driver sees the image.
        private static readonly string[] KnownExtensions = { ".img", ".ima", ".vfd", ".flp", ".hdd", ".vhd" };

        /// <summary>
        /// Gets the file system type identifier of this provider: <c>PC-Floppy</c>.
        /// </summary>
        public override string FileSystemType => "PC-Floppy";

        /// <summary>
        /// Gets the extensions this format is stored under: .img, .ima, .vfd, .flp, and for hard disks .hdd and .vhd (fixed VHD).
        /// </summary>
        /// <remarks>
        /// Detection uses the same list as its extension signal.
        /// </remarks>
        public override IReadOnlyList<string> FileExtensions => KnownExtensions;

        /// <summary>
        /// Gets the human-readable description of the PC floppy image formats.
        /// </summary>
        public override string Description => "PC floppy disk images (360KB-2.88MB, FAT12/FAT16)";

        /// <inheritdoc/>
        public override System.Collections.Generic.IReadOnlyList<string> Machines => MachineList;

        /// <inheritdoc/>
        public override bool SupportsWrite => true;

        /// <summary>
        /// Scores the file from its extension, its size and its boot sector fields.
        /// </summary>
        /// <param name="hints">
        /// The file name, size and header bytes.
        /// </param>
        /// <returns>
        /// The confidence from 0 to 1.
        /// </returns>
        public override float DetectConfidence(DetectionHints hints)
        {
            float confidence = 0.0f;

            // 1. Check file extension
            if (HasKnownFileExtension(hints))
            {
                confidence += 0.2f;
            }

            // 2. Check file size against known fixed floppy sizes (strong signal, but only ever
            // applies to whole-floppy-image files — a FAT partition carved out of a larger PC
            // hard disk image by VolumeProviderFactory's MBR support will never match one of
            // these, since its size is however many sectors the MBR partition table says it is).
            float sizeConfidence = DetectConfidenceFromSize(hints.FileSize);
            confidence += sizeConfidence;

            bool hasValidBootSignature = hints.HeaderBytes != null && hints.HeaderBytes.Length >= 512 &&
                hints.HeaderBytes[510] == 0x55 && hints.HeaderBytes[511] == 0xAA;

            if (!hasValidBootSignature)
            {
                // No boot signature. An early DOS floppy may lack it, but every FAT volume starts its
                // first FAT, in sector 1 of a floppy, with the media byte (F0h-FFh) and FFh FFh. Without
                // that the file is not FAT, whatever its size (a blank Dragon disk of 360 or 720 KB is
                // E5h there), and the size alone is not a claim.
                bool hasFatStart = hints.HeaderBytes != null && hints.HeaderBytes.Length >= 515 &&
                    hints.HeaderBytes[512] >= 0xF0 && hints.HeaderBytes[513] == 0xFF && hints.HeaderBytes[514] == 0xFF;
                return hasFatStart ? confidence : 0.0f;
            }

            confidence += 0.3f;

            // 3. BPB field sanity — a much stronger, size-independent signal than the fixed-size
            // check above. Real DOS boot sectors from before ~DOS 4.0 (like this fixture: a
            // genuine 1987-era PC-DOS 3.3 disk) never had the later "FAT12   "/"FAT16   " type
            // string at offset 0x36, so that string can't be relied on either — these are exactly
            // the same core fields Fat12FileSystem.ParseBootSector itself validates, checked here
            // so a plausible, real FAT volume is never rejected purely for having a non-floppy
            // size (e.g. a FAT partition on a PC hard disk image).
            //
            // NOTE: hasValidBootSignature + BPB-field-sanity alone is NOT enough — an MBR sector
            // ALSO ends in 0x55AA (that's part of the MBR format too, not unique to FAT), and its
            // boot CODE bytes can coincidentally look like plausible BPB fields. The real
            // discriminator that closes this false-positive is requiring the boot sector's own
            // declared total-sector count to actually describe the volume being examined — an
            // MBR's boot code has no reason to coincidentally encode a sector count matching the
            // whole disk's size, whereas a genuine FAT boot sector's declared size is exactly the
            // volume it lives on (confirmed empirically against the real hd10.img fixture: its
            // MBR sector passes the BPB-sanity check but fails this size cross-check, while its
            // actual FAT12 partition passes both).
            if (HasSaneBpbFields(hints.HeaderBytes!) && DeclaredSizeMatchesVolume(hints.HeaderBytes!, hints.FileSize))
            {
                confidence += 0.3f;
            }

            return Math.Min(confidence, 1.0f);
        }

        private static bool HasSaneBpbFields(byte[] header)
        {
            ushort bytesPerSector = (ushort)(header[11] | (header[12] << 8));
            byte sectorsPerCluster = header[13];
            byte numberOfFats = header[16];
            ushort sectorsPerFat = (ushort)(header[22] | (header[23] << 8));

            return bytesPerSector == 512
                && sectorsPerCluster >= 1 && sectorsPerCluster <= 128
                && (numberOfFats == 1 || numberOfFats == 2)
                && sectorsPerFat != 0;
        }

        private static bool DeclaredSizeMatchesVolume(byte[] header, long actualFileSize)
        {
            ushort totalSectors16 = (ushort)(header[19] | (header[20] << 8));
            uint totalSectors32 = (uint)(header[32] | (header[33] << 8) | (header[34] << 16) | (header[35] << 24));
            uint declaredSectors = totalSectors16 != 0 ? totalSectors16 : totalSectors32;

            if (declaredSectors == 0)
            {
                return false;
            }

            long declaredBytes = (long)declaredSectors * 512;

            // Allow the declared size to be slightly smaller than the actual stream (a partition
            // can have trailing slack/alignment padding) but never larger — a boot sector can't
            // legitimately describe a volume bigger than the bytes it actually lives in.
            return declaredBytes <= actualFileSize && declaredBytes >= actualFileSize / 2;
        }

        /// <summary>
        /// Creates a new, unmounted PC floppy driver.
        /// </summary>
        /// <returns>
        /// A new <see cref="PcFloppyDriver"/>.
        /// </returns>
        public override IFileSystemDriver CreateDriver()
        {
            return new PcFloppyDriver();
        }

        /// <inheritdoc/>
        public IReadOnlyList<BlankImageLayout> BlankLayouts => Layouts;

        /// <inheritdoc/>
        public byte[] CreateBlank(string layoutId, IReadOnlyDictionary<string, string> options)
        {
            if (TryHardDisk(layoutId, out PcHddPreset preset))
            {
                return PcImageFactory.CreateBlankHardDisk(preset, VolumeLabel.ValueIn(options));
            }

            return PcImageFactory.CreateBlankFloppy(SizeOf(layoutId), VolumeLabel.ValueIn(options));
        }

        /// <inheritdoc/>
        public IFileSystemDriver Mount(Stream stream, bool readOnly, string layoutId)
        {
            PcFloppyDriver driver = new PcFloppyDriver();
            if (TryHardDisk(layoutId, out _))
            {
                // The file system of a hard disk created here is in partition 0.
                driver.SelectPartition("0");
            }
            else
            {
                SizeOf(layoutId);
            }

            driver.Mount(stream, readOnly);
            return driver;
        }

        private static bool TryHardDisk(string layoutId, out PcHddPreset preset)
        {
            switch (layoutId)
            {
                case "pc-hd-xt-5m": preset = PcHddPreset.Xt5MB; return true;
                case "pc-hd-xt-10m": preset = PcHddPreset.Xt10MB; return true;
                case "pc-hd-xt-15m": preset = PcHddPreset.Xt15MB; return true;
                case "pc-hd-xt-26m": preset = PcHddPreset.Xt26MB; return true;
                case "pc-hd-at-20m": preset = PcHddPreset.At20MB; return true;
                case "pc-hd-at-30m": preset = PcHddPreset.At30MB; return true;
                case "pc-hd-at-47m": preset = PcHddPreset.At47MB; return true;
                case "pc-hd-at-62m": preset = PcHddPreset.At62MB; return true;
                default: preset = PcHddPreset.Xt5MB; return false;
            }
        }

        private static PcFloppySize SizeOf(string layoutId)
        {
            return layoutId switch
            {
                "pc-360k" => PcFloppySize.Size360K,
                "pc-720k" => PcFloppySize.Size720K,
                "pc-1200k" => PcFloppySize.Size1_2M,
                "pc-1440k" => PcFloppySize.Size1_44M,
                "pc-2880k" => PcFloppySize.Size2_88M,
                _ => throw new ArgumentException("not a PC floppy blank-image layout: " + layoutId, nameof(layoutId)),
            };
        }

        /// <summary>
        /// Detects confidence based on file size matching known PC floppy formats.
        /// </summary>
        private float DetectConfidenceFromSize(long length)
        {
            return length switch
            {
                368640 => 0.6f,   // 360KB
                737280 => 0.6f,   // 720KB
                1228800 => 0.6f,  // 1.2MB
                1474560 => 0.6f,  // 1.44MB
                2949120 => 0.6f,  // 2.88MB
                _ => 0.0f
            };
        }
    }
}
