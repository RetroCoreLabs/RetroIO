//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using RetroIO.Storage.Ndfs.BlockDevices;

namespace RetroIO.Storage.Ndfs.Creation
{
    /// <summary>
    /// Creates new empty NDFS disk images with pre-defined templates or custom configurations.
    /// </summary>
    /// <remarks>
    /// <para><b>Layout</b></para>
    /// The layout is what real SINTRAN M writes with @CREATE-DIRECTORY, measured in the nd100x emulator
    /// (docs/CREATE-DIRECTORY-ORACLE.md and docs/CREATE-DIRECTORY-PLACEMENT.md in the norskdata-ndfs
    /// repository):
    ///  - The bit file goes at the address of SINTRAN's prompt OCTAL BIT FILE ADDRESS (-1 END OF DISC).
    ///    The default answer gives 9 * floor(floor(capacity / 2) / 9), the answer -1 gives the capacity
    ///    minus the bit file pages, and a typed address is used as given.
    ///  - The bit file is ceil(capacity / 16384) pages. It marks page 0, all its own pages, and the
    ///    pages from the capacity up to the next multiple of 256 (not past the end of the device).
    ///  - The user data page, user index block, object data page and object index block are the first
    ///    four free pages going down from (BA - 1) OR 15 (SINTRAN routine RSBPG).
    ///  - Hard disks get an Extended Info Block with the declared capacity, floppies none.
    ///  - Unreserved Pages is the capacity minus 1, minus the bit file pages, minus 2, minus 1, minus
    ///    the SYSTEM quota.
    /// </remarks>
    public static class NdfsImageCreator
    {
        /// <summary>
        /// Size of the page-0 boot region in bytes: bytes 0..1999. The extended-info block begins
        /// at byte 2000, so a bootstrap may fill up to this length but must never reach it.
        /// </summary>
        private const int BOOT_REGION_SIZE = 2000;

        /// <summary>
        /// Creates a new NDFS disk image with the specified options.
        /// </summary>
        /// <param name="outputPath">
        /// Path to write the image file.
        /// </param>
        /// <param name="options">
        /// Image creation options.
        /// </param>
        public static void CreateImage(string outputPath, NdfsImageCreationOptions options)
        {
            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentNullException(nameof(outputPath));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            // Get template specification
            NdfsTemplateSpec spec;
            if (options.Template == NdfsImageTemplate.Custom)
            {
                spec = CreateCustomSpec(options);
            }
            else
            {
                var templateSpec = NdfsTemplates.GetTemplate(options.Template);
                if (templateSpec == null)
                    throw new ArgumentException($"Invalid template: {options.Template}");
                spec = templateSpec;
            }

            // Work on a copy: the pre-defined templates are shared static objects and the layout is
            // computed into the spec below.
            spec = spec.Clone();

            // Override boot loader if explicitly specified (null means use template default)
            if (options.BootLoaderType.HasValue)
            {
                spec.BootLoaderType = options.BootLoaderType.Value;
            }

            // Create the image
            CreateImageFromSpec(outputPath, options, spec);
        }

        /// <summary>
        /// Creates a new NDFS disk image using a pre-defined template.
        /// </summary>
        /// <param name="outputPath">
        /// Path to write the image file.
        /// </param>
        /// <param name="template">
        /// Template to use.
        /// </param>
        /// <param name="directoryName">
        /// Directory name (default "EMPTY-DISK").
        /// </param>
        /// <param name="bootLoaderType">
        /// Boot loader type (default from template).
        /// </param>
        public static void CreateImageFromTemplate(
            string outputPath,
            NdfsImageTemplate template,
            string directoryName = "EMPTY-DISK",
            NdfsBootLoaderType? bootLoaderType = null)
        {
            var options = new NdfsImageCreationOptions
            {
                Template = template,
                DirectoryName = directoryName,
                BootLoaderType = bootLoaderType
            };

            CreateImage(outputPath, options);
        }

