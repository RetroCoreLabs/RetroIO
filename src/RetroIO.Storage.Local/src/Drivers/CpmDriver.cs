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

        private CpmFileSystem? _fileSystem;
        private Stream? _stream;
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
        /// Both formats have 1 KB blocks and 64 directory entries (two blocks), and every 512-byte
        /// sector is used: the data area is the 40 tracks (data format) or the 38 after the two
        /// system tracks (system format), so DSM is 179 or 170. The directory was found at the start
        /// of the first data track on real system-format disks.
        /// </remarks>
        /// <param name="recordCount">
        /// Number of 128-byte records in the data area.
        /// </param>
        /// <param name="firstSectorId">
        /// 41h (system format) or C1h (data format).
        /// </param>
        /// <returns>
        /// The parameters.
        /// </returns>
        public static CpmDiskParameters AmstradParameters(int recordCount, byte firstSectorId)
        {
            return new CpmDiskParameters(1024, recordCount / 8, 64, 0, firstSectorId == 0x41 ? 2 : 0);
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

            if (TryKaypro(image.Length, out CpmDiskParameters? kaypro, out ICpmRecordMap? kayproMap))
            {
                if (!CpmFileSystem.LooksLikeDirectory(CpmFileSystem.DirectoryBytes(image, kaypro!, kayproMap!), kaypro!, out _))
                {
                    throw new InvalidDataException("Not a Kaypro CP/M disk: track 1 does not hold a CP/M directory.");
                }

                _fileSystem = new CpmFileSystem(image, kaypro!, kayproMap!);
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
        /// Lets go of the image.
        /// </summary>
        public void Dispose()
        {
            _fileSystem = null;
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
            _stream!.Position = 0;
            _stream.Write(fs.Image, 0, fs.Image.Length);
            _stream.Flush();
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
