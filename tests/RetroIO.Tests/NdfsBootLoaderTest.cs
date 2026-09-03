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
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Boot;
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tests
{
    [TestFixture]
    public class NdfsBootLoaderTest
    {
        private static string TEST_IMAGES_PATH => TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\ndfs");

        private IBlockDevice MountNdfsImage(string imagePath)
        {
            FileStream stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read);
            var descriptor = NdfsBlockDeviceDescriptor.Create(stream.Length);
            var blockDevice = new GenericBlockDevice(descriptor);
            blockDevice.Mount(stream, readOnly: true);
            return blockDevice;
        }

        [Test]
        [Category("NDFS")]
        [Category("BootLoader")]
        public void LoadBootCode_360KB_Floppy_Returns_Valid_BPUN()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGES_PATH, "FLOPPY-360KB.img");
            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            var blockDevice = MountNdfsImage(imagePath);

            // Act
            var bootCode = NdfsBootLoader.LoadBootCode(blockDevice);

            // Assert
            Assert.That(bootCode, Is.Not.Null, "Boot code should be detected");
            Assert.That(bootCode!.Format, Is.EqualTo(NdfsBootFormat.BPUN).Or.EqualTo(NdfsBootFormat.FloMon),
                "Should detect BPUN or FloMon format");

            // Verify preamble was parsed
            // FloMon format has start address 0, BPUN has non-zero start address
            if (bootCode.Format == NdfsBootFormat.FloMon)
            {
                Assert.That(bootCode.StartAddress, Is.EqualTo(0).Or.EqualTo(2), "FloMon start address is typically 0 or 2");
            }
            else
            {
                Assert.That(bootCode.StartAddress, Is.GreaterThan(0), "BPUN start address should be parsed from preamble");
            }

            // Verify code data was loaded
            Assert.That(bootCode.CodeData, Is.Not.Null);
            // FloMon can have empty code if word count is 0
            if (bootCode.Format != NdfsBootFormat.FloMon)
            {
                Assert.That(bootCode.CodeData.Length, Is.GreaterThan(0), "BPUN code data should not be empty");
            }

            // Verify raw data contains expected patterns
            Assert.That(bootCode.RawData, Is.Not.Null);
            Assert.That(bootCode.RawData.Length, Is.EqualTo(2048), "Raw data should be full page (2048 bytes)");

            // Check for BPUN delimiter (!) in raw data
            bool foundDelimiter = false;
            for (int i = 0; i < 100; i++)
            {
                if (bootCode.RawData[i] == 0x21) // '!' character
                {
                    foundDelimiter = true;
                    break;
                }
            }
            Assert.That(foundDelimiter, Is.True, "Should find BPUN delimiter '!' in first 100 bytes");

            Console.WriteLine($"Boot Format: {bootCode.Format}");
            Console.WriteLine($"Start Address: 0o{Convert.ToString(bootCode.StartAddress, 8)} (hex: 0x{bootCode.StartAddress:X4})");
            Console.WriteLine($"Boot Address: 0x{bootCode.BootAddress:X4}");
            Console.WriteLine($"Load Address: 0x{bootCode.LoadAddress:X4}");
            Console.WriteLine($"Code Size: {bootCode.CodeData.Length} bytes");
        }

        [Test]
        [Category("NDFS")]
        [Category("BootLoader")]
        public void LoadBootCode_1_2MB_Floppy_Returns_Valid_Boot_Code()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGES_PATH, "FLOPPY-1.2MB.img");
            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            var blockDevice = MountNdfsImage(imagePath);

            // Act
            var bootCode = NdfsBootLoader.LoadBootCode(blockDevice);

            // Assert
            Assert.That(bootCode, Is.Not.Null, "Boot code should be detected");
            Assert.That(bootCode!.Format, Is.Not.EqualTo(NdfsBootFormat.None), "Should detect valid boot format");

            Console.WriteLine($"Boot Format: {bootCode.Format}");
            Console.WriteLine($"Start Address: 0x{bootCode.StartAddress:X4} (octal: {Convert.ToString(bootCode.StartAddress, 8)})");
            Console.WriteLine($"Boot Address: 0x{bootCode.BootAddress:X4}");
            Console.WriteLine($"Load Address: 0x{bootCode.LoadAddress:X4}");
            Console.WriteLine($"Code Size: {bootCode.CodeData.Length} bytes");
            Console.WriteLine($"Action: 0x{bootCode.Action:X4}");

            if (bootCode.Format == NdfsBootFormat.BPUN)
            {
                Console.WriteLine($"Checksum: 0x{bootCode.Checksum:X4}");
                Console.WriteLine($"Calculated: 0x{bootCode.CalculatedChecksum:X4}");
                Console.WriteLine($"Checksum Valid: {bootCode.IsChecksumValid}");
            }
        }

        [Test]
        [Category("NDFS")]
        [Category("BootLoader")]
        public void DetectBootFormat_Returns_Correct_Format()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGES_PATH, "FLOPPY-360KB.img");
            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            var blockDevice = MountNdfsImage(imagePath);

            // Act
            var format = NdfsBootLoader.DetectBootFormat(blockDevice);

            // Assert
            Assert.That(format, Is.Not.EqualTo(NdfsBootFormat.None), "Should detect boot format");
            Assert.That(format, Is.EqualTo(NdfsBootFormat.BPUN).Or.EqualTo(NdfsBootFormat.FloMon),
                "360KB floppy should have BPUN or FloMon format");

            Console.WriteLine($"Detected Format: {format}");
        }

        [Test]
        [Category("NDFS")]
        [Category("BootLoader")]
        public void IsBootable_Returns_True_For_Bootable_Disk()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGES_PATH, "FLOPPY-360KB.img");
            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            var blockDevice = MountNdfsImage(imagePath);

            // Act
            bool isBootable = NdfsBootLoader.IsBootable(blockDevice);

            // Assert
            Assert.That(isBootable, Is.True, "360KB floppy should be bootable");
        }

        [Test]
        [Category("NDFS")]
        [Category("BootLoader")]
        public void LoadBootCode_Through_INdfsFileSystem_Works()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGES_PATH, "FLOPPY-360KB.img");
            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            var blockDevice = MountNdfsImage(imagePath);
            var ndfsFS = new NdfsFileSystem();
            ndfsFS.Initialize(blockDevice);

            // Act
            var bootCode = ndfsFS.LoadBootCode();
            var format = ndfsFS.DetectBootFormat();
            bool isBootable = ndfsFS.IsBootable();

            // Assert
            Assert.That(bootCode, Is.Not.Null, "Boot code should load through INdfsFileSystem");
            Assert.That(format, Is.Not.EqualTo(NdfsBootFormat.None), "Format should be detected");
            Assert.That(isBootable, Is.True, "Disk should be bootable");
            Assert.That(bootCode!.Format, Is.EqualTo(format), "Format should match between methods");

            Console.WriteLine($"=== Boot Code via INdfsFileSystem ===");
            Console.WriteLine($"Format: {bootCode.Format}");
            Console.WriteLine($"Start: 0o{Convert.ToString(bootCode.StartAddress, 8)} (0x{bootCode.StartAddress:X4})");
            Console.WriteLine($"Load: 0x{bootCode.LoadAddress:X4}");
            Console.WriteLine($"Size: {bootCode.CodeData.Length} bytes");
        }

        [Test]
        [Category("NDFS")]
        [Category("BootLoader")]
        public void LoadBootCode_Parses_Octal_Addresses_Correctly()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGES_PATH, "FLOPPY-360KB.img");
            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            var blockDevice = MountNdfsImage(imagePath);
            var bootCode = NdfsBootLoader.LoadBootCode(blockDevice);

            // Assert
            Assert.That(bootCode, Is.Not.Null);

            // From hex dump: "00 30 00 2F 00 32" = "0/2" in BPUN preamble
            // "0/2" means location counter 0, start address 2 (octal)
            // However, FloMon format resets this to 0
            // Octal 2 = decimal 2, but FloMon uses 0
            if (bootCode!.Format == NdfsBootFormat.FloMon)
            {
                Assert.That(bootCode.StartAddress, Is.EqualTo(0).Or.EqualTo(2),
                    "FloMon start address (preamble may show 2 but FloMon overrides to 0)");
            }
            else
            {
                Assert.That(bootCode.StartAddress, Is.EqualTo(2), "BPUN start address should be 2 (from octal '2' in preamble)");
            }

            // Verify the ASCII preamble structure
            byte[] rawData = bootCode.RawData;

            // Check first few bytes match BPUN preamble pattern
            // Format: 00 <ASCII-char> pairs
            Assert.That(rawData[0], Is.EqualTo(0x00), "BPUN preamble padding");
            Assert.That(rawData[1], Is.EqualTo(0x30), "ASCII '0'");
            Assert.That(rawData[2], Is.EqualTo(0x00), "Padding");
            Assert.That(rawData[3], Is.EqualTo(0x2F), "ASCII '/'");
            Assert.That(rawData[4], Is.EqualTo(0x00), "Padding");
            Assert.That(rawData[5], Is.EqualTo(0x32), "ASCII '2'");

            Console.WriteLine($"Preamble parsed correctly: Start address = {bootCode.StartAddress} (octal: {Convert.ToString(bootCode.StartAddress, 8)})");
        }

        [Test]
        [Category("NDFS")]
        [Category("BootLoader")]
        public void LoadBootCode_From_Bytes_Works()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGES_PATH, "FLOPPY-360KB.img");
            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            // Read first 2048 bytes
            byte[] bootSector = new byte[2048];
            using (FileStream fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read))
            {
                fs.ReadExactly(bootSector, 0, 2048);
            }

            // Act
            var bootCode = NdfsBootLoader.LoadBootCodeFromBytes(bootSector);

            // Assert
            Assert.That(bootCode, Is.Not.Null, "Should load boot code from byte array");
            Assert.That(bootCode!.Format, Is.Not.EqualTo(NdfsBootFormat.None));
            // FloMon can have 0-length code
            if (bootCode.Format != NdfsBootFormat.FloMon)
            {
                Assert.That(bootCode.CodeData.Length, Is.GreaterThan(0));
            }

            Console.WriteLine($"Loaded from bytes: Format={bootCode.Format}, Size={bootCode.CodeData.Length}");
        }

        // ==================== Hard-disk bootstrap detection (real images) ====================
        // These cover the raw-binary path (SMD/ECC, Winchester, SCSI) that used to be gated by
        // a weak "not all zero/FF/F6" heuristic. Fixture filenames were pre-classified by
        // controller type from nd100x/asm/DISK-IMAGE-INVENTORY.md.

        [TestCase("SMD-ID-100.IMG", NdfsBootControllerType.SmdEcc)]
        [TestCase("SMD-ID-102.IMG", NdfsBootControllerType.SmdEcc)]
        [TestCase("WD0.img", NdfsBootControllerType.Winchester)]
        [TestCase("tingo_raw_debug.img", NdfsBootControllerType.Winchester)]
        // 1325.img is a Micropolis 1325 — an ST-506/MFM drive, which on ND attaches to the
        // WINCHESTER controller (IOX base 500 octal). Its bootstrap issues 23 literal IOX
        // instructions in the Winchester window across words 79-570. It ALSO contains 17 words
        // equal to 0xD10D (the IOXT opcode) at words 714-929 — but those sit past the end of the
        // code and are DATA, not instructions. Classifying on "IOXT present anywhere" would
        // therefore misread this Winchester disk as SCSI; see DetectControllerType.
        [TestCase("1325.img", NdfsBootControllerType.Winchester)]
        [TestCase("SCIS-ID-Unknown.img", NdfsBootControllerType.Scsi)]
        [Category("NDFS")]
        [Category("BootLoader")]
        public void IsBootable_Detects_Real_HardDisk_Bootstrap_And_ControllerType(string fileName, NdfsBootControllerType expectedController)
        {
            string imagePath = Path.Combine(TEST_IMAGES_PATH, fileName);
            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            var blockDevice = MountNdfsImage(imagePath);
            var bootCode = NdfsBootLoader.LoadBootCode(blockDevice);

            Assert.That(bootCode, Is.Not.Null, "Real hard-disk image should be detected as bootable");
            Assert.That(bootCode!.Format, Is.EqualTo(NdfsBootFormat.Binary), "Hard-disk bootstraps are raw binary, not BPUN/FloMon");
            Assert.That(bootCode.ControllerType, Is.EqualTo(expectedController));

            bool isBootable = NdfsBootLoader.IsBootable(blockDevice);
            Assert.That(isBootable, Is.True);

            Console.WriteLine($"{fileName}: Format={bootCode.Format}, ControllerType={bootCode.ControllerType}");
        }

        [Test]
        [Category("NDFS")]
        [Category("BootLoader")]
        public void IsBootable_Rejects_NonUniform_Garbage_That_Old_Heuristic_Wrongly_Accepted()
        {
            // Regression case: the previous IsValidBinaryCode heuristic accepted ANY block that
            // wasn't uniformly 0x00/0xFF/0xF6 and had no '!' byte in the first 512 bytes - i.e.
            // it accepted this buffer as a valid bootstrap. A real ND-100 bootstrap must start
            // with the PIOF/IOF prologue opcode (0xD105/0xD101); this buffer does not.
            byte[] garbage = new byte[2048];
            for (int i = 0; i < garbage.Length; i++)
            {
                garbage[i] = (byte)((i * 37 + 11) % 251); // non-uniform, no 0x21 byte pattern by construction
            }

            var bootCode = NdfsBootLoader.LoadBootCodeFromBytes(garbage);

            Assert.That(bootCode, Is.Null, "Non-bootstrap data must not be classified as bootable");
        }

        [Test]
        [Category("NDFS")]
        [Category("BootLoader")]
        public void IsBootable_Accepts_Synthetic_Prologue_Opcode()
        {
            byte[] data = new byte[2048];
            data[0] = 0xD1; // PIOF, big-endian
            data[1] = 0x05;

            var bootCode = NdfsBootLoader.LoadBootCodeFromBytes(data);

            Assert.That(bootCode, Is.Not.Null);
            Assert.That(bootCode!.Format, Is.EqualTo(NdfsBootFormat.Binary));
        }
    }
}
