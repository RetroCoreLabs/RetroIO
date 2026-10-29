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
using RetroIO.Storage.Local.Streams;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Driver for Dragon 32/64 DSK/JVC disk images with DragonDOS filesystem.
    /// Supports optional 0-255 byte header with geometry detection.
    /// Thin wrapper that composes GenericBlockDevice + an OS-9, DECB or DragonDOS file system.
    /// </summary>
    /// <remarks>
    /// <para><b>The DSK and JVC format</b></para>
    /// A DSK or JVC image is the raw sectors of the disk, optionally behind a short header that gives the geometry. The format is the one used
    /// by the Color Computer emulators and read by XRoar; the layout below is what the common emulators do, checked here on the headerless images
    /// WALKABOU.DSK, syzygy.dsk and NITR09.DSK. No real image with a JVC header is in the test folder, so the header fields are as documented by XRoar and have no
    /// real-image proof here.
    /// <para><b>Header</b></para>
    /// The header is as long as the remainder of the file size divided by 128. The original JVC description says 256; XRoar uses 128 so that
    /// 128-byte sectors and sector attribute bytes do not confuse it, and so does this driver (a header of 128 bytes or more cannot be told
    /// from sector data, and no real file has one). This driver accepts 256-byte sectors only. A header holds up to five bytes, and a missing byte takes its default:
    ///  - Byte 0: sectors per track. Default 18.
    ///  - Byte 1: sides. Default 1.
    ///  - Byte 2: sector size code, the size is 128 shifted left by the code. Default 1, which is 256 bytes.
    ///  - Byte 3: the number of the first sector on a track. Default 1.
    ///  - Byte 4: the sector attribute flag, which adds a byte in front of every sector. Default 0. This driver does not support it and refuses
    ///    an image that sets it (NotSupportedException).
    /// <para><b>Sectors</b></para>
    /// The sectors follow in the order cylinder, then side, then sector. The number of tracks is the data size divided by sectors per track,
    /// sides and sector size. XRoar adds one more track when the rest holds at least one sector ("Some images are distributed with partial last
    /// tracks") and takes the missing sectors as zero; this driver does the same and shows the missing sectors as zero bytes. A rest of less than
    /// one sector is refused. A single sided image of 88 tracks or more is taken as double sided (XRoar: too many tracks is implausible, so it is
    /// a 720 KB disk).
    /// <para><b>Images without a header</b></para>
    /// A headerless image is taken as 18 sectors per track and one side, which cannot tell a 40 track two sided disk from an 80 track one sided disk of
    /// the same size. For an OS-9 disk the identification sector settles it: track size in sectors (byte 3), sectors per track (bytes 17 and 18)
    /// and bit 0 of the disk format byte (byte 16, set for two sides), and the declared total sectors (bytes 0 to 2) may not exceed the file.
    /// <para><b>OS-9 hard disk volumes</b></para>
    /// A headerless image can also hold an OS-9 volume that is no floppy: the CoCo SDC images (18432 sectors), DriveWire images (524268
    /// sectors, from "os9 format -t29126") and other hard disk volumes. When sector 0 is a consistent OS-9 identification sector and its volume
    /// does not fit the 35, 40 or 80 track layout, the image is mounted as DD.TOT sectors in a row with the OS-9 file system. The file may hold
    /// fewer sectors than DD.TOT, because Toolshed writes the rest of a new volume only when asked to (its -e option); the sectors it leaves
    /// out read as zero, as they would after "os9 format -e". Such a short image can be written: only the sectors that change are written
    /// back, so the file grows only as far as the last sector written, as it does when Toolshed writes to it.
    /// <para><b>What this driver does with it</b></para>
    /// It mounts the sectors and picks the file system from the contents of the disk: OS-9 first (strict signature in sector 0), then DECB for a
    /// 35 track disk, then DragonDOS; a one sided 40 or 80 track disk whose DragonDOS BAM does not check out is taken as DECB when its FAT fits. The VDK format is described in <see cref="DragonVdkDriver"/>, and the DMK format in <see cref="DmkDiskImage"/>.
    /// <para><b>Writing</b></para>
    /// The mounted volume (DragonDOS, DECB or OS-9) can be written (create, overwrite, delete, rename) through its file system; the changes go back to the image stream after each operation. The
    /// header is kept as it is.
    /// </remarks>
    public sealed class DragonDskDriver : IFileSystemDriver
    {
        private GenericBlockDevice? _blockDevice;
        private IBlockFileSystem? _filesystem;
        private BlockDeviceDescriptor? _descriptor;

        /// <summary>
        /// Gets the file system type of the mounted file system (DragonDOS, OS-9 or DECB),
        /// or <c>DragonDOS</c> when nothing is mounted.
        /// </summary>
        public string FileSystemType => _filesystem?.FileSystemType ?? "DragonDOS";
        /// <summary>
        /// Gets the volume label of the mounted file system, or an empty string when nothing is mounted.
        /// </summary>
        public string VolumeLabel => _filesystem?.VolumeLabel ?? string.Empty;
        /// <summary>
        /// Gets the separator between file name and extension of the mounted file system, or <c>.</c> when nothing is mounted.
        /// </summary>
        public string ExtensionSeparator => _filesystem?.ExtensionSeparator ?? ".";

        /// <summary>
        /// Gets the capability flags of this driver: read, directory listing, file types, volume label and block read, and write when <see cref="CanWrite"/> is true.
        /// </summary>
        public DriverCapabilities Capabilities
        {
            get
            {
                DriverCapabilities caps = DriverCapabilities.Read |
                                          DriverCapabilities.ListDirectory |
                                          DriverCapabilities.FileTypes |
                                          DriverCapabilities.VolumeLabel |
                                          DriverCapabilities.BlockRead;

                if (CanWrite)
                {
                    caps |= DriverCapabilities.Write;
                }

                return caps;
            }
        }

        /// <summary>
        /// Gets <c>true</c>, because this driver reads files.
        /// </summary>
        public bool CanRead => true;
        /// <summary>
        /// Gets <c>true</c> when an image is mounted read-write and holds a volume that can be written: DragonDOS (with a valid BAM), DECB or OS-9.
        /// </summary>
        public bool CanWrite => _blockDevice != null && !_blockDevice.IsReadOnly && _filesystem != null && (_filesystem is not DragonDosFileSystem dragonDos || dragonDos.IsDragonDosVolume);
        /// <summary>
        /// Gets <c>true</c>, because this driver can read raw blocks of the image.
        /// </summary>
        public bool HasBlockAccess => true;

        /// <summary>
        /// Returns null, because this driver has no metadata descriptor.
        /// </summary>
        /// <returns>
        /// Always null.
        /// </returns>
        public IFileSystemMetadataDescriptor? GetMetadataDescriptor()
        {
            return null;
        }

        /// <summary>
        /// Reads the optional JVC header, checks the geometry, mounts the data part of the image as a block device and initializes the file system.
        /// </summary>
        /// <remarks>
        /// The header size is the file size modulo 128. DragonDOS is tried first. For a 35-track disk the DECB file system is tried before the fall back to DragonDOS; for a 40 or 80 track disk DECB is tried after DragonDOS.
        /// </remarks>
        /// <param name="imageStream">
        /// The stream holding the disk image.
        /// </param>
        /// <param name="readOnly">
        /// True to mount read-only.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="imageStream"/> is null.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// Thrown when the header cannot be read or the sector size, first sector number, track count or data size do not fit a DragonDOS disk.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// Thrown when the track and side count are not supported, or the header sets the sector attribute flag.
        /// </exception>
        public void Mount(Stream imageStream, bool readOnly)
        {
            if (imageStream == null)
            {
                throw new ArgumentNullException(nameof(imageStream));
            }

            long fileSize = imageStream.Length;

            // Calculate header size: fileSize % 128. The JVC description says % 256, XRoar uses % 128 so that 128-byte sectors
            // and sector attribute bytes do not hide a header (see the remarks of this class).
            int headerSize = (int)(fileSize % 128);

            // Read header bytes if present
            byte sectorsPerTrack = 18;  // Default
            byte sides = 1;              // Default
            byte sectorSizeCode = 1;     // Default (256 bytes)
            byte firstSectorId = 1;      // Default
            byte attributeFlag = 0;      // Default

            if (headerSize > 0)
            {
                byte[] header = new byte[headerSize];
                imageStream.Position = 0;
                int bytesRead = imageStream.Read(header, 0, headerSize);
                if (bytesRead < headerSize)
                {
                    throw new InvalidDataException("Unable to read DSK/JVC header");
                }

                // Parse header bytes (missing bytes use defaults)
                if (headerSize >= 1) sectorsPerTrack = header[0];
                if (headerSize >= 2) sides = header[1];
                if (headerSize >= 3) sectorSizeCode = header[2];
                if (headerSize >= 4) firstSectorId = header[3];
                if (headerSize >= 5) attributeFlag = header[4];
            }
            else if (TryReadOs9Geometry(imageStream, fileSize, out byte os9SectorsPerTrack, out byte os9Sides))
            {
                // A headerless image that holds an OS-9 volume: the identification sector says how the disk is laid out.
                sectorsPerTrack = os9SectorsPerTrack;
                sides = os9Sides;
            }

            // A set sector attribute flag puts one byte in front of every sector; that layout is not read.
            if (attributeFlag != 0)
            {
                throw new NotSupportedException(
                    $"JVC sector attribute bytes (header byte 4 = {attributeFlag}) are not supported");
            }

            // Calculate sector size from code: 128 << code
            int sectorSize = 128 << sectorSizeCode;

            // Validate sector size (must be 256 for DragonDOS)
            if (sectorSize != 256)
            {
                throw new InvalidDataException(
                    $"Unsupported sector size: {sectorSize} bytes. DragonDOS requires 256-byte sectors.");
            }

            // Validate first sector ID (must be 1 for DragonDOS)
            if (firstSectorId != 1)
            {
                throw new InvalidDataException(
                    $"Unsupported first sector ID: {firstSectorId}. DragonDOS requires sector numbering starting at 1.");
            }

            if (sectorsPerTrack == 0 || sides == 0)
            {
                throw new InvalidDataException(
                    $"Bad DSK/JVC header: {sectorsPerTrack} sectors per track, {sides} side(s)");
            }

            // Calculate track count
            long dataSize = fileSize - headerSize;
            long bytesPerTrack = sectorsPerTrack * sectorSize;
            long bytesPerCylinder = bytesPerTrack * sides;
            int tracks = (int)(dataSize / bytesPerCylinder);

            // XRoar: a single sided image with this many tracks is implausible, it is a 720 KB double sided disk.
            if (tracks >= 88 && sides == 1)
            {
                sides = 2;
                bytesPerCylinder = bytesPerTrack * sides;
                tracks = (int)(dataSize / bytesPerCylinder);
            }

            // XRoar: when the rest holds at least one sector, the file has one more (partial) last track.
            bool partialLastTrack = false;
            long rest = dataSize % bytesPerCylinder;
            if (rest >= sectorSize)
            {
                tracks++;
                partialLastTrack = true;
            }

            // A headerless image whose sector 0 is an OS-9 identification sector, but whose volume is no floppy this driver lays out (another
            // track count, or more sectors than the floppy holds), is an OS-9 hard disk volume: CoCo SDC and DriveWire images, and any volume
            // made with "os9 format -t<tracks>". Sector 0 gives its size, so it is mounted as that many sectors in a row.
            if (headerSize == 0 && TryReadOs9Volume(ReadSectorZero(imageStream, fileSize), fileSize, out long os9Sectors))
            {
                bool floppyLayout = tracks == 35 || tracks == 40 || tracks == 80;
                if (!floppyLayout || os9Sectors > (long)tracks * sides * sectorsPerTrack)
                {
                    MountOs9Volume(imageStream, fileSize, os9Sectors, readOnly);
                    return;
                }
            }

            // Validate track count
            if (tracks != 35 && tracks != 40 && tracks != 80)
            {
                throw new InvalidDataException(
                    $"Invalid track count: {tracks}. Expected 35 (CoCo), 40, or 80 tracks.");
            }

            // Detect descriptor based on geometry
            _descriptor = DetectDescriptor(tracks, sides);

            // Validate data size matches expected geometry. A partial last track is the only accepted shortage.
            long expectedSize = tracks * sides * sectorsPerTrack * sectorSize;
            if (dataSize != expectedSize && !partialLastTrack)
            {
                throw new InvalidDataException(
                    $"DSK/JVC data size mismatch: expected {expectedSize} bytes ({tracks}T × {sides}S × {sectorsPerTrack}SPT × {sectorSize}), " +
                    $"got {dataSize} bytes after {headerSize}-byte header");
            }

            // Create a stream to expose only the data portion (skipping header). A partial last track is padded with zero bytes behind
            // the whole sectors that are there. The first write makes the file the whole disk, as XRoar's save does (it writes every
            // cylinder, head and sector, src/vdisk.c vdisk_save_jvc).
            Stream dataStream;
            if (partialLastTrack)
            {
                dataStream = new PaddedSubStream(imageStream, headerSize, (dataSize / sectorSize) * sectorSize, expectedSize, true);
            }
            else
            {
                dataStream = new SubStream(imageStream, headerSize, dataSize);
            }

            // Mount block device with descriptor using data-only stream
            _blockDevice = new GenericBlockDevice(_descriptor);
            _blockDevice.Mount(dataStream, readOnly);

            // An OS-9 volume has a strict signature in sector 0, so it is tried first. Then DragonDOS, with DECB
            // tried before it for 35-track disks and after it for 40 and 80 track disks.
            Os9RbfFileSystem os9 = new Os9RbfFileSystem();
            if (os9.CanHandle(_blockDevice))
            {
                _filesystem = os9;
                _filesystem.Initialize(_blockDevice);
                return;
            }

            _filesystem = new DragonDosFileSystem();

            if (_blockDevice.GetBlockDeviceInfo()?.CylinderCount == 35)
            {
                // 35-track disk - could be DECB, try that first
                DecbFileSystem decbFs = new DecbFileSystem();
                if (decbFs.CanHandle(_blockDevice))
                {
                    _filesystem = decbFs;
                    _filesystem.Initialize(_blockDevice);
                }
                else
                {
                    // Fall back to DragonDOS
                    _filesystem.Initialize(_blockDevice);
                }
            }
            else
            {
                // Not 35-track: DragonDOS when its BAM checks out, else DECB when the disk is a 40 or 80 track one sided DECB disk
                // (Toolshed decb dskini -4 and -8 make those), else DragonDOS as before.
                _filesystem = PickDragonDosOrDecb(_blockDevice);
            }
        }

        /// <summary>
        /// Picks the file system of a disk that is not OS-9 and not 35 tracks: DragonDOS when its BAM geometry checks out, otherwise
        /// DECB when the FAT on track 17 holds only DECB values (first with the granule count of the track count, then with the 68
        /// granules of a 35 track disk), otherwise DragonDOS (which then reports that it is not a DragonDOS volume).
        /// </summary>
        /// <remarks>
        /// DragonDOS is asked first so that every disk that mounted as DragonDOS before still does. A DECB disk has its directory on track 17
        /// and keeps track 20 for data, so its track 20 has a DragonDOS BAM only if file data happens to hold the four geometry bytes and their
        /// complements in the right places.
        /// </remarks>
        /// <param name="device">
        /// The mounted block device.
        /// </param>
        /// <returns>
        /// The initialized file system.
        /// </returns>
        internal static IBlockFileSystem PickDragonDosOrDecb(IBlockDevice device)
        {
            DragonDosFileSystem dragonDos = new DragonDosFileSystem();
            if (!dragonDos.CanHandle(device))
            {
                DecbFileSystem decb = new DecbFileSystem();
                if (decb.CanHandle(device))
                {
                    decb.Initialize(device);
                    return decb;
                }

                // A DECB disk of 35 track layout in a larger image: 68 granules on track 17, nothing on the tracks past 34
                // (HTCHHKER.VDK and PLANETFALL.VDK are 40 track images of this kind).
                DecbFileSystem sixtyEight = DecbFileSystem.WithSixtyEightGranules();
                if (sixtyEight.CanHandle(device))
                {
                    sixtyEight.Initialize(device);
                    return sixtyEight;
                }
            }

            dragonDos.Initialize(device);
            return dragonDos;
        }

        /// <summary>
        /// Reads sector 0 of a headerless image and, when it is the identification sector of an OS-9 volume, gives the geometry it describes.
        /// </summary>
        /// <param name="imageStream">
        /// The image, positioned anywhere.
        /// </param>
        /// <param name="fileSize">
        /// The size of the image in bytes.
        /// </param>
        /// <param name="sectorsPerTrack">
        /// Receives the sectors per track (the track size in sectors at byte 3).
        /// </param>
        /// <param name="sides">
        /// Receives 2 when bit 0 of the disk format byte (byte 16) is set, otherwise 1.
        /// </param>
        /// <returns>
        /// True when the sector fits an OS-9 disk of exactly this image: the track size equals the sectors per track field, the declared
        /// size is not larger than the image, and the image is a whole number of tracks on this many sides. Otherwise false.
        /// </returns>
        /// <remarks>
        /// Field offsets are from the RBF identification sector layout: total sectors (3 bytes at 0), track size (1 at 3), disk format (1 at 16),
        /// sectors per track (2 at 17). Nothing is read from a header because a headerless image has none.
        /// </remarks>
        private static bool TryReadOs9Geometry(Stream imageStream, long fileSize, out byte sectorsPerTrack, out byte sides)
        {
            sectorsPerTrack = 0;
            sides = 0;

            if (fileSize < 256)
            {
                return false;
            }

            byte[] id = new byte[256];
            imageStream.Position = 0;
            int read = imageStream.Read(id, 0, id.Length);
            if (read < id.Length)
            {
                return false;
            }

            long total = (id[0] << 16) | (id[1] << 8) | id[2];
            int trackSize = id[3];
            int declaredSectorsPerTrack = (id[17] << 8) | id[18];
            int sidesFromFormat = (id[16] & 1) + 1;

            if (total == 0 || trackSize == 0 || trackSize != declaredSectorsPerTrack)
            {
                return false;
            }

            if (total * 256 > fileSize || fileSize % (trackSize * 256L * sidesFromFormat) != 0)
            {
                return false;
            }

            sectorsPerTrack = (byte)trackSize;
            sides = (byte)sidesFromFormat;
            return true;
        }

        // Sector 0 of the image, or an empty span when the file is shorter than one sector.
        private static byte[] ReadSectorZero(Stream imageStream, long fileSize)
        {
            if (fileSize < 256)
            {
                return Array.Empty<byte>();
            }

            byte[] id = new byte[256];
            imageStream.Position = 0;
            imageStream.ReadExactly(id, 0, id.Length);
            return id;
        }

        /// <summary>
        /// Tells whether a sector is the identification sector of an OS-9 volume that a headerless image holds, and gives the size of the volume.
        /// </summary>
        /// <param name="id">
        /// Sector 0 of the image.
        /// </param>
        /// <param name="fileSize">
        /// The size of the image file in bytes.
        /// </param>
        /// <param name="totalSectors">
        /// Receives DD.TOT, the sectors in the volume.
        /// </param>
        /// <returns>
        /// True when the fields agree with each other and with the file: the file is whole 256-byte sectors, DD.TOT is at least 3, the track
        /// size DD.TKS is not zero and equals the sectors per track DD.SPT, the allocation map (DD.MAP bytes, DD.BIT sectors per bit) covers
        /// DD.TOT, the root directory descriptor DD.DIR lies behind the map and inside the volume, and the file holds every sector up to and
        /// including DD.DIR. Otherwise false.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Field offsets are those of the RBF identification sector (NitrOS-9 defs/rbf.d): DD.TOT 0 (3 bytes), DD.TKS 3, DD.MAP 4 (2 bytes),
        /// DD.BIT 6 (2 bytes), DD.DIR 8 (3 bytes), DD.SPT 17 (2 bytes). The allocation map starts at sector 1.
        /// </para>
        /// <para>
        /// The file may hold fewer sectors than DD.TOT. Toolshed's "os9 format" writes the rest of the disk as zero sectors only with its -e
        /// option (os9/os9format.c, "Write Rest of disk as empty sectors"), and the NitrOS-9 build formats its DriveWire disks without it
        /// (rules.mak: OS9FORMAT_DW = os9 format -t29126 -ss -dd), so those files stop after the last sector that was written.
        /// </para>
        /// </remarks>
        public static bool TryReadOs9Volume(ReadOnlySpan<byte> id, long fileSize, out long totalSectors)
        {
            totalSectors = 0;
            if (id.Length < 256 || fileSize % 256 != 0)
            {
                return false;
            }

            long total = (id[0] << 16) | (id[1] << 8) | id[2];
            int trackSize = id[3];
            long mapBytes = (id[4] << 8) | id[5];
            long sectorsPerBit = (id[6] << 8) | id[7];
            long rootFd = (id[8] << 16) | (id[9] << 8) | id[10];
            int declaredSectorsPerTrack = (id[17] << 8) | id[18];

            if (total < 3 || trackSize == 0 || trackSize != declaredSectorsPerTrack)
            {
                return false;
            }

            if (mapBytes == 0 || sectorsPerBit == 0 || mapBytes * 8 * sectorsPerBit < total)
            {
                return false;
            }

            long mapSectors = (mapBytes + 255) / 256;
            if (rootFd < 1 + mapSectors || rootFd >= total || (rootFd + 1) * 256 > fileSize)
            {
                return false;
            }

            totalSectors = total;
            return true;
        }

        // Mounts a headerless OS-9 volume as DD.TOT sectors in a row (or the file's sectors, when the file holds more). Sectors the file does
        // not hold read as zero, as they would be on a disk formatted with "os9 format -e".
        private void MountOs9Volume(Stream imageStream, long fileSize, long os9Sectors, bool readOnly)
        {
            long fileSectors = fileSize / 256;
            long sectors = Math.Max(os9Sectors, fileSectors);

            // The block device works out byte offsets in a 32-bit int, so the volume must stay below 2 GB.
            if (sectors * 256 > int.MaxValue)
            {
                throw new NotSupportedException(
                    $"The OS-9 volume has {sectors} sectors ({sectors * 256} bytes), more than the block device can address");
            }

            _descriptor = new BlockDeviceDescriptor
            {
                Name = $"OS-9 volume {sectors} sectors",
                BlockSize = 256,
                AddressMode = BlockAddressMode.LBA,
                Cylinders = 1,
                Heads = 1,
                UniformSectorsPerTrack = 1,
                TotalBlocks = (uint)sectors,
                ExpectedImageSize = sectors * 256,
                SupportsVariableGeometry = false
            };

            // A file shorter than its volume is shown as the whole volume, with zero bytes behind the end of the file. The block device holds
            // only the sectors the file has and the ones written, and writes back only the sectors that changed, so a write behind the end of
            // the file grows it only as far as that sector, with zero sectors in any gap (the PaddedSubStream writes them). Toolshed "os9 copy"
            // grows such a file the same way: a 100000 byte file copied onto ArcadePack_dw.dsk took it from 256768 to 357120 bytes.
            bool shortFile = os9Sectors > fileSectors;
            Stream dataStream = shortFile
                ? new PaddedSubStream(imageStream, 0, fileSectors * 256, sectors * 256)
                : new SubStream(imageStream, 0, fileSize);
            _blockDevice = new GenericBlockDevice(_descriptor);
            _blockDevice.Mount(dataStream, readOnly);

            Os9RbfFileSystem os9 = new Os9RbfFileSystem();
            if (!os9.CanHandle(_blockDevice))
            {
                _blockDevice = null;
                throw new InvalidDataException(
                    $"Sector 0 describes an OS-9 volume of {os9Sectors} sectors, but its root directory is not a usable OS-9 directory");
            }

            os9.Initialize(_blockDevice);
            _filesystem = os9;
        }

        private BlockDeviceDescriptor DetectDescriptor(int tracks, byte sides)
        {
            // Determine descriptor based on geometry
            if (sides == 1)
            {
                // Single-sided
                if (tracks == 35)
                {
                    // CoCo standard (35 tracks, not common for Dragon)
                    return new BlockDeviceDescriptor
                    {
                        Name = "CoCo DSK/JVC 35T/SS (160KB)",
                        BlockSize = 256,
                        AddressMode = BlockAddressMode.LBA,
                        Cylinders = 35,
                        Heads = 1,
                        UniformSectorsPerTrack = 18,
                        TotalBlocks = 630,
                        ExpectedImageSize = 161280,
                        SupportsVariableGeometry = false
                    };
                }
                else if (tracks == 40)
                {
                    return BlockDeviceDescriptor.DragonVDK_SS40;
                }
                else if (tracks == 80)
                {
                    return BlockDeviceDescriptor.DragonVDK_SS80;
                }
            }
            else if (sides == 2)
            {
                // Double-sided
                if (tracks == 40)
                {
                    return BlockDeviceDescriptor.DragonVDK_DS40;
                }
                else if (tracks == 80)
                {
                    return BlockDeviceDescriptor.DragonVDK_DS80;
                }
            }

            throw new NotSupportedException(
                $"Unsupported disk geometry: {tracks} tracks, {sides} side(s)");
        }

        /// <summary>
        /// Lists the directory of the mounted file system.
        /// </summary>
        /// <param name="path">
        /// Directory path.
        /// </param>
        /// <returns>
        /// The directory entries.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public DirectoryEntry[] ListDirectory(string path)
        {
            if (_filesystem == null)
            {
                throw new InvalidOperationException("Filesystem not mounted");
            }

            return _filesystem.ListDirectory(path);
        }

        /// <summary>
        /// Reads a whole file from the mounted file system.
        /// </summary>
        /// <param name="path">
        /// Name of the file.
        /// </param>
        /// <returns>
        /// The file contents.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public byte[] ReadFile(string path)
        {
            if (_filesystem == null)
            {
                throw new InvalidOperationException("Filesystem not mounted");
            }

            return _filesystem.ReadFile(path);
        }

        /// <summary>
        /// Creates or replaces a file on the mounted volume and writes the changed sectors to the image.
        /// </summary>
        /// <param name="path">
        /// Name of the file.
        /// </param>
        /// <param name="data">
        /// The file contents.
        /// </param>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when nothing is mounted, the image is mounted read-only or the volume cannot be written.
        /// </exception>
        public void WriteFile(string path, byte[] data)
        {
            EnsureWritable();
            _filesystem!.WriteFile(path, data);
            _blockDevice!.Flush();
        }

        /// <summary>
        /// Deletes a file from the mounted volume and writes the changed sectors to the image.
        /// </summary>
        /// <param name="path">
        /// Name of the file.
        /// </param>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when nothing is mounted, the image is mounted read-only or the volume cannot be written.
        /// </exception>
        public void DeleteFile(string path)
        {
            EnsureWritable();
            _filesystem!.DeleteFile(path);
            _blockDevice!.Flush();
        }

        /// <summary>
        /// Renames a file on the mounted volume and writes the changed sectors to the image.
        /// </summary>
        /// <param name="oldPath">
        /// Current name of the file.
        /// </param>
        /// <param name="newPath">
        /// New name of the file.
        /// </param>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when nothing is mounted, the image is mounted read-only or the volume cannot be written.
        /// </exception>
        public void RenameFile(string oldPath, string newPath)
        {
            EnsureWritable();
            _filesystem!.Rename(oldPath, newPath);
            _blockDevice!.Flush();
        }

        private void EnsureWritable()
        {
            if (!CanWrite)
            {
                throw new UnauthorizedAccessException("The DSK/JVC volume is not mounted for writing, or cannot be written, so it is read-only");
            }
        }

        /// <summary>
        /// Gets metadata of a file in the mounted file system.
        /// </summary>
        /// <param name="path">
        /// Name of the file.
        /// </param>
        /// <returns>
        /// The metadata, or <c>null</c> when the file system does not find <paramref name="path"/>.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public FileMetadata? GetMetadata(string path)
        {
            if (_filesystem == null)
            {
                throw new InvalidOperationException("Filesystem not mounted");
            }

            return _filesystem.GetMetadata(path);
        }

        /// <summary>
        /// Returns the number of free blocks reported by the file system.
        /// </summary>
        /// <returns>
        /// The free block count.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the image is not mounted.
        /// </exception>
        public int GetBlocksFree()
        {
            if (_filesystem == null)
            {
                throw new InvalidOperationException("Filesystem not mounted");
            }

            return _filesystem.GetBlocksFree();
        }

        /// <summary>
        /// Returns the block device of the mounted image.
        /// </summary>
        /// <returns>
        /// The block device, or null when not mounted.
        /// </returns>
        public IBlockDevice? GetBlockDevice()
        {
            return _blockDevice;
        }

        /// <summary>
        /// Reads one 256-byte sector of the image data (after the header) from the block device.
        /// </summary>
        /// <param name="address">
        /// The block to read.
        /// </param>
        /// <param name="buffer">
        /// Receives the block data.
        /// </param>
        /// <returns>
        /// The number of bytes read.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            if (_blockDevice == null)
            {
                throw new InvalidOperationException("Filesystem not mounted");
            }

            return _blockDevice.ReadBlock(address, buffer);
        }

        /// <summary>
        /// Writes one sector of the image data and writes the image back.
        /// </summary>
        /// <param name="address">
        /// The block to write.
        /// </param>
        /// <param name="data">
        /// The 256 bytes of the sector.
        /// </param>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when nothing is mounted or the image is mounted read-only.
        /// </exception>
        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            if (_blockDevice == null || _blockDevice.IsReadOnly)
            {
                throw new UnauthorizedAccessException("The DSK/JVC image is not mounted for writing, so the volume is read-only");
            }

            _blockDevice.WriteBlock(address, data);
            _blockDevice.Flush();
        }

        /// <summary>
        /// Gets the information of the block device the image data is mounted on.
        /// </summary>
        /// <returns>
        /// The block device information; never <c>null</c> once mounted.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no image is mounted.
        /// </exception>
        public BlockDeviceInfo? GetBlockDeviceInfo()
        {
            if (_blockDevice == null)
            {
                throw new InvalidOperationException("Filesystem not mounted");
            }

            return _blockDevice.GetBlockDeviceInfo();
        }

        /// <summary>
        /// Drops the block device and the file system. The block device is not disposed.
        /// </summary>
        public void Dispose()
        {
            _blockDevice = null;
            _filesystem = null;
            _descriptor = null;
        }
    }
}
