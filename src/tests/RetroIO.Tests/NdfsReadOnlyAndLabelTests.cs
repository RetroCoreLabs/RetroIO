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
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tests
{
    /// <summary>
    /// Tests the write flag of <see cref="NdfsDriver"/> on a read-only mount, the password clear of
    /// <see cref="NdfsFileSystem"/> on a read-only device, and its volume label before initialization.
    /// </summary>
    [TestFixture]
    public class NdfsReadOnlyAndLabelTests
    {
        private const int TotalPages = 512;

        private static MemoryStream MakeImage()
        {
            int size = TotalPages * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            MemoryStream stream = new MemoryStream(size);
            stream.SetLength(size);
            NdfsWriteTests.CreateTestNdfsImage(stream, TotalPages);
            stream.Position = 0;
            return stream;
        }

        [Test]
        public void NdfsDriver_ReadOnlyMount_ReportsNoWrite()
        {
            using MemoryStream stream = MakeImage();
            NdfsDriver driver = new NdfsDriver();
            driver.Mount(stream, readOnly: true);

            Assert.That(driver.CanWrite, Is.False);
            Assert.That(driver.Capabilities.HasFlag(DriverCapabilities.Write), Is.False);
            Assert.That(driver.Capabilities.HasFlag(DriverCapabilities.Delete), Is.False);
            Assert.That(driver.Capabilities.HasFlag(DriverCapabilities.CreateDirectory), Is.False);
            Assert.That(driver.Capabilities.HasFlag(DriverCapabilities.Read), Is.True);
        }

        [Test]
        public void NdfsDriver_ReadWriteMount_ReportsWrite()
        {
            using MemoryStream stream = MakeImage();
            NdfsDriver driver = new NdfsDriver();
            driver.Mount(stream, readOnly: false);

            Assert.That(driver.CanWrite, Is.True);
            Assert.That(driver.Capabilities.HasFlag(DriverCapabilities.Write), Is.True);
        }

        [Test]
        public void ClearUserPassword_OnReadOnlyDevice_LeavesPasswordUntouched()
        {
            using MemoryStream stream = MakeImage();
            using GenericBlockDevice device = new GenericBlockDevice(NdfsBlockDeviceDescriptor.Create((int)stream.Length));
            device.Mount(stream, true);
            using NdfsFileSystem fs = new NdfsFileSystem();
            fs.Initialize(device);

            UserEntry? firstUser = null;
            foreach (UserEntry user in fs.GetUsers())
            {
                firstUser = user;
                break;
            }
            Assert.That(firstUser, Is.Not.Null, "The test image needs a user.");

            // Set a password in memory only, so there is something for the call to wrongly erase.
            firstUser!.Password = 12345;

            bool result = fs.ClearUserPassword(firstUser.UserIndex);

            Assert.That(result, Is.False);
            Assert.That(firstUser.Password, Is.EqualTo((ushort)12345));
        }

        [Test]
        public void VolumeLabel_BeforeInitialize_IsEmptyString()
        {
            using NdfsFileSystem fs = new NdfsFileSystem();

            Assert.That(fs.VolumeLabel, Is.EqualTo(string.Empty));
        }
    }
}
