//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FileSystems;
using RetroIO.Storage.Local.MBR.Structures;
using RetroIO.Storage.Local.Streams;

namespace RetroIO.Storage.Local
{
    /// <summary>
    /// PC floppy sizes that can be created blank (all formatted FAT12, matching real DOS).
    /// </summary>
    public enum PcFloppySize
    {
        /// <summary>
        /// 360 KB, 5.25 inch double density, 40 tracks, 2 sides, 9 sectors per track.
        /// </summary>
        Size360K,  // 5.25" DD, 40/2/9, the normal PC/XT floppy
        /// <summary>
        /// 720 KB, 3.5 inch double density, 80 tracks, 2 sides, 9 sectors per track.
        /// </summary>
        Size720K,  // 3.5" DD, 80/2/9
        /// <summary>
        /// 1.2 MB, 5.25 inch high density, 80 tracks, 2 sides, 15 sectors per track.
        /// </summary>
        Size1_2M,  // 5.25" HD, 80/2/15, the normal PC/AT floppy
        /// <summary>
        /// 1.44 MB, 3.5 inch high density, 80 tracks, 2 sides, 18 sectors per track.
        /// </summary>
        Size1_44M, // 3.5" HD, 80/2/18, the later AT/286/386-era standard
        /// <summary>
        /// 2.88 MB, 3.5 inch extra density, 80 tracks, 2 sides, 36 sectors per track.
        /// </summary>
        Size2_88M  // 3.5" ED, 80/2/36
    }

    /// <summary>
    /// Real ST-506/ST-412-class PC/XT and PC/AT hard disk presets, using the classic IBM fixed
    /// BIOS drive-type geometries (all 17 sectors/track).
    /// </summary>
    public enum PcHddPreset
    {
        /// <summary>
        /// 5 MB, 306 cylinders, 2 heads, 17 sectors per track (IBM XT BIOS drive type 0).
        /// </summary>
        Xt5MB,   // 306/2/17 — IBM XT BIOS drive type 0
        /// <summary>
        /// 10 MB, 306 cylinders, 4 heads, 17 sectors per track (ST-412 class XT drive, also IBM AT BIOS drive type 1).
        /// </summary>
        Xt10MB,  // 306/4/17 — the "usual" XT drive, ST-412-class (also IBM AT type 1)
        /// <summary>
        /// 15 MB, 306 cylinders, 6 heads, 17 sectors per track (IBM XT BIOS drive type 2).
        /// </summary>
        Xt15MB,  // 306/6/17 — IBM XT BIOS drive type 2
        /// <summary>
        /// 26 MB, 375 cylinders, 8 heads, 17 sectors per track (IBM XT BIOS drive type 1).
        /// </summary>
        Xt26MB,  // 375/8/17 — IBM XT BIOS drive type 1
        /// <summary>
        /// 20 MB, 615 cylinders, 4 heads, 17 sectors per track (IBM AT BIOS drive type 2).
        /// </summary>
        At20MB,  // 615/4/17 — IBM AT BIOS drive type 2, ST-225 class
        /// <summary>
        /// 30 MB, 615 cylinders, 6 heads, 17 sectors per track (IBM AT BIOS drive type 3).
        /// </summary>
        At30MB,  // 615/6/17 — IBM AT BIOS drive type 3
        /// <summary>
        /// 47 MB, 940 cylinders, 6 heads, 17 sectors per track (IBM AT BIOS drive type 5).
        /// </summary>
        At47MB,  // 940/6/17 — IBM AT BIOS drive type 5
        /// <summary>
        /// 62 MB, 940 cylinders, 8 heads, 17 sectors per track (IBM AT BIOS drive type 4).
        /// </summary>
        At62MB   // 940/8/17 — IBM AT BIOS drive type 4
    }

    /// <summary>
    /// Creates blank PC floppy and hard disk images, formatted through
    /// <see cref="Fat12FileSystem.Format"/>/<see cref="Fat16FileSystem.Format"/> — mirrors
    /// <see cref="CbmImageFactory"/>'s convention of formatting a zeroed, correctly-sized buffer
    /// rather than hand-assembling bytes at the call site.
    /// </summary>
    public static class PcImageFactory
    {
        /// <summary>
        /// Creates a blank floppy image, formatted FAT12 with real DOS-era defaults for the given
        /// size (cluster size and root directory capacity match what MS-DOS itself used).
        /// </summary>
        public static byte[] CreateBlankFloppy(PcFloppySize size, string volumeLabel = "")
        {
            BlockDeviceDescriptor descriptor = FloppyDescriptorFor(size);
            (byte sectorsPerCluster, ushort rootEntryCount) = FloppyFatParamsFor(size);

            using MemoryStream ms = new MemoryStream();
            ms.SetLength(descriptor.ExpectedImageSize);

            GenericBlockDevice device = new GenericBlockDevice(descriptor);
            device.Mount(ms, readOnly: false);
            Fat12FileSystem.Format(device, descriptor.TotalBlocks, sectorsPerCluster, reservedSectors: 1,
                numberOfFats: 2, rootEntryCount: rootEntryCount, volumeLabel: volumeLabel ?? string.Empty);
            device.Dispose(); // flushes the formatted image back to the stream

            return ms.ToArray();
        }

        /// <summary>
        /// Creates a blank floppy image and writes it to <paramref name="path"/>.
        /// </summary>
        public static void CreateBlankFloppyFile(PcFloppySize size, string path, string volumeLabel = "")
        {
            File.WriteAllBytes(path, CreateBlankFloppy(size, volumeLabel));
        }

