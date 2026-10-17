//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Creation;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Tests
{
    [TestFixture]
    public class NdfsImageCreationTests
    {
        private string _testOutputDir;

        [SetUp]
        public void Setup()
        {
            _testOutputDir = Path.Combine(Path.GetTempPath(), "RetroFS_ImageCreationTests");
            Directory.CreateDirectory(_testOutputDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testOutputDir))
            {
                Directory.Delete(_testOutputDir, true);
            }
        }

        private static void AssertMasterBlockPointers(MasterBlock masterBlock)
        {
            Assert.That(masterBlock.ObjectFilePointer, Is.Not.Null);
            Assert.That(masterBlock.UserFilePointer, Is.Not.Null);
            Assert.That(masterBlock.BitFilePointer, Is.Not.Null);
        }

        [Test]
        public void CreateFloppy360KB_Success()
        {
            // Arrange
            string outputPath = Path.Combine(_testOutputDir, "test_floppy360.img");

            // Act
            NdfsImageCreator.CreateImageFromTemplate(
                outputPath,
                NdfsImageTemplate.Floppy360KB,
                directoryName: "TEST-360KB",
                bootLoaderType: NdfsBootLoaderType.Floppy
            );

            // Assert
            Assert.That(File.Exists(outputPath), Is.True, "Image file should exist");

            FileInfo fileInfo = new FileInfo(outputPath);
            long expectedSize = 154 * 2048;  // 154 pages * 2KB
            Assert.That(fileInfo.Length, Is.EqualTo(expectedSize), $"File size should be {expectedSize} bytes");

            // Verify Master Block
            byte[] block0 = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            using (FileStream fs = File.OpenRead(outputPath))
            {
                fs.ReadExactly(block0, 0, NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);
            }

            MasterBlock masterBlock = MasterBlock.FromBytes(block0);
            Assert.That(masterBlock.DirectoryName, Is.EqualTo("TEST-360KB"), "Directory name should match");
            Assert.That(masterBlock.HasFlomon, Is.True, "Floppy should have FLOMON");
            Assert.That(masterBlock.ExtValid, Is.False, "Floppy should not have Extended Info");
            AssertMasterBlockPointers(masterBlock);

            Console.WriteLine($"✅ Created Floppy360KB: {outputPath}");
            Console.WriteLine($"   Directory: {masterBlock.DirectoryName}");
            Console.WriteLine($"   File Size: {fileInfo.Length:N0} bytes");
            Console.WriteLine($"   Object File: Block {masterBlock.ObjectFilePointer!.BlockID}");
            Console.WriteLine($"   User File: Block {masterBlock.UserFilePointer!.BlockID}");
            Console.WriteLine($"   Bit File: Block {masterBlock.BitFilePointer!.BlockID}");
        }

        [Test]
        public void CreateFloppy12MB_Success()
        {
            // Arrange
            string outputPath = Path.Combine(_testOutputDir, "test_floppy12mb.img");

            // Act
            NdfsImageCreator.CreateImageFromTemplate(
                outputPath,
                NdfsImageTemplate.Floppy12MB,
                directoryName: "TEST-1.2MB",
                bootLoaderType: NdfsBootLoaderType.Floppy
            );

            // Assert
            Assert.That(File.Exists(outputPath), Is.True);

            FileInfo fileInfo = new FileInfo(outputPath);
            long expectedSize = 616 * 2048;  // 616 pages * 2KB
            Assert.That(fileInfo.Length, Is.EqualTo(expectedSize));

            byte[] block0 = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            using (FileStream fs = File.OpenRead(outputPath))
            {
                fs.ReadExactly(block0, 0, NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);
            }

            MasterBlock masterBlock = MasterBlock.FromBytes(block0);
            Assert.That(masterBlock.DirectoryName, Is.EqualTo("TEST-1.2MB"));
            Assert.That(masterBlock.HasFlomon, Is.True);
            Assert.That(masterBlock.ExtValid, Is.False);

            Console.WriteLine($"✅ Created Floppy1.2MB: {outputPath}");
            Console.WriteLine($"   File Size: {fileInfo.Length:N0} bytes");
        }

        [Test]
        public void CreateSmd75MB_WithExtendedInfo_Success()
        {
            // Arrange
            string outputPath = Path.Combine(_testOutputDir, "test_smd75mb.img");
            ushort systemNumber = 100;

            var options = new NdfsImageCreationOptions
            {
                Template = NdfsImageTemplate.Smd75MB,
                DirectoryName = "SMD-TEST",
                SystemNumber = systemNumber,
                BootLoaderType = NdfsBootLoaderType.Smd,
                IncludeExtendedInfo = true
            };

            // Act
            NdfsImageCreator.CreateImage(outputPath, options);

            // Assert
            Assert.That(File.Exists(outputPath), Is.True);

            FileInfo fileInfo = new FileInfo(outputPath);
            long expectedSize = 38400 * 2048;  // 38,400 file blocks * 2KB
            Assert.That(fileInfo.Length, Is.EqualTo(expectedSize));

            byte[] block0 = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            using (FileStream fs = File.OpenRead(outputPath))
            {
                fs.ReadExactly(block0, 0, NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);
            }

            MasterBlock masterBlock = MasterBlock.FromBytes(block0);
            Assert.That(masterBlock.DirectoryName, Is.EqualTo("SMD-TEST"));
            Assert.That(masterBlock.HasFlomon, Is.False, "SMD should not have FLOMON");
            Assert.That(masterBlock.ExtValid, Is.True, "SMD should have Extended Info");
            Assert.That(masterBlock.ExtPagesAvailable, Is.EqualTo(36945), "Should have 36,945 NDFS pages");
            Assert.That(masterBlock.ExtLastSystemNumber, Is.EqualTo(systemNumber), "System number should match");

            // Verify checksum state
            Assert.That(masterBlock.ChecksumState, Is.EqualTo(ChecksumValidationState.CrcValid),
                "Extended Info checksum should be valid");

            // Verify BitFile placement: floor(pages/2) rounded DOWN to a multiple of 9
            // (kernel ALBIT 137526B). For 36945 that is 18468 = the real PACK-ONE bit_file_ptr.
            uint expectedBitFileBlock = (36945u / 2u / 9u) * 9u;  // = 18468
            Assert.That(masterBlock.BitFilePointer, Is.Not.Null);
            Assert.That(masterBlock.BitFilePointer!.BlockID, Is.EqualTo(expectedBitFileBlock),
                "BitFile should be at floor(pages/2) rounded to a multiple of 9");

            Console.WriteLine($"✅ Created SMD-75MB: {outputPath}");
            Console.WriteLine($"   Directory: {masterBlock.DirectoryName}");
            Console.WriteLine($"   File Size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");
            Console.WriteLine($"   NDFS Pages: {masterBlock.ExtPagesAvailable:N0}");
            Console.WriteLine($"   System Number: {masterBlock.ExtLastSystemNumber}");
            Console.WriteLine($"   Checksum State: {masterBlock.ChecksumState}");
            Console.WriteLine($"   BitFile Block: {masterBlock.BitFilePointer.BlockID} (50% = {expectedBitFileBlock})");
        }

        [Test]
        public void CreateWinchester74MB_Success()
        {
            // Arrange
            string outputPath = Path.Combine(_testOutputDir, "test_winchester74mb.img");

            var options = new NdfsImageCreationOptions
            {
                Template = NdfsImageTemplate.Winchester74MB,
                DirectoryName = "WD-TEST",
                SystemNumber = 102,
                BootLoaderType = NdfsBootLoaderType.Winchester,
                IncludeExtendedInfo = true
            };

            // Act
            NdfsImageCreator.CreateImage(outputPath, options);

            // Assert
            Assert.That(File.Exists(outputPath), Is.True);

            FileInfo fileInfo = new FileInfo(outputPath);
            // Real Winchester drive: a Micropolis 1325 (5.25" ST-506/MFM), measured across 7 real
            // images (1325.img, c3_2024_*.img, tingo_raw_debug.img):
            //   device 36,864 pages = 75,497,472 B = exactly 72.0 MiB
            //   declared capacity 36,396 (DOCUMENTED: ND-30.003.007 DEVICE-COPY "Pages to copy: 36396")
            //   spare 468 pages (bad-sector remap area)
            // Every real ND drive reserves a spare region, so the device is always LARGER than the
            // capacity. This assertion previously expected 36,360 pages — a truncated size SMALLER
            // than the declared capacity, so the image's last 36 pages did not even exist.
            long expectedSize = 36864L * 2048;
            Assert.That(fileInfo.Length, Is.EqualTo(expectedSize));

            byte[] block0 = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            using (FileStream fs = File.OpenRead(outputPath))
            {
                fs.ReadExactly(block0, 0, NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);
            }

            MasterBlock masterBlock = MasterBlock.FromBytes(block0);
            Assert.That(masterBlock.DirectoryName, Is.EqualTo("WD-TEST"));
            Assert.That(masterBlock.ExtValid, Is.True);
            Assert.That(masterBlock.ExtPagesAvailable, Is.EqualTo(36396));
            Assert.That(masterBlock.ChecksumState, Is.EqualTo(ChecksumValidationState.CrcValid));

            Console.WriteLine($"✅ Created Winchester-74MB: {outputPath}");
            Console.WriteLine($"   File Size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");
            Console.WriteLine($"   NDFS Pages: {masterBlock.ExtPagesAvailable:N0}");
        }

        [Test]
        public void CreateCustomSizeImage_Success()
        {
            // Arrange
            string outputPath = Path.Combine(_testOutputDir, "test_custom.img");
            uint customPages = 10000;

            var options = new NdfsImageCreationOptions
            {
                Template = NdfsImageTemplate.Custom,
                CustomNdfsPages = customPages,
                ControllerType = NdfsControllerType.Winchester,
                DirectoryName = "CUSTOM-10K",
                BootLoaderType = NdfsBootLoaderType.Winchester,
                IncludeExtendedInfo = true
            };

            // Act
            NdfsImageCreator.CreateImage(outputPath, options);

            // Assert
            Assert.That(File.Exists(outputPath), Is.True);

            byte[] block0 = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            using (FileStream fs = File.OpenRead(outputPath))
            {
                fs.ReadExactly(block0, 0, NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);
            }

            MasterBlock masterBlock = MasterBlock.FromBytes(block0);
            Assert.That(masterBlock.DirectoryName, Is.EqualTo("CUSTOM-10K"));
            Assert.That(masterBlock.ExtPagesAvailable, Is.EqualTo(customPages));
            Assert.That(masterBlock.ExtValid, Is.True);

            Console.WriteLine($"✅ Created Custom Image: {outputPath}");
            Console.WriteLine($"   NDFS Pages: {masterBlock.ExtPagesAvailable:N0}");
        }

        [Test]
        public void CreateImage_NoBootLoader_Success()
        {
            // Arrange
            string outputPath = Path.Combine(_testOutputDir, "test_no_boot.img");

            // Act
            NdfsImageCreator.CreateImageFromTemplate(
                outputPath,
                NdfsImageTemplate.Floppy360KB,
                directoryName: "NO-BOOT",
                bootLoaderType: NdfsBootLoaderType.None
            );

            // Assert
            Assert.That(File.Exists(outputPath), Is.True);

            byte[] block0 = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            using (FileStream fs = File.OpenRead(outputPath))
            {
                fs.ReadExactly(block0, 0, NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);
            }

            // Verify boot sector is zeroed
            bool bootSectorZeroed = true;
            for (int i = 0; i < 1024; i++)
            {
                if (block0[i] != 0)
                {
                    bootSectorZeroed = false;
                    break;
                }
            }

            Assert.That(bootSectorZeroed, Is.True, "Boot sector should be zeroed when BootLoaderType is None");

            MasterBlock masterBlock = MasterBlock.FromBytes(block0);
            Assert.That(masterBlock.DirectoryName, Is.EqualTo("NO-BOOT"));

            Console.WriteLine($"✅ Created image without boot loader: {outputPath}");
        }

        [Test]
        public void CreateImage_VerifyPointerPositioning_Floppy()
        {
            // Arrange
            string outputPath = Path.Combine(_testOutputDir, "test_pointers_floppy.img");

            // Act
            NdfsImageCreator.CreateImageFromTemplate(
                outputPath,
                NdfsImageTemplate.Floppy360KB,
                directoryName: "POINTERS"
            );

            // Assert
            byte[] block0 = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            using (FileStream fs = File.OpenRead(outputPath))
            {
                fs.ReadExactly(block0, 0, NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);
            }

            MasterBlock masterBlock = MasterBlock.FromBytes(block0);

            // Floppy pointer positions (near end)
            uint ndfsPages = 154;
            AssertMasterBlockPointers(masterBlock);
            Assert.That(masterBlock.ObjectFilePointer!.BlockID, Is.EqualTo(ndfsPages - 5), "ObjectFile should be at NdfsPages - 5");
            Assert.That(masterBlock.UserFilePointer!.BlockID, Is.EqualTo(ndfsPages - 3), "UserFile should be at NdfsPages - 3");
            Assert.That(masterBlock.BitFilePointer!.BlockID, Is.EqualTo(ndfsPages - 1), "BitFile should be at NdfsPages - 1");

            Console.WriteLine($"✅ Floppy pointer positioning verified");
            Console.WriteLine($"   Object: {masterBlock.ObjectFilePointer.BlockID} (expected {ndfsPages - 5})");
            Console.WriteLine($"   User: {masterBlock.UserFilePointer.BlockID} (expected {ndfsPages - 3})");
            Console.WriteLine($"   Bit: {masterBlock.BitFilePointer.BlockID} (expected {ndfsPages - 1})");
        }

        [Test]
        public void CreateImage_VerifyPointerPositioning_HardDisk()
        {
            // Arrange
            string outputPath = Path.Combine(_testOutputDir, "test_pointers_hdd.img");

            // Act
            NdfsImageCreator.CreateImageFromTemplate(
                outputPath,
                NdfsImageTemplate.Smd75MB,
                directoryName: "POINTERS-HD"
            );

            // Assert
            byte[] block0 = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            using (FileStream fs = File.OpenRead(outputPath))
            {
                fs.ReadExactly(block0, 0, NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);
            }

            MasterBlock masterBlock = MasterBlock.FromBytes(block0);

            // Hard disk pointer positions (Smd75MB template). BitFile = floor(pages/2)
            // rounded down to a multiple of 9 = 18468 (real PACK-ONE value, ALBIT 137526B).
            uint expectedBitFile = 18468;

            AssertMasterBlockPointers(masterBlock);
            Assert.That(masterBlock.BitFilePointer!.BlockID, Is.EqualTo(expectedBitFile),
                "BitFile should be at floor(pages/2) rounded to a multiple of 9");

            Console.WriteLine($"✅ Hard disk pointer positioning verified");
            Console.WriteLine($"   BitFile: {masterBlock.BitFilePointer.BlockID} (expected {expectedBitFile})");
            Console.WriteLine($"   Object: {masterBlock.ObjectFilePointer!.BlockID}");
            Console.WriteLine($"   User: {masterBlock.UserFilePointer!.BlockID}");
        }

        [Test]
        public void CreateImage_InvalidTemplate_ThrowsException()
        {
            // Arrange
            string outputPath = Path.Combine(_testOutputDir, "test_invalid.img");

            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
            {
                NdfsImageCreator.CreateImageFromTemplate(
                    outputPath,
                    (NdfsImageTemplate)999,
                    directoryName: "INVALID"
                );
            });
        }

        [Test]
        public void CreateImage_CustomWithoutNdfsPages_ThrowsException()
        {
            // Arrange
            string outputPath = Path.Combine(_testOutputDir, "test_custom_invalid.img");

            var options = new NdfsImageCreationOptions
            {
                Template = NdfsImageTemplate.Custom,
                CustomNdfsPages = 0,  // Invalid
                DirectoryName = "INVALID"
            };

            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
            {
                NdfsImageCreator.CreateImage(outputPath, options);
            });
        }

        [Test]
        public void CreateImage_VerifyBitFileAllocation()
        {
            // Arrange
            string outputPath = Path.Combine(_testOutputDir, "test_bitmap.img");

            // Act
            NdfsImageCreator.CreateImageFromTemplate(
                outputPath,
                NdfsImageTemplate.Floppy360KB,
                directoryName: "BITMAP-TEST"
            );

            // Assert - Read BitFile and verify reserved blocks are marked
            byte[] block0 = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            using (FileStream fs = File.OpenRead(outputPath))
            {
                fs.ReadExactly(block0, 0, NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);
            }

            MasterBlock masterBlock = MasterBlock.FromBytes(block0);
            Assert.That(masterBlock.BitFilePointer, Is.Not.Null);

            // Read BitFile
            byte[] bitFile = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            using (FileStream fs = File.OpenRead(outputPath))
            {
                fs.Seek(masterBlock.BitFilePointer!.BlockID * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE, SeekOrigin.Begin);
                fs.ReadExactly(bitFile, 0, NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);
            }

            // The bit file is an array of 16-bit BIG-ENDIAN words: page N is bit N%16 of word
            // N/16, counting from the LSB. So page 0 is bit 0 of the LOW byte of word 0 - byte
            // index 1, mask 0x01. Authority: ND-30.003.007 EN appendix F.2,
            // "PAGE = BLOCK*400B + WORD*20B + BIT" with 20B = 16.
            //
            // This assertion previously read bitFile[0] and cited "VERIFIED against PACK-ONE".
            // It was not verified: the check behind that claim was a popcount comparison, which
            // is invariant under byte-swapping and so could never have detected the error.
            bool block0Allocated = (bitFile[1] & 0x01) != 0;
            Assert.That(block0Allocated, Is.True, "Block 0 (Master Block) should be allocated");
            Assert.That(bitFile[0] & 0x01, Is.EqualTo(0),
                "page 0 must NOT be in the high byte - that is the discredited byte convention");

            Console.WriteLine($"✅ Bit File allocation verified");
            Console.WriteLine($"   Block 0 allocated: {block0Allocated}");
        }
    }
}
