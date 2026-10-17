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
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tests
{
    /// <summary>
    /// Tests that <see cref="NdfsFileSystem"/> keeps the promises of <see cref="IBlockFileSystem"/>
    /// and <see cref="INdfsFileSystem"/>: which exception each failure throws, and that an edit to a
    /// user reaches the disk.
    /// </summary>
    [TestFixture]
    public class NdfsFileSystemContractTests
    {
        private const int TotalPages = 512;

        private MemoryStream _stream = null!;
        private GenericBlockDevice _device = null!;
        private NdfsFileSystem _fs = null!;

        // Opens a mounted device on the given stream. Writes are held in memory by the device until it is flushed.
        private static GenericBlockDevice Open(MemoryStream stream, bool readOnly)
        {
            stream.Position = 0;
            GenericBlockDevice device = new GenericBlockDevice(NdfsBlockDeviceDescriptor.Create((int)stream.Length));
            device.Mount(stream, readOnly);
            return device;
        }

        [SetUp]
        public void SetUp()
        {
            int size = TotalPages * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            _stream = new MemoryStream(size);
            _stream.SetLength(size);
            NdfsWriteTests.CreateTestNdfsImage(_stream, TotalPages);

            _device = Open(_stream, readOnly: false);
            _fs = new NdfsFileSystem();
            _fs.Initialize(_device);
        }

        [TearDown]
        public void TearDown()
        {
            _fs?.Dispose();
            _device?.Dispose();
            _stream?.Dispose();
        }

        // Re-reads the image from the stream, as a fresh mount would, and returns the user with this name.
        private UserEntry? ReadBackUser(string userName)
        {
            using GenericBlockDevice fresh = Open(_stream, readOnly: true);
            using NdfsFileSystem reopened = new NdfsFileSystem();
            reopened.Initialize(fresh);
            foreach (UserEntry user in reopened.GetUsers())
            {
                if (string.Equals(user.UserName, userName, StringComparison.OrdinalIgnoreCase))
                    return user;
            }
            return null;
        }

        [Test]
        public void DeleteFile_ThatDoesNotExist_ThrowsFileNotFoundException()
        {
            // IBlockFileSystem.DeleteFile documents FileNotFoundException, and ReadFile, RenameFile and
            // the property reader already throw it. DeleteFile threw InvalidOperationException.
            // The SYSTEM user is part of the test image.
            Assert.Throws<FileNotFoundException>(() => _fs.DeleteFile("SYSTEM/NOPE:DAT"));
        }

        [Test]
        public void Initialize_OnADeviceWithoutAnNdfsMasterBlock_ThrowsInvalidOperationException()
        {
            // IBlockFileSystem.Initialize documents InvalidOperationException. This threw InvalidDataException.
            byte[] blank = new byte[TotalPages * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            using MemoryStream blankStream = new MemoryStream(blank);
            using GenericBlockDevice blankDevice = Open(blankStream, readOnly: false);
            using NdfsFileSystem fs = new NdfsFileSystem();

            Assert.Throws<InvalidOperationException>(() => fs.Initialize(blankDevice));
        }

        [Test]
        public void UpdateUserQuota_IsWrittenToTheDisk()
        {
            // Regression: UpdateUserQuota changed the in-memory user only, so the new quota was lost when
            // the image was opened again. AddUser and RemoveUser write the user's page.
            Assert.That(_fs.AddUser("ALICE", 40), Is.True);
            int index = FindIndex("ALICE");
            // Flush the new user here, so that what is read back below can only come from UpdateUserQuota itself.
            _device.Flush();

            Assert.That(_fs.UpdateUserQuota(index, 99), Is.True);

            UserEntry? onDisk = ReadBackUser("ALICE");
            Assert.That(onDisk, Is.Not.Null);
            Assert.That(onDisk!.PagesReserved, Is.EqualTo(99u));
        }

        [Test]
        public void UpdateUserQuota_UnknownUser_ReturnsFalse()
        {
            Assert.That(_fs.UpdateUserQuota(200, 10), Is.False);
        }

        [Test]
        public void UpdateUserQuota_OnAReadOnlyDevice_ReturnsFalse_AndChangesNothing()
        {
            Assert.That(_fs.AddUser("BOB", 40), Is.True);
            int index = FindIndex("BOB");
            _device.Flush();

            using GenericBlockDevice readOnlyDevice = Open(_stream, readOnly: true);
            using NdfsFileSystem readOnlyFs = new NdfsFileSystem();
            readOnlyFs.Initialize(readOnlyDevice);

            Assert.That(readOnlyFs.UpdateUserQuota(index, 7), Is.False);
            UserEntry? still = null;
            foreach (UserEntry user in readOnlyFs.GetUsers())
            {
                if (string.Equals(user.UserName, "BOB", StringComparison.OrdinalIgnoreCase))
                    still = user;
            }
            Assert.That(still, Is.Not.Null);
            Assert.That(still!.PagesReserved, Is.EqualTo(40u), "a refused change must not leave the in-memory user changed");
        }

        private int FindIndex(string userName)
        {
            foreach (UserEntry user in _fs.GetUsers())
            {
                if (string.Equals(user.UserName, userName, StringComparison.OrdinalIgnoreCase))
                    return user.UserIndex;
            }
            Assert.Fail("user not found: " + userName);
            return -1;
        }
    }
}
