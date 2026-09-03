//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FFS.Core;
using RetroIO.Storage.Local.FFS.Structures;
using RetroIO.Storage.Local.Sun.Structures;
using RetroIO.Storage.Local.V7.Core;
using RetroIO.Storage.Local.V7.Structures;

namespace RetroIO.Tests
{
    /// <summary>
    /// Targeted regression tests for specific FFS/V7/Sun bugs that were found and fixed.
    ///
    /// Each test here is written to FAIL if its corresponding fix is reverted, and every one is
    /// self-contained — synthetic buffers and in-memory images only, no external disk images —
    /// so they run in the default suite rather than depending on an [Explicit] fixture or on
    /// files that may not exist on a given machine.
    /// </summary>
    [TestFixture]
    public class FfsRegressionTests
    {
        // =================================================================
        // BUG: di_size quad half order (NDIX val[0] vs SunOS val[1])
        // =================================================================

        /// <summary>
        /// Builds a minimal 128-byte FFS dinode with a given mode and the two halves of the
        /// ic_size quad set explicitly.
        /// </summary>
        private static byte[] BuildInode(ushort mode, uint sizeHighWord, uint sizeLowWord)
        {
            byte[] buffer = new byte[FfsConstants.INODE_SIZE];

            // di_mode at +0, big-endian
            buffer[0] = (byte)(mode >> 8);
            buffer[1] = (byte)(mode & 0xFF);

            // di_nlink at +2 = 1
            buffer[2] = 0x00;
            buffer[3] = 0x01;

            // ic_size quad: val[0] at +8, val[1] at +12, each big-endian
            WriteUInt32BE(buffer, 8, sizeHighWord);
            WriteUInt32BE(buffer, 12, sizeLowWord);

            return buffer;
        }