        /// <summary>
        /// Creates a blank PC/XT or PC/AT hard disk image: a real MBR partition table with a
        /// single partition spanning the whole disk (starting at the first sector after cylinder
        /// 0's head 0 — the classic FDISK alignment convention), formatted FAT12 for volumes up
        /// to ~16MB and FAT16 above that (this codebase's own size threshold, not a specific real
        /// DOS version's exact behavior — see <see cref="Fat12FileSystem.Format"/> for why FAT12
        /// caps out around there: at most 4084 clusters).
        /// </summary>
        public static byte[] CreateBlankHardDisk(PcHddPreset preset, string volumeLabel = "")
        {
            BlockDeviceDescriptor descriptor = HddDescriptorFor(preset);
            uint totalSectors = descriptor.TotalBlocks;
            uint partitionStart = (uint)descriptor.UniformSectorsPerTrack;
            uint partitionSectors = totalSectors - partitionStart;

            const uint fat12SectorCeiling = 32000; // ~16MB at 512 bytes/sector — see remarks above
            bool useFat16 = partitionSectors > fat12SectorCeiling;
            byte partitionTypeCode = useFat16 ? (byte)0x06 : (byte)0x01;

            byte[] mbr = MbrPartitionTable.CreateWithSinglePartition(totalSectors, partitionTypeCode, partitionStart);

            using MemoryStream ms = new MemoryStream();
            ms.SetLength((long)totalSectors * 512);
            ms.Position = 0;
            ms.Write(mbr, 0, mbr.Length);

            var partitionDescriptor = new BlockDeviceDescriptor
            {
                Name = descriptor.Name + " (partition 0)",
                BlockSize = 512,
                AddressMode = BlockAddressMode.LBA,
                TotalBlocks = partitionSectors
            };

            using (SubStream partitionStream = new SubStream(ms, (long)partitionStart * 512, (long)partitionSectors * 512))
            {
                GenericBlockDevice device = new GenericBlockDevice(partitionDescriptor);
                device.Mount(partitionStream, readOnly: false);

                if (useFat16)
                {
                    Fat16FileSystem.Format(device, partitionSectors, volumeLabel: volumeLabel ?? string.Empty);
                }
                else
                {
                    Fat12FileSystem.Format(device, partitionSectors, sectorsPerCluster: 8,
                        rootEntryCount: 512, volumeLabel: volumeLabel ?? string.Empty);
                }

                device.Dispose(); // flushes into partitionStream, which writes through into ms
            }

            return ms.ToArray();
        }

        /// <summary>
        /// Creates a blank hard disk image and writes it to <paramref name="path"/>.
        /// </summary>
        public static void CreateBlankHardDiskFile(PcHddPreset preset, string path, string volumeLabel = "")
        {
            File.WriteAllBytes(path, CreateBlankHardDisk(preset, volumeLabel));
        }

        private static BlockDeviceDescriptor FloppyDescriptorFor(PcFloppySize size)
        {
            return size switch
            {
                PcFloppySize.Size360K => BlockDeviceDescriptor.PC_360K,
                PcFloppySize.Size720K => BlockDeviceDescriptor.PC_720K,
                PcFloppySize.Size1_2M => BlockDeviceDescriptor.PC_1_2M,
                PcFloppySize.Size1_44M => BlockDeviceDescriptor.PC_1_44M,
                PcFloppySize.Size2_88M => BlockDeviceDescriptor.PC_2_88M,
                _ => throw new ArgumentOutOfRangeException(nameof(size))
            };
        }

        /// <summary>
        /// Real MS-DOS defaults: (sectors/cluster, root directory entry count).
        /// </summary>
        private static (byte SectorsPerCluster, ushort RootEntryCount) FloppyFatParamsFor(PcFloppySize size)
        {
            return size switch
            {
                PcFloppySize.Size360K => ((byte)2, (ushort)112),
                PcFloppySize.Size720K => ((byte)2, (ushort)112),
                PcFloppySize.Size1_2M => ((byte)1, (ushort)224),
                PcFloppySize.Size1_44M => ((byte)1, (ushort)224),
                PcFloppySize.Size2_88M => ((byte)2, (ushort)240),
                _ => throw new ArgumentOutOfRangeException(nameof(size))
            };
        }

        private static BlockDeviceDescriptor HddDescriptorFor(PcHddPreset preset)
        {
            return preset switch
            {
                PcHddPreset.Xt5MB => BlockDeviceDescriptor.PcXt_5MB,
                PcHddPreset.Xt10MB => BlockDeviceDescriptor.PcXt_10MB,
                PcHddPreset.Xt15MB => BlockDeviceDescriptor.PcXt_15MB,
                PcHddPreset.Xt26MB => BlockDeviceDescriptor.PcXt_26MB,
                PcHddPreset.At20MB => BlockDeviceDescriptor.PcAt_20MB,
                PcHddPreset.At30MB => BlockDeviceDescriptor.PcAt_30MB,
                PcHddPreset.At47MB => BlockDeviceDescriptor.PcAt_47MB,
                PcHddPreset.At62MB => BlockDeviceDescriptor.PcAt_62MB,
                _ => throw new ArgumentOutOfRangeException(nameof(preset))
            };
        }

        /// <summary>
        /// Maps a floppy size to its conventional file extension.
        /// </summary>
        public static string ExtensionFor(PcFloppySize size) => ".img";

        /// <summary>
        /// Maps a hard disk preset to its conventional file extension.
        /// </summary>
        public static string ExtensionFor(PcHddPreset preset) => ".img";
    }
}
