//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Boot;
using RetroIO.Storage.Ndfs.Creation;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.FileSystems;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Tests
{
    /// <summary>
    /// Tests for the NDFS behaviours reverse-engineered from the producing SINTRAN III
    /// kernel (the carved 006-S3FS segment) plus the real SMD0.IMG / PACK-ONE disk:
    /// the additive extended-info checksum, the flag-word "entered" interlock, and the
    /// high→low bitmap allocation order. Kernel anchors: WXDIR 37702B, CHDSI 37763B,
    /// REENB 40162B, TESTP 51355B, ALBIT 137517B.
    /// </summary>
    [TestFixture]
    public class NdfsKernelFindingsTests
    {
        // ---- Checksum: additive 16-bit sum of words 1751B-1757B (WXDIR / CHDSI) ----

        [Test]
        public void ExtChecksum_PackOne_IsAdditiveSum_0x10B7()
        {
            // PACK-ONE extended-info words (from SMD0.IMG 0x07D0):
            //   reserved1-3 = 0, flag = 0x8000, system# = 0x0066, pages-hi = 0x0000, pages-lo = 0x9051
            // Additive sum = 0x8000 + 0x0066 + 0x9051 = 0x110B7 -> truncated 16-bit = 0x10B7.
            ushort checksum = MasterBlock.ComputeExtChecksum(
                reserved3: 0, reserved2: 0, reserved1: 0,
                flagWord: 0x8000, systemNumber: 0x0066,
                pagesHi: 0x0000, pagesLo: 0x9051);

            Assert.That(checksum, Is.EqualTo((ushort)0x10B7));
        }

        [Test]
        public void ExtChecksum_RoundTrips_ThroughPageBytes()
        {
            // Build a page-0 with a PACK-ONE-like extended info block, parse it back, and
            // confirm the parser computes the same additive checksum and marks it valid.
            byte[] page = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];

            var mb = new MasterBlock
            {
                ExtFlagWord = 0x8000,
                ExtLastSystemNumber = 0x0066,
                ExtPagesAvailable = 0x00009051,
                ExtReserved1 = 0,
                ExtReserved2 = 0,
                ExtReserved3 = 0
            };
            mb.WriteExtendedInfoToBytes(page);

            // Stored checksum word (1750B / 0x07D0) must be 0x10B7, big-endian.
            ushort stored = (ushort)((page[0x07D0] << 8) | page[0x07D1]);
            Assert.That(stored, Is.EqualTo((ushort)0x10B7));

            // Give the master block a name + a pointer so FromBytes accepts it, then re-parse.
            var nameBytes = System.Text.Encoding.ASCII.GetBytes("PACK-ONE");
            Array.Copy(nameBytes, 0, page, NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET, nameBytes.Length);
            page[NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET + 8] = 0x27; // ' terminator
            page[NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET + 0x1B] = 0x08; // a bit-file pointer

            var parsed = MasterBlock.FromBytes(page);
            Assert.That(parsed.ExtCheckSum, Is.EqualTo((ushort)0x10B7));
            Assert.That(parsed.ExtCalculatedChecksum, Is.EqualTo((ushort)0x10B7));
            Assert.That(parsed.ChecksumState, Is.EqualTo(ChecksumValidationState.CrcValid));
            Assert.That(parsed.ExtValid, Is.True);
            Assert.That(parsed.IsDirectoryEntered, Is.True, "flag 0x8000 means entered");
        }

        // ---- Flag word bit 15 + ClearEnteredDirectory (CHDSI set / REENB clear) ----

        [Test]
        public void ClearEnteredDirectory_ClearsBit15_AndRecomputesChecksum()
        {
            string path = Path.Combine(Path.GetTempPath(), $"ndfs_entered_{Guid.NewGuid():N}.img");
            try
            {
                // Create an SMD image whose flag word already has bit 15 set ("left entered").
                NdfsImageCreator.CreateImage(path, new NdfsImageCreationOptions
                {
                    Template = NdfsImageTemplate.Smd75MB,
                    DirectoryName = "ENTERED",
                    SystemNumber = 102,
                    FlagWord = MasterBlock.EXT_FLAG_ENTERED,
                    IncludeExtendedInfo = true
                });

                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
                {
                    var driver = new NdfsDriver();
                    driver.Mount(stream, readOnly: false);
                    INdfsFileSystem ndfs = driver.GetFileSystem()!;

                    Assert.That(ndfs.GetMasterBlock().IsDirectoryEntered, Is.True,
                        "image was created with the entered bit set");

                    bool cleared = ndfs.ClearEnteredDirectory();
                    Assert.That(cleared, Is.True, "clearing a set interlock returns true");
                    Assert.That(ndfs.GetMasterBlock().IsDirectoryEntered, Is.False);

                    // Second call is a no-op now that the flag is clear.
                    Assert.That(ndfs.ClearEnteredDirectory(), Is.False);

                    driver.Dispose();
                }

                // Remount fresh: the cleared flag + recomputed additive checksum must persist and validate.
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    var driver = new NdfsDriver();
                    driver.Mount(stream, readOnly: true);
                    MasterBlock mb = driver.GetFileSystem()!.GetMasterBlock();

                    Assert.That(mb.IsDirectoryEntered, Is.False, "cleared flag survived remount");
                    Assert.That(mb.ChecksumState, Is.EqualTo(ChecksumValidationState.CrcValid),
                        "checksum recomputed additively and still valid after clearing the flag");
                    Assert.That(mb.ExtValid, Is.True);
                    driver.Dispose();
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // ---- Bitmap allocation direction: HIGH -> LOW (TESTP 51355B) ----

        [Test]
        public void Allocation_ScansHighToLow_BoundedByCapacityCeiling()
        {
            var bitFile = new BitFile();
            bitFile.Initialize(1000);        // device pages
            bitFile.AllocCeiling = 900;      // declared capacity < device size

            // First free block is the top of the capacity window, not the device top.
            Assert.That(bitFile.FindFirstFreeBlock(), Is.EqualTo(899u),
                "descending scan starts at capacity-1, never in the free-but-unreachable gap 900..999");

            // A contiguous run is placed at the highest addresses: pages 895..899 -> start 895.
            Assert.That(bitFile.FindFreeBlockRange(5), Is.EqualTo(895u));

            // The gap above the ceiling is never handed out even though it is free.
            for (uint p = 900; p < 1000; p++)
                Assert.That(bitFile.IsBlockUsed(p), Is.False, $"page {p} is free-but-unreachable");
        }

        [Test]
        public void Allocation_StopsAtBlock7Floor()
        {
            var bitFile = new BitFile();
            bitFile.Initialize(20);

            // Mark everything from the floor (7) up to the top as used; only system blocks 0-6 remain "free".
            for (uint i = 7; i < 20; i++)
                bitFile.MarkBlockUsed(i);

            // The allocator must NOT return a system block (0-6); it returns null instead.
            Assert.That(bitFile.FindFirstFreeBlock(), Is.Null,
                "blocks 0-6 are reserved and never allocated, even when everything else is full");
        }

        [Test]
        public void AllocCeiling_DefaultsToTotalPages()
        {
            var bitFile = new BitFile();
            bitFile.Initialize(500);

            // Until a caller supplies the declared capacity, the whole device is allocatable.
            Assert.That(bitFile.AllocCeiling, Is.EqualTo(500u));
            Assert.That(bitFile.FindFirstFreeBlock(), Is.EqualTo(499u),
                "with no capacity ceiling the top allocatable page is the device top");
        }

        // ---- Checksum invalidation: the removed "low-byte-only valid" state ----

        [Test]
        public void ExtChecksum_TamperedValue_IsInvalid_NotLowByteTolerated()
        {
            // Build a valid ext-info block, then corrupt the stored checksum's HIGH byte to zero
            // while leaving the low byte matching. Under the OLD "low-byte-only" heuristic this
            // would have been accepted; the kernel compares the full 16 bits, so it is INVALID.
            byte[] page = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            var mb = new MasterBlock
            {
                ExtFlagWord = 0x8000,
                ExtLastSystemNumber = 0x0066,
                ExtPagesAvailable = 0x00009051
            };
            mb.WriteExtendedInfoToBytes(page); // stores 0x10B7 big-endian at 0x07D0/0x07D1

            // Correct checksum is 0x10B7. Zero the high byte -> stored becomes 0x00B7 (low byte still 0xB7).
            page[0x07D0] = 0x00;

            // Give it a name + pointer so FromBytes parses it.
            var nameBytes = System.Text.Encoding.ASCII.GetBytes("PACK-ONE");
            Array.Copy(nameBytes, 0, page, NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET, nameBytes.Length);
            page[NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET + 8] = 0x27;
            page[NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET + 0x1B] = 0x08;

            var parsed = MasterBlock.FromBytes(page);
            Assert.That(parsed.ExtCheckSum, Is.EqualTo((ushort)0x00B7));
            Assert.That(parsed.ExtCalculatedChecksum, Is.EqualTo((ushort)0x10B7));
            Assert.That(parsed.ChecksumState, Is.EqualTo(ChecksumValidationState.CrcInvalid),
                "a low-byte-only match is NOT valid — the kernel compares the full 16 bits");
        }

        [Test]
        public void ChecksumValidationState_HasExactlyTwoStates()
        {
            // Guard against anyone re-introducing a low-byte-only (or other) tolerance state.
            Array values = Enum.GetValues(typeof(ChecksumValidationState));
            Assert.That(values.Length, Is.EqualTo(2),
                "only CrcValid and CrcInvalid should exist");
            Assert.That(Enum.IsDefined(typeof(ChecksumValidationState), "CrcValid"), Is.True);
            Assert.That(Enum.IsDefined(typeof(ChecksumValidationState), "CrcInvalid"), Is.True);
        }

        // ---- Flag word: entered-bit semantics + preservation of bits 0-14 ----

        [Test]
        public void IsDirectoryEntered_FalseWhenBit15Clear()
        {
            var mb = new MasterBlock { ExtFlagWord = 0x0000 };
            Assert.That(mb.IsDirectoryEntered, Is.False);
            mb.ExtFlagWord = 0x7FFF; // all bits except 15
            Assert.That(mb.IsDirectoryEntered, Is.False);
            mb.ExtFlagWord = 0x8000;
            Assert.That(mb.IsDirectoryEntered, Is.True);
        }

        [Test]
        public void ClearEnteredDirectory_NoOp_WhenFlagAlreadyClear()
        {
            string path = Path.Combine(Path.GetTempPath(), $"ndfs_clear_{Guid.NewGuid():N}.img");
            try
            {
                // FlagWord = 0 (default): not entered.
                NdfsImageCreator.CreateImage(path, new NdfsImageCreationOptions
                {
                    Template = NdfsImageTemplate.Smd75MB,
                    DirectoryName = "NOTENTERED",
                    SystemNumber = 100,
                    FlagWord = 0,
                    IncludeExtendedInfo = true
                });

                using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
                var driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                INdfsFileSystem ndfs = driver.GetFileSystem()!;

                Assert.That(ndfs.GetMasterBlock().IsDirectoryEntered, Is.False);
                Assert.That(ndfs.ClearEnteredDirectory(), Is.False,
                    "clearing an already-clear interlock is a no-op returning false");
                driver.Dispose();
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void ClearEnteredDirectory_PreservesLowerFlagBits()
        {
            string path = Path.Combine(Path.GetTempPath(), $"ndfs_flagbits_{Guid.NewGuid():N}.img");
            try
            {
                // Set bit 15 (entered) plus some lower bits that must survive the clear.
                ushort flag = (ushort)(MasterBlock.EXT_FLAG_ENTERED | 0x0005);
                NdfsImageCreator.CreateImage(path, new NdfsImageCreationOptions
                {
                    Template = NdfsImageTemplate.Smd75MB,
                    DirectoryName = "FLAGBITS",
                    SystemNumber = 100,
                    FlagWord = flag,
                    IncludeExtendedInfo = true
                });

                using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
                var driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                INdfsFileSystem ndfs = driver.GetFileSystem()!;

                Assert.That(ndfs.ClearEnteredDirectory(), Is.True);
                // Bit 15 cleared, bits 0-14 (0x0005) preserved verbatim.
                Assert.That(ndfs.GetMasterBlock().ExtFlagWord, Is.EqualTo((uint)0x0005),
                    "only bit 15 is cleared; bits 0-14 are preserved");
                driver.Dispose();
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // ---- Creator bitmap: LSB-first bit order + ALL bit-file pages marked (multi-page) ----

        [Test]
        public void CreatedImage_ReservedBlocks_MarkedUsedAfterMount_IncludingHighBlocks()
        {
            // A custom image large enough that its bitmap spans MORE THAN ONE page and its
            // object/user/bit metadata sits well above page-0's first 16384 blocks. This is the
            // regression guard for BOTH bugs: (1) the creator now marks every bit-file page and
            // uses LSB-first order; (2) LoadBitFile now reads the full contiguous bitmap, so
            // high reserved blocks are known used after mount (the old loader read only ~1 page
            // and treated everything above ~16383 as free).
            const uint pages = 40000;
            string path = Path.Combine(Path.GetTempPath(), $"ndfs_bigbitmap_{Guid.NewGuid():N}.img");
            try
            {
                NdfsImageCreator.CreateImage(path, new NdfsImageCreationOptions
                {
                    Template = NdfsImageTemplate.Custom,
                    CustomNdfsPages = pages,
                    ControllerType = NdfsControllerType.Smd,
                    DirectoryName = "BIGBITMAP",
                    IncludeExtendedInfo = true
                });

                using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read);
                var driver = new NdfsDriver();
                driver.Mount(stream, readOnly: true);
                INdfsFileSystem ndfs = driver.GetFileSystem()!;
                MasterBlock mb = ndfs.GetMasterBlock();

                // Default placement: bit = floor(pages/2) rounded to multiple of 9.
                uint expectedBit = (pages / 2u / 9u) * 9u; // 40000 -> 19998
                Assert.That(mb.BitFilePointer!.BlockID, Is.EqualTo(expectedBit));
                // The bit file is 3 pages (19998..20000), so the home word of the hint (19997 OR 15 =
                // 19999) is taken by it: the four pages are 19997 user data, 19996 user index, 19995
                // object data and 19994 object index.
                Assert.That(mb.ObjectFilePointer!.BlockID, Is.EqualTo(expectedBit - 4));
                Assert.That(mb.UserFilePointer!.BlockID, Is.EqualTo(expectedBit - 2));

                // Page 0 (master), object index, user index — all HIGH blocks — must read used.
                Assert.That(ndfs.IsBlockUsed(0), Is.True, "master block page 0");
                Assert.That(ndfs.IsBlockUsed(mb.ObjectFilePointer.BlockID), Is.True,
                    "object index (high block) must be marked used — loader read past page 0");
                Assert.That(ndfs.IsBlockUsed(mb.UserFilePointer.BlockID), Is.True,
                    "user index (high block) must be marked used");

                // The bit file itself spans multiple pages; every one must be marked used.
                int bitmapBytes = (int)((41576u + 7) / 8);            // FileBlocks for Smd 40000 pages
                int bitmapPages = (bitmapBytes + 2048 - 1) / 2048;    // > 1
                Assert.That(bitmapPages, Is.GreaterThan(1), "test needs a multi-page bitmap");
                for (int p = 0; p < bitmapPages; p++)
                    Assert.That(ndfs.IsBlockUsed(mb.BitFilePointer.BlockID + (uint)p), Is.True,
                        $"bit-file page {p} must be marked used");

                driver.Dispose();
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // ---- Rename directory (SetDirectoryName) ----

        [Test]
        public void SetDirectoryName_RenamesVolume_AndPersists_LeavingChecksumIntact()
        {
            string path = Path.Combine(Path.GetTempPath(), $"ndfs_rename_{Guid.NewGuid():N}.img");
            try
            {
                NdfsImageCreator.CreateImage(path, new NdfsImageCreationOptions
                {
                    Template = NdfsImageTemplate.Smd75MB,
                    DirectoryName = "OLDNAME",
                    SystemNumber = 100,
                    IncludeExtendedInfo = true
                });

                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
                {
                    var driver = new NdfsDriver();
                    driver.Mount(stream, readOnly: false);
                    INdfsFileSystem ndfs = driver.GetFileSystem()!;

                    Assert.That(ndfs.GetMasterBlock().DirectoryName, Is.EqualTo("OLDNAME"));
                    Assert.That(ndfs.SetDirectoryName("NEWVOL"), Is.True);
                    Assert.That(ndfs.GetMasterBlock().DirectoryName, Is.EqualTo("NEWVOL"));
                    driver.Dispose();
                }

                // Remount fresh: new name persisted, and the ext-info checksum (untouched by rename) still valid.
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    var driver = new NdfsDriver();
                    driver.Mount(stream, readOnly: true);
                    MasterBlock mb = driver.GetFileSystem()!.GetMasterBlock();

                    Assert.That(mb.DirectoryName, Is.EqualTo("NEWVOL"), "rename survived remount");
                    Assert.That(mb.ChecksumState, Is.EqualTo(ChecksumValidationState.CrcValid),
                        "renaming the directory does not disturb the extended-info checksum");
                    Assert.That(mb.ExtValid, Is.True);
                    driver.Dispose();
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void SetDirectoryName_Rejects_EmptyTooLongAndNonAscii()
        {
            string path = Path.Combine(Path.GetTempPath(), $"ndfs_rename_bad_{Guid.NewGuid():N}.img");
            try
            {
                NdfsImageCreator.CreateImage(path, new NdfsImageCreationOptions
                {
                    Template = NdfsImageTemplate.Smd75MB,
                    DirectoryName = "VALIDNAME",
                    IncludeExtendedInfo = true
                });

                using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
                var driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                INdfsFileSystem ndfs = driver.GetFileSystem()!;

                Assert.Throws<ArgumentException>(() => ndfs.SetDirectoryName(""));
                Assert.Throws<ArgumentException>(() => ndfs.SetDirectoryName("THIS-NAME-IS-WAY-TOO-LONG"));
                Assert.Throws<ArgumentException>(() => ndfs.SetDirectoryName("BAD\tTAB"));
                driver.Dispose();
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // ---- Templates must be internally consistent ----

        // Device/capacity/spare MEASURED from real ND disk images. Every real hard drive reserves a
        // spare (bad-sector remap) region, so the device is always LARGER than the capacity — and
        // the spare size differs per drive (SMD 1,455 / Winchester 468 / SCSI 3,620), so it can
        // never be computed as a percentage of capacity.
        [TestCase(NdfsImageTemplate.Floppy360KB, 154u, 154u)]
        [TestCase(NdfsImageTemplate.Floppy12MB, 616u, 616u)]
        [TestCase(NdfsImageTemplate.Smd75MB, 36945u, 38400u)]
        [TestCase(NdfsImageTemplate.Winchester74MB, 36396u, 36864u)]  // Micropolis 1325, ST-506/MFM
        [TestCase(NdfsImageTemplate.Scsi126MB, 61036u, 64656u)]
        public void Template_CreatesImage_MatchingRealDriveGeometry(
            NdfsImageTemplate template, uint expectedCapacity, uint expectedDevicePages)
        {
            // REGRESSION GUARD: Winchester74MB used to declare 36,396 pages of capacity while only
            // allocating 36,360 device blocks — the created file was 36 pages SHORTER than the size
            // it advertised, so any tail access ran off the end. A template must never declare more
            // capacity than the file it produces can hold.
            string path = Path.Combine(Path.GetTempPath(), $"ndfs_tpl_{Guid.NewGuid():N}.img");
            try
            {
                NdfsImageCreator.CreateImageFromTemplate(path, template, "TPLTEST");

                long lengthBytes = new FileInfo(path).Length;
                uint devicePages = (uint)(lengthBytes / NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);

                Assert.That(lengthBytes % NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE, Is.EqualTo(0),
                    "image size must be a whole number of 2048-byte pages");

                Assert.That(devicePages, Is.EqualTo(expectedDevicePages),
                    $"{template}: device size must match the real drive geometry measured from disk");

                Assert.That(devicePages, Is.GreaterThanOrEqualTo(expectedCapacity),
                    $"{template}: the file ({devicePages} pages) must be at least as large as the " +
                    $"capacity it declares ({expectedCapacity} pages)");

                // Every metadata pointer must land inside the file.
                byte[] page = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
                using (FileStream fs = File.OpenRead(path))
                {
                    fs.ReadExactly(page, 0, page.Length);
                }

                MasterBlock mb = MasterBlock.FromBytes(page);
                Assert.That(mb.ObjectFilePointer!.BlockID, Is.LessThan(devicePages), "object index inside the file");
                Assert.That(mb.UserFilePointer!.BlockID, Is.LessThan(devicePages), "user index inside the file");
                Assert.That(mb.BitFilePointer!.BlockID, Is.LessThan(devicePages), "bit file inside the file");

                // Hard disks carry a valid extended-info block declaring exactly this capacity.
                if (template != NdfsImageTemplate.Floppy360KB && template != NdfsImageTemplate.Floppy12MB)
                {
                    Assert.That(mb.ExtValid, Is.True, $"{template}: hard disk must have valid extended info");
                    Assert.That(mb.ExtPagesAvailable, Is.EqualTo(expectedCapacity),
                        $"{template}: declared capacity must match the documented page count");
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // ---- Boot region is 0..1999, not 1024 (full-length bootstrap) ----

        [Test]
        public void CreatedBootableSmdImage_HasFullLengthBootstrap_NotTruncatedAt1024()
        {
            // REGRESSION GUARD: the boot region is page-0 bytes 0..1999 (the extended-info block
            // starts at 2000), and a real SMD bootstrap has live code well past byte 1024. The old
            // 1024-byte cap cut the bootstrap in half, producing an image that would not boot.
            string path = Path.Combine(Path.GetTempPath(), $"ndfs_boot_{Guid.NewGuid():N}.img");
            try
            {
                NdfsImageCreator.CreateImage(path, new NdfsImageCreationOptions
                {
                    Template = NdfsImageTemplate.Smd75MB,
                    DirectoryName = "BOOTABLE",
                    BootLoaderType = NdfsBootLoaderType.Smd,
                    IncludeExtendedInfo = true
                });

                byte[] page = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
                using (FileStream fs = File.OpenRead(path))
                {
                    fs.ReadExactly(page, 0, page.Length);
                }

                // Word 0 is the PIOF prologue (octal 150405 = 0xD105) — the raw-bootstrap signature.
                Assert.That(page[0], Is.EqualTo(0xD1));
                Assert.That(page[1], Is.EqualTo(0x05));

                // There must be real boot code PAST byte 1024 — the exact region the old cap dropped.
                int nonZeroPast1024 = 0;
                for (int i = 1024; i < 2000; i++)
                {
                    if (page[i] != 0) nonZeroPast1024++;
                }
                Assert.That(nonZeroPast1024, Is.GreaterThan(100),
                    "the SMD bootstrap must extend past byte 1024 — it was being truncated");

                // The extended-info block at 2000 must be intact (boot write stops at 1999).
                MasterBlock mb = MasterBlock.FromBytes(page);
                Assert.That(mb.ExtValid, Is.True, "boot write must not corrupt the extended-info block");

                // Detected as a raw SMD/ECC bootstrap.
                var boot = NdfsBootLoader.LoadBootCodeFromBytes(page);
                Assert.That(boot, Is.Not.Null);
                Assert.That(boot!.Format, Is.EqualTo(NdfsBootFormat.Binary));
                Assert.That(boot.ControllerType, Is.EqualTo(NdfsBootControllerType.SmdEcc));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // ---- Blank / metadata-free image creation ----

        [Test]
        public void CreateBlankImage_ProducesCorrectlySizedAllZeroFile()
        {
            // Sometimes all you need is an empty device of the right geometry — no boot sector, no
            // name, no metadata. The file must be exactly the drive's size and entirely 0x00.
            string path = Path.Combine(Path.GetTempPath(), $"ndfs_blank_{Guid.NewGuid():N}.img");
            try
            {
                NdfsImageCreator.CreateImage(path, new NdfsImageCreationOptions
                {
                    Template = NdfsImageTemplate.Smd75MB,
                    DirectoryName = "IGNORED",     // must be ignored
                    SystemNumber = 102,            // must be ignored
                    IncludeExtendedInfo = true,    // must be overridden
                    CreateBlankImage = true
                });

                byte[] data = File.ReadAllBytes(path);

                // Correct SMD device geometry (38,400 pages incl. the spare region).
                Assert.That(data.Length, Is.EqualTo(38400L * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE));

                // Entirely zero — no boot code, no master block, no extended info, no bitmap.
                for (int i = 0; i < data.Length; i++)
                {
                    if (data[i] != 0)
                        Assert.Fail($"blank image must be all 0x00, but byte {i} = 0x{data[i]:X2}");
                }

                // And it must NOT be detected as a mountable NDFS volume (all-zero pointers).
                byte[] page = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
                Array.Copy(data, page, page.Length);
                float score = new NdfsDriverProvider().DetectConfidence(new DetectionHints
                {
                    FileExtension = ".img",
                    FileSize = data.Length,
                    HeaderBytes = page
                });
                Assert.That(score, Is.LessThan(0.5f),
                    "a blank image has nothing to mount and must not auto-mount");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void CreateImage_WithoutBootSector_IsStillAValidMountableVolume()
        {
            // A valid NDFS volume need not be bootable: skip the boot sector but keep the master
            // block, extended info and bitmap. It must still mount and detect.
            string path = Path.Combine(Path.GetTempPath(), $"ndfs_noboot_{Guid.NewGuid():N}.img");
            try
            {
                NdfsImageCreator.CreateImage(path, new NdfsImageCreationOptions
                {
                    Template = NdfsImageTemplate.Smd75MB,
                    DirectoryName = "NOBOOT",
                    SystemNumber = 102,
                    IncludeExtendedInfo = true,
                    IncludeBootSector = false
                });

                byte[] page = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
                using (FileStream fs = File.OpenRead(path))
                {
                    fs.ReadExactly(page, 0, page.Length);
                }

                // Boot area (bytes 0..1023) must be untouched zeros.
                for (int i = 0; i < 1024; i++)
                    Assert.That(page[i], Is.EqualTo(0), $"boot byte {i} should be zero");

                // ...but the filesystem itself is intact and valid.
                MasterBlock mb = MasterBlock.FromBytes(page);
                Assert.That(mb.DirectoryName, Is.EqualTo("NOBOOT"));
                Assert.That(mb.ExtValid, Is.True);
                Assert.That(mb.ChecksumState, Is.EqualTo(ChecksumValidationState.CrcValid));
                Assert.That(mb.BootFormat, Is.EqualTo(NdfsBootFormat.None), "not bootable");

                // Structure-only tier: still comfortably detected.
                float score = new NdfsDriverProvider().DetectConfidence(new DetectionHints
                {
                    FileExtension = ".img",
                    FileSize = new FileInfo(path).Length,
                    HeaderBytes = page
                });
                Assert.That(score, Is.GreaterThanOrEqualTo(0.8f),
                    "a valid non-bootable NDFS volume must still be detected");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // ---- Boot controller: IOXT (SCSI) must beat an earlier literal IOX ----

        [Test]
        public void BootController_EarlyWinchesterIox_BeatsLaterIoxtLookingDATA()
        {
            // REGRESSION GUARD, modelled on the real Micropolis 1325 (D:\ND\HDD\1325.img) — an
            // ST-506/MFM drive on the WINCHESTER controller. Its boot CODE issues literal IOX in
            // the Winchester window (octal 500-507) from word 79, and its later DATA region happens
            // to contain words equal to 0xD10D, the IOXT opcode.
            //
            // Page 0 is code followed by data and the scan cannot tell them apart, so an
            // "IOXT anywhere => SCSI" rule would misclassify this real Winchester disk as SCSI.
            // First-match-in-order must win.
            byte[] page = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];

            void Word(int wordIndex, ushort value)
            {
                page[wordIndex * 2] = (byte)(value >> 8);
                page[wordIndex * 2 + 1] = (byte)(value & 0xFF);
            }

            Word(0, 0xD105);                       // PIOF — boot prologue
            Word(79, (ushort)(0xE800 | 0x144));    // IOX 504 octal — real code, Winchester window
            Word(714, 0xD10D);                     // 0xD10D in the DATA region — NOT an instruction
            Word(800, 0xD10D);

            var boot = NdfsBootLoader.LoadBootCodeFromBytes(page);

            Assert.That(boot, Is.Not.Null);
            Assert.That(boot!.Format, Is.EqualTo(NdfsBootFormat.Binary));
            Assert.That(boot.ControllerType, Is.EqualTo(NdfsBootControllerType.Winchester),
                "an IOX in the Winchester window inside the code must beat IOXT-looking DATA later on");
        }

        [Test]
        public void BootController_IoxtWithNoControllerWindowIox_IsScsi()
        {
            // The real SCSI disk (scsi-1.img) has NO literal IOX in any controller window at all —
            // its IOXT instructions begin early, inside the code. That is the genuine SCSI shape.
            byte[] page = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];

            void Word(int wordIndex, ushort value)
            {
                page[wordIndex * 2] = (byte)(value >> 8);
                page[wordIndex * 2 + 1] = (byte)(value & 0xFF);
            }

            Word(0, 0xD105);                       // PIOF
            Word(290, 0xD10D);                     // IOXT in the code, no controller-window IOX

            var boot = NdfsBootLoader.LoadBootCodeFromBytes(page);

            Assert.That(boot, Is.Not.Null);
            Assert.That(boot!.ControllerType, Is.EqualTo(NdfsBootControllerType.Scsi));
        }

        [Test]
        public void BootController_NoIoxt_FallsBackToLiteralIoxWindow()
        {
            // Without IOXT, the literal IOX device window classifies the controller.
            byte[] page = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];

            void Word(int wordIndex, ushort value)
            {
                page[wordIndex * 2] = (byte)(value >> 8);
                page[wordIndex * 2 + 1] = (byte)(value & 0xFF);
            }

            Word(0, 0xD105);                       // PIOF
            Word(50, (ushort)(0xE800 | 0x364));    // IOX 1544 octal — SMD/ECC window (as on SMD0.IMG)

            var boot = NdfsBootLoader.LoadBootCodeFromBytes(page);

            Assert.That(boot, Is.Not.Null);
            Assert.That(boot!.ControllerType, Is.EqualTo(NdfsBootControllerType.SmdEcc));
        }

        // ---- Detection hardening: reject look-alikes, accept genuine NDFS ----

        /// <summary>
        /// Builds a page-0 that satisfies the structural core (3 in-bounds pointers, user = object + 2).
        /// </summary>
        private static byte[] BuildStructuralPage(uint objectBlock, uint bitBlock, string name)
        {
            byte[] page = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            int mb = NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET;

            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
            Array.Copy(nameBytes, 0, page, mb, nameBytes.Length);
            page[mb + nameBytes.Length] = 0x27;

            void WritePtr(int off, uint blockId, uint type)
            {
                uint native = (type << 30) | (blockId & 0x3FFFFFFF);
                page[off + 0] = (byte)(native >> 24);
                page[off + 1] = (byte)(native >> 16);
                page[off + 2] = (byte)(native >> 8);
                page[off + 3] = (byte)native;
            }

            WritePtr(mb + 0x10, objectBlock, 1);      // object: Indexed
            WritePtr(mb + 0x14, objectBlock + 2, 1);  // user = object + 2 (the invariant)
            WritePtr(mb + 0x18, bitBlock, 0);         // bit: Contiguous
            return page;
        }

        [Test]
        public void Detection_RejectsNonNdfsFile_ThatMerelyLooksPointerish()
        {
            // A 2048-aligned file whose master-block area holds garbage that the OLD IsValid()
            // would have accepted (printable name OR one non-zero pointer word) => used to score 0.90.
            byte[] page = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            int mb = NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET;

            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes("HELLO");
            Array.Copy(nameBytes, 0, page, mb, nameBytes.Length);

            // A pointer-shaped word far outside any plausible device (blockId ~708 million).
            page[mb + 0x10] = 0x2A; page[mb + 0x11] = 0x3F; page[mb + 0x12] = 0x1C; page[mb + 0x13] = 0x55;

            var provider = new NdfsDriverProvider();
            float score = provider.DetectConfidence(new DetectionHints
            {
                FileExtension = ".img",
                FileSize = 1024L * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE, // 1024-page device
                HeaderBytes = page
            });

            Assert.That(score, Is.LessThan(0.5f),
                "out-of-bounds pointers and no structure must NOT reach the auto-mount threshold");
        }

        [Test]
        public void Detection_RejectsWhenUserPointerBreaksObjectPlusTwoInvariant()
        {
            // All three pointers in-bounds, but user != object + 2 -> not a real directory label.
            byte[] page = BuildStructuralPage(objectBlock: 500, bitBlock: 250, name: "FAKE");
            int mb = NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET;
            // Rewrite the user pointer to object + 7 (breaks the invariant).
            uint native = (1u << 30) | 507u;
            page[mb + 0x14] = (byte)(native >> 24);
            page[mb + 0x15] = (byte)(native >> 16);
            page[mb + 0x16] = (byte)(native >> 8);
            page[mb + 0x17] = (byte)native;

            var provider = new NdfsDriverProvider();
            float score = provider.DetectConfidence(new DetectionHints
            {
                FileExtension = ".img",
                FileSize = 1024L * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE,
                HeaderBytes = page
            });

            Assert.That(score, Is.LessThan(0.5f), "user must sit exactly 2 blocks above object");
        }

        [Test]
        public void Detection_RejectsBlankImage_WithAllZeroPointers()
        {
            // A blank/unformatted floppy (all three pointers zero) — mirrors the real disk22.img,
            // the ONE image out of 52 that has no pointers. It must not be detected as NDFS.
            byte[] page = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];

            var provider = new NdfsDriverProvider();
            float score = provider.DetectConfidence(new DetectionHints
            {
                FileExtension = ".img",
                FileSize = 154L * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE,
                HeaderBytes = page
            });

            Assert.That(score, Is.LessThan(0.5f), "a blank image with no pointers is not a mountable NDFS volume");
        }

        [Test]
        public void Detection_RejectsWrongPointerTypes()
        {
            // Right ids and the user=object+2 invariant, but the pointer TYPES are wrong.
            // Verified across 52 real images: object/user are always Indexed(01) and bit is always
            // Contiguous(00). Here we make them all Contiguous, as the blank-ish look-alikes do.
            byte[] page = BuildStructuralPage(objectBlock: 500, bitBlock: 250, name: "WRONGTYPE");
            int mb = NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET;

            // Rewrite object + user as Contiguous (type 00) instead of Indexed (01).
            void WritePtr(int off, uint blockId, uint type)
            {
                uint native = (type << 30) | (blockId & 0x3FFFFFFF);
                page[off + 0] = (byte)(native >> 24);
                page[off + 1] = (byte)(native >> 16);
                page[off + 2] = (byte)(native >> 8);
                page[off + 3] = (byte)native;
            }
            WritePtr(mb + 0x10, 500, 0);
            WritePtr(mb + 0x14, 502, 0);

            var provider = new NdfsDriverProvider();
            float score = provider.DetectConfidence(new DetectionHints
            {
                FileExtension = ".img",
                FileSize = 1024L * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE,
                HeaderBytes = page
            });

            Assert.That(score, Is.LessThan(0.5f),
                "object/user must be Indexed and bit Contiguous — the shape every real image has");
        }

        [Test]
        public void Detection_BootlessCheckslessFloppy_StillScoresAboveThreshold()
        {
            // 20 of the 52 real ND floppies have NO boot code AND no valid extended-info checksum.
            // They are genuine NDFS volumes carried entirely by the structural core, and must still
            // beat competing drivers (e.g. FAT12) for the same 1.2MB floppy.
            // 616-page floppy, default-path placement: bit = 9*floor(floor(616/2)/9) = 306,
            // object = bit + 202 = 508, user = 510 — exactly what the real images show.
            byte[] page = BuildStructuralPage(objectBlock: 508, bitBlock: 306, name: "OSM");

            var mbParsed = MasterBlock.FromBytes(page);
            Assert.That(mbParsed.ExtValid, Is.False, "no extended info");
            Assert.That(mbParsed.BootFormat, Is.EqualTo(NdfsBootFormat.None), "no boot code");

            var provider = new NdfsDriverProvider();
            float score = provider.DetectConfidence(new DetectionHints
            {
                FileExtension = ".img",
                FileSize = 616L * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE,
                HeaderBytes = page
            });

            Assert.That(score, Is.GreaterThanOrEqualTo(0.8f),
                "a boot-less, checksum-less real NDFS floppy must still score decisively");
        }

        [Test]
        public void Detection_ExtensionAlone_DoesNotAutoMount()
        {
            // Right extension, but the page is empty (no pointers at all).
            byte[] page = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];

            var provider = new NdfsDriverProvider();
            float score = provider.DetectConfidence(new DetectionHints
            {
                FileExtension = ".ndfs",
                FileSize = 1024L * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE,
                HeaderBytes = page
            });

            Assert.That(score, Is.LessThan(0.5f),
                "a renamed non-NDFS file must not auto-mount on its extension alone");
        }

        [Test]
        public void Detection_GenuineHardDiskImage_ScoresNearCertain()
        {
            // A real created SMD image: valid additive checksum, reserved words 0, all pointers sane.
            string path = Path.Combine(Path.GetTempPath(), $"ndfs_detect_{Guid.NewGuid():N}.img");
            try
            {
                NdfsImageCreator.CreateImage(path, new NdfsImageCreationOptions
                {
                    Template = NdfsImageTemplate.Smd75MB,
                    DirectoryName = "DETECT",
                    SystemNumber = 102,
                    IncludeExtendedInfo = true
                });

                byte[] page = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
                using (FileStream fs = File.OpenRead(path))
                {
                    fs.ReadExactly(page, 0, page.Length);
                }

                var provider = new NdfsDriverProvider();
                float score = provider.DetectConfidence(new DetectionHints
                {
                    FileExtension = ".img",
                    FileSize = new FileInfo(path).Length,
                    HeaderBytes = page
                });

                Assert.That(score, Is.GreaterThanOrEqualTo(0.95f),
                    "a genuine NDFS hard-disk image with a valid additive checksum must score near-certain");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void Detection_FlomonFloppy_StillDetected_DespiteNoExtendedInfo()
        {
            // FLOMON floppies legitimately have NO valid extended-info checksum — they must still
            // be detected (via the boot signature), not penalised for the missing checksum.
            string path = Path.Combine(Path.GetTempPath(), $"ndfs_flop_{Guid.NewGuid():N}.img");
            try
            {
                NdfsImageCreator.CreateImageFromTemplate(path, NdfsImageTemplate.Floppy12MB, "FLOPPYVOL");

                byte[] page = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
                using (FileStream fs = File.OpenRead(path))
                {
                    fs.ReadExactly(page, 0, page.Length);
                }

                var mbParsed = MasterBlock.FromBytes(page);
                Assert.That(mbParsed.ExtValid, Is.False, "floppy has no valid extended info (by design)");

                var provider = new NdfsDriverProvider();
                float score = provider.DetectConfidence(new DetectionHints
                {
                    FileExtension = ".img",
                    FileSize = new FileInfo(path).Length,
                    HeaderBytes = page
                });

                Assert.That(score, Is.GreaterThanOrEqualTo(0.5f),
                    "a genuine FLOMON floppy must still be detected without an extended-info checksum");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // ---- Placement override path (BitFileAddress supplied) ----

        [Test]
        public void Placement_OverridePath_PacksMetadataBelowBitAddress()
        {
            const uint pages = 5000;
            const uint bitAddr = 4990;
            string path = Path.Combine(Path.GetTempPath(), $"ndfs_override_{Guid.NewGuid():N}.img");
            try
            {
                NdfsImageCreator.CreateImage(path, new NdfsImageCreationOptions
                {
                    Template = NdfsImageTemplate.Custom,
                    CustomNdfsPages = pages,
                    ControllerType = NdfsControllerType.Smd,
                    BitFileAddress = bitAddr,      // OVERRIDE path
                    DirectoryName = "OVERRIDE",
                    IncludeExtendedInfo = true
                });

                using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read);
                var driver = new NdfsDriver();
                driver.Mount(stream, readOnly: true);
                MasterBlock mb = driver.GetFileSystem()!.GetMasterBlock();

                // Typed address: bit = BA. The four pages are the first free pages going down from
                // (BA - 1) OR 15 = 4991 (SINTRAN routine RSBPG): 4991 user data, 4989 user index (4990
                // is the bit file), 4988 object data, 4987 object index. BA minus 4 and minus 2 hold only
                // when the bit file ends the home word (the answer -1 case).
                Assert.That(mb.BitFilePointer!.BlockID, Is.EqualTo(bitAddr));
                Assert.That(mb.ObjectFilePointer!.BlockID, Is.EqualTo(4987u));
                Assert.That(mb.UserFilePointer!.BlockID, Is.EqualTo(4989u));
                driver.Dispose();
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