        /// <summary>
        /// Creates a custom template specification from options.
        /// </summary>
        /// <param name="options">
        /// Image creation options with <see cref="NdfsImageCreationOptions.CustomNdfsPages"/> set.
        /// </param>
        /// <returns>
        /// A specification with the sizes filled in. The page numbers of the bit file, user file and
        /// object file are worked out later by <see cref="ComputeLayout"/>.
        /// </returns>
        private static NdfsTemplateSpec CreateCustomSpec(NdfsImageCreationOptions options)
        {
            if (options.CustomNdfsPages == 0)
                throw new ArgumentException("CustomNdfsPages must be specified for Custom template");

            uint ndfsPages = options.CustomNdfsPages;

            // The image is DevicePages long when the caller gave a length (a named disk type needs its
            // full SINTRAN layout length); otherwise the spare area of the controller type is added.
            uint fileBlocks;
            if (options.DevicePages.HasValue)
                fileBlocks = Math.Max(options.DevicePages.Value, ndfsPages);
            else
                fileBlocks = ndfsPages + CalculateSpareBlocks(ndfsPages, options.ControllerType);

            bool isFloppy = (options.ControllerType == NdfsControllerType.Floppy);

            return new NdfsTemplateSpec
            {
                NdfsPages = ndfsPages,
                FileBlocks = fileBlocks,
                ControllerType = options.ControllerType,
                HasFlomon = isFloppy,
                ExtValid = !isFloppy && options.IncludeExtendedInfo,
                BootLoaderType = options.BootLoaderType ?? (isFloppy ? NdfsBootLoaderType.Floppy : NdfsBootLoaderType.Winchester),
                SpareBlocks = fileBlocks - ndfsPages
            };
        }

        /// <summary>
        /// Calculates spare blocks based on controller type.
        /// </summary>
        /// <param name="ndfsPages">
        /// Directory size in pages.
        /// </param>
        /// <param name="controllerType">
        /// Controller type of the disk.
        /// </param>
        /// <returns>
        /// The number of spare pages added after the directory.
        /// </returns>
        private static uint CalculateSpareBlocks(uint ndfsPages, NdfsControllerType controllerType)
        {
            return controllerType switch
            {
                NdfsControllerType.Smd => (uint)(ndfsPages * 0.0394),      // 3.94% overhead
                NdfsControllerType.Scsi => (uint)(ndfsPages * 0.0593),     // 5.93% overhead
                NdfsControllerType.Winchester => 0,                         // Minimal overhead
                NdfsControllerType.Floppy => 0,                             // No overhead
                _ => 0
            };
        }

        /// <summary>
        /// Where the structures of a new directory go, and the bit file as @CREATE-DIRECTORY leaves it.
        /// </summary>
        private sealed class Layout
        {
            /// <summary>
            /// Pages in the bit file, ceil(capacity / 16384).
            /// </summary>
            public uint BitmapPages;

            /// <summary>
            /// The bit file's contents: BitmapPages pages.
            /// </summary>
            public byte[] Bitmap = Array.Empty<byte>();

            /// <summary>
            /// Pages left for user quotas: the capacity minus page 0, the bit file, the user file and the
            /// object file index block.
            /// </summary>
            public uint Available;

            /// <summary>
            /// The quota given to SYSTEM.
            /// </summary>
            public uint Quota;

            /// <summary>
            /// Page of the user file data block that holds the SYSTEM entry.
            /// </summary>
            public uint UserData;

            /// <summary>
            /// Page of the object file data block.
            /// </summary>
            public uint ObjectData;
        }

