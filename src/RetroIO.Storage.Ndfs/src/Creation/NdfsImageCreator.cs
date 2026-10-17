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
        /// <param name="outputPath">Path to write the image file.</param>
        /// <param name="options">Image creation options.</param>
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
        /// <param name="outputPath">Path to write the image file.</param>
        /// <param name="template">Template to use.</param>
        /// <param name="directoryName">Directory name (default "EMPTY-DISK").</param>
        /// <param name="bootLoaderType">Boot loader type (default from template).</param>
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
        private static NdfsTemplateSpec CreateCustomSpec(NdfsImageCreationOptions options)
        {
            if (options.CustomNdfsPages == 0)
                throw new ArgumentException("CustomNdfsPages must be specified for Custom template");

            uint ndfsPages = options.CustomNdfsPages;
            uint spareBlocks = CalculateSpareBlocks(ndfsPages, options.ControllerType);
            uint fileBlocks = ndfsPages + spareBlocks;

            // Calculate pointer positions. KERNEL-DERIVED from ALBIT (137517B): the branch
            // is whether @CREATE-DIRECTORY was given an explicit bit-file address, NOT the
            // device size. (The old ">is-floppy / mid-disk-vs-end" switch was wrong — a
            // small floppy created on the default path still lands mid-disk.)
            // Placement is driven by the DECLARED page count (pages_available), not the
            // physical file byte-size.
            bool isFloppy = (options.ControllerType == NdfsControllerType.Floppy);
            uint bitFileBlock;
            uint objectFileBlock;
            uint userFileBlock;

            if (options.BitFileAddress.HasValue && options.BitFileAddress.Value != 0)
            {
                // OVERRIDE path: metadata packed just below the supplied bit-file address.
                // (Real floppies use this: 615/613/611 of 616 pages -> bit, bit-2, bit-4.)
                bitFileBlock = options.BitFileAddress.Value;
                objectFileBlock = bitFileBlock - 4;
                userFileBlock = bitFileBlock - 2;      // = objectFileBlock + 2
            }
            else
            {
                // DEFAULT path: bit_file = floor(pages/2) rounded DOWN to a multiple of 9.
                // ALBIT 137526B-137532B: SAD ZIN SHR 1 (÷2) -> SAT 11 (=9) -> RDIV ST (÷9)
                // -> RMPY ST DA (×9). For 36945: 18472 -> 18468 = the real PACK-ONE
                // bit_file_ptr (plain pages/2 gives 18472, off by 4 — it skips the round).
                uint half = ndfsPages / 2;
                bitFileBlock = (half / 9) * 9;

                // APPROXIMATE (NOT statically kernel-derivable): the default-path object/user
                // base comes from the CRDIR scan loop (137173B-137352B) and has no clean
                // closed form — empirically bit+216 on SMD, bit+202 on floppy. We approximate
                // as bit + 216. Only bit-exactness of freshly-created images is affected; the
                // reader is pointer-driven and reads any placement correctly. To pin this
                // exactly, trace @CREATE-DIRECTORY (break 136741B) and dump the frame words
                // after ALBIT returns.
                objectFileBlock = bitFileBlock + 216;
                userFileBlock = objectFileBlock + 2;   // user = object + 2 (holds on every real image)
            }

            return new NdfsTemplateSpec
            {
                NdfsPages = ndfsPages,
                FileBlocks = fileBlocks,
                ObjectFileBlock = objectFileBlock,
                UserFileBlock = userFileBlock,
                BitFileBlock = bitFileBlock,
                UnreservedPages = ndfsPages,
                ControllerType = options.ControllerType,
                HasFlomon = isFloppy,
                ExtValid = !isFloppy && options.IncludeExtendedInfo,
                BootLoaderType = options.BootLoaderType ?? (isFloppy ? NdfsBootLoaderType.Floppy : NdfsBootLoaderType.Winchester),
                SpareBlocks = spareBlocks
            };
        }

        /// <summary>
        /// Calculates spare blocks based on controller type.
        /// </summary>
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
        /// Creates the actual disk image file from specification.
        /// </summary>
        private static void CreateImageFromSpec(string outputPath, NdfsImageCreationOptions options, NdfsTemplateSpec spec)
        {
            // Calculate file size
            long fileSize = spec.FileBlocks * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;

            // Create disk image (zero-filled)
            byte[] diskImage = new byte[fileSize];

            // BLANK IMAGE: a correctly-sized, entirely 0x00 file — no boot sector, no master block,
            // no extended info, no bitmap. Sometimes all you need is an empty device of the right
            // geometry (e.g. to let SINTRAN's own @CREATE-DIRECTORY format it, or to dd into).
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

            // Step 1: Write boot sector (optional — a valid NDFS volume need not be bootable).
            // The boot region is page-0 bytes 0..1999 (the extended-info block starts at 2000), so
            // the bootstrap may be up to BOOT_REGION_SIZE bytes — NOT capped at 1024, which would
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
                    // Use template boot sector
                    byte[] bootSector = NdfsBootSectors.GetBootSector(spec.BootLoaderType);
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
            // bitmap and index blocks it points at are pointless too — skip them together.
            if (options.IncludeMasterBlock)
            {
                // Step 3: Write Master Block
                WriteMasterBlock(diskImage, options.DirectoryName, spec);

                // Step 4: Create Bit File (allocation bitmap)
                CreateBitFile(diskImage, spec);

                // Step 5: Create Object File Index (initially empty)
                CreateObjectFile(diskImage, spec);

                // Step 6: Create User File Index (with SYSTEM user and custom users)
                CreateUserFile(diskImage, spec, options.Users ?? new System.Collections.Generic.List<NdfsUserCreationInfo>());
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
        /// Creates Bit File (allocation bitmap) and marks reserved blocks as allocated.
        /// </summary>
        private static void CreateBitFile(byte[] diskImage, NdfsTemplateSpec spec)
        {
            // Calculate bitmap size: 1 bit per page. The bitmap tracks the FULL physical device
            // (FileBlocks), not just the declared NDFS capacity — SINTRAN sizes the on-disk
            // bitmap to device geometry (VERIFIED: PACK-ONE's popcount is over all 38400 pages).
            // Whole 16-bit words: the bit file is word addressed (see BitFile.BitPosition).
            int bitmapBytes = (int)(((spec.FileBlocks + 7) / 8 + 1) & ~1u);
            byte[] bitmap = new byte[bitmapBytes];

            // The bit file is CONTIGUOUS and itself spans this many pages starting at BitFileBlock.
            int pageSize = NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            int bitmapPages = (bitmapBytes + pageSize - 1) / pageSize;

            // Mark reserved blocks as allocated.
            MarkBlockAllocated(bitmap, 0);                          // Master block (page 0)
            MarkBlockAllocated(bitmap, (int)spec.ObjectFileBlock);  // Object file index
            MarkBlockAllocated(bitmap, (int)spec.UserFileBlock);    // User file index
            // The bit file occupies bitmapPages consecutive pages — mark ALL of them used, not
            // just the first, or the allocator would later hand out the bitmap's own tail pages.
            for (int p = 0; p < bitmapPages; p++)
                MarkBlockAllocated(bitmap, (int)spec.BitFileBlock + p);

            // Write the (possibly multi-page) bitmap to disk starting at BitFileBlock.
            long bitFileOffset = spec.BitFileBlock * (long)pageSize;
            Array.Copy(bitmap, 0, diskImage, bitFileOffset, bitmapBytes);
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
        /// Creates Object File Index (initially empty).
        /// </summary>
        private static void CreateObjectFile(byte[] diskImage, NdfsTemplateSpec spec)
        {
            // Object file is initially empty (all zeros)
            // SINTRAN will populate as files are created
            // Block is already zeroed, just ensure it exists
            long objectFileOffset = spec.ObjectFileBlock * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;

            // Write empty object file index page (already zero-filled)
            // Nothing to do - diskImage is already zeroed
        }

        /// <summary>
        /// Creates User File Index with SYSTEM user and optional custom users.
        /// </summary>
        private static void CreateUserFile(byte[] diskImage, NdfsTemplateSpec spec, System.Collections.Generic.List<NdfsUserCreationInfo> users)
        {
            // User file contains SYSTEM user (ID=0) at minimum
            long userFileOffset = spec.UserFileBlock * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;

            // TODO: Implement proper user entry serialization
            // For now, just create an empty user file
            // SINTRAN will initialize users on first mount

            // Write empty user file index page (already zero-filled)
            // Full user management implementation requires UserFile.cs integration
        }
    }
}
