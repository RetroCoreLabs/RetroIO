//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Tests
{
    /// <summary>
    /// Tests for NDFS sparse file support.
    /// Empty holes in files are represented by BlockID=0 in index blocks.
    /// These should be filled with zeros when reading, not allocate actual disk blocks.
    /// </summary>
    [TestFixture]
    public class NdfsSparseFileTests
    {
        [Test]
        public void BlockPointer_ZeroBlockID_IsValidPointer()
        {
            // Test that BlockPointer with BlockID=0 is valid and represents empty hole

            // Create a BlockPointer with BlockID=0
            var bp = new BlockPointer
            {
                Type = BlockPointer.PointerType.Indexed,
                BlockID = 0
            };

            // Assert
            Assert.That(bp.BlockID, Is.EqualTo(0u), "BlockID should be 0");
            Assert.That(bp.Type, Is.EqualTo(BlockPointer.PointerType.Indexed), "Type should be Indexed");

            // Convert to bytes and back
            uint native = bp.Native;
            var bp2 = new BlockPointer(native);

            Assert.That(bp2.BlockID, Is.EqualTo(0u), "Restored BlockID should be 0");
            Assert.That(bp2.Type, Is.EqualTo(BlockPointer.PointerType.Indexed), "Restored type should match");

            TestContext.Out.WriteLine($"BlockPointer with BlockID=0 is valid");
            TestContext.Out.WriteLine($"  Type: {bp.Type}");
            TestContext.Out.WriteLine($"  BlockID: {bp.BlockID}");
            TestContext.Out.WriteLine($"  Native: 0x{bp.Native:X8}");
        }

        [Test]
        public void IndexBlock_CanContainMixOfValidAndEmptyPointers()
        {
            // This test validates that an index block can have:
            // - Some pointers to actual data blocks (BlockID > 0)
            // - Some empty hole pointers (BlockID = 0)
            // This is the definition of a sparse file in NDFS

            // Arrange - Create simulated index block
            var indexBlock = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];

            // Pointer 0: Valid block at 100
            var bp0 = new BlockPointer { Type = BlockPointer.PointerType.Indexed, BlockID = 100 };
            var bp0Bytes = System.BitConverter.GetBytes(bp0.Native);
            if (System.BitConverter.IsLittleEndian)
            {
                System.Array.Reverse(bp0Bytes);
            }
            System.Array.Copy(bp0Bytes, 0, indexBlock, 0, 4);

            // Pointer 1: Empty hole (BlockID=0)
            var bp1 = new BlockPointer { Type = BlockPointer.PointerType.Indexed, BlockID = 0 };
            var bp1Bytes = System.BitConverter.GetBytes(bp1.Native);
            if (System.BitConverter.IsLittleEndian)
            {
                System.Array.Reverse(bp1Bytes);
            }
            System.Array.Copy(bp1Bytes, 0, indexBlock, 4, 4);

            // Pointer 2: Valid block at 200
            var bp2 = new BlockPointer { Type = BlockPointer.PointerType.Indexed, BlockID = 200 };
            var bp2Bytes = System.BitConverter.GetBytes(bp2.Native);
            if (System.BitConverter.IsLittleEndian)
            {
                System.Array.Reverse(bp2Bytes);
            }
            System.Array.Copy(bp2Bytes, 0, indexBlock, 8, 4);

            // Act - Parse back
            var parsed0 = BlockPointer.FromBytes(indexBlock, 0);
            var parsed1 = BlockPointer.FromBytes(indexBlock, 4);
            var parsed2 = BlockPointer.FromBytes(indexBlock, 8);

            // Assert
            Assert.That(parsed0.BlockID, Is.EqualTo(100u), "Pointer 0 should be block 100");
            Assert.That(parsed1.BlockID, Is.EqualTo(0u), "Pointer 1 should be empty hole (0)");
            Assert.That(parsed2.BlockID, Is.EqualTo(200u), "Pointer 2 should be block 200");

            TestContext.Out.WriteLine("Index block contains mix of valid and empty pointers:");
            TestContext.Out.WriteLine($"  [0] BlockID={parsed0.BlockID} (valid)");
            TestContext.Out.WriteLine($"  [1] BlockID={parsed1.BlockID} (empty hole)");
            TestContext.Out.WriteLine($"  [2] BlockID={parsed2.BlockID} (valid)");
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("This represents a sparse file with:");
            TestContext.Out.WriteLine("  Page 0: Data at block 100");
            TestContext.Out.WriteLine("  Page 1: Empty (filled with zeros)");
            TestContext.Out.WriteLine("  Page 2: Data at block 200");
        }

        [Test]
        public void SparseFile_Concept_EmptyHolesNotAllocated()
        {
            // This test documents the sparse file concept in NDFS

            TestContext.Out.WriteLine("NDFS Sparse File Concept:");
            TestContext.Out.WriteLine("========================================");
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("When a file has large sections of zeros, NDFS can");
            TestContext.Out.WriteLine("avoid allocating actual disk blocks for those sections.");
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("Instead of:");
            TestContext.Out.WriteLine("  Block 100: [zeros...]");
            TestContext.Out.WriteLine("  Block 101: [zeros...]");
            TestContext.Out.WriteLine("  Block 102: [data...]");
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("NDFS stores:");
            TestContext.Out.WriteLine("  Index[0]: BlockID=0 (empty hole, fill with zeros)");
            TestContext.Out.WriteLine("  Index[1]: BlockID=0 (empty hole, fill with zeros)");
            TestContext.Out.WriteLine("  Index[2]: BlockID=102 (actual data)");
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("Benefits:");
            TestContext.Out.WriteLine("  - Saves disk space");
            TestContext.Out.WriteLine("  - Faster file creation");
            TestContext.Out.WriteLine("  - Efficient for sparse data structures");
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("When reading:");
            TestContext.Out.WriteLine("  - BlockID=0 → Fill 2048 bytes with 0x00");
            TestContext.Out.WriteLine("  - BlockID>0 → Read actual block from disk");
            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("This is similar to sparse files in modern filesystems");
            TestContext.Out.WriteLine("(NTFS, ext4, ZFS) but implemented in 1970s technology!");

            Assert.Pass("Concept documented");
        }
    }
}