        /// <summary>
        /// Works out the bit file address, the four directory pages, the bitmap and the quotas, and
        /// stores the page numbers in <paramref name="spec"/>.
        /// </summary>
        /// <param name="options">
        /// Image creation options, for the bit file address answer.
        /// </param>
        /// <param name="spec">
        /// Specification; its BitFileBlock, UserFileBlock, ObjectFileBlock and UnreservedPages are
        /// set here.
        /// </param>
        /// <returns>
        /// The layout with the bitmap and the data page numbers.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when the bit file address does not fit on the disk, or the disk is too small.
        /// </exception>
        private static Layout ComputeLayout(NdfsImageCreationOptions options, NdfsTemplateSpec spec)
        {
            uint cap = spec.NdfsPages;
            uint dev = spec.FileBlocks;
            var layout = new Layout();

            // The bit file: one bit per page, 16384 bits per page, sized by the declared capacity.
            layout.BitmapPages = (cap + 16383) / 16384;

            // Bit file address, the answer to SINTRAN's prompt OCTAL BIT FILE ADDRESS (-1 END OF DISC).
            uint ba;
            if (options.BitFileAddress.HasValue && options.BitFileAddress.Value != 0)
                ba = options.BitFileAddress.Value;
            else if (options.BitFileAtEndOfDisc)
                ba = cap - layout.BitmapPages;
            else
                ba = 9 * ((cap / 2) / 9);
            if (ba < 8 || ba + layout.BitmapPages > cap)
                throw new ArgumentException("The bit file address is outside the disk");

            // Pages left for the user quota. Unreserved Pages in the master block is this minus the
            // sum of the user quotas (measured on 9 runs).
            if (cap < 40 + layout.BitmapPages)
                throw new ArgumentException("The disk is too small for a directory");
            layout.Available = cap - 1 - layout.BitmapPages - 2 - 1;
            layout.Quota = cap > 1000 ? cap / 2 : Math.Min(layout.Available, 500u);

            // The bitmap, as @CREATE-DIRECTORY leaves it.
            layout.Bitmap = new byte[layout.BitmapPages * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            MarkBlockAllocated(layout.Bitmap, 0);
            for (uint i = 0; i < layout.BitmapPages; i++)
                MarkBlockAllocated(layout.Bitmap, (int)(ba + i));

            // When the device is longer than the capacity SINTRAN marks the pages from the capacity up
            // to the next multiple of 256 as used, but never a page past the end of the device.
            if (spec.ExtValid && dev > cap)
            {
                uint end = Math.Min(((cap + 255) / 256) * 256, dev);
                for (uint i = cap; i < end; i++)
                    MarkBlockAllocated(layout.Bitmap, (int)i);
            }

            // The four pages the first user and the first file take (RSBPG): user data page, user
            // index block, object data page, object index block.
            int userData = AllocNearHint(layout.Bitmap, cap, ba - 1);
            int userIndex = AllocNearHint(layout.Bitmap, cap, ba - 1);
            int objectData = AllocNearHint(layout.Bitmap, cap, ba - 1);
            int objectIndex = AllocNearHint(layout.Bitmap, cap, ba - 1);
            if (userData < 0 || userIndex < 0 || objectData < 0 || objectIndex < 0)
                throw new ArgumentException("No space for the directory files");

            layout.UserData = (uint)userData;
            layout.ObjectData = (uint)objectData;
            spec.BitFileBlock = ba;
            spec.UserFileBlock = (uint)userIndex;
            spec.ObjectFileBlock = (uint)objectIndex;
            spec.UnreservedPages = layout.Available - layout.Quota;
            return layout;
        }

        /// <summary>
        /// SINTRAN's RSBPG rule: the first free page going down from <c>hint OR 15</c>, then word+1
        /// (lowest bit first), word-1 (highest bit first), word+2 and so on. Marks the page used.
        /// </summary>
        /// <param name="bitmap">
        /// The bit file contents.
        /// </param>
        /// <param name="capacity">
        /// Directory size; pages at or above it are never given.
        /// </param>
        /// <param name="hint">
        /// The hint page, the bit file address minus 1.
        /// </param>
        /// <returns>
        /// The page given, or -1 when there is none.
        /// </returns>
        private static int AllocNearHint(byte[] bitmap, uint capacity, uint hint)
        {
            uint word = hint >> 4;
            uint nwords = (capacity + 15) >> 4;

            bool Take(uint page)
            {
                if (page >= capacity)
                    return false;
                int byteIndex = (int)((page >> 3) ^ 1u);
                int mask = 1 << (int)(page & 7u);
                if ((bitmap[byteIndex] & mask) != 0)
                    return false;
                bitmap[byteIndex] |= (byte)mask;
                return true;
            }

            for (int bit = 15; bit >= 0; bit--)
            {
                uint p = (word << 4) + (uint)bit;
                if (Take(p))
                    return (int)p;
            }

            for (uint step = 1; step <= nwords; step++)
            {
                uint w = word + step;
                if (w < nwords)
                {
                    for (uint bit = 0; bit < 16; bit++)
                    {
                        uint p = (w << 4) + bit;
                        if (Take(p))
                            return (int)p;
                    }
                }

                if (word >= step)
                {
                    w = word - step;
                    for (int bit = 15; bit >= 0; bit--)
                    {
                        uint p = (w << 4) + (uint)bit;
                        if (Take(p))
                            return (int)p;
                    }
                }
            }

            return -1;
        }

        /// <summary>
        /// Creates the actual disk image file from specification.
        /// </summary>
        /// <param name="outputPath">
        /// Path of the image file to write.
        /// </param>
        /// <param name="options">
        /// Image creation options.
        /// </param>
        /// <param name="spec">
        /// Specification to build from; the layout is stored in it.
        /// </param>
        private static void CreateImageFromSpec(string outputPath, NdfsImageCreationOptions options, NdfsTemplateSpec spec)
        {
            // Calculate file size
            long fileSize = spec.FileBlocks * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;

            // Create disk image (zero-filled)
            byte[] diskImage = new byte[fileSize];

            // BLANK IMAGE: a correctly-sized, entirely 0x00 file - no boot sector, no master block,
            // no extended info, no bitmap. Sometimes all you need is an empty device of the right
            // geometry (for example to let SINTRAN's own @CREATE-DIRECTORY format it, or to dd into).
            // Deliberately NOT a mountable NDFS volume: with all-zero pointers it is exactly the
            // shape the hardened detector rejects, so it will not auto-mount. That is intended.
            if (options.CreateBlankImage)
            {
                File.WriteAllBytes(outputPath, diskImage);

                var blankInfo = new FileInfo(outputPath);
                if (blankInfo.Length != fileSize)
                    throw new Exception($"File size mismatch: expected {fileSize}, got {blankInfo.Length}");

                return;
            }

            // Where everything goes. Done before any byte is written so a disk that is too small fails
            // without leaving a half-written file.
            Layout layout = ComputeLayout(options, spec);

            // Step 1: Write boot sector (optional - a valid NDFS volume need not be bootable).
            // The boot region is page-0 bytes 0..1999 (the extended-info block starts at 2000), so
            // the bootstrap may be up to BOOT_REGION_SIZE bytes - NOT capped at 1024, which would
            // cut a real hard-disk bootstrap in half (see NdfsBootSectors remarks).
            if (options.IncludeBootSector)
            {
                if (options.CustomBootSector != null)
                {
                    // Use custom boot sector
                    int copyLen = Math.Min(options.CustomBootSector.Length, BOOT_REGION_SIZE);
                    Array.Copy(options.CustomBootSector, 0, diskImage, 0, copyLen);
                }
                else if (spec.BootLoaderType != NdfsBootLoaderType.None)
                {
                    // A 616 page floppy has its own loader; the other floppies use the 154 page one.
                    byte[] bootSector = (spec.BootLoaderType == NdfsBootLoaderType.Floppy && spec.FileBlocks == 616)
                        ? NdfsBootBlobs.Floppy616
                        : NdfsBootSectors.GetBootSector(spec.BootLoaderType);
                    Array.Copy(bootSector, 0, diskImage, 0, Math.Min(bootSector.Length, BOOT_REGION_SIZE));
                }
            }

            // Step 2: Write Extended Info block (hard disks only)
            if (spec.ExtValid && options.IncludeExtendedInfo)
            {
                WriteExtendedInfo(diskImage, spec.NdfsPages, options.SystemNumber, options.FlagWord);
            }

            // Steps 3-6: the filesystem structures. Skipping the master block leaves the volume
            // unmountable by design (no directory name, no object/user/bit-file pointers), so the
            // bitmap and index blocks it points at are pointless too - skip them together.
            if (options.IncludeMasterBlock)
            {
                // Step 3: Write Master Block
                WriteMasterBlock(diskImage, options.DirectoryName, spec);

                // Step 4: Bit file (allocation bitmap)
                Array.Copy(layout.Bitmap, 0, diskImage, spec.BitFileBlock * (long)NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE, layout.Bitmap.Length);

                // Step 5: Object file index block -> data page (empty)
                CreateObjectFile(diskImage, spec, layout);

                // Step 6: User file index block -> data page with SYSTEM and the extra users
                CreateUserFile(diskImage, spec, layout, options.Users ?? new System.Collections.Generic.List<NdfsUserCreationInfo>());
            }

            // The real SCSI disk carries data in its last page that SINTRAN requires.
            if (options.Template == NdfsImageTemplate.Scsi126MB)
            {
                byte[] last = NdfsBootBlobs.ScsiLastPage;
                Array.Copy(last, 0, diskImage, fileSize - NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE, NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);
            }

            // Step 7: Write to file
            File.WriteAllBytes(outputPath, diskImage);

            // Verify file size
            var fileInfo = new FileInfo(outputPath);
            if (fileInfo.Length != fileSize)
            {
                throw new Exception($"File size mismatch: expected {fileSize}, got {fileInfo.Length}");
            }
        }

        /// <summary>
        /// Writes Extended Info block (bytes 2000-2015, 0x07D0-0x07DF).
        /// </summary>
        private static void WriteExtendedInfo(byte[] diskImage, uint ndfsPages, ushort systemNumber, ushort flagWord)
        {
            int extOffset = 0x07D0;

            // Calculate checksum the way the SINTRAN kernel does (WXDIR 37702B): a plain
            // 16-bit additive sum of the seven words that follow the checksum slot
            // (reserved1-3 = 0, flag, system#, pages-hi, pages-lo). NOT XOR-then-add.
            ushort pagesLo = (ushort)(ndfsPages & 0xFFFF);
            ushort pagesHi = (ushort)((ndfsPages >> 16) & 0xFFFF);
            ushort checksum = Structures.MasterBlock.ComputeExtChecksum(
                0, 0, 0, flagWord, systemNumber, pagesHi, pagesLo);

            // Write checksum (word 1750, offset 0x07D0)
            diskImage[extOffset + 0] = (byte)(checksum >> 8);
            diskImage[extOffset + 1] = (byte)(checksum & 0xFF);

            // Write reserved fields (words 1751-1753, must be 0 for hard disks)
            diskImage[extOffset + 2] = 0;
            diskImage[extOffset + 3] = 0;
            diskImage[extOffset + 4] = 0;
            diskImage[extOffset + 5] = 0;
            diskImage[extOffset + 6] = 0;
            diskImage[extOffset + 7] = 0;

            // Write flag word (word 1754, offset 0x07D8)
            diskImage[extOffset + 8] = (byte)(flagWord >> 8);
            diskImage[extOffset + 9] = (byte)(flagWord & 0xFF);

            // Write system number (word 1755, offset 0x07DA)
            diskImage[extOffset + 10] = (byte)(systemNumber >> 8);
            diskImage[extOffset + 11] = (byte)(systemNumber & 0xFF);

            // Write pages available (words 1756-1757, offset 0x07DC-0x07DF, big-endian 32-bit)
            diskImage[extOffset + 12] = (byte)((ndfsPages >> 24) & 0xFF);
            diskImage[extOffset + 13] = (byte)((ndfsPages >> 16) & 0xFF);
            diskImage[extOffset + 14] = (byte)((ndfsPages >> 8) & 0xFF);
            diskImage[extOffset + 15] = (byte)(ndfsPages & 0xFF);
        }

        /// <summary>
        /// Writes Master Block (bytes 2016-2047, 0x07E0-0x07FF).
        /// </summary>
        private static void WriteMasterBlock(byte[] diskImage, string directoryName, NdfsTemplateSpec spec)
        {
            int mbOffset = NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET; // 0x07E0

            // Directory Name (16 bytes, terminated with ')
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(directoryName);
            int nameLen = Math.Min(nameBytes.Length, 15);  // Reserve 1 byte for terminator
            Array.Copy(nameBytes, 0, diskImage, mbOffset, nameLen);
            diskImage[mbOffset + nameLen] = 0x27;  // ' terminator

            // Object File Pointer (offset 0x10, type 01 = Indexed)
            uint objectPointer = 0x40000000 | spec.ObjectFileBlock;  // Type 01 in bits 31-30
            diskImage[mbOffset + 0x10] = (byte)((objectPointer >> 24) & 0xFF);
            diskImage[mbOffset + 0x11] = (byte)((objectPointer >> 16) & 0xFF);
            diskImage[mbOffset + 0x12] = (byte)((objectPointer >> 8) & 0xFF);
            diskImage[mbOffset + 0x13] = (byte)(objectPointer & 0xFF);

            // User File Pointer (offset 0x14, type 01 = Indexed)
            uint userPointer = 0x40000000 | spec.UserFileBlock;
            diskImage[mbOffset + 0x14] = (byte)((userPointer >> 24) & 0xFF);
            diskImage[mbOffset + 0x15] = (byte)((userPointer >> 16) & 0xFF);
            diskImage[mbOffset + 0x16] = (byte)((userPointer >> 8) & 0xFF);
            diskImage[mbOffset + 0x17] = (byte)(userPointer & 0xFF);

            // Bit File Pointer (offset 0x18, type 00 = Contiguous)
            uint bitPointer = spec.BitFileBlock;  // Type 00 (no bits set)
            diskImage[mbOffset + 0x18] = (byte)((bitPointer >> 24) & 0xFF);
            diskImage[mbOffset + 0x19] = (byte)((bitPointer >> 16) & 0xFF);
            diskImage[mbOffset + 0x1A] = (byte)((bitPointer >> 8) & 0xFF);
            diskImage[mbOffset + 0x1B] = (byte)(bitPointer & 0xFF);

            // Unreserved Pages (offset 0x1C)
            uint unreserved = spec.UnreservedPages;
            diskImage[mbOffset + 0x1C] = (byte)((unreserved >> 24) & 0xFF);
            diskImage[mbOffset + 0x1D] = (byte)((unreserved >> 16) & 0xFF);
            diskImage[mbOffset + 0x1E] = (byte)((unreserved >> 8) & 0xFF);
            diskImage[mbOffset + 0x1F] = (byte)(unreserved & 0xFF);
        }

        /// <summary>
        /// Marks a block as allocated in the bitmap.
        /// </summary>
        private static void MarkBlockAllocated(byte[] bitmap, int blockNumber)
        {
            // Must match BitFile.BitPosition: page N is bit N%16 of the 16-bit big-endian
            // word N/16, i.e. byte (N>>3)^1 on a byte array. This was a second, independent
            // copy of the arithmetic — when the bit order was corrected, a copy left behind
            // here would mark a new image's reserved pages in one convention while the reader
            // used the other, and the allocator would hand out structural pages.
            int byteIndex = (blockNumber >> 3) ^ 1;
            int bitIndex = blockNumber & 7;

            if (byteIndex < bitmap.Length)
            {
                // LSB-first within each byte: page 0 = bit 0 (mask 0x01), page 7 = bit 7
                // (mask 0x80), page 8 = bit 0 of byte 1. VERIFIED against PACK-ONE's on-disk
                // bitmap (its popcount matches ndtool -i) and matches the reader in
                // BitFile.IsBlockUsed. The previous MSB-first (1 << (7-bit)) form wrote the
                // wrong bits, so created images marked the wrong pages as used.
                bitmap[byteIndex] |= (byte)(1 << bitIndex);
            }
        }

        /// <summary>
        /// Writes the object file: the index block gets one pointer to the (empty) data page.
        /// </summary>
        /// <param name="diskImage">
        /// The image being built.
        /// </param>
        /// <param name="spec">
        /// Specification with the page of the object file index block.
        /// </param>
        /// <param name="layout">
        /// Layout with the page of the object file data block.
        /// </param>
        private static void CreateObjectFile(byte[] diskImage, NdfsTemplateSpec spec, Layout layout)
        {
            long indexOffset = spec.ObjectFileBlock * (long)NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            WriteBlockPointerBytes(diskImage, indexOffset, layout.ObjectData);
        }

        /// <summary>
        /// Writes the user file: the index block gets one pointer to the data page, and the data page
        /// holds the SYSTEM user and any extra users.
        /// </summary>
        /// <param name="diskImage">
        /// The image being built.
        /// </param>
        /// <param name="spec">
        /// Specification with the page of the user file index block.
        /// </param>
        /// <param name="layout">
        /// Layout with the page of the user file data block and the SYSTEM quota.
        /// </param>
        /// <param name="users">
        /// Extra users. Their passwords are not applied: the password hash is not written by this creator.
        /// </param>
        private static void CreateUserFile(byte[] diskImage, NdfsTemplateSpec spec, Layout layout, System.Collections.Generic.List<NdfsUserCreationInfo> users)
        {
            long indexOffset = spec.UserFileBlock * (long)NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            WriteBlockPointerBytes(diskImage, indexOffset, layout.UserData);

            long dataOffset = layout.UserData * (long)NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;

            // SYSTEM is charged the object data page, as SINTRAN charges it after the first file
            // (measured, docs/NDFS-ALLOCATION-RULES.md section 7).
            var system = new Elements.UserEntry
            {
                UserName = "SYSTEM",
                UserIndex = 0,
                PagesReserved = layout.Quota,
                PagesUsed = 1
            };
            Array.Copy(system.ToBytes(), 0, diskImage, dataOffset, 64);

            for (int i = 0; i < users.Count && i + 1 < 32; i++)
            {
                int slot = i + 1;
                var user = new Elements.UserEntry
                {
                    UserName = users[i].UserName,
                    UserIndex = slot,
                    PagesReserved = users[i].PageQuota,
                    PagesUsed = 0
                };
                Array.Copy(user.ToBytes(), 0, diskImage, dataOffset + slot * 64, 64);
            }
        }

        /// <summary>
        /// Writes one Contiguous block pointer (type 00) at the start of a page.
        /// </summary>
        /// <param name="diskImage">
        /// The image being built.
        /// </param>
        /// <param name="offset">
        /// Byte offset in the image.
        /// </param>
        /// <param name="blockId">
        /// Page number the pointer names.
        /// </param>
        private static void WriteBlockPointerBytes(byte[] diskImage, long offset, uint blockId)
        {
            diskImage[offset + 0] = (byte)((blockId >> 24) & 0x3F);
            diskImage[offset + 1] = (byte)((blockId >> 16) & 0xFF);
            diskImage[offset + 2] = (byte)((blockId >> 8) & 0xFF);
            diskImage[offset + 3] = (byte)(blockId & 0xFF);
        }
    }
}
