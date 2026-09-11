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
    public class NdfsPasswordInstructionsTest
    {
        private static string TEST_IMAGE => TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\ndfs\FLOPPY-360KB.img");

        [Test]
        [Category("NDFS")]
        [Category("UserManagement")]
        public void GetPasswordClearInstructions_ByIndex_ReturnsValidInstructions()
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

                var firstUser = users[0];
                int userIndex = firstUser.UserIndex;

                // Act
                string? instructions = ndfs.GetPasswordClearInstructions(userIndex);

                // Assert
                Assert.That(instructions, Is.Not.Null, "Instructions should be returned");
                Assert.That(instructions, Does.Contain("CHANGE-PAGE"), "Should contain CHANGE-PAGE command");
                Assert.That(instructions, Does.Contain(firstUser.UserName), "Should contain username");
                Assert.That(instructions, Does.Contain("000000"), "Should contain clear value");
                Assert.That(instructions, Does.Contain("Give page no."), "Should contain page prompt");
                Assert.That(instructions, Does.Contain("OCTAL"), "Should mention octal");

                Console.WriteLine("=== Generated Instructions ===");
                Console.WriteLine(instructions);
            }
        }

        [Test]
        [Category("NDFS")]
        [Category("UserManagement")]
        public void GetPasswordClearInstructions_ByUsername_ReturnsValidInstructions()
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

                string userName = users[0].UserName;

                // Act
                string? instructions = ndfs.GetPasswordClearInstructions(userName);

                // Assert
                Assert.That(instructions, Is.Not.Null, "Instructions should be returned");
                Assert.That(instructions, Does.Contain(userName), "Should contain username");
                Assert.That(instructions, Does.Contain("FILE-SYSTEM-INVESTIGATOR"), "Should mention tool");
            }
        }

        [Test]
        [Category("NDFS")]
        [Category("UserManagement")]
        public void GetPasswordClearInstructions_InvalidUserIndex_ReturnsNull()
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
                string? instructions = ndfs.GetPasswordClearInstructions(999);

                // Assert
                Assert.That(instructions, Is.Null, "Should return null for invalid user index");
            }
        }

        [Test]
        [Category("NDFS")]
        [Category("UserManagement")]
        public void GetPasswordClearInstructions_InvalidUsername_ReturnsNull()
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
                string? instructions = ndfs.GetPasswordClearInstructions("NONEXISTENT");

                // Assert
                Assert.That(instructions, Is.Null, "Should return null for invalid username");
            }
        }

        [Test]
        [Category("NDFS")]
        [Category("UserManagement")]
        public void GetPasswordClearInstructions_ContainsTechnicalDetails()
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
                var firstUser = users[0];

                // Act
                string? instructions = ndfs.GetPasswordClearInstructions(firstUser.UserIndex);

                // Assert
                Assert.That(instructions, Is.Not.Null);
                Assert.That(instructions, Does.Contain("TECHNICAL DETAILS"), "Should contain technical details section");
                Assert.That(instructions, Does.Contain("Master Block Page"), "Should mention master block");
                Assert.That(instructions, Does.Contain("User File Index Page"), "Should mention index page");
                Assert.That(instructions, Does.Contain("User Data Block Page"), "Should mention data block");
                Assert.That(instructions, Does.Contain("Password Word Offset"), "Should mention word offset");
                Assert.That(instructions, Does.Contain("Current Password Hash"), "Should show current password");
            }
        }
    }
}