        private static void WriteUInt32BE(byte[] buffer, int offset, uint value)
        {
            buffer[offset + 0] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        [Test]
        [Category("FFS")]
        [Category("Regression")]
        public void FfsInode_NdixSizeLayout_ReadsFromVal0()
        {
            // NDIX h/inode.h:98 -> #define di_size di_ic.ic_size.val[0]
            // The size sits in the FIRST half (+8) and the second half (+12) is unused.
            // A plain big-endian 64-bit read at +8 would yield 68 << 32 and overflow downstream.
            byte[] buffer = BuildInode(FfsConstants.IFDIR | 0x01ED, sizeHighWord: 68, sizeLowWord: 0);

            FfsInode inode = FfsInode.Parse(buffer, 0);

            Assert.That(inode.di_size, Is.EqualTo(68UL),
                "NDIX stores di_size in ic_size.val[0]; reading it as a 64-bit quad gives 68<<32");
        }

        [Test]
        [Category("FFS")]
        [Category("Regression")]
        public void FfsInode_SunOsSizeLayout_ReadsFromVal1()
        {
            // SunOS / mc68000 -> i_size == ic_size.val[1]: high half zero, size in the SECOND
            // half (+12). This must keep working exactly as before the NDIX fix.
            byte[] buffer = BuildInode(FfsConstants.IFREG | 0x01A4, sizeHighWord: 0, sizeLowWord: 3072);

            FfsInode inode = FfsInode.Parse(buffer, 0);

            Assert.That(inode.di_size, Is.EqualTo(3072UL),
                "SunOS stores di_size in ic_size.val[1]");
        }

        [Test]
        [Category("FFS")]
        [Category("Regression")]
        public void FfsInode_ZeroSize_ReadsZero()
        {
            // Both halves zero - an empty file or a device node. Must not be mistaken for either
            // layout and must not throw.
            byte[] buffer = BuildInode(FfsConstants.IFCHR | 0x01B6, sizeHighWord: 0, sizeLowWord: 0);

            FfsInode inode = FfsInode.Parse(buffer, 0);

            Assert.That(inode.di_size, Is.EqualTo(0UL));
        }

        [Test]
        [Category("FFS")]
        [Category("Regression")]
        public void FfsInode_BothHalvesSet_ReadsAsTrue64BitQuad()
        {
            // Only reachable for a genuinely >4 GB file. Neither single-half rule applies, so the
            // value must be assembled as a real big-endian 64-bit quad.
            byte[] buffer = BuildInode(FfsConstants.IFREG | 0x01A4, sizeHighWord: 2, sizeLowWord: 5);

            FfsInode inode = FfsInode.Parse(buffer, 0);

            Assert.That(inode.di_size, Is.EqualTo((2UL << 32) | 5UL),
                "With both halves populated the field is a true 64-bit quad");
        }

        // =================================================================
        // BUG: directory lookup dropped the LAST entry when its d_reclen
        //      padded past the data bounded by di_size
        // =================================================================

        /// <summary>
        /// Builds one FFS directory entry at the given offset.
        /// </summary>
        private static int WriteDirEntry(byte[] buffer, int offset, uint ino, ushort reclen, string name)
        {
            WriteUInt32BE(buffer, offset, ino);
            buffer[offset + 4] = (byte)(reclen >> 8);
            buffer[offset + 5] = (byte)(reclen & 0xFF);
            buffer[offset + 6] = 0;                       // d_type (unused on 4.2/4.3BSD)
            buffer[offset + 7] = (byte)name.Length;       // d_namlen

            for (int i = 0; i < name.Length; i++)
            {
                buffer[offset + 8 + i] = (byte)name[i];
            }

            return offset + reclen;
        }

        /// <summary>
        /// Reproduces the exact NDIX root directory layout: five entries whose d_reclen values sum
        /// to a full 1024-byte block, but only the first 68 bytes (the packed sum of DIRSIZ) are
        /// within di_size and therefore within the data a reader actually holds.
        /// </summary>
        private static byte[] BuildNdixRootDirectoryData()
        {
            // 68 bytes = 12 ('.') + 12 ('..') + 20 ('lost+found') + 12 ('etc') + 12 ('dev')
            byte[] data = new byte[68];

            int offset = 0;
            offset = WriteDirEntry(data, offset, 2, 12, ".");
            offset = WriteDirEntry(data, offset, 2, 12, "..");
            offset = WriteDirEntry(data, offset, 3, 20, "lost+found");
            offset = WriteDirEntry(data, offset, 11, 12, "etc");

            // The final entry pads its reclen out to the end of the 1024-byte block, so it
            // overruns the 68 bytes we hold. This is legal FFS and normal on NDIX.
            WriteDirEntry(data, offset, 13, 968, "dev");

            return data;
        }

        [Test]
        [Category("FFS")]
        [Category("Regression")]
        public void FfsDirectoryEntry_ParseAll_ReturnsFinalEntryDespiteOverrunReclen()
        {
            byte[] data = BuildNdixRootDirectoryData();

            FfsDirectoryEntry[] entries = FfsDirectoryEntry.ParseAll(data, includeDots: true);

            Assert.That(FindName(entries, "lost+found"), Is.True);
            Assert.That(FindName(entries, "etc"), Is.True);
            Assert.That(FindName(entries, "dev"), Is.True,
                "The final entry must be returned even though its d_reclen (968) overruns the 68 bytes held");
        }

        [Test]
        [Category("FFS")]
        [Category("Regression")]
        public void FfsDirectory_LookupAndListing_AgreeOnFinalEntry()
        {
            // This is the invariant the bug broke: anything ParseAll lists must also be findable
            // by name. Before the fix, 'dev' was listed but FindEntry returned null for it, so
            // ListDirectory("/") showed it while ResolvePath("/dev") threw DirectoryNotFound.
            byte[] data = BuildNdixRootDirectoryData();

            FfsDirectoryEntry[] listed = FfsDirectoryEntry.ParseAll(data, includeDots: false);
            Assert.That(listed.Length, Is.GreaterThan(0));

            for (int i = 0; i < listed.Length; i++)
            {
                string name = listed[i].d_name;
                Assert.That(ScanForName(data, name), Is.True,
                    $"'{name}' is listed, so a name scan over the same bytes must also find it");
            }
        }

        [Test]
        [Category("FFS")]
        [Category("Regression")]
        public void FfsDirectoryReader_FindEntry_ResolvesFinalEntryWithOverrunReclen()
        {
            // Guards the REAL call site. The sibling test above exercises ParseAll and a local
            // scan, which is not enough: verified by mutation, restoring the old
            // "d_reclen > dirData.Length - offset" stop rule left every synthetic test green and
            // was caught only by the NDIX disk-image tests. Without the images present the
            // regression would have shipped.
            //
            // Builds a real one-directory FFS: a directory inode whose di_size is the packed 68
            // bytes, with data at a known fragment, then asks FfsDirectoryReader to find the LAST
            // entry ('dev') by name - the one the bug dropped.
            const int sectorSize = 512;
            const int fragSize = 1024;
            const int blockSize = 8192;
            const uint dirDataFragment = 8;
            const int totalSectors = 128;

            string tempFile = Path.Combine(Path.GetTempPath(), $"ffs-dir-{Guid.NewGuid():N}.img");
            try
            {
                byte[] image = new byte[totalSectors * sectorSize];

                byte[] dirData = BuildNdixRootDirectoryData();
                Array.Copy(dirData, 0, image, (int)(dirDataFragment * fragSize), dirData.Length);

                File.WriteAllBytes(tempFile, image);

                using var stream = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read);

                var descriptor = new BlockDeviceDescriptor
                {
                    Name = "ffs dir test",
                    BlockSize = sectorSize,
                    AddressMode = BlockAddressMode.LBA,
                    TotalBlocks = totalSectors,
                    ExpectedImageSize = image.Length
                };

                using var device = new GenericBlockDevice(descriptor);
                device.Mount(stream, readOnly: true);

                var superblock = new FfsSuperblock
                {
                    fs_bsize = blockSize,
                    fs_fsize = fragSize,
                    fs_frag = blockSize / fragSize,
                    fs_magic = FfsConstants.FS_MAGIC
                };

                var mapper = new FfsBlockMapper(device, superblock);
                var reader = new FfsDirectoryReader(mapper);

                // Directory inode: packed size 68, data at our fragment.
                byte[] inodeBytes = BuildInode(FfsConstants.IFDIR | 0x01ED, sizeHighWord: 68, sizeLowWord: 0);
                WriteUInt32BE(inodeBytes, 40, dirDataFragment);   // di_db[0]
                FfsInode dirInode = FfsInode.Parse(inodeBytes, 0);

                Assert.That(dirInode.IsDirectory, Is.True, "Test inode must be a directory");
                Assert.That(dirInode.di_size, Is.EqualTo(68UL), "Test inode must carry the packed size");

                // Earlier entries were never affected - they prove the fixture is sound.
                Assert.That(reader.FindEntry(dirInode, "lost+found"), Is.Not.Null,
                    "A mid-directory entry must resolve");
                Assert.That(reader.FindEntry(dirInode, "etc"), Is.Not.Null,
                    "A mid-directory entry must resolve");

                // The regression: last entry, reclen 968 overrunning the 68 bytes held.
                FfsDirectoryEntry? dev = reader.FindEntry(dirInode, "dev");
                Assert.That(dev, Is.Not.Null,
                    "FindEntry must resolve the LAST entry even though its d_reclen overruns di_size");
                Assert.That(dev!.d_ino, Is.EqualTo(13u), "Resolved entry should carry the right inode number");

                // Case-insensitive lookup shares the same stop rule and must behave identically.
                Assert.That(reader.FindEntryIgnoreCase(dirInode, "DEV"), Is.Not.Null,
                    "FindEntryIgnoreCase must resolve the last entry too");

                // And listing must agree with lookup - the invariant the bug violated.
                FfsDirectoryEntry[] listed = reader.ListEntries(dirInode, includeDots: false);
                for (int i = 0; i < listed.Length; i++)
                {
                    Assert.That(reader.FindEntry(dirInode, listed[i].d_name), Is.Not.Null,
                        $"'{listed[i].d_name}' is listed, so FindEntry must resolve it");
                }
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        /// <summary>
        /// Walks directory data the way FfsDirectoryReader.FindEntry does, applying the same
        /// stop rule, and reports whether the name was reached.
        /// </summary>
        private static bool ScanForName(byte[] data, string name)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                if (offset + FfsConstants.DIRENT_HEADER_SIZE > data.Length)
                {
                    break;
                }

                FfsDirectoryEntry entry = FfsDirectoryEntry.Parse(data, offset);

                // Must mirror the fixed rule: stop only on a malformed reclen, NOT on one that
                // merely overruns the buffer.
                if (entry.d_reclen == 0 || entry.d_reclen > 8192)
                {
                    break;
                }

                if (!entry.IsUnused && string.Equals(entry.d_name, name, StringComparison.Ordinal))
                {
                    return true;
                }

                offset += entry.d_reclen;
            }

            return false;
        }

