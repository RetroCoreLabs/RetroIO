//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Storage.Local.FileSystems;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tests
{
    /// <summary>
    /// Tests for ExtensionSeparator property across different filesystems.
    /// </summary>
    [TestFixture]
    public class ExtensionSeparatorTests
    {
        [Test]
        public void NdfsFileSystem_ExtensionSeparator_IsColon()
        {
            // Arrange
            var fs = new NdfsFileSystem();

            // Act
            string separator = fs.ExtensionSeparator;

            // Assert
            Assert.That(separator, Is.EqualTo(":"));
        }

        [Test]
        public void CbmDosFileSystem_ExtensionSeparator_IsEmpty()
        {
            // Arrange
            var fs = new CbmDosFileSystem();

            // Act
            string separator = fs.ExtensionSeparator;

            // Assert
            Assert.That(separator, Is.EqualTo(""));
        }

        [Test]
        public void Fat12FileSystem_ExtensionSeparator_IsPeriod()
        {
            // Arrange
            var fs = new Fat12FileSystem();

            // Act
            string separator = fs.ExtensionSeparator;

            // Assert
            Assert.That(separator, Is.EqualTo("."));
        }

        [Test]
        public void Fat16FileSystem_ExtensionSeparator_IsPeriod()
        {
            // Arrange
            var fs = new Fat16FileSystem();

            // Act
            string separator = fs.ExtensionSeparator;

            // Assert
            Assert.That(separator, Is.EqualTo("."));
        }

        [Test]
        public void Fat32FileSystem_ExtensionSeparator_IsPeriod()
        {
            // Arrange
            var fs = new Fat32FileSystem();

            // Act
            string separator = fs.ExtensionSeparator;

            // Assert
            Assert.That(separator, Is.EqualTo("."));
        }

        [Test]
        public void DragonDosFileSystem_ExtensionSeparator_IsPeriod()
        {
            // Arrange
            var fs = new DragonDosFileSystem();

            // Act
            string separator = fs.ExtensionSeparator;

            // Assert
            Assert.That(separator, Is.EqualTo("."));
        }

        [Test]
        public void DecbFileSystem_ExtensionSeparator_IsPeriod()
        {
            // Arrange
            var fs = new DecbFileSystem();

            // Act
            string separator = fs.ExtensionSeparator;

            // Assert
            Assert.That(separator, Is.EqualTo("."));
        }

        [Test]
        public void NdfsDriverProvider_RequiredHeaderBytes_Is2048()
        {
            // NDFS requires at least 2048 bytes (one page) to detect filesystem
            // via MasterBlock validation. This ensures detection doesn't fail
            // for .img files when only 512 bytes are read.
            var provider = new NdfsDriverProvider();

            int required = provider.RequiredHeaderBytes;

            Assert.That(required, Is.EqualTo(NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE));
            Assert.That(required, Is.EqualTo(2048));
        }
    }
}
