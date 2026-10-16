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
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tests
{
    [TestFixture]
    public class NdfsClearPasswordTest
    {
        private static string TEST_IMAGE => TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\ndfs\FLOPPY-360KB.img");

        [Test]
        [Category("NDFS")]
        [Category("UserManagement")]
        public void ClearUserPassword_ByIndex_ClearsPassword()
        {
            // Arrange
            if (!File.Exists(TEST_IMAGE))
            {
                Assert.Ignore($"Test image not found: {TEST_IMAGE}");
                return;
            }

            // Create a copy to avoid modifying the original
            string tempFile = Path.GetTempFileName();
            File.Copy(TEST_IMAGE, tempFile, true);

            try
            {
                using (FileStream stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create(stream.Length);
                    var blockDevice = new GenericBlockDevice(descriptor);
                    blockDevice.Mount(stream, readOnly: false);

                    var ndfs = new NdfsFileSystem();
                    ndfs.Initialize(blockDevice);

                    // Get first user
                    var users = ndfs.GetUsers();
                    Assert.That(users.Count, Is.GreaterThan(0), "Should have at least one user");

                    var firstUser = users[0];
                    int userIndex = firstUser.UserIndex;

                    // Set a non-zero password first
                    firstUser.Password = 12345;
                    Assert.That(firstUser.Password, Is.EqualTo(12345), "Password should be set");

                    // Act - Clear password
                    bool result = ndfs.ClearUserPassword(userIndex);

                    // Assert
                    Assert.That(result, Is.True, "ClearUserPassword should succeed");

                    // Reload user to verify persistence
                    var reloadedUser = ndfs.GetUser(userIndex);
                    Assert.That(reloadedUser, Is.Not.Null, "User should still exist");
                    Assert.That(reloadedUser.Password, Is.EqualTo(0), "Password should be cleared (zero)");

                }
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        [Test]
        [Category("NDFS")]
        [Category("UserManagement")]
        public void ClearUserPassword_ByUsername_ClearsPassword()
        {
            // Arrange
            if (!File.Exists(TEST_IMAGE))
            {
                Assert.Ignore($"Test image not found: {TEST_IMAGE}");
                return;
            }

            string tempFile = Path.GetTempFileName();
            File.Copy(TEST_IMAGE, tempFile, true);

            try
            {
                using (FileStream stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create(stream.Length);
                    var blockDevice = new GenericBlockDevice(descriptor);
                    blockDevice.Mount(stream, readOnly: false);

                    var ndfs = new NdfsFileSystem();
                    ndfs.Initialize(blockDevice);

                    // Get first user
                    var users = ndfs.GetUsers();
                    Assert.That(users.Count, Is.GreaterThan(0), "Should have at least one user");

                    var firstUser = users[0];
                    string userName = firstUser.UserName;

                    // Set a non-zero password first
                    firstUser.Password = 54321;
                    Assert.That(firstUser.Password, Is.EqualTo(54321), "Password should be set");

                    // Act - Clear password by username
                    bool result = ndfs.ClearUserPassword(userName);

                    // Assert
                    Assert.That(result, Is.True, "ClearUserPassword should succeed");

                    // Reload user to verify persistence
                    var reloadedUser = ndfs.GetUser(firstUser.UserIndex);
                    Assert.That(reloadedUser, Is.Not.Null, "User should still exist");
                    Assert.That(reloadedUser.Password, Is.EqualTo(0), "Password should be cleared (zero)");

                }
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        [Test]
        [Category("NDFS")]
        [Category("UserManagement")]
        public void ClearUserPassword_InvalidUserIndex_ReturnsFalse()
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

                // Act - Try to clear password for non-existent user
                bool result = ndfs.ClearUserPassword(999);

                // Assert
                Assert.That(result, Is.False, "Should return false for invalid user index");

            }
        }

        [Test]
        [Category("NDFS")]
        [Category("UserManagement")]
        public void ClearUserPassword_InvalidUsername_ReturnsFalse()
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

                // Act - Try to clear password for non-existent username
                bool result = ndfs.ClearUserPassword("NONEXISTENT");

                // Assert
                Assert.That(result, Is.False, "Should return false for invalid username");

            }
        }

        [Test]
        [Category("NDFS")]
        [Category("UserManagement")]
        public void ClearUserPassword_ReadOnlyDisk_ReturnsFalse()
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

                var users = ndfs.GetUsers();
                Assert.That(users.Count, Is.GreaterThan(0), "Should have at least one user");

                // Act - Try to clear password on read-only disk
                bool result = ndfs.ClearUserPassword(users[0].UserIndex);

                // Assert
                Assert.That(result, Is.False, "Should return false for read-only disk");

            }
        }

        [Test]
        [Category("NDFS")]
        [Category("UserManagement")]
        public void ClearUserPassword_PersistsAcrossReload()
        {
            // Arrange
            if (!File.Exists(TEST_IMAGE))
            {
                Assert.Ignore($"Test image not found: {TEST_IMAGE}");
                return;
            }

            string tempFile = Path.GetTempFileName();
            File.Copy(TEST_IMAGE, tempFile, true);

            try
            {
                int userIndex;
                string userName;

                // First session - clear password
                using (FileStream stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create(stream.Length);
                    var blockDevice = new GenericBlockDevice(descriptor);
                    blockDevice.Mount(stream, readOnly: false);

                    var ndfs = new NdfsFileSystem();
                    ndfs.Initialize(blockDevice);

                    var users = ndfs.GetUsers();
                    var firstUser = users[0];
                    userIndex = firstUser.UserIndex;
                    userName = firstUser.UserName;

                    // Set and clear password
                    firstUser.Password = 55555;
                    bool result = ndfs.ClearUserPassword(userIndex);
                    Assert.That(result, Is.True, "First clear should succeed");

                }

                // Second session - verify password is still cleared
                using (FileStream stream = new FileStream(tempFile, FileMode.Open, FileAccess.Read))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create(stream.Length);
                    var blockDevice = new GenericBlockDevice(descriptor);
                    blockDevice.Mount(stream, readOnly: true);

                    var ndfs = new NdfsFileSystem();
                    ndfs.Initialize(blockDevice);

                    var reloadedUser = ndfs.GetUser(userIndex);
                    Assert.That(reloadedUser, Is.Not.Null, "User should exist after reload");
                    Assert.That(reloadedUser.UserName, Is.EqualTo(userName), "Username should match");
                    Assert.That(reloadedUser.Password, Is.EqualTo(0), "Password should still be cleared after reload");

                }
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }
    }
}
