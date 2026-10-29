//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.FileSystems
{
    /// <summary>
    /// CoCo DECB (Disk Extended Color BASIC) filesystem implementation.
    /// Used on Color Computer disk images of 35, 40 or 80 tracks, one side, 18 sectors of 256 bytes.
    /// Directory and FAT on Track 17, data on other tracks.
    /// </summary>
    /// <remarks>
    /// The geometries are the ones Toolshed makes and reads. Toolshed <c>decb dskini</c> (libdecbdskini.c in 2.6.1, decbdskini.c in
    /// the 2.2 tag) takes 35, 40 or 80 tracks and writes a FAT of 68, 78 or 156 free granules; the rest of the FAT sector is zero.
    /// Toolshed 2.6.1 <c>decb -g</c> names the same three counts (68 for 35 tracks, 78 for 40, 156 for 80). A granule is placed by
    /// libdecbseek.c <c>_decb_seekgranule</c>: granule times 2304 bytes from the start of the image, plus two granules (track 17) for
    /// granules above 33. So granules past 67 simply go on past track 35 in the same way, two per track: 78 granules end on track 39
    /// and 156 granules end on track 78 (track 79 of an 80 track disk holds no granule). Toolshed treats the image as one side of
    /// 18 sectors per track and has no two sided DECB layout, so a two sided device is not taken.
    /// </remarks>
    public class DecbFileSystem : IBlockFileSystem
    {
        private IBlockDevice? _device;
        private string _volumeLabel = string.Empty;
        private byte[]? _fat;
        private int _sectorsPerTrack = 18;
        private int _trackCount = 35;
        private int _granuleCount = 68;

        // 68 for an instance made by WithSixtyEightGranules, which takes the granule count from the FAT layout rather than the track count;
        // 0 when the granule count comes from the track count.
        private readonly int _fixedGranuleCount;

        private const int FAT_TRACK = 17;
        private const int FAT_SECTOR = 2;
        private const int DIR_START_SECTOR = 3;
        private const int DIR_END_SECTOR = 11;
        private const int DIR_ENTRY_SIZE = 32;
        private const int ENTRIES_PER_SECTOR = 8;
        private const int SECTOR_SIZE = 256;
        private const int GRANULE_SIZE = 2304; // 9 sectors * 256 bytes
        private const int SECTORS_PER_GRANULE = 9;
        private const int SECTORS_PER_TRACK = 18;

        /// <summary>
        /// Makes a file system that takes the granule count from the track count of the device: 68, 78 or 156.
        /// </summary>
        public DecbFileSystem()
        {
        }

        private DecbFileSystem(int fixedGranuleCount)
        {
            _fixedGranuleCount = fixedGranuleCount;
        }

        /// <summary>
        /// Gives the number of granules of a DECB disk with this many tracks, as Toolshed dskini writes the FAT.
        /// </summary>
        /// <remarks>
        /// From Toolshed libdecbdskini.c: 40 tracks give 78 granules, 80 tracks give 156, 35 tracks give 68.
        /// </remarks>
        /// <param name="tracks">
        /// Tracks of one side.
        /// </param>
        /// <returns>
        /// 68, 78 or 156, or 0 when Toolshed makes no DECB disk with this many tracks.
        /// </returns>
        public static int GranulesForTracks(int tracks)
        {
            switch (tracks)
            {
                case 35:
                    return 68;
                case 40:
                    return 78;
                case 80:
                    return 156;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Gets the number of granules of the mounted disk: 68, 78 or 156.
        /// </summary>
        public int GranuleCount => _granuleCount;

        /// <summary>
        /// Gets the file system type identifier: <c>DECB</c>.
        /// </summary>
        public string FileSystemType => "DECB";
        /// <summary>
        /// Gets <c>COCO DISK</c> after initialization, or an empty string before it. DECB disks have no stored label.
        /// </summary>
        public string VolumeLabel => _volumeLabel;
        /// <summary>
        /// Gets <c>.</c>, the separator between file name and extension in DECB.
        /// </summary>
        public string ExtensionSeparator => ".";  // DECB uses name.ext format

        /// <summary>
        /// Attaches the file system to a block device, takes the geometry from it and reads the FAT from track 17, sector 2.
        /// </summary>
        /// <remarks>
        /// When the device gives no sectors-per-track or cylinder count, 18 sectors and 35 tracks are used. The granule count
        /// comes from the track count (see <see cref="GranulesForTracks"/>).
        /// </remarks>
        /// <param name="device">
        /// Block device holding the disk image, addressed by LBA.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="device"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// Thrown when the device is not one side of 35, 40 or 80 tracks of 18 sectors, which are the DECB disks Toolshed makes.
        /// </exception>
        public void Initialize(IBlockDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));

            BlockDeviceInfo? info = device.GetBlockDeviceInfo();
            int heads = 1;
            if (info != null)
            {
                _sectorsPerTrack = info.SectorsPerTrack > 0 ? info.SectorsPerTrack : 18;
                _trackCount = info.CylinderCount > 0 ? info.CylinderCount : 35;
                heads = info.HeadCount > 0 ? info.HeadCount : 1;
            }

            // An instance made by WithSixtyEightGranules uses 68 granules on any device of at least 35 tracks.
            int granules = _fixedGranuleCount != 0
                ? (_trackCount >= 35 ? _fixedGranuleCount : 0)
                : GranulesForTracks(_trackCount);
            if (granules == 0 || _sectorsPerTrack != SECTORS_PER_TRACK || heads != 1)
            {
                _device = null;
                throw new NotSupportedException(
                    $"DECB disks are one side of 35, 40 or 80 tracks of 18 sectors; this device has {_trackCount} tracks, {heads} side(s), {_sectorsPerTrack} sectors per track");
            }

            _granuleCount = granules;

            // Read FAT from Track 17, Sector 2
            uint fatLsn = CalculateLSN(FAT_TRACK, FAT_SECTOR);
            _fat = new byte[SECTOR_SIZE];
            device.ReadBlock(BlockAddress.FromLBA(fatLsn), _fat);

            // Try to extract volume label from first directory entry filename
            uint firstDirLsn = CalculateLSN(FAT_TRACK, DIR_START_SECTOR);
            byte[] firstDirSector = new byte[SECTOR_SIZE];
            device.ReadBlock(BlockAddress.FromLBA(firstDirLsn), firstDirSector);

            // Check if first entry exists (not 0x00 or 0xFF)
            if (firstDirSector[0] != 0x00 && firstDirSector[0] != 0xFF)
            {
                string firstFileName = Encoding.ASCII.GetString(firstDirSector, 0, 8).Trim();
                if (!string.IsNullOrWhiteSpace(firstFileName))
                {
                    _volumeLabel = $"COCO DISK";
                }
                else
                {
                    _volumeLabel = "COCO DISK";
                }
            }
            else
            {
                _volumeLabel = "COCO DISK";
            }
        }

        /// <summary>
        /// Checks whether the device looks like a DECB disk.
        /// </summary>
        /// <remarks>
        /// The device must report one side of 35, 40 or 80 cylinders and 18 sectors per track. The FAT sector (track 17, sector 2)
        /// is then read. On 35 tracks its first 68 bytes must hold at least one free or allocated granule value, as this method has
        /// always asked. On 40 and 80 tracks every one of its first 78 or 156 bytes (one per granule) must be a FAT value: 0xFF for
        /// a free granule, a granule number below the granule count for the next granule of a file, or 0xC0 to 0xC9 for the last
        /// granule of a file. Those are the values the DECB FAT holds (Toolshed libdecbopen.c, the format notes at the top).
        /// The FAT must also be a set of linked lists: no granule is the next granule of two others, and every chain ends in a
        /// last-granule value without running into a free granule or going round in a loop. And the rest of the FAT sector must
        /// be zero, as Toolshed dskini leaves it.
        /// The device is not changed. Any exception while reading gives <c>false</c>.
        /// </remarks>
        /// <param name="device">
        /// Block device to inspect.
        /// </param>
        /// <returns>
        /// <c>true</c> when the geometry and the FAT sector fit DECB; otherwise <c>false</c>.
        /// </returns>
        public bool CanHandle(IBlockDevice device)
        {
            try
            {
                BlockDeviceInfo? info = device.GetBlockDeviceInfo();
                if (info == null) return false;

                int granules = GranulesForTracks(info.CylinderCount);
                if (granules == 0 || info.SectorsPerTrack != SECTORS_PER_TRACK || info.HeadCount > 1)
                {
                    return false;
                }

                // Read FAT sector to check validity
                uint fatLsn = (uint)((FAT_TRACK * SECTORS_PER_TRACK) + (FAT_SECTOR - 1)); // Track 17, Sector 2
                byte[] fatSector = new byte[SECTOR_SIZE];
                device.ReadBlock(BlockAddress.FromLBA(fatLsn), fatSector);

                if (_fixedGranuleCount != 0)
                {
                    // The 68 granule layout on a device of any track count (see WithSixtyEightGranules): linked lists in bytes 0 to 67, and
                    // one filler, 0x00 or 0xFF, in all the rest of the sector.
                    if (!IsLinkedListFat(fatSector, _fixedGranuleCount))
                    {
                        return false;
                    }

                    byte filler = fatSector[_fixedGranuleCount];
                    if (filler != 0x00 && filler != 0xFF)
                    {
                        return false;
                    }

                    for (int i = _fixedGranuleCount + 1; i < SECTOR_SIZE; i++)
                    {
                        if (fatSector[i] != filler)
                        {
                            return false;
                        }
                    }

                    return true;
                }

                if (granules == 68)
                {
                    // 35 tracks: the check this method has always made is kept as it was. No other file system of the Dragon drivers has a 35
                    // track layout (DragonDOS is 40 or 80 tracks, OS-9 is tried before DECB), so the DSK driver takes a 35 track disk as DECB
                    // whenever this says yes, and RetroIO.Tests Mount_CoCo35Track_ShouldSucceed pins that with a FAT sector of 0x33 bytes,
                    // which is not a set of linked lists. Count free granules (0xFF bytes) and allocated granules.
                    int freeCount = 0;
                    int allocCount = 0;
                    for (int i = 0; i < granules; i++)
                    {
                        if (fatSector[i] == 0xFF)
                        {
                            freeCount++;
                        }
                        else if (fatSector[i] < granules || fatSector[i] >= 0xC0)
                        {
                            allocCount++;
                        }
                    }

                    // Valid DECB should have some allocated or free granules
                    return freeCount > 0 || allocCount > 0;
                }

                // 40 and 80 tracks: DragonDOS is the other file system of the same size, and it is asked first, so this is only asked about
                // a disk that is not DragonDOS. It says yes only for a FAT that holds DECB values, forms linked lists and is laid out as
                // Toolshed lays it out.
                if (!IsLinkedListFat(fatSector, granules))
                {
                    return false;
                }

                // On 40 and 80 tracks the only layout known is the one Toolshed dskini -4 and -8 writes: the bytes of the FAT sector after the
                // last granule are zero (libdecbdskini.c fills the sector with 0 and sets only the granule bytes to 0xFF), and neither Toolshed
                // nor this code writes them later. A real 35 track disk need not follow it (syzygy.dsk has 0xFF in bytes 68 to 255), so it is
                // a rule for these two sizes only. Two real 40 track game images (HTCHHKER.VDK, PLANETFALL.VDK) hold a 68 granule FAT (0xFF or
                // 0x00 after byte 67), which is not this layout, so they are not taken as 78 granule DECB disks; the instance made by
                // WithSixtyEightGranules takes them.
                for (int i = granules; i < SECTOR_SIZE; i++)
                {
                    if (fatSector[i] != 0x00)
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Makes an instance that reads and writes a disk as 68 granules, the FAT of a 35 track disk, whatever the track count of the device.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Some 40 track Dragon and CoCo images hold a DECB disk of 35 track layout: the FAT on track 17 has 68 granules and the last 5
        /// tracks hold no granule. HTCHHKER.VDK and PLANETFALL.VDK are two (their FAT has 0xFF, and 0xC1 for PLANETFALL's GAME.BIN, in bytes
        /// 0 to 67, and 0xFF or 0x00 in all of bytes 68 to 255). Toolshed decb reads them so: on the 40 track image and on a cut of its first
        /// 35 tracks it lists the same directory and, for PLANETFALL, 67 free granules.
        /// </para>
        /// <para>
        /// <see cref="CanHandle"/> on this instance asks for one side of at least 35 tracks of 18 sectors and a FAT whose 68 granule bytes
        /// are DECB values forming linked lists (the same rule as on 40 and 80 tracks), with bytes 68 to 255 all 0x00 or all 0xFF. Those two
        /// are the only fillers seen on real disks (PLANETFALL 0x00; HTCHHKER and the 35 track syzygy.dsk 0xFF). A file written to such a
        /// disk goes in granules 0 to 67 only, so tracks 35 and up are never written.
        /// </para>
        /// </remarks>
        /// <returns>
        /// A file system that is not initialized.
        /// </returns>
        public static DecbFileSystem WithSixtyEightGranules()
        {
            return new DecbFileSystem(68);
        }

        /// <summary>
        /// Tells whether the first granule bytes of a FAT sector are DECB values that form one linked list per file.
        /// </summary>
        /// <remarks>
        /// Every byte must be 0xFF for a free granule, a granule number below the granule count for the next granule of a file, or 0xC0 to
        /// 0xC9 for the last granule of a file (Toolshed libdecbopen.c, the format notes at the top). No granule may be the next granule of two
        /// others, and every chain must end in a last-granule value without running into a free granule or going round in a loop.
        /// </remarks>
        /// <param name="fatSector">
        /// The FAT sector.
        /// </param>
        /// <param name="granules">
        /// The granule count.
        /// </param>
        /// <returns>
        /// <c>true</c> when the bytes form a DECB FAT.
        /// </returns>
        private static bool IsLinkedListFat(ReadOnlySpan<byte> fatSector, int granules)
        {
            Span<bool> hasPredecessor = stackalloc bool[granules];
            for (int i = 0; i < granules; i++)
            {
                byte value = fatSector[i];
                bool free = value == 0xFF;
                bool next = value < granules;
                bool last = value >= 0xC0 && value <= 0xC9;
                if (!free && !next && !last)
                {
                    return false;
                }

                // The FAT is one linked list per file ("the bytes in the FAT form a linked list for each file", libdecbopen.c), so no
                // granule is the next granule of two others. An all-zero sector would otherwise pass as every granule linking to granule 0.
                if (next)
                {
                    if (hasPredecessor[value])
                    {
                        return false;
                    }

                    hasPredecessor[value] = true;
                }
            }

            // Every chain must end in a last-granule value: it may not run into a free granule or go round in a loop.
            for (int i = 0; i < granules; i++)
            {
                int granule = i;
                int steps = 0;
                while (fatSector[granule] < granules)
                {
                    granule = fatSector[granule];
                    if (fatSector[granule] == 0xFF || ++steps > granules)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Lists the files in the root directory (track 17, sectors 3 to 11).
        /// </summary>
        /// <param name="path">
        /// Must be <c>null</c>, empty or <c>/</c>, because DECB has only a root directory.
        /// </param>
        /// <returns>
        /// The directory entries.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the file system is not initialized.
        /// </exception>
        /// <exception cref="DirectoryNotFoundException">
        /// Thrown when <paramref name="path"/> names anything other than the root directory.
        /// </exception>
        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureInitialized();

            // DECB only supports root directory
            if (!string.IsNullOrEmpty(path) && path != "/")
            {
                throw new DirectoryNotFoundException($"DECB has only a root directory: {path}");
            }

            return ListRootDirectory();
        }

        /// <summary>
        /// Reads a whole file by following its granule chain in the FAT.
        /// </summary>
        /// <param name="path">
        /// File name as <c>NAME.EXT</c>, with or without a leading <c>/</c>. The part before the first dot is the name.
        /// </param>
        /// <returns>
        /// The file contents.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the file system is not initialized.
        /// </exception>
        /// <exception cref="System.IO.FileNotFoundException">
        /// Thrown when the file is not in the directory.
        /// </exception>
        public byte[] ReadFile(string path)
        {
            EnsureInitialized();

            // Normalize path
            if (path.StartsWith("/"))
            {
                path = path.Substring(1);
            }

            // Split filename and extension
            string filename;
            string extension;
            int dotIndex = path.IndexOf('.');
            if (dotIndex >= 0)
            {
                filename = path.Substring(0, dotIndex);
                extension = path.Substring(dotIndex + 1);
            }
            else
            {
                filename = path;
                extension = string.Empty;
            }

            // Find file in directory
            DecbEntry? entry = FindFileInDirectory(filename, extension);
            if (entry == null)
            {
                throw new System.IO.FileNotFoundException($"File not found: {path}");
            }

            // Read file data using granule chain
            return ReadFileData(entry);
        }

        /// <summary>
        /// Creates a file or replaces the contents of an existing one.
        /// </summary>
        /// <remarks>
        /// An existing file keeps its file type and ASCII flag. A new file gets them from its extension: BAS is type 0
        /// binary, DAT is type 1 ASCII, BIN is type 2 binary, TXT is type 3 ASCII, anything else is type 2 binary.
        /// The extension table is a choice made here, because the disk format does not tie a type to an extension.
        /// Use the overload with the type and the flag to set them yourself.
        /// </remarks>
        /// <param name="path">
        /// File name as <c>NAME.EXT</c>, with or without a leading <c>/</c>. Letters are stored in upper case.
        /// </param>
        /// <param name="data">
        /// The new contents.
        /// </param>
        /// <exception cref="IOException">
        /// Thrown when the volume is read-only, the directory has no free entry or the disk has too few free granules.
        /// The disk is not changed in the last two cases.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when the name does not fit a DECB name.
        /// </exception>
        public void WriteFile(string path, byte[] data)
        {
            EnsureInitialized();
            SplitName(path, out string name, out string extension);
            WriteFileCore(name, extension, data, 0, 0, false);
        }

        /// <summary>
        /// Creates a file or replaces an existing one and sets the file type and the ASCII flag in the directory entry.
        /// </summary>
        /// <param name="path">
        /// File name as <c>NAME.EXT</c>, with or without a leading <c>/</c>.
        /// </param>
        /// <param name="data">
        /// The new contents.
        /// </param>
        /// <param name="fileType">
        /// 0 BASIC program, 1 BASIC data, 2 machine language, 3 text.
        /// </param>
        /// <param name="asciiFlag">
        /// 0x00 for a binary file, 0xFF for an ASCII file.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the type is above 3 or the flag is neither 0x00 nor 0xFF.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the volume is read-only, the directory is full or the disk is full.
        /// </exception>
        public void WriteFile(string path, byte[] data, byte fileType, byte asciiFlag)
        {
            EnsureInitialized();
            if (fileType > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(fileType), "DECB file types are 0 to 3");
            }

            if (asciiFlag != 0x00 && asciiFlag != 0xFF)
            {
                throw new ArgumentOutOfRangeException(nameof(asciiFlag), "The DECB ASCII flag is 0x00 or 0xFF");
            }

            SplitName(path, out string name, out string extension);
            WriteFileCore(name, extension, data, fileType, asciiFlag, true);
        }

        /// <summary>
        /// Deletes a file: the first byte of its directory entry is set to 0 and every granule of its chain becomes free (0xFF) in the FAT.
        /// </summary>
        /// <param name="path">
        /// File name as <c>NAME.EXT</c>, with or without a leading <c>/</c>.
        /// </param>
        /// <exception cref="System.IO.FileNotFoundException">
        /// Thrown when the file is not in the directory.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the volume is read-only.
        /// </exception>
        public void DeleteFile(string path)
        {
            EnsureInitialized();
            EnsureWritable();
            SplitName(path, out string name, out string extension);
            ScanDirectory(name, extension, out int found, out int firstEmpty);
            if (found < 0)
            {
                throw new System.IO.FileNotFoundException($"File not found: {path}");
            }

            byte[] entry = ReadDirEntry(found);
            byte[] fat = (byte[])_fat!.Clone();
            FreeChain(fat, entry[13], _granuleCount);

            // Same as Toolshed kill: only the first byte of the entry is cleared, the rest stays.
            entry[0] = 0x00;
            CommitFat(fat);
            WriteDirEntry(found, entry);
            _device!.Flush();
        }

        /// <summary>
        /// Gets metadata of a file from its directory entry.
        /// </summary>
        /// <param name="path">
        /// File name as <c>NAME.EXT</c>, with or without a leading <c>/</c>.
        /// </param>
        /// <returns>
        /// The metadata, or <c>null</c> when the file is not in the directory.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the file system is not initialized.
        /// </exception>
        public FileMetadata? GetMetadata(string path)
        {
            EnsureInitialized();

            if (path.StartsWith("/"))
            {
                path = path.Substring(1);
            }

            string filename;
            string extension;
            int dotIndex = path.IndexOf('.');
            if (dotIndex >= 0)
            {
                filename = path.Substring(0, dotIndex);
                extension = path.Substring(dotIndex + 1);
            }
            else
            {
                filename = path;
                extension = string.Empty;
            }

            DecbEntry? entry = FindFileInDirectory(filename, extension);
            if (entry == null)
            {
                return null;
            }

            string fullName = string.IsNullOrEmpty(entry.Extension)
                ? entry.Filename
                : entry.Filename + "." + entry.Extension;

            return new FileMetadata
            {
                Name = fullName,
                Path = "/" + fullName,
                IsDirectory = false,
                Size = entry.FileSize,
                FileType = entry.Extension,
                Attributes = GetFileTypeDescription(entry.FileType),
                UrnIdentifier = UrnIdentifier.Encode(fullName)
            };
        }

        /// <summary>
        /// Counts the free granules in the FAT and gives the result in sectors.
        /// </summary>
        /// <returns>
        /// The number of free 256-byte sectors: free granules times 9.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the file system is not initialized.
        /// </exception>
        public int GetBlocksFree()
        {
            EnsureInitialized();

            int freeGranules = 0;
            for (int i = 0; i < _granuleCount; i++)
            {
                if (_fat![i] == 0xFF)
                {
                    freeGranules++;
                }
            }

            return freeGranules * SECTORS_PER_GRANULE;
        }

        /// <summary>
        /// Not supported, because DECB has no subdirectories. Always throws.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public void CreateDirectory(string path)
        {
            throw new NotSupportedException("DECB does not support subdirectories");
        }

        /// <summary>
        /// Not supported, because DECB has no subdirectories. Always throws.
        /// </summary>
        /// <param name="path">
        /// Not used.
        /// </param>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public void DeleteDirectory(string path)
        {
            throw new NotSupportedException("DECB does not support subdirectories");
        }

        /// <summary>
        /// Renames a file by rewriting the name and extension in its directory entry. Nothing else changes.
        /// </summary>
        /// <param name="oldPath">
        /// Current name as <c>NAME.EXT</c>.
        /// </param>
        /// <param name="newPath">
        /// New name as <c>NAME.EXT</c>.
        /// </param>
        /// <exception cref="System.IO.FileNotFoundException">
        /// Thrown when <paramref name="oldPath"/> is not in the directory.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the volume is read-only or a file with the new name exists.
        /// </exception>
        public void Rename(string oldPath, string newPath)
        {
            EnsureInitialized();
            EnsureWritable();
            SplitName(oldPath, out string oldName, out string oldExtension);
            SplitName(newPath, out string newName, out string newExtension);

            ScanDirectory(oldName, oldExtension, out int found, out int firstEmpty);
            if (found < 0)
            {
                throw new System.IO.FileNotFoundException($"File not found: {oldPath}");
            }

            ScanDirectory(newName, newExtension, out int clash, out firstEmpty);
            if (clash >= 0)
            {
                throw new IOException($"A file named {newPath} exists");
            }

            byte[] entry = ReadDirEntry(found);
            PutName(entry, newName, newExtension);
            WriteDirEntry(found, entry);
            _device!.Flush();
        }

        // ===================================================================
        // PRIVATE METHODS
        // ===================================================================

        private DirectoryEntry[] ListRootDirectory()
        {
            List<DirectoryEntry> entries = new List<DirectoryEntry>();

            // Read directory sectors (Track 17, Sectors 3-11)
            for (int sector = DIR_START_SECTOR; sector <= DIR_END_SECTOR; sector++)
            {
                uint lsn = CalculateLSN(FAT_TRACK, sector);
                byte[] sectorData = new byte[SECTOR_SIZE];
                _device!.ReadBlock(BlockAddress.FromLBA(lsn), sectorData);

                // Parse 8 entries per sector
                for (int entryNum = 0; entryNum < ENTRIES_PER_SECTOR; entryNum++)
                {
                    int offset = entryNum * DIR_ENTRY_SIZE;
                    DecbEntry? entry = ParseDirectoryEntry(sectorData, offset);

                    if (entry != null && !entry.IsDeleted && !entry.IsEndOfDirectory)
                    {
                        string fullName = string.IsNullOrEmpty(entry.Extension)
                            ? entry.Filename
                            : entry.Filename + "." + entry.Extension;

                        FileMetadata metadata = new FileMetadata
                        {
                            Name = fullName,
                            Path = "/" + fullName,
                            IsDirectory = false,
                            Size = entry.FileSize,
                            FileType = entry.Extension,
                            Attributes = GetFileTypeDescription(entry.FileType),
                            UrnIdentifier = UrnIdentifier.Encode(fullName)
                        };

                        entries.Add(new DirectoryEntry { Name = fullName, Metadata = metadata });
                    }

                    // Stop if we hit end of directory marker
                    if (entry != null && entry.IsEndOfDirectory)
                    {
                        return entries.ToArray();
                    }
                }
            }

            return entries.ToArray();
        }

        private DecbEntry? ParseDirectoryEntry(byte[] data, int offset)
        {
            byte firstByte = data[offset];

            // Check for deleted file
            if (firstByte == 0x00)
            {
                return new DecbEntry { IsDeleted = true };
            }

            // Check for end of directory
            if (firstByte == 0xFF)
            {
                return new DecbEntry { IsEndOfDirectory = true };
            }

            // Parse file entry
            string filename = Encoding.ASCII.GetString(data, offset + 0, 8).Trim();
            string extension = Encoding.ASCII.GetString(data, offset + 8, 3).Trim();
            // DECB directory entry (32 bytes): name 0-7, extension 8-10, file type 11, ASCII flag 12, first granule 13,
            // bytes used in the last sector 14-15 (big-endian, as on the 6809). Checked on syzygy.dsk: SYZYGY30.BIN has
            // first granule 11 at offset 13 and 11 bytes at 14-15, and the granule chain then gives the size that its
            // own BIN load records add up to (28939). Reading these one byte late made it start at granule 0 and
            // return the data of SYZYGY10.BIN.
            byte fileType = data[offset + 11];
            byte asciiFlag = data[offset + 12];
            byte firstGranule = data[offset + 13];
            ushort lastSectorBytes = (ushort)((data[offset + 14] << 8) | data[offset + 15]);

            // Calculate file size by following granule chain
            int fileSize = CalculateFileSize(firstGranule, lastSectorBytes);

            return new DecbEntry
            {
                Filename = filename,
                Extension = extension,
                FileType = fileType,
                AsciiFlag = asciiFlag,
                FirstGranule = firstGranule,
                LastSectorBytes = lastSectorBytes,
                FileSize = fileSize,
                IsDeleted = false,
                IsEndOfDirectory = false
            };
        }

        private DecbEntry? FindFileInDirectory(string filename, string extension)
        {
            for (int sector = DIR_START_SECTOR; sector <= DIR_END_SECTOR; sector++)
            {
                uint lsn = CalculateLSN(FAT_TRACK, sector);
                byte[] sectorData = new byte[SECTOR_SIZE];
                _device!.ReadBlock(BlockAddress.FromLBA(lsn), sectorData);

                for (int entryNum = 0; entryNum < ENTRIES_PER_SECTOR; entryNum++)
                {
                    int offset = entryNum * DIR_ENTRY_SIZE;
                    DecbEntry? entry = ParseDirectoryEntry(sectorData, offset);

                    if (entry != null && entry.IsEndOfDirectory)
                    {
                        return null;
                    }

                    if (entry != null && !entry.IsDeleted)
                    {
                        if (string.Equals(entry.Filename, filename, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(entry.Extension, extension, StringComparison.OrdinalIgnoreCase))
                        {
                            return entry;
                        }
                    }
                }
            }

            return null;
        }

        private int CalculateFileSize(byte firstGranule, ushort lastSectorBytes)
        {
            if (firstGranule >= _granuleCount)
            {
                return 0;
            }

            int totalSectors = 0;
            byte currentGranule = firstGranule;
            int granuleCount = 0;

            while (currentGranule < _granuleCount && granuleCount < _granuleCount)
            {
                byte fatEntry = _fat![currentGranule];

                if (fatEntry >= 0xC0) // Last granule (top 2 bits set)
                {
                    int sectorsInLastGranule = fatEntry & 0x1F; // Bottom 5 bits
                    totalSectors += sectorsInLastGranule;
                    break;
                }
                else if (fatEntry < _granuleCount)
                {
                    totalSectors += SECTORS_PER_GRANULE;
                    currentGranule = fatEntry;
                    granuleCount++;
                }
                else
                {
                    break;
                }
            }

            if (totalSectors == 0)
            {
                return 0;
            }

            // The byte count of the last sector is taken as it stands. A full last sector is stored as 256 (syzygy.dsk
            // SYZYGY10.BIN holds 0100) and an empty file is one granule with one sector and a count of 0, which is how
            // Toolshed (libdecb gs_size: 2304 per full granule + 256 per full sector + the count) and DECB size it.
            int fileSize = ((totalSectors - 1) * SECTOR_SIZE) + Math.Min((int)lastSectorBytes, SECTOR_SIZE);
            return fileSize;
        }

        private byte[] ReadFileData(DecbEntry entry)
        {
            List<byte> fileData = new List<byte>(entry.FileSize);
            byte currentGranule = entry.FirstGranule;
            int granuleCount = 0;
            bool isLastGranule = false;
            int sectorsInLastGranule = 0;

            while (currentGranule < _granuleCount && granuleCount < _granuleCount)
            {
                byte fatEntry = _fat![currentGranule];

                if (fatEntry >= 0xC0) // Last granule
                {
                    isLastGranule = true;
                    sectorsInLastGranule = fatEntry & 0x1F;
                }

                // Calculate starting LSN for this granule
                // Granule 0-67 map to tracks (excluding track 17); on 40 and 80 track disks granules 68 and up go on the same way
                // (Toolshed _decb_seekgranule: granule * 2304, plus two granules for granules above 33).
                int trackOffset = currentGranule / 2;
                if (trackOffset >= FAT_TRACK) trackOffset++; // Skip track 17

                int granuleInTrack = currentGranule % 2;
                int startSector = (granuleInTrack * SECTORS_PER_GRANULE) + 1;

                int sectorsToRead = isLastGranule ? sectorsInLastGranule : SECTORS_PER_GRANULE;

                for (int s = 0; s < sectorsToRead; s++)
                {
                    uint lsn = CalculateLSN(trackOffset, startSector + s);
                    byte[] sectorData = new byte[SECTOR_SIZE];
                    _device!.ReadBlock(BlockAddress.FromLBA(lsn), sectorData);

                    bool isLastSector = isLastGranule && (s == sectorsToRead - 1);
                    if (isLastSector)
                    {
                        int usedBytes = Math.Min((int)entry.LastSectorBytes, SECTOR_SIZE);
                        for (int j = 0; j < usedBytes; j++)
                        {
                            fileData.Add(sectorData[j]);
                        }
                    }
                    else
                    {
                        for (int j = 0; j < SECTOR_SIZE; j++)
                        {
                            fileData.Add(sectorData[j]);
                        }
                    }
                }

                if (isLastGranule)
                {
                    break;
                }

                currentGranule = fatEntry;
                granuleCount++;
            }

            return fileData.ToArray();
        }

        private uint CalculateLSN(int track, int sector)
        {
            return (uint)((track * _sectorsPerTrack) + (sector - 1));
        }

        private string GetFileTypeDescription(byte fileType)
        {
            return fileType switch
            {
                0 => "BASIC Program",
                1 => "BASIC Data",
                2 => "Machine Language",
                3 => "Text",
                _ => $"Type {fileType}"
            };
        }

        // ---- write support ----
        //
        // Rules used, all checked against real disks written by Toolshed 2.2 (decb dskini, copy, kill, rename, attr)
        // and against the Toolshed libdecb source (write, open/create, kill, rename, gs_size):
        //  - FAT: sector 2 of track 17, one byte per granule (68, 78 or 156 for 35, 40 or 80 tracks). 0xFF is free. A value
        //    below 0xC0 is the next granule of the chain. 0xC0 plus N (N = 1 to 9) ends the chain and says how many sectors
        //    of the last granule are used. The FAT bytes after the last granule are never written (dskini leaves them 0).
        //  - Directory: sectors 3 to 11 of track 17, 8 entries of 32 bytes, 72 in all. First byte 0x00 is a deleted
        //    entry (the rest stays), 0xFF is the end of the directory.
        //  - Entry: name 0-7, extension 8-10 (space padded), type 11, ASCII flag 12, first granule 13, bytes used in the
        //    last sector 14-15 (big-endian, 256 for a full sector, 0 for an empty file), 16 zero bytes.
        //  - An empty file still owns one granule (FAT value 0xC1, count 0).
        //  - Free granule search is the Toolshed port of what the DECB ROM does (find_free_granule): first granule of a
        //    file is searched from granule 34, each next granule from the one before it.

        private void EnsureWritable()
        {
            if (_device!.IsReadOnly)
            {
                throw new IOException("The volume is mounted read-only");
            }
        }

        private static bool IsNameChar(char c)
        {
            return c > ' ' && c < 0x7F && c != '.' && c != '/' && c != ',' && c != ':';
        }

        private static void SplitName(string path, out string name, out string extension)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (path.StartsWith("/"))
            {
                path = path.Substring(1);
            }

            int dot = path.IndexOf('.');
            if (dot >= 0)
            {
                name = path.Substring(0, dot);
                extension = path.Substring(dot + 1);
            }
            else
            {
                name = path;
                extension = string.Empty;
            }

            name = name.ToUpperInvariant();
            extension = extension.ToUpperInvariant();

            if (name.Length < 1 || name.Length > 8 || extension.Length > 3)
            {
                throw new ArgumentException($"Not a DECB name (1 to 8 characters, extension up to 3): {path}");
            }

            for (int i = 0; i < name.Length; i++)
            {
                if (!IsNameChar(name[i]))
                {
                    throw new ArgumentException($"Character not allowed in a DECB name: {path}");
                }
            }

            for (int i = 0; i < extension.Length; i++)
            {
                if (!IsNameChar(extension[i]))
                {
                    throw new ArgumentException($"Character not allowed in a DECB extension: {path}");
                }
            }
        }

        private static void PutName(byte[] entry, string name, string extension)
        {
            for (int i = 0; i < 8; i++)
            {
                entry[i] = i < name.Length ? (byte)name[i] : (byte)' ';
            }

            for (int i = 0; i < 3; i++)
            {
                entry[8 + i] = i < extension.Length ? (byte)extension[i] : (byte)' ';
            }
        }

        private byte[] ReadDirEntry(int index)
        {
            byte[] sector = new byte[SECTOR_SIZE];
            _device!.ReadBlock(BlockAddress.FromLBA(CalculateLSN(FAT_TRACK, DIR_START_SECTOR + (index / ENTRIES_PER_SECTOR))), sector);
            byte[] entry = new byte[DIR_ENTRY_SIZE];
            Array.Copy(sector, (index % ENTRIES_PER_SECTOR) * DIR_ENTRY_SIZE, entry, 0, DIR_ENTRY_SIZE);
            return entry;
        }

        private void WriteDirEntry(int index, byte[] entry)
        {
            uint lsn = CalculateLSN(FAT_TRACK, DIR_START_SECTOR + (index / ENTRIES_PER_SECTOR));
            byte[] sector = new byte[SECTOR_SIZE];
            _device!.ReadBlock(BlockAddress.FromLBA(lsn), sector);
            Array.Copy(entry, 0, sector, (index % ENTRIES_PER_SECTOR) * DIR_ENTRY_SIZE, DIR_ENTRY_SIZE);
            _device.WriteBlock(BlockAddress.FromLBA(lsn), sector);
        }

        // Looks for a name in the directory. found is the entry index or -1. firstEmpty is the first entry that is
        // deleted (0x00) or the end marker (0xFF), or -1 when all 72 are in use. The scan stops at the end marker.
        private void ScanDirectory(string name, string extension, out int found, out int firstEmpty)
        {
            found = -1;
            firstEmpty = -1;
            byte[] sector = new byte[SECTOR_SIZE];

            for (int s = 0; s <= DIR_END_SECTOR - DIR_START_SECTOR; s++)
            {
                _device!.ReadBlock(BlockAddress.FromLBA(CalculateLSN(FAT_TRACK, DIR_START_SECTOR + s)), sector);
                for (int e = 0; e < ENTRIES_PER_SECTOR; e++)
                {
                    int offset = e * DIR_ENTRY_SIZE;
                    int index = (s * ENTRIES_PER_SECTOR) + e;
                    byte first = sector[offset];

                    if (first == 0xFF)
                    {
                        if (firstEmpty < 0)
                        {
                            firstEmpty = index;
                        }

                        return;
                    }

                    if (first == 0x00)
                    {
                        if (firstEmpty < 0)
                        {
                            firstEmpty = index;
                        }

                        continue;
                    }

                    string entryName = Encoding.ASCII.GetString(sector, offset, 8).Trim();
                    string entryExtension = Encoding.ASCII.GetString(sector, offset + 8, 3).Trim();
                    if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(entryExtension, extension, StringComparison.OrdinalIgnoreCase))
                    {
                        found = index;
                        return;
                    }
                }
            }
        }

        // Sets every granule of the chain that starts at firstGranule to free. The walk is bounded so a damaged
        // (looping) chain cannot run for ever.
        private static void FreeChain(byte[] fat, byte firstGranule, int granuleCount)
        {
            int granule = firstGranule;
            int steps = 0;
            while (granule < granuleCount && steps <= granuleCount)
            {
                byte next = fat[granule];
                fat[granule] = 0xFF;
                if (next >= 0xC0 || next == 0xFF)
                {
                    break;
                }

                granule = next;
                steps++;
            }
        }

        // Port of Toolshed find_free_granule, which says it matches the DECB ROM: start at the first granule of the
        // track of nextTo, then alternate outward over the tracks. Returns -1 when no granule is free.
        private static int FindFreeGranule(byte[] fat, int nextTo, int granuleCount)
        {
            int counter = 0;
            int direction = 0;
            int granule = nextTo & ~1;

            while (counter < granuleCount)
            {
                if (granule >= 0 && granule < granuleCount && fat[granule] == 0xFF)
                {
                    return granule;
                }

                counter++;
                granule++;

                if ((granule & 1) != 0)
                {
                    continue;
                }

                int save = granule;
                granule -= 2;
                direction = ~direction;

                if (direction != 0)
                {
                    granule -= counter;
                    if (granule < 0)
                    {
                        granule = save;
                        direction = ~direction;
                    }
                }
                else
                {
                    granule += counter;
                    if (granule >= granuleCount)
                    {
                        // The upward step ran past the last granule, so there are no tracks left above: Toolshed's comment is "If
                        // overflow, try previous granule (minus 4), in other direction". At this point save is the first granule
                        // AFTER the lower track that was just checked, so save - 2 is that same track again and save - 4 is the
                        // next track down. Minus 4 is therefore what lets the search go on downward one new track per step once the
                        // top is used up. With minus 2 it would land on the track it has just checked, take the same branch again
                        // next time, and check that one track over and over until the counter runs out, so a free granule further
                        // down would never be found and the disk would be called full. This happens when the search starts near the
                        // top of the disk: DecbWriteTests WriteFile_SearchPastTheTop_GoesOnDownward shows it at 68 and 78 granules
                        // (first granule 66, the only other free granule far below). At 156 granules a search through WriteFile
                        // cannot get there: the first granule of a file is the first free one in the order from granule 34, which
                        // sweeps 0 to 67 before it climbs 68 to 155, so a search from a granule above 67 only has free granules
                        // above it, and a search from 67 or below meets granule 0 before the top.
                        granule = save;
                        granule -= 4;
                        direction = ~direction;
                    }
                }
            }

            return -1;
        }

        private void CommitFat(byte[] fat)
        {
            uint lsn = CalculateLSN(FAT_TRACK, FAT_SECTOR);
            _device!.WriteBlock(BlockAddress.FromLBA(lsn), fat);
            _fat = fat;
        }

        private static void DefaultTypeFor(string extension, out byte fileType, out byte asciiFlag)
        {
            fileType = 2;
            asciiFlag = 0x00;
            if (extension == "BAS")
            {
                fileType = 0;
            }
            else if (extension == "DAT")
            {
                fileType = 1;
                asciiFlag = 0xFF;
            }
            else if (extension == "TXT")
            {
                fileType = 3;
                asciiFlag = 0xFF;
            }
        }

        private void WriteFileCore(string name, string extension, byte[] data, byte fileType, byte asciiFlag, bool typeGiven)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            EnsureWritable();
            ScanDirectory(name, extension, out int found, out int firstEmpty);
            byte[] fat = (byte[])_fat!.Clone();

            int slot = firstEmpty;
            if (found >= 0)
            {
                byte[] old = ReadDirEntry(found);
                if (!typeGiven)
                {
                    fileType = old[11];
                    asciiFlag = old[12];
                }

                // Toolshed overwrites by killing the old file and creating a new one, so its granules are free again
                // for the new data and the first empty entry is used (which can be the old one).
                FreeChain(fat, old[13], _granuleCount);
                if (slot < 0 || found < slot)
                {
                    slot = found;
                }
            }
            else if (!typeGiven)
            {
                DefaultTypeFor(extension, out fileType, out asciiFlag);
            }

            if (slot < 0)
            {
                throw new IOException("The DECB directory is full (72 entries)");
            }

            int granulesNeeded = Math.Max(1, (data.Length + GRANULE_SIZE - 1) / GRANULE_SIZE);
            int[] chain = new int[granulesNeeded];
            int previous = 34;
            for (int i = 0; i < granulesNeeded; i++)
            {
                int granule = FindFreeGranule(fat, previous, _granuleCount);
                if (granule < 0)
                {
                    throw new IOException("The disk is full: not enough free granules");
                }

                fat[granule] = 0xC1; // taken for now; the real value is set below
                chain[i] = granule;
                previous = granule;
            }

            // Sectors used in the last granule and bytes used in the last sector.
            int remaining = data.Length - ((granulesNeeded - 1) * GRANULE_SIZE);
            int sectorsInLast = Math.Max(1, (remaining + SECTOR_SIZE - 1) / SECTOR_SIZE);
            int lastSectorBytes = data.Length == 0 ? 0 : (remaining % SECTOR_SIZE == 0 ? SECTOR_SIZE : remaining % SECTOR_SIZE);

            for (int i = 0; i < granulesNeeded - 1; i++)
            {
                fat[chain[i]] = (byte)chain[i + 1];
            }

            fat[chain[granulesNeeded - 1]] = (byte)(0xC0 + sectorsInLast);

            // Data first, then FAT, then the directory entry, so a failure leaves the old directory entry valid.
            byte[] buffer = new byte[SECTOR_SIZE];
            int position = 0;
            for (int i = 0; i < granulesNeeded; i++)
            {
                int track = chain[i] / 2;
                if (track >= FAT_TRACK)
                {
                    track++;
                }

                int startSector = ((chain[i] % 2) * SECTORS_PER_GRANULE) + 1;
                int sectors = i == granulesNeeded - 1 ? sectorsInLast : SECTORS_PER_GRANULE;
                for (int s = 0; s < sectors; s++)
                {
                    Array.Clear(buffer, 0, buffer.Length);
                    int count = Math.Min(SECTOR_SIZE, data.Length - position);
                    if (count > 0)
                    {
                        Array.Copy(data, position, buffer, 0, count);
                        position += count;
                    }

                    _device!.WriteBlock(BlockAddress.FromLBA(CalculateLSN(track, startSector + s)), buffer);
                }
            }

            CommitFat(fat);

            byte[] entry = new byte[DIR_ENTRY_SIZE];
            PutName(entry, name, extension);
            entry[11] = fileType;
            entry[12] = asciiFlag;
            entry[13] = (byte)chain[0];
            entry[14] = (byte)(lastSectorBytes >> 8);
            entry[15] = (byte)(lastSectorBytes & 0xFF);
            WriteDirEntry(slot, entry);

            // The new entry went into an earlier empty slot: the old entry of the same name is deleted as Toolshed kill does.
            if (found >= 0 && found != slot)
            {
                byte[] oldEntry = ReadDirEntry(found);
                oldEntry[0] = 0x00;
                WriteDirEntry(found, oldEntry);
            }

            _device!.Flush();
        }

        private void EnsureInitialized()
        {
            if (_device == null || _fat == null)
            {
                throw new InvalidOperationException("Filesystem not initialized");
            }
        }

        // ===================================================================
        // HELPER CLASSES
        // ===================================================================

        private sealed class DecbEntry
        {
            public string Filename { get; set; } = string.Empty;
            public string Extension { get; set; } = string.Empty;
            public byte FileType { get; set; }
            public byte AsciiFlag { get; set; }
            public byte FirstGranule { get; set; }
            public ushort LastSectorBytes { get; set; }
            public int FileSize { get; set; }
            public bool IsDeleted { get; set; }
            public bool IsEndOfDirectory { get; set; }
        }
    }
}
