//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.Elements;

namespace RetroIO.Tests
{
    [TestFixture]
    public class NdfsComprehensiveTests
    {
        private string _testImageDir;
        private DriverFactory _factory;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            // Create test images directory
            _testImageDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                         @"..\..\..\..\demo\test-images\ndfs");
            Directory.CreateDirectory(_testImageDir);

            // Generate test images
            GenerateTestImages();
        }

        [SetUp]
        public void Setup()
        {
            _factory = new DriverFactory();
            _factory.Register(new NdfsDriverProvider());
        }

        private void GenerateTestImages()
        {
            // Generate empty filesystem
            var emptyImage = NdfsTestImageGenerator.CreateEmptyFilesystem("EMPTY", 50);
            File.WriteAllBytes(Path.Combine(_testImageDir, "empty.ndfs"), emptyImage);

            // Generate filesystem with files
            var withFilesImage = NdfsTestImageGenerator.CreateFilesystemWithFiles();
            File.WriteAllBytes(Path.Combine(_testImageDir, "withfiles.ndfs"), withFilesImage);
        }

        [Test]
        public void Test_EmptyFilesystem_CanMount()
        {
            // Arrange
            var imagePath = Path.Combine(_testImageDir, "empty.ndfs");
            using var stream = File.OpenRead(imagePath);

            // Act
            var driver = _factory.Detect(imagePath);
            Assert.That(driver, Is.Not.Null);
            driver!.Mount(stream, readOnly: true);

            // Assert
            Assert.That(driver.FileSystemType, Is.EqualTo("NDFS"));
            Assert.That(driver.VolumeLabel, Is.EqualTo("EMPTY"));

            // Verify empty directory
            var files = driver.ListDirectory("/");
            Assert.That(files.Length, Is.EqualTo(0), "Empty filesystem should have no files");

            driver.Dispose();
        }

        [Test]
        public void Test_FilesystemWithFiles_ListsCorrectly()
        {
            // Arrange
            var imagePath = Path.Combine(_testImageDir, "withfiles.ndfs");
            using var stream = File.OpenRead(imagePath);

            // Act
            var driver = _factory.Detect(imagePath);
            Assert.That(driver, Is.Not.Null);
            driver!.Mount(stream, readOnly: true);

            // Assert volume label
            Assert.That(driver.VolumeLabel, Is.EqualTo("NORDISK"));

            // List SYSTEM user directory (NDFS files are under user directories, not root)
            var files = driver.ListDirectory("SYSTEM");
            Assert.That(files.Length, Is.GreaterThanOrEqualTo(1), "Should have at least 1 file in SYSTEM");

            // Verify file entries
            DirectoryEntry? readme = null;
            foreach (var file in files)
            {
                if (file.Name.Contains("README")) readme = file;
            }
            Assert.That(readme, Is.Not.Null, "Should find README file");

            // Note: Files belong to different users in test image:
            // README.TXT - user 0 (SYSTEM)
            // PROGRAM.BIN - user 1 (USER1)
            // DATA.DAT - user 1 (USER1)

            driver.Dispose();
        }

        [Test]
        public void Test_ReadFile_ReturnsCorrectContent()
        {
            // Arrange
            var imagePath = Path.Combine(_testImageDir, "withfiles.ndfs");
            using var stream = File.OpenRead(imagePath);
            var driver = _factory.Detect(imagePath);
            Assert.That(driver, Is.Not.Null);
            driver!.Mount(stream, readOnly: true);

            // Act - Read README.TXT
            var content = driver.ReadFile("README.TXT");

            // Assert
            Assert.That(content, Is.Not.Null);
            var text = Encoding.ASCII.GetString(content).TrimEnd('\0');
            Assert.That(text, Does.Contain("test NDFS filesystem"));
            Assert.That(text, Does.Contain("RetroFS test suite"));

            driver.Dispose();
        }

        [Test]
        public void Test_NdfsSpecificFeatures_UserManagement()
        {
            // Arrange
            var imagePath = Path.Combine(_testImageDir, "withfiles.ndfs");
            using var stream = File.OpenRead(imagePath);
            var driver = new NdfsDriver();
            driver.Mount(stream, readOnly: true);

            // Act - Cast to NDFS-specific interface
            var ndfsDriver = driver as NdfsDriver;
            Assert.That(ndfsDriver, Is.Not.Null, "Should be NdfsDriver");
            var ndfs = ndfsDriver.GetFileSystem() as RetroIO.Storage.Ndfs.FileSystems.INdfsFileSystem;
            Assert.That(ndfs, Is.Not.Null, "Driver should implement INdfsFileSystem");

            // Verify users
            var users = ndfs.GetUsers();
            Assert.That(users.Count, Is.EqualTo(3), "Should have 3 users");

            // Check specific user
            UserEntry? systemUser = null;
            foreach (var user in users)
            {
                if (user.UserName == "SYSTEM")
                {
                    systemUser = user;
                    break;
                }
            }
            Assert.That(systemUser, Is.Not.Null);
            Assert.That(systemUser.PagesReserved, Is.EqualTo(50));
            Assert.That(systemUser.PagesUsed, Is.EqualTo(10));

            driver.Dispose();
        }

        [Test]
        public void Test_BitmapAllocation_TracksUsedPages()
        {
            // Arrange
            var imagePath = Path.Combine(_testImageDir, "withfiles.ndfs");
            using var stream = File.OpenRead(imagePath);
            var driver = new NdfsDriver();
            driver.Mount(stream, readOnly: true);

            // Act
            var ndfsDriver = driver as NdfsDriver;
            Assert.That(ndfsDriver, Is.Not.Null, "Should be NdfsDriver");
            var ndfs = ndfsDriver.GetFileSystem();
            Assert.That(ndfs, Is.Not.Null);
            var freePages = ndfs!.GetPagesAvailable();
            var report = ndfs.GenerateBitmapReport();

            // Assert
            Assert.That(freePages, Is.GreaterThan(0));
            Assert.That(report, Does.Contain("Pages"));
            Assert.That(report, Does.Contain("Free"));

            // Verify specific pages are marked as used (based on actual test image layout)
            Assert.That(ndfs.IsBlockUsed(0), Is.True, "Page 0 (master block) should be used");
            Assert.That(ndfs.IsBlockUsed(2), Is.True, "Page 2 (object file index) should be used");
            Assert.That(ndfs.IsBlockUsed(5), Is.True, "Page 5 (user file index) should be used");
            Assert.That(ndfs.IsBlockUsed(8), Is.True, "Page 8 (bitmap) should be used");

            driver.Dispose();
        }

        [Test]
        public void Test_FileSystemInfo_DumpCorrectly()
        {
            // Arrange
            var imagePath = Path.Combine(_testImageDir, "withfiles.ndfs");
            using var stream = File.OpenRead(imagePath);
            var driver = new NdfsDriver();
            driver.Mount(stream, readOnly: true);

            // Act
            var ndfsDriver = driver as NdfsDriver;
            Assert.That(ndfsDriver, Is.Not.Null, "Should be NdfsDriver");
            var ndfs = ndfsDriver.GetFileSystem();
            Assert.That(ndfs, Is.Not.Null);
            var info = ndfs!.DumpFileSystemInfo();

            // Assert
            Assert.That(info, Does.Contain("NORDISK"));
            Assert.That(info, Does.Contain("Users"));
            Assert.That(info, Does.Contain("SYSTEM"));
            Assert.That(info, Does.Contain("USER1"));
            Assert.That(info, Does.Contain("Files"));
            Assert.That(info, Does.Contain("README"));

            driver.Dispose();
        }

        [Test]
        public void Test_Detection_RecognizesNdfsFormat()
        {
            // Arrange
            var imagePath = Path.Combine(_testImageDir, "withfiles.ndfs");
            using var stream = File.OpenRead(imagePath);

            // Act
            var driver = _factory.Detect(imagePath);

            // Assert
            Assert.That(driver, Is.Not.Null);
            Assert.That(driver.GetType().Name, Is.EqualTo("NdfsDriver"));
        }
    }
}
