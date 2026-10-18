//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using System.IO;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.FileSystems;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Storage.Ndfs.Drivers
{
    /// <summary>
    /// NDFS driver following the same pattern as CommodoreDiskDriver.
    /// Thin wrapper composing GenericBlockDevice + NdfsFileSystem.
    /// Implements IExtendedAttributeSupport for .xat (Extended Attribute) file support.
    /// </summary>
    public sealed class NdfsDriver : IFileSystemDriver, IExtendedAttributeSupport, IVolumeInfoProvider
    {
        private GenericBlockDevice? _blockDevice;
        private NdfsFileSystem? _filesystem;
        private BlockDeviceDescriptor? _descriptor;

        /// <summary>
        /// Gets the underlying NDFS filesystem for advanced operations.
        /// </summary>
        public INdfsFileSystem? GetFileSystem() => _filesystem;

        /// <summary>
        /// Gets the filesystem type identifier.
        /// </summary>
        /// <value>
        /// The type reported by the mounted filesystem, or <c>"NDFS"</c> when nothing is mounted.
        /// </value>
        public string FileSystemType => _filesystem?.FileSystemType ?? "NDFS";
        /// <summary>
        /// Gets the volume label of the mounted filesystem.
        /// </summary>
        /// <value>
        /// The label reported by the mounted filesystem, or an empty string when nothing is mounted.
        /// </value>
        public string VolumeLabel => _filesystem?.VolumeLabel ?? string.Empty;
        /// <summary>
        /// Gets the separator used between a file name and its type (extension).
        /// </summary>
        /// <value>
        /// The separator reported by the mounted filesystem, or <c>":"</c> when nothing is mounted.
        /// </value>
        public string ExtensionSeparator => _filesystem?.ExtensionSeparator ?? ":";

        /// <summary>
        /// Gets the capability flags of this driver, for runtime capability detection.
        /// </summary>
        /// <value>
        /// Read, ListDirectory, Subdirectories, FileTypes, BlockAllocationMap, VolumeLabel, BlockRead,
        /// Permissions and Quotas, plus Write, Delete and CreateDirectory when <see cref="CanWrite"/> is
        /// <c>true</c> (an image mounted read-write). Rename is not included.
        /// </value>
        public DriverCapabilities Capabilities
        {
            get
            {
                DriverCapabilities caps =
                    DriverCapabilities.Read |
                    DriverCapabilities.ListDirectory |
                    DriverCapabilities.Subdirectories |
                    DriverCapabilities.FileTypes |
                    DriverCapabilities.BlockAllocationMap |
                    DriverCapabilities.VolumeLabel |
                    DriverCapabilities.BlockRead |
                    DriverCapabilities.Permissions |    // NDFS-specific: user-based permissions
                    DriverCapabilities.Quotas;          // NDFS-specific: user quotas

                if (CanWrite)
                {
                    caps |= DriverCapabilities.Write |          // WriteFile: real implementation (files <=1MB, Indexed allocation)
                            DriverCapabilities.Delete |         // DeleteFile: real implementation
                            DriverCapabilities.CreateDirectory; // CreateDirectory: real implementation (root path = create User)
                }

                // NOTE: Rename is deliberately NOT included - NdfsDriver doesn't override RenameFile,
                // so it falls through to IFileSystemDriver's default NotSupportedException (user
                // rename is a known, documented gap, not implemented here).
                return caps;
            }
        }

        /// <summary>
        /// Gets whether this driver supports read operations.
        /// </summary>
        /// <value>
        /// Always <c>true</c>.
        /// </value>
        public bool CanRead => true;
        /// <summary>
        /// Gets whether this driver supports write operations.
        /// </summary>
        /// <value>
        /// <c>true</c> when an image is mounted read-write; <c>false</c> before mount and on a read-only mount.
        /// </value>
        public bool CanWrite => _blockDevice != null && !_blockDevice.IsReadOnly; // Write support: files <=1MB, uses Indexed allocation
        /// <summary>
        /// Gets whether this driver supports block-level access.
        /// </summary>
        /// <value>
        /// Always <c>true</c>.
        /// </value>
        public bool HasBlockAccess => true;

        /// <summary>
        /// Returns null, because this driver has no NDFS metadata descriptor yet.
        /// </summary>
        /// <returns>
        /// Always null.
        /// </returns>
        public IFileSystemMetadataDescriptor? GetMetadataDescriptor()
        {
            // TODO: Implement NDFS metadata descriptor (user, pages, type)
            return null;
        }

        /// <summary>
        /// Mounts an NDFS filesystem from the provided stream.
        /// </summary>
        /// <remarks>
        /// Builds an NDFS block device descriptor from the stream length, mounts a block device on the
        /// stream, and initializes an <see cref="NdfsFileSystem"/> on that block device. Errors from the
        /// block device or the filesystem pass through to the caller.
        /// </remarks>
        /// <param name="imageStream">
        /// The stream containing the filesystem image.
        /// </param>
        /// <param name="readOnly">
        /// <c>true</c> to mount the block device read-only.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="imageStream"/> is null.
        /// </exception>
        public void Mount(Stream imageStream, bool readOnly)
        {
            if (imageStream == null)
                throw new ArgumentNullException(nameof(imageStream));

            // 1. Create descriptor for NDFS
            _descriptor = NdfsBlockDeviceDescriptor.Create(imageStream.Length);

            // 2. Mount block device using existing GenericBlockDevice
            _blockDevice = new GenericBlockDevice(_descriptor);
            _blockDevice.Mount(imageStream, readOnly);

            // 3. Mount filesystem
            _filesystem = new NdfsFileSystem();
            _filesystem.Initialize(_blockDevice);
        }

        /// <summary>
        /// Reads an entire file from the mounted filesystem.
        /// </summary>
        /// <remarks>
        /// The call is passed to the mounted filesystem; its errors pass through to the caller.
        /// </remarks>
        /// <param name="path">
        /// The filesystem path of the file.
        /// </param>
        /// <returns>
        /// The file contents.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the driver is not mounted.
        /// </exception>
        public byte[] ReadFile(string path)
        {
            EnsureMounted();
            return _filesystem!.ReadFile(path);
        }

        /// <summary>
        /// Writes data to a file in the mounted filesystem.
        /// </summary>
        /// <remarks>
        /// The call is passed to the mounted filesystem; its errors pass through to the caller.
        /// </remarks>
        /// <param name="path">
        /// The filesystem path of the file.
        /// </param>
        /// <param name="data">
        /// The data to write.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the driver is not mounted.
        /// </exception>
        public void WriteFile(string path, byte[] data)
        {
            EnsureMounted();
            _filesystem!.WriteFile(path, data);
        }

        /// <summary>
        /// Deletes a file from the mounted filesystem.
        /// </summary>
        /// <remarks>
        /// The call is passed to the mounted filesystem; its errors pass through to the caller.
        /// </remarks>
        /// <param name="path">
        /// The filesystem path of the file.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the driver is not mounted.
        /// </exception>
        public void DeleteFile(string path)
        {
            EnsureMounted();
            _filesystem!.DeleteFile(path);
        }

        // NDFS has no nested directories: a root-level "directory" is a User. NdfsFileSystem
        // interprets a path like "/ALICE" as "create user ALICE" with a default quota.
        /// <summary>
        /// Creates a user. NDFS has no nested directories, so a root-level path such as <c>/ALICE</c> is passed to the file system, which creates a user with a default quota.
        /// </summary>
        /// <param name="path">
        /// The root-level path whose last part is the new user name.
        /// </param>
        public void CreateDirectory(string path)
        {
            EnsureMounted();
            _filesystem!.CreateDirectory(path);
        }

        /// <summary>
        /// Lists the contents of a directory in the mounted filesystem.
        /// </summary>
        /// <remarks>
        /// The call is passed to the mounted filesystem; its errors pass through to the caller.
        /// </remarks>
        /// <param name="path">
        /// The directory path.
        /// </param>
        /// <returns>
        /// The directory entries.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the driver is not mounted.
        /// </exception>
        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureMounted();
            return _filesystem!.ListDirectory(path);
        }

        /// <summary>
        /// Gets metadata about a file or directory in the mounted filesystem.
        /// </summary>
        /// <param name="path">
        /// The path of the file or directory.
        /// </param>
        /// <returns>
        /// The metadata returned by the mounted filesystem, or <c>null</c> when it returns none.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the driver is not mounted.
        /// </exception>
        public FileMetadata? GetMetadata(string path)
        {
            EnsureMounted();
            return _filesystem!.GetMetadata(path);
        }

        /// <summary>
        /// Reads a block of data at the specified address from the mounted block device.
        /// </summary>
        /// <remarks>
        /// The call is passed to the block device; its errors pass through to the caller.
        /// </remarks>
        /// <param name="address">
        /// The block address.
        /// </param>
        /// <param name="buffer">
        /// The buffer that receives the block data.
        /// </param>
        /// <returns>
        /// The number of bytes read, as returned by the block device.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the driver is not mounted.
        /// </exception>
        public int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            EnsureMounted();
            return _blockDevice!.ReadBlock(address, buffer);
        }

        /// <summary>
        /// Writes a block of data at the specified address on the mounted block device.
        /// </summary>
        /// <remarks>
        /// The call is passed to the block device; its errors pass through to the caller.
        /// </remarks>
        /// <param name="address">
        /// The block address.
        /// </param>
        /// <param name="data">
        /// The data to write.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the driver is not mounted.
        /// </exception>
        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            EnsureMounted();
            _blockDevice!.WriteBlock(address, data);
        }

        /// <summary>
        /// Gets information about the mounted block device.
        /// </summary>
        /// <returns>
        /// The information returned by the block device.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the driver is not mounted.
        /// </exception>
        public BlockDeviceInfo? GetBlockDeviceInfo()
        {
            EnsureMounted();
            return _blockDevice!.GetBlockDeviceInfo();
        }

        /// <summary>
        /// Gets the filesystem capabilities: extended attribute support and the property keys.
        /// </summary>
        /// <remarks>
        /// Does not need a mounted image; the answer is fixed.
        /// </remarks>
        /// <returns>
        /// A new capabilities object with extended attributes supported and the 23 <c>ndfs:</c>
        /// property keys.
        /// </returns>
        public FileSystemCapabilities GetCapabilities()
        {
            // NDFS supports full extended attributes (23 metadata fields)
            return new FileSystemCapabilities
            {
                SupportsExtendedAttributes = true,
                SupportedPropertyKeys = new[]
                {
                    "ndfs:ObjectName", "ndfs:Type", "ndfs:UserName", "ndfs:UserIndex",
                    "ndfs:ObjectIndex", "ndfs:BytesInFile", "ndfs:PagesInFile",
                    "ndfs:FilePointer", "ndfs:FileType", "ndfs:AccessBits",
                    "ndfs:CreatedDate", "ndfs:ModifiedDate", "ndfs:AccessedDate",
                    "ndfs:Owner", "ndfs:Group", "ndfs:Permissions",
                    "ndfs:FriendList", "ndfs:QuotaPagesUsed", "ndfs:QuotaMaxPages",
                    "ndfs:FileTypeFlags", "ndfs:IndexBlockId", "ndfs:DataBlockIds",
                    "ndfs:SparseHoleIndices"
                }
            };
        }

        /// <summary>
        /// Gets the properties (metadata) of a file without reading its contents.
        /// </summary>
        /// <param name="path">
        /// The filesystem path of the file.
        /// </param>
        /// <returns>
        /// The property bag returned by the mounted filesystem, or <c>null</c> when it returns none.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the driver is not mounted.
        /// </exception>
        public System.Collections.Generic.IDictionary<string, object>? GetFileProperties(string path)
        {
            EnsureMounted();
            return _filesystem!.GetFileProperties(path);
        }

        /// <summary>
        /// Writes a file together with its extended properties (metadata).
        /// </summary>
        /// <remarks>
        /// The call is passed to the mounted filesystem; its errors pass through to the caller.
        /// </remarks>
        /// <param name="path">
        /// The filesystem path of the file.
        /// </param>
        /// <param name="data">
        /// The file data.
        /// </param>
        /// <param name="propertyBag">
        /// The extended properties to apply.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the driver is not mounted.
        /// </exception>
        public void WriteFileWithProperties(string path, byte[] data, System.Collections.Generic.IDictionary<string, object> propertyBag)
        {
            EnsureMounted();
            _filesystem!.WriteFileWithProperties(path, data, propertyBag);
        }

        /// <summary>
        /// Reads a file together with its extended properties (metadata).
        /// </summary>
        /// <remarks>
        /// The call is passed to the mounted filesystem; its errors pass through to the caller.
        /// </remarks>
        /// <param name="path">
        /// The filesystem path of the file.
        /// </param>
        /// <param name="propertyBag">
        /// The dictionary that receives the file properties.
        /// </param>
        /// <returns>
        /// The file contents.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the driver is not mounted.
        /// </exception>
        public byte[] ReadFileWithProperties(string path, System.Collections.Generic.IDictionary<string, object> propertyBag)
        {
            EnsureMounted();
            return _filesystem!.ReadFileWithProperties(path, propertyBag);
        }

        /// <summary>
        /// Gets the extended NDFS-specific API for legacy applications.
        /// </summary>
        public INdfsFileSystem? GetExtendedApi()
        {
            return _filesystem as INdfsFileSystem;
        }

        private void EnsureMounted()
        {
            if (_blockDevice == null || _filesystem == null)
                throw new InvalidOperationException("Driver not mounted");
        }

        // ===================================================================
        // VOLUME INFO PROVIDER
        // ===================================================================

        /// <summary>
        /// Describes the mounted NDFS volume for the generic disk-info window.
        ///
        /// This is deliberately READ-ONLY and a subset of what NdfsDiskInfoWindow shows: that
        /// window stays the primary NDFS screen because it can also rename the volume, clear
        /// the entered flag and edit the SINTRAN initial-command buffer. This exists so that
        /// generic callers (the shared disk-info window, scripting, reports) get real NDFS
        /// detail instead of the bare IFileSystemDriver fallback.
        /// </summary>
        public VolumeInfo? GetVolumeInfo()
        {
            if (_blockDevice == null || _filesystem == null)
            {
                return null;
            }

            MasterBlock master;
            try
            {
                master = _filesystem.GetMasterBlock();
            }
            catch
            {
                // An unreadable master block is not fatal here - let the caller fall back.
                return null;
            }

            var info = new VolumeInfo
            {
                FileSystemType = FileSystemType,
                VolumeLabel = VolumeLabel,
                IsReadOnly = _blockDevice.IsReadOnly
            };

            // -- Volume -------------------------------------------------------------------
            var volume = info.AddSection("Volume", "\U0001F4C0");
            volume.Add("Directory name", string.IsNullOrEmpty(master.DirectoryName) ? "(unnamed)" : master.DirectoryName);
            volume.Add("Image size", $"{master.ImageSize:N0} pages");
            volume.Add("Directory size", $"{master.DirectorySize:N0} pages");
            volume.Add("Unreserved pages", $"{master.UnreservedPages:N0}");
            volume.Add("Boot format", master.BootFormat.ToString());
            volume.Add("Entered", master.IsDirectoryEntered ? "Yes" : "No",
                $"flag word 0x{master.ExtFlagWord:X4}");

            // -- Extended info ------------------------------------------------------------
            var ext = info.AddSection("Extended Info", "\U0001F5A5");
            ext.Add("Checksum state", master.ChecksumState.ToString(),
                $"stored 0x{master.ExtCheckSum:X4}, computed 0x{master.ExtCalculatedChecksum:X4}");
            ext.Add("Owning system number", master.ExtLastSystemNumber.ToString());
            ext.Add("Pages available", $"{master.ExtPagesAvailable:N0}");
            ext.Add("FLOMON signature", master.HasFlomon ? "Present (floppy)" : "Absent");

            // -- Users --------------------------------------------------------------------
            try
            {
                IReadOnlyList<UserEntry> users = _filesystem.GetUsers();
                var userSection = info.AddSection("Users", "\U0001F465");
                userSection.Add("User count", users.Count.ToString());

                for (int i = 0; i < users.Count; i++)
                {
                    UserEntry u = users[i];
                    userSection.Add(
                        string.IsNullOrEmpty(u.UserName) ? $"#{i}" : u.UserName,
                        $"{u.PagesUsed:N0} used / {u.PagesReserved:N0} reserved");
                }
            }
            catch
            {
                // A volume with an unreadable user file still has useful volume info.
            }

            // -- Allocation map -----------------------------------------------------------
            // One unit per device page, straight from the bitmap. Pages above the declared
            // capacity are marked Beyond rather than Free: the bitmap can describe more pages
            // than the media actually has, and showing those as allocatable would be a lie.
            try
            {
                // Prefer the master block's own page count, but fall back to the device size when
                // the field is unset. ImageSize is genuinely 0 on some images (hand-built packs
                // and our own synthetic test images never populate it), and the block device is
                // already measured in NDFS pages - NdfsBlockDeviceDescriptor sets BlockSize to
                // NDFS_PAGE_SIZE - so TotalBlocks is directly comparable, not a sector count.
                uint totalPages = master.ImageSize > 0 ? master.ImageSize : _blockDevice.TotalBlocks;

                if (totalPages > 0 && totalPages <= 4_000_000)
                {
                    uint ceiling = master.ExtPagesAvailable;
                    var units = new VolumeAllocationState[totalPages];

                    for (uint page = 0; page < totalPages; page++)
                    {
                        bool used = _filesystem.IsBlockUsed(page);

                        if (used)
                        {
                            units[page] = VolumeAllocationState.Used;
                        }
                        else if (ceiling > 0 && page >= ceiling)
                        {
                            units[page] = VolumeAllocationState.Beyond;
                        }
                        else
                        {
                            units[page] = VolumeAllocationState.Free;
                        }
                    }

                    info.AllocationMap = new VolumeAllocationMap
                    {
                        Units = units,
                        UnitName = "page",
                        UnitSizeBytes = 2048   // NDFS pages are 2048 bytes, not 512-byte sectors
                    };
                }
            }
            catch
            {
                // No allocation map is better than a wrong one.
            }

            return info;
        }

        /// <summary>
        /// Disposes the mounted filesystem and block device, if any.
        /// </summary>
        public void Dispose()
        {
            _filesystem?.Dispose();
            _blockDevice?.Dispose();
        }
    }
}
