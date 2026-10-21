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
    /// <para><b>What this driver does with it</b></para>
    /// It mounts the sectors and picks the file system from the contents of the disk: OS-9 first (strict signature in sector 0), then DECB for a
    /// 35 track disk, then DragonDOS. The VDK format is described in <see cref="DragonVdkDriver"/>, and the DMK format in <see cref="DmkDiskImage"/>.
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
        /// Gets the file system type of the mounted file system (DragonDOS or, for some 35-track disks, DECB),
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
        /// The header size is the file size modulo 128. DragonDOS is tried first. For a 35-track disk the DECB file system is tried before the fall back to DragonDOS.
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
            // the whole sectors that are there.
            Stream dataStream;
            if (partialLastTrack)
            {
                dataStream = new PaddedSubStream(imageStream, headerSize, (dataSize / sectorSize) * sectorSize, expectedSize);
            }
            else
            {
                dataStream = new SubStream(imageStream, headerSize, dataSize);
            }

            // Mount block device with descriptor using data-only stream
            _blockDevice = new GenericBlockDevice(_descriptor);
            _blockDevice.Mount(dataStream, readOnly);

            // An OS-9 volume has a strict signature in sector 0, so it is tried first. Then DragonDOS, with DECB
            // tried before it for 35-track disks.
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
                // Not 35-track, use DragonDOS
                _filesystem.Initialize(_blockDevice);
            }
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
