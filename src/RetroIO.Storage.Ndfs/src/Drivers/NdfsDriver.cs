//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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

        public string FileSystemType => _filesystem?.FileSystemType ?? "NDFS";
        public string VolumeLabel => _filesystem?.VolumeLabel ?? string.Empty;
        public string ExtensionSeparator => _filesystem?.ExtensionSeparator ?? ":";

        public DriverCapabilities Capabilities =>
            DriverCapabilities.Read |
            DriverCapabilities.Write |          // WriteFile: real implementation (files <=1MB, Indexed allocation)
            DriverCapabilities.Delete |         // DeleteFile: real implementation
            DriverCapabilities.CreateDirectory | // CreateDirectory: real implementation (root path = create User)
            DriverCapabilities.ListDirectory |
            DriverCapabilities.Subdirectories |
            DriverCapabilities.FileTypes |
            DriverCapabilities.BlockAllocationMap |
            DriverCapabilities.VolumeLabel |
            DriverCapabilities.BlockRead |
            DriverCapabilities.Permissions |    // NDFS-specific: user-based permissions
            DriverCapabilities.Quotas;          // NDFS-specific: user quotas
                                                // NOTE: Rename is deliberately NOT included — NdfsDriver doesn't override RenameFile,
                                                // so it falls through to IFileSystemDriver's default NotSupportedException (user
                                                // rename is a known, documented gap, not implemented here).

        public bool CanRead => true;
        public bool CanWrite => true; // Write support enabled (files ≤1MB, uses Indexed allocation)
        public bool HasBlockAccess => true;

        public IFileSystemMetadataDescriptor? GetMetadataDescriptor()
        {
            // TODO: Implement NDFS metadata descriptor (user, pages, type)
            return null;
        }

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

        public byte[] ReadFile(string path)
        {
            EnsureMounted();
            return _filesystem!.ReadFile(path);
        }

        public void WriteFile(string path, byte[] data)
        {
            EnsureMounted();
            _filesystem!.WriteFile(path, data);
        }

        public void DeleteFile(string path)
        {
            EnsureMounted();
            _filesystem!.DeleteFile(path);
        }

        // NDFS has no nested directories: a root-level "directory" is a User. NdfsFileSystem
        // interprets a path like "/ALICE" as "create user ALICE" with a default quota.
        public void CreateDirectory(string path)
        {
            EnsureMounted();
            _filesystem!.CreateDirectory(path);
        }

        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureMounted();
            return _filesystem!.ListDirectory(path);
        }

        public FileMetadata? GetMetadata(string path)
        {
            EnsureMounted();
            return _filesystem!.GetMetadata(path);
        }

        public int ReadBlock(BlockAddress address, Span<byte> buffer)
        {
            EnsureMounted();
            return _blockDevice!.ReadBlock(address, buffer);
        }

        public void WriteBlock(BlockAddress address, ReadOnlySpan<byte> data)
        {
            EnsureMounted();
            _blockDevice!.WriteBlock(address, data);
        }

        public BlockDeviceInfo? GetBlockDeviceInfo()
        {
            EnsureMounted();
            return _blockDevice!.GetBlockDeviceInfo();
        }

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

        public System.Collections.Generic.IDictionary<string, object>? GetFileProperties(string path)
        {
            EnsureMounted();
            return _filesystem!.GetFileProperties(path);
        }

        public void WriteFileWithProperties(string path, byte[] data, System.Collections.Generic.IDictionary<string, object> propertyBag)
        {
            EnsureMounted();
            _filesystem!.WriteFileWithProperties(path, data, propertyBag);
        }

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

        public void Dispose()
        {
            _filesystem?.Dispose();
            _blockDevice?.Dispose();
        }
    }
}
