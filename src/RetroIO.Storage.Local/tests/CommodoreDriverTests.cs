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
using RetroIO.Storage.Local;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Tests;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Unit tests for Commodore disk image drivers (D64, D71, D81, T64).
    /// Tests detection, mounting, directory listing, and file reading.
    ///
    /// The images come from the folder named by RETROIO_TEST_IMAGES (see src/eng/test-env.example.bat).
    /// </summary>
    [TestFixture]
    public class CommodoreDriverTests
    {
        // The test-image folder, from RETROIO_TEST_IMAGES.
        private static readonly string TEST_IMAGE_PATH = TestData.FromVariable(TestData.TestImagesVariable, string.Empty);

        // ***** CONFIGURE THESE FILENAMES FOR YOUR TEST IMAGES *****
        private const string D64_TEST_FILE = "d64/CARMEN.D64";       // Change to your D64 test file
        private const string D71_TEST_FILE = "d71/CPMY2K.D71";       // Change to your D71 test file
        private const string D81_TEST_FILE = "d81/Die_Hard_1_1989_Activision.d81";  // Change to your D81 test file
        private const string T64_TEST_FILE = "t64/cauldron.t64";     // Change to your T64 test file

        private DriverFactory? _factory;

        [SetUp]
        public void Setup()
        {
            _factory = new DriverFactory();

            // Register all Commodore drivers
            _factory.Register(new CommodoreDiskDriverProvider());
            _factory.Register(new T64DriverProvider());
        }

        [TearDown]
        public void TearDown()
        {
            _factory = null;
        }

        #region D64 Tests

        [Test]
        [Category("D64")]
        public void D64_DetectFormat_ValidImage_ReturnsD64Driver()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGE_PATH, D64_TEST_FILE);

            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            // Act
            IFileSystemDriver? driver = _factory!.Detect(imagePath);

            // Assert
            Assert.That(driver, Is.Not.Null, "Driver should be detected");
            // D64, D71 and D81 are all CBM DOS; one driver reads the three. What tells them apart is
            // the shape of the disk, so that is what is checked.
            using FileStream stream = File.OpenRead(imagePath);
            driver!.Mount(stream, readOnly: true);
            Assert.That(driver.FileSystemType, Is.EqualTo("CBM-DOS"), "a D64 is a CBM DOS disk");
            Assert.That(driver.GetBlockDeviceInfo()!.TrackCount, Is.EqualTo(35), "a D64 has 35 tracks");
        }

        [Test]
        [Category("D64")]
        public void D64_Mount_ValidImage_SuccessfullyMounts()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGE_PATH, D64_TEST_FILE);

            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            IFileSystemDriver? driver = _factory!.Detect(imagePath);
            if (driver == null)
            {
                Assert.Fail("Failed to detect D64 driver");
                return;
            }

            // Act
            using FileStream stream = File.OpenRead(imagePath);
            TestDelegate mountAction = () => driver.Mount(stream, readOnly: true);

            // Assert
            Assert.DoesNotThrow(mountAction, "Mounting D64 should not throw exception");
            Assert.That(driver.CanRead, Is.True, "Driver should support reading");
            Console.WriteLine($"D64 Volume Label: '{driver.VolumeLabel}'");
        }

        [Test]
        [Category("D64")]
        public void D64_ListDirectory_ValidImage_ReturnsEntries()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGE_PATH, D64_TEST_FILE);

            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            IFileSystemDriver? driver = _factory!.Detect(imagePath);
            if (driver == null)
            {
                Assert.Fail("Failed to detect D64 driver");
                return;
            }

            using FileStream stream = File.OpenRead(imagePath);
            driver.Mount(stream, readOnly: true);

            // Act
            DirectoryEntry[] entries = driver.ListDirectory("/");

            // Assert
            Assert.That(entries, Is.Not.Null, "Directory entries should not be null");
            Console.WriteLine($"D64 Directory contains {entries.Length} entries:");

            for (int i = 0; i < entries.Length && i < 10; i++)
            {
                DirectoryEntry entry = entries[i];
                Console.WriteLine($"  [{i}] {entry.Name} - {entry.Metadata.Size} bytes ({entry.Metadata.FileType})");
            }

            if (entries.Length > 10)
            {
                Console.WriteLine($"  ... and {entries.Length - 10} more files");
            }
        }

        [Test]
        [Category("D64")]
        public void D64_ReadFile_FirstFile_ReturnsData()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGE_PATH, D64_TEST_FILE);

            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            IFileSystemDriver? driver = _factory!.Detect(imagePath);
            if (driver == null)
            {
                Assert.Fail("Failed to detect D64 driver");
                return;
            }

            using FileStream stream = File.OpenRead(imagePath);
            driver.Mount(stream, readOnly: true);
            DirectoryEntry[] entries = driver.ListDirectory("/");

            if (entries.Length == 0)
            {
                Assert.Ignore("D64 image is empty");
                return;
            }

            // Act
            string firstFileName = entries[0].Name;
            byte[] fileData = driver.ReadFile(firstFileName);

            // Assert
            Assert.That(fileData, Is.Not.Null, "File data should not be null");
            Assert.That(fileData.Length, Is.GreaterThan(0), "File should have content");
            Console.WriteLine($"D64 Read file '{firstFileName}': {fileData.Length} bytes");
            Console.WriteLine($"  First 16 bytes: {BitConverter.ToString(fileData, 0, Math.Min(16, fileData.Length))}");
        }

        #endregion

        #region D71 Tests

        [Test]
        [Category("D71")]
        public void D71_DetectFormat_ValidImage_ReturnsD71Driver()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGE_PATH, D71_TEST_FILE);

            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            // Act
            IFileSystemDriver? driver = _factory!.Detect(imagePath);

            // Assert
            Assert.That(driver, Is.Not.Null, "Driver should be detected");
            // D64, D71 and D81 are all CBM DOS; one driver reads the three. What tells them apart is
            // the shape of the disk, so that is what is checked.
            using FileStream stream = File.OpenRead(imagePath);
            driver!.Mount(stream, readOnly: true);
            Assert.That(driver.FileSystemType, Is.EqualTo("CBM-DOS"), "a D71 is a CBM DOS disk");
            Assert.That(driver.GetBlockDeviceInfo()!.TrackCount, Is.EqualTo(70), "a D71 has 70 tracks");
        }

        [Test]
        [Category("D71")]
        public void D71_Mount_ValidImage_SuccessfullyMounts()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGE_PATH, D71_TEST_FILE);

            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            IFileSystemDriver? driver = _factory!.Detect(imagePath);
            if (driver == null)
            {
                Assert.Fail("Failed to detect D71 driver");
                return;
            }

            // Act & Assert
            try
            {
                using FileStream stream = File.OpenRead(imagePath);
                driver.Mount(stream, readOnly: true);
                Assert.That(driver.CanRead, Is.True, "Driver should support reading");
                Console.WriteLine($"D71 Volume Label: '{driver.VolumeLabel}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"D71 Mount Exception: {ex.GetType().Name}");
                Console.WriteLine($"  Message: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// The D71 test image is a C128 CP/M disk (its header says "CP/M PLUS"), not a CBM DOS file disk.
        /// </summary>
        /// <remarks>
        /// CP/M keeps its own data where CBM DOS expects the directory (track 18 sector 1). Read as CBM
        /// DOS, that sector's first two bytes - the link to the next directory sector - say track 41
        /// sector 209, and no 1571 track has more than 21 sectors. A real drive reports this as
        /// "66, ILLEGAL TRACK OR SECTOR"; VICE's c1541 stops on the same link. Listing it must give that
        /// error, naming the link, and not an argument error from deep inside the geometry code.
        /// A listing test with real files needs an ordinary CBM DOS D71 image, which the test-image
        /// collection does not have yet.
        /// </remarks>
        [Test]
        [Category("D71")]
        public void D71_CpmDisk_ListDirectory_ReportsIllegalTrackOrSector()
        {
            string imagePath = Path.Combine(TEST_IMAGE_PATH, D71_TEST_FILE);
            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            IFileSystemDriver? driver = _factory!.Detect(imagePath);
            Assert.That(driver, Is.Not.Null, "Driver should be detected");

            using FileStream stream = File.OpenRead(imagePath);
            driver!.Mount(stream, readOnly: true);

            InvalidDataException error = Assert.Throws<InvalidDataException>(() => driver.ListDirectory("/"))!;
            Assert.That(error.Message, Does.StartWith("66, ILLEGAL TRACK OR SECTOR"));
            Assert.That(error.Message, Does.Contain("track 41 sector 209"));
        }

        #endregion

        #region D81 Tests

        [Test]
        [Category("D81")]
        public void D81_DetectFormat_ValidImage_ReturnsD81Driver()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGE_PATH, D81_TEST_FILE);

            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            // Act
            IFileSystemDriver? driver = _factory!.Detect(imagePath);

            // Assert
            Assert.That(driver, Is.Not.Null, "Driver should be detected");
            // D64, D71 and D81 are all CBM DOS; one driver reads the three. What tells them apart is
            // the shape of the disk, so that is what is checked.
            using FileStream stream = File.OpenRead(imagePath);
            driver!.Mount(stream, readOnly: true);
            Assert.That(driver.FileSystemType, Is.EqualTo("CBM-DOS"), "a D81 is a CBM DOS disk");
            Assert.That(driver.GetBlockDeviceInfo()!.TrackCount, Is.EqualTo(80), "a D81 has 80 tracks");
        }

        [Test]
        [Category("D81")]
        public void D81_Mount_ValidImage_SuccessfullyMounts()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGE_PATH, D81_TEST_FILE);

            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            IFileSystemDriver? driver = _factory!.Detect(imagePath);
            if (driver == null)
            {
                Assert.Fail("Failed to detect D81 driver");
                return;
            }

            // Act & Assert
            try
            {
                using FileStream stream = File.OpenRead(imagePath);
                driver.Mount(stream, readOnly: true);
                Assert.That(driver.CanRead, Is.True, "Driver should support reading");
                Console.WriteLine($"D81 Volume Label: '{driver.VolumeLabel}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"D81 Mount Exception: {ex.GetType().Name}");
                Console.WriteLine($"  Message: {ex.Message}");
                if (ex.StackTrace != null)
                {
                    Console.WriteLine($"  Stack Trace: {ex.StackTrace}");
                }
                throw;
            }
        }

        [Test]
        [Category("D81")]
        public void D81_ListDirectory_ValidImage_ReturnsEntries()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGE_PATH, D81_TEST_FILE);

            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            IFileSystemDriver? driver = _factory!.Detect(imagePath);
            if (driver == null)
            {
                Assert.Fail("Failed to detect D81 driver");
                return;
            }

            using FileStream stream = File.OpenRead(imagePath);
            driver.Mount(stream, readOnly: true);

            // Act & Assert
            try
            {
                DirectoryEntry[] entries = driver.ListDirectory("/");
                Assert.That(entries, Is.Not.Null, "Directory entries should not be null");
                Console.WriteLine($"D81 Directory contains {entries.Length} entries:");

                for (int i = 0; i < entries.Length && i < 10; i++)
                {
                    DirectoryEntry entry = entries[i];
                    // Display blocks (Commodore standard) instead of bytes
                    ushort blocks = entry.Metadata.GetProperty<ushort>("Blocks");
                    Console.WriteLine($"  [{blocks,3}] \"{entry.Name}\" - {entry.Metadata.FileType}");
                }

                if (entries.Length > 10)
                {
                    Console.WriteLine($"  ... and {entries.Length - 10} more files");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"D81 ListDirectory Exception: {ex.GetType().Name}");
                Console.WriteLine($"  Message: {ex.Message}");
                if (ex.StackTrace != null)
                {
                    Console.WriteLine($"  Stack Trace: {ex.StackTrace}");
                }
                throw;
            }
        }

        [Test]
        [Category("D81")]
        public void D81_ReadFile_FirstFile_ReturnsData()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGE_PATH, D81_TEST_FILE);

            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            IFileSystemDriver? driver = _factory!.Detect(imagePath);
            if (driver == null)
            {
                Assert.Fail("Failed to detect D81 driver");
                return;
            }

            using FileStream stream = File.OpenRead(imagePath);
            driver.Mount(stream, readOnly: true);
            DirectoryEntry[] entries = driver.ListDirectory("/");

            if (entries.Length == 0)
            {
                Assert.Ignore("D81 image is empty");
                return;
            }

            // Act & Assert
            try
            {
                string firstFileName = entries[0].Name;
                byte[] fileData = driver.ReadFile(firstFileName);

                Assert.That(fileData, Is.Not.Null, "File data should not be null");
                Assert.That(fileData.Length, Is.GreaterThan(0), "File should have content");
                Console.WriteLine($"D81 Read file '{firstFileName}': {fileData.Length} bytes");
                Console.WriteLine($"  First 16 bytes: {BitConverter.ToString(fileData, 0, Math.Min(16, fileData.Length))}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"D81 ReadFile Exception: {ex.GetType().Name}");
                Console.WriteLine($"  Message: {ex.Message}");
                if (ex.StackTrace != null)
                {
                    Console.WriteLine($"  Stack Trace: {ex.StackTrace}");
                }
                throw;
            }
        }

        #endregion

        #region T64 Tests

        [Test]
        [Category("T64")]
        public void T64_DetectFormat_ValidImage_ReturnsT64Driver()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGE_PATH, T64_TEST_FILE);

            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            // Act
            IFileSystemDriver? driver = _factory!.Detect(imagePath);

            // Assert
            Assert.That(driver, Is.Not.Null, "Driver should be detected");
            Assert.That(driver!.FileSystemType, Is.EqualTo("T64"), "Should detect T64 format");
        }

        [Test]
        [Category("T64")]
        public void T64_Mount_ValidImage_SuccessfullyMounts()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGE_PATH, T64_TEST_FILE);

            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            IFileSystemDriver? driver = _factory!.Detect(imagePath);
            if (driver == null)
            {
                Assert.Fail("Failed to detect T64 driver");
                return;
            }

            // Act & Assert
            try
            {
                using FileStream stream = File.OpenRead(imagePath);
                driver.Mount(stream, readOnly: true);
                Assert.That(driver.CanRead, Is.True, "Driver should support reading");
                Console.WriteLine($"T64 Volume Label: '{driver.VolumeLabel}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"T64 Mount Exception: {ex.GetType().Name}");
                Console.WriteLine($"  Message: {ex.Message}");
                throw;
            }
        }

        [Test]
        [Category("T64")]
        public void T64_ListDirectory_ValidImage_ReturnsEntries()
        {
            // Arrange
            string imagePath = Path.Combine(TEST_IMAGE_PATH, T64_TEST_FILE);

            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not found: {imagePath}");
                return;
            }

            IFileSystemDriver? driver = _factory!.Detect(imagePath);
            if (driver == null)
            {
                Assert.Fail("Failed to detect T64 driver");
                return;
            }

            using FileStream stream = File.OpenRead(imagePath);
            driver.Mount(stream, readOnly: true);

            // Act & Assert
            try
            {
                DirectoryEntry[] entries = driver.ListDirectory("/");
                Assert.That(entries, Is.Not.Null, "Directory entries should not be null");
                Console.WriteLine($"T64 Archive contains {entries.Length} entries:");

                for (int i = 0; i < entries.Length && i < 10; i++)
                {
                    DirectoryEntry entry = entries[i];
                    Console.WriteLine($"  [{i}] {entry.Name} - {entry.Metadata.Size} bytes");
                }

                if (entries.Length > 10)
                {
                    Console.WriteLine($"  ... and {entries.Length - 10} more files");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"T64 ListDirectory Exception: {ex.GetType().Name}");
                Console.WriteLine($"  Message: {ex.Message}");
                throw;
            }
        }

        #endregion
    }
}
