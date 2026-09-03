//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Storage.Local
{
    /// <summary>Commodore sector-disk image types that can be created blank.</summary>
    public enum CbmDiskType
    {
        D64, // 1541, 35 tracks, 174,848 bytes
        D71, // 1571, 70 tracks, 349,696 bytes
        D81  // 1581, 80 tracks, 819,200 bytes
    }

    /// <summary>
    /// Creates blank, c1541-valid Commodore disk images (D64/D71/D81) in memory or to a file, by
    /// formatting a zeroed buffer through <see cref="CbmDosFileSystem.Format"/>.
    /// </summary>
    public static class CbmImageFactory
    {
        /// <summary>Returns a freshly-formatted blank disk image of the given type.</summary>
        public static byte[] CreateBlank(CbmDiskType type, string diskName, string diskId)
        {
            BlockDeviceDescriptor descriptor;
            int directoryTrack;
            int bamTrack;
            int size;

            switch (type)
            {
                case CbmDiskType.D64:
                    descriptor = BlockDeviceDescriptor.D64_35Track;
                    directoryTrack = 18;
                    bamTrack = 18;
                    size = 174848;
                    break;
                case CbmDiskType.D71:
                    descriptor = BlockDeviceDescriptor.D71;
                    directoryTrack = 18;
                    bamTrack = 18;
                    size = 349696;
                    break;
                case CbmDiskType.D81:
                    descriptor = BlockDeviceDescriptor.D81;
                    directoryTrack = 40;
                    bamTrack = 40;
                    size = 819200;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }

            using MemoryStream ms = new MemoryStream();
            ms.SetLength(size); // pre-size + zero-fill so the block device mounts the right geometry

            GenericBlockDevice device = new GenericBlockDevice(descriptor);
            device.Mount(ms, readOnly: false);
            CbmDosFileSystem fs = new CbmDosFileSystem(directoryTrack, bamTrack);
            fs.Initialize(device);
            fs.Format(diskName ?? string.Empty, diskId ?? string.Empty);
            device.Dispose(); // flushes the formatted image back to the stream

            return ms.ToArray();
        }

        /// <summary>Creates a blank disk image and writes it to <paramref name="path"/>.</summary>
        public static void CreateBlankFile(CbmDiskType type, string path, string diskName, string diskId)
        {
            File.WriteAllBytes(path, CreateBlank(type, diskName, diskId));
        }

        /// <summary>Maps a disk type to its conventional file extension (".d64" etc.).</summary>
        public static string ExtensionFor(CbmDiskType type)
        {
            return type switch
            {
                CbmDiskType.D64 => ".d64",
                CbmDiskType.D71 => ".d71",
                CbmDiskType.D81 => ".d81",
                _ => ".d64"
            };
        }
    }
}