        private static bool FindName(FfsDirectoryEntry[] entries, string name)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (string.Equals(entries[i].d_name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // =================================================================
        // BUG: full-block read ran past the end of the device when a file's
        //      tail was a short fragment run at the end of the filesystem
        // =================================================================

        [Test]
        [Category("FFS")]
        [Category("Regression")]
        public void FfsBlockMapper_TailFragmentAtEndOfDevice_DoesNotReadPastEnd()
        {
            // Recreates the /lib/fonts failure in miniature. The device is deliberately sized so
            // that reading a WHOLE fs block (8192 bytes = 16 sectors) starting from the last
            // fragment would run past the final sector. Before the fix that threw
            // ArgumentOutOfRangeException ("Block address N exceeds partition size").
            const int sectorSize = 512;
            const int fragSize = 1024;
            const int blockSize = 8192;

            // 20 sectors total; the last fragment starts at sector 18, so a full-block read from
            // there would want sectors 18..33 - well past the end.
            const int totalSectors = 20;

            string tempFile = Path.Combine(Path.GetTempPath(), $"ffs-tail-{Guid.NewGuid():N}.img");
            try
            {
                File.WriteAllBytes(tempFile, new byte[totalSectors * sectorSize]);

                using var stream = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read);

                var descriptor = new BlockDeviceDescriptor
                {
                    Name = "tail test",
                    BlockSize = sectorSize,
                    AddressMode = BlockAddressMode.LBA,
                    TotalBlocks = totalSectors,
                    ExpectedImageSize = totalSectors * sectorSize
                };

                using var device = new GenericBlockDevice(descriptor);
                device.Mount(stream, readOnly: true);

                var superblock = new FfsSuperblock
                {
                    fs_bsize = blockSize,
                    fs_fsize = fragSize,
                    fs_frag = blockSize / fragSize,
                    fs_magic = FfsConstants.FS_MAGIC
                };

                var mapper = new FfsBlockMapper(device, superblock);

                // Fragment 9 -> sector 18. A full-block read from here overruns the 20-sector device.
                byte[] buffer = new byte[blockSize];

                Assert.DoesNotThrow(() => mapper.ReadFilesystemBlock(9, buffer),
                    "Reading a block whose tail extends past the device must clamp, not throw");

                // Asking for only the bytes that exist must also work and must not throw.
                Assert.DoesNotThrow(() => mapper.ReadFilesystemBlock(9, buffer, fragSize),
                    "A short tail read must be satisfied without touching sectors the file does not own");
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        [Test]
        [Category("FFS")]
        [Category("Regression")]
        public void FfsBlockMapper_ShortRead_ZeroFillsUnreadSectors()
        {
            // The clamp must leave untouched sectors zeroed rather than stale, so a caller that
            // over-allocates its buffer never sees data belonging to another file.
            const int sectorSize = 512;
            const int fragSize = 1024;
            const int blockSize = 8192;
            const int totalSectors = 64;

            string tempFile = Path.Combine(Path.GetTempPath(), $"ffs-zero-{Guid.NewGuid():N}.img");
            try
            {
                // Fill the whole image with 0xAA so any sector we DO read is visibly non-zero.
                byte[] content = new byte[totalSectors * sectorSize];
                for (int i = 0; i < content.Length; i++)
                {
                    content[i] = 0xAA;
                }

                File.WriteAllBytes(tempFile, content);

                using var stream = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read);

                var descriptor = new BlockDeviceDescriptor
                {
                    Name = "zero fill test",
                    BlockSize = sectorSize,
                    AddressMode = BlockAddressMode.LBA,
                    TotalBlocks = totalSectors,
                    ExpectedImageSize = content.Length
                };

                using var device = new GenericBlockDevice(descriptor);
                device.Mount(stream, readOnly: true);

                var superblock = new FfsSuperblock
                {
                    fs_bsize = blockSize,
                    fs_fsize = fragSize,
                    fs_frag = blockSize / fragSize,
                    fs_magic = FfsConstants.FS_MAGIC
                };

                var mapper = new FfsBlockMapper(device, superblock);

                byte[] buffer = new byte[blockSize];
                mapper.ReadFilesystemBlock(0, buffer, bytesNeeded: fragSize);

                // First fragment (2 sectors) came from the image...
                Assert.That(buffer[0], Is.EqualTo(0xAA), "Requested bytes should be read");
                Assert.That(buffer[fragSize - 1], Is.EqualTo(0xAA), "Requested bytes should be read");

                // ...everything past what we asked for must be zero, not stale image data.
                Assert.That(buffer[fragSize], Is.EqualTo(0),
                    "Bytes past the requested length must be zero-filled, not read from disk");
                Assert.That(buffer[blockSize - 1], Is.EqualTo(0),
                    "Bytes past the requested length must be zero-filled, not read from disk");
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        // =================================================================
        // BUG: V7 indirect block pointers read as plain little-endian
        //      instead of PDP-11 mixed-endian
        // =================================================================

        [Test]
        [Category("V7")]
        [Category("Regression")]
        public void V7EndianHelper_ReadUInt32PDP_SwapsWordHalves()
        {
            // The observed failure: block 282 (0x0000011A) was read as 18481152 (0x011A0000) -
            // the same bytes with the two 16-bit halves transposed. PDP-11 daddr_t stores the
            // HIGH word first, each word little-endian.
            //
            // 0x0000011A in PDP order on disk: word0 = 0x0000, word1 = 0x011A
            //   word0 little-endian -> bytes 00 00
            //   word1 little-endian -> bytes 1A 01
            byte[] buffer = { 0x00, 0x00, 0x1A, 0x01 };

            uint pdp = V7EndianHelper.ReadUInt32PDP(buffer, 0);
            uint plainLittleEndian = BitConverter.ToUInt32(buffer, 0);

            Assert.That(pdp, Is.EqualTo(282u), "PDP-endian decode should yield block 282");
            Assert.That(plainLittleEndian, Is.EqualTo(18481152u),
                "A plain little-endian decode is what produced the bogus 18481152");
            Assert.That(pdp, Is.Not.EqualTo(plainLittleEndian),
                "The two decoders must differ - this is precisely why using the wrong one broke /dev");
        }

        [Test]
        [Category("V7")]
        [Category("Regression")]
        public void V7BlockMapper_IndirectBlockPointer_DecodedAsPdpEndian()
        {
            // Guards the CALL SITE, not just the helper. Testing V7EndianHelper alone does not
            // prove V7BlockMapper.ReadIndirectBlock actually uses it - verified by mutation:
            // swapping that one call back to BitConverter.ToUInt32 left every other test green.
            //
            // Layout: a 1024-byte-block V7 filesystem. Inode Addr[4] is the single-indirect
            // pointer; index 0 inside that block holds block number 282, PDP-encoded. MapFileBlock
            // for file block 4 (the first indirect one, since DIRECT_BLOCKS = 4) must return 282.
            const int sectorSize = 512;
            const int fsBlockSize = V7Constants.FS_BLOCK_SIZE;   // 1024
            const uint indirectBlock = 6;
            const uint targetBlock = 282;
            const int totalSectors = 64;

            string tempFile = Path.Combine(Path.GetTempPath(), $"v7-indirect-{Guid.NewGuid():N}.img");
            try
            {
                byte[] image = new byte[totalSectors * sectorSize];

                // Write the pointer table for the indirect block, PDP-encoded, at its block offset.
                int indirectByteOffset = (int)(indirectBlock * fsBlockSize);
                V7EndianHelper.WriteUInt32PDP(image, indirectByteOffset + 0, targetBlock);
                V7EndianHelper.WriteUInt32PDP(image, indirectByteOffset + 4, targetBlock + 1);

                File.WriteAllBytes(tempFile, image);

                using var stream = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read);

                var descriptor = new BlockDeviceDescriptor
                {
                    Name = "v7 indirect test",
                    BlockSize = sectorSize,
                    AddressMode = BlockAddressMode.LBA,
                    TotalBlocks = totalSectors,
                    ExpectedImageSize = image.Length
                };

                using var device = new GenericBlockDevice(descriptor);
                device.Mount(stream, readOnly: true);

                var mapper = new V7BlockMapper(device);

                var inode = new V7Inode();
                inode.Addr[4] = indirectBlock;   // single-indirect pointer

                uint mapped = mapper.MapFileBlock(inode, V7Constants.DIRECT_BLOCKS);
                uint mappedNext = mapper.MapFileBlock(inode, V7Constants.DIRECT_BLOCKS + 1);

                Assert.That(mapped, Is.EqualTo(targetBlock),
                    "Indirect pointers must be decoded PDP-endian; a plain little-endian read " +
                    "yields the word-swapped 0x011A0000 = 18481152");
                Assert.That(mappedNext, Is.EqualTo(targetBlock + 1),
                    "The second pointer in the indirect block must decode correctly too");
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        [Test]
        [Category("V7")]
        [Category("Regression")]
        public void V7EndianHelper_ReadUInt32PDP_RoundTripsThroughWrite()
        {
            // Whatever the mapper reads, the writer must produce - otherwise a written indirect
            // block would not be readable by the same code.
            byte[] buffer = new byte[4];

            uint[] values = { 0u, 1u, 282u, 65535u, 65536u, 340670u, uint.MaxValue };

            for (int i = 0; i < values.Length; i++)
            {
                V7EndianHelper.WriteUInt32PDP(buffer, 0, values[i]);
                uint readBack = V7EndianHelper.ReadUInt32PDP(buffer, 0);

                Assert.That(readBack, Is.EqualTo(values[i]),
                    $"PDP write/read round-trip failed for {values[i]}");
            }
        }

        // =================================================================
        // BUG: swap partitions counted as mount failures
        // =================================================================

        [Test]
        [Category("Sun")]
        [Category("Regression")]
        public void SunPartition_UntaggedLabel_TreatsBAsSwapByConvention()
        {
            // SunOS 3.x labels carry no VTOC tags at all - every tag reads 0 (verified on both
            // sun2-sunos-3.2.img and SUN2.img). For those we must fall back to the Sun convention
            // that 'b' is swap, or a whole-disk traversal counts swap as a mount failure.
            var untagged = new SunPartition { Tag = 0, NumSectors = 33558 };

            Assert.That(untagged.IsSwap('b'), Is.True, "Untagged partition 'b' is swap by convention");
            Assert.That(untagged.IsSwap('a'), Is.False, "'a' is root, not swap");
            Assert.That(untagged.IsSwap('g'), Is.False, "'g' is not swap");
            Assert.That(untagged.IsWholeDisk('c'), Is.True, "Untagged 'c' is the whole disk by convention");
            Assert.That(untagged.IsWholeDisk('a'), Is.False);
        }

        [Test]
        [Category("Sun")]
        [Category("Regression")]
        public void SunPartition_TaggedLabel_UsesVtocTagNotLetter()
        {
            // When the label DOES carry tags, the tag wins over the letter convention.
            var taggedSwap = new SunPartition { Tag = 3, NumSectors = 1000 };
            var taggedRoot = new SunPartition { Tag = 2, NumSectors = 1000 };
            var taggedBackup = new SunPartition { Tag = 5, NumSectors = 1000 };

            // Tag 3 is swap even when it is not partition 'b'.
            Assert.That(taggedSwap.IsSwap('d'), Is.True, "Tag 3 means swap regardless of letter");

            // And partition 'b' is NOT swap if the tag says it is root.
            Assert.That(taggedRoot.IsSwap('b'), Is.False,
                "An explicit tag must override the 'b = swap' convention");

            Assert.That(taggedBackup.IsWholeDisk('g'), Is.True, "Tag 5 is the backup/whole-disk slice");
            Assert.That(taggedSwap.IsWholeDisk('c'), Is.False,
                "An explicit tag must override the 'c = whole disk' convention");
        }

        // =================================================================
        // BUG: superblock locator must not accept a bogus base
        // =================================================================

        [Test]
        [Category("FFS")]
        [Category("Regression")]
        public void FfsSuperblockLocator_MagicWithoutValidSuperblock_IsRejected()
        {
            // Magic alone must never be enough. An image carrying the magic bytes at a plausible
            // position but no parseable superblock behind them must be rejected, or detection
            // would latch onto random data.
            byte[] image = new byte[512 * 1024];

            // Plant the magic exactly where a base-0 filesystem would put it, with nothing else.
            int magicPos = FfsConstants.SBLOCK_OFFSET + 1372;
            image[magicPos + 0] = 0x00;
            image[magicPos + 1] = 0x01;
            image[magicPos + 2] = 0x19;
            image[magicPos + 3] = 0x54;

            using var stream = new MemoryStream(image);

            bool found = FfsSuperblockLocator.TryLocate(stream, out long baseOffset, out FfsSuperblock? sb);

            Assert.That(found, Is.False,
                "Magic without a valid, size-consistent superblock must be rejected");
            Assert.That(baseOffset, Is.EqualTo(-1));
            Assert.That(sb, Is.Null);
        }

        [Test]
        [Category("FFS")]
        [Category("Regression")]
        public void FfsSuperblockLocator_TooSmallImage_ReturnsFalseWithoutThrowing()
        {
            // Smaller than SBLOCK_OFFSET + SBSIZE - there is nowhere for a superblock to live.
            using var stream = new MemoryStream(new byte[1024]);

            bool found = FfsSuperblockLocator.TryLocate(stream, out long baseOffset, out FfsSuperblock? sb);

            Assert.That(found, Is.False);
            Assert.That(baseOffset, Is.EqualTo(-1));
            Assert.That(sb, Is.Null);
        }
    }
}
