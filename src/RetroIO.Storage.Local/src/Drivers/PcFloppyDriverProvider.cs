//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Provider for PC floppy disk drivers (FAT12/FAT16).
    /// Handles format detection and driver instantiation.
    /// </summary>
    public sealed class PcFloppyDriverProvider : FileSystemDriverProviderBase
    {
        public override string FileSystemType => "PC-Floppy";

        public override string Description => "PC floppy disk images (360KB-2.88MB, FAT12/FAT16)";

        public override float DetectConfidence(DetectionHints hints)
        {
            float confidence = 0.0f;

            // 1. Check file extension
            string ext = hints.FileExtension.ToLowerInvariant();
            if (ext == ".img" || ext == ".ima" || ext == ".vfd" || ext == ".flp")
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
                // No boot signature at all — whatever this is, it isn't a FAT volume.
                return confidence;
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

        public override IFileSystemDriver CreateDriver()
        {
            return new PcFloppyDriver();
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
