//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests that file systems throw the exception types promised by <see cref="IBlockFileSystem"/>.
    /// </summary>
    [TestFixture]
    public class FileSystemInterfaceContractTests
    {
        private static GenericBlockDevice MountBlank(BlockDeviceDescriptor descriptor, bool readOnly)
        {
            byte[] image = new byte[descriptor.ExpectedImageSize];
            GenericBlockDevice device = new GenericBlockDevice(descriptor);
            device.Mount(new MemoryStream(image, writable: true), readOnly);
            return device;
        }

        [Test]
        public void DecbFileSystem_ListDirectory_NonRootPath_ThrowsDirectoryNotFoundException()
        {
            using GenericBlockDevice device = MountBlank(BlockDeviceDescriptor.DragonVDK_SS40, false);
            DecbFileSystem fs = new DecbFileSystem();
            fs.Initialize(device);

            Assert.Throws<DirectoryNotFoundException>(() => fs.ListDirectory("/SUB"));
        }

        [Test]
        public void DragonDosFileSystem_ListDirectory_NonRootPath_ThrowsDirectoryNotFoundException()
        {
            using GenericBlockDevice device = MountBlank(BlockDeviceDescriptor.DragonVDK_SS40, false);
            DragonDosFileSystem fs = new DragonDosFileSystem();
            fs.Initialize(device);

            Assert.Throws<DirectoryNotFoundException>(() => fs.ListDirectory("/SUB"));
        }

        private static CbmDosFileSystem MountReadOnlyCbm()
        {
            byte[] image = CbmImageFactory.CreateBlank(CbmDiskType.D64, "TEST", "01");
            GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.D64_35Track);
            device.Mount(new MemoryStream(image, writable: true), true);
            CbmDosFileSystem fs = new CbmDosFileSystem(18, 18);
            fs.Initialize(device);
            return fs;
        }

        [Test]
        public void CbmDosFileSystem_WriteFile_ReadOnlyDevice_ThrowsIOException()
        {
            CbmDosFileSystem fs = MountReadOnlyCbm();

            Assert.Throws<IOException>(() => fs.WriteFile("/HELLO", new byte[] { 1, 2, 3 }));
        }

        [Test]
        public void CbmDosFileSystem_DeleteFile_ReadOnlyDevice_ThrowsIOException()
        {
            CbmDosFileSystem fs = MountReadOnlyCbm();

            Assert.Throws<IOException>(() => fs.DeleteFile("/HELLO"));
        }

        [Test]
        public void CbmDosFileSystem_Rename_ReadOnlyDevice_ThrowsIOException()
        {
            CbmDosFileSystem fs = MountReadOnlyCbm();

            Assert.Throws<IOException>(() => fs.Rename("/HELLO", "/WORLD"));
        }
    }
}
