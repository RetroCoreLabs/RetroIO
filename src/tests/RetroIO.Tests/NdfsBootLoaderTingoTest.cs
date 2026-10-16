//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
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
    public class NdfsBootLoaderTingoTest
    {
        private static string TEST_IMAGE => TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\ndfs\tingo_raw_debug.img");

        [Test]
        [Category("NDFS")]
        [Category("BootLoader")]
        [Category("Tingo")]
        [Ignore("Test image tingo_raw_debug.img has changed - does not contain expected BPUN pattern")]
        public void LoadBootCode_Tingo_Returns_Valid_BPUN()
        {
            // Arrange
            if (!File.Exists(TEST_IMAGE))
            {
                Assert.Ignore($"Test image not found: {TEST_IMAGE}");
                return;
            }

            using (FileStream stream = new FileStream(TEST_IMAGE, FileMode.Open, FileAccess.Read))
            {
                var descriptor = NdfsBlockDeviceDescriptor.Create(stream.Length);
                var blockDevice = new GenericBlockDevice(descriptor);
                blockDevice.Mount(stream, readOnly: true);

                var fileSystem = new NdfsFileSystem();
                fileSystem.Initialize(blockDevice);

                // Act
                var bootCode = NdfsBootLoader.LoadBootCode(blockDevice);

                // Assert
                Assert.That(bootCode, Is.Not.Null, "Boot code should be detected");
                Assert.That(bootCode!.Format, Is.EqualTo(NdfsBootFormat.BPUN), "Should be BPUN format");

                // Verify BPUN structure
                Console.WriteLine($"\n=== tingo_raw_debug.img Boot Analysis ===");
                Console.WriteLine($"Format: {bootCode.Format}");
                Console.WriteLine($"Start Address: 0x{bootCode.StartAddress:X4}");
                Console.WriteLine($"Boot Address: 0x{bootCode.BootAddress:X4}");
                Console.WriteLine($"Load Address: 0x{bootCode.LoadAddress:X4}");
                Console.WriteLine($"Code Size: {bootCode.CodeData.Length} bytes");
                Console.WriteLine($"Action: 0x{bootCode.Action:X4}");
                Console.WriteLine($"Checksum: 0x{bootCode.Checksum:X4}");
                Console.WriteLine($"Calculated: 0x{bootCode.CalculatedChecksum:X4}");
                Console.WriteLine($"Checksum Valid: {bootCode.IsChecksumValid}");

                // Expected values from PowerShell analysis
                Assert.That(bootCode.LoadAddress, Is.EqualTo(0x00A8), "Load address should be 0x00A8");
                Assert.That(bootCode.CodeData.Length, Is.EqualTo(492), "Code should be 246 words = 492 bytes");
                Assert.That(bootCode.Checksum, Is.EqualTo(0x00E8), "Checksum should be 0x00E8");
                Assert.That(bootCode.Action, Is.EqualTo(0x006A), "Action should be 0x006A (remain in OPCOM)");

                // Show first 8 words of boot code
                Console.WriteLine("\nFirst 8 words of boot code:");
                for (int i = 0; i < Math.Min(16, bootCode.CodeData.Length); i += 2)
                {
                    ushort word = (ushort)((bootCode.CodeData[i] << 8) | bootCode.CodeData[i + 1]);
                    Console.WriteLine($"  [{i / 2:D3}] 0x{word:X4}");
                }
            }
        }

        [Test]
        [Category("NDFS")]
        [Category("BootLoader")]
        [Category("Tingo")]
        public void Tingo_Has_78_Byte_Bootstrap_Loader()
        {
            // Arrange
            if (!File.Exists(TEST_IMAGE))
            {
                Assert.Ignore($"Test image not found: {TEST_IMAGE}");
                return;
            }

            // Read first 256 bytes to analyze preamble
            byte[] buffer = new byte[256];
            using (FileStream fs = new FileStream(TEST_IMAGE, FileMode.Open, FileAccess.Read))
            {
                fs.ReadExactly(buffer, 0, 256);
            }

            // Find '!' delimiter
            int delimiterPos = -1;
            for (int i = 0; i < 100; i++)
            {
                if (buffer[i] == 0x21)
                {
                    delimiterPos = i;
                    break;
                }
            }

            // Assert
            Assert.That(delimiterPos, Is.EqualTo(78), "Delimiter should be at offset 78");

            Console.WriteLine($"\nPreamble analysis:");
            Console.WriteLine($"Delimiter '!' at offset: {delimiterPos}");
            Console.WriteLine($"Preamble length: {delimiterPos} bytes (bootstrap loader code)");

            // Show preamble bytes
            Console.WriteLine("\nPreamble (hex):");
            for (int i = 0; i < delimiterPos; i += 16)
            {
                Console.Write($"  {i:X4}: ");
                for (int j = 0; j < 16 && (i + j) < delimiterPos; j++)
                {
                    Console.Write($"{buffer[i + j]:X2} ");
                }
                Console.WriteLine();
            }
        }

        [Test]
        [Category("NDFS")]
        [Category("BootLoader")]
        [Category("Tingo")]
        [Ignore("Test image tingo_raw_debug.img has changed - does not contain expected BPUN pattern")]
        public void Tingo_Via_INdfsFileSystem_Works()
        {
            // Arrange
            if (!File.Exists(TEST_IMAGE))
            {
                Assert.Ignore($"Test image not found: {TEST_IMAGE}");
                return;
            }

            using (FileStream stream = new FileStream(TEST_IMAGE, FileMode.Open, FileAccess.Read))
            {
                var descriptor = NdfsBlockDeviceDescriptor.Create(stream.Length);
                var blockDevice = new GenericBlockDevice(descriptor);
                blockDevice.Mount(stream, readOnly: true);

                var ndfs = new NdfsFileSystem();
                ndfs.Initialize(blockDevice);

                // Act
                bool isBootable = ndfs.IsBootable();
                NdfsBootFormat format = ndfs.DetectBootFormat();
                NdfsBootCode? bootCode = ndfs.LoadBootCode();

                // Assert
                Assert.That(isBootable, Is.True, "tingo_raw_debug.img should be bootable");
                Assert.That(format, Is.EqualTo(NdfsBootFormat.BPUN), "Format should be BPUN");
                Assert.That(bootCode, Is.Not.Null, "Boot code should load");
                Assert.That(bootCode!.Format, Is.EqualTo(format), "Formats should match");

                Console.WriteLine($"\n=== Via INdfsFileSystem ===");
                Console.WriteLine($"Bootable: {isBootable}");
                Console.WriteLine($"Format: {format}");
                Console.WriteLine($"Load Address: 0x{bootCode.LoadAddress:X4}");
                Console.WriteLine($"Code Size: {bootCode.CodeData.Length} bytes");
            }
        }
    }
}
