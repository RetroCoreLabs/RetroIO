//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Ndfs.Creation;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.FileSystems;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Tests
{
    /// <summary>
    /// Validates the F7/Mkdir plumbing for NDFS: IFileSystemDriver.CreateDirectory -> NdfsDriver ->
    /// NdfsFileSystem.CreateDirectory, where a root-level "directory" is an NDFS User. This is the
    /// path the RetroCommander F7 key drives on an NDFS volume. The change persists to the image.
    /// </summary>
    [TestFixture]
    public class NdfsCreateDirectoryTests
    {
        private string _testDir = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "RetroFS_NdfsMkdirTests");
            Directory.CreateDirectory(_testDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }

        // Find a user by name without LINQ (project rule: no LINQ).
        private static bool HasUser(System.Collections.Generic.IReadOnlyList<UserEntry> users, string name)
        {
            for (int i = 0; i < users.Count; i++)
            {
                if (users[i].UserName == name)
                {
                    return true;
                }
            }
            return false;
        }

        [Test]
        public void CreateDirectory_OnNdfs_CreatesUser_AndPersists()
        {
            string imagePath = Path.Combine(_testDir, "mkdir.img");
            NdfsImageCreator.CreateImageFromTemplate(
                imagePath,
                NdfsImageTemplate.Floppy360KB,
                directoryName: "MKDIR-TEST",
                bootLoaderType: NdfsBootLoaderType.Floppy);

            // --- Mount read-write and create a "directory" (= NDFS user) ---
            using (FileStream stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);

                INdfsFileSystem? ndfs = driver.GetFileSystem();
                Assert.That(ndfs, Is.Not.Null, "driver must expose the NDFS filesystem");
                Assert.That(HasUser(ndfs!.GetUsers(), "ALICE"), Is.False, "ALICE must not exist yet");

                // This is exactly what F7/Mkdir calls through the panel on an NDFS volume.
                driver.CreateDirectory("/ALICE");

                Assert.That(HasUser(ndfs.GetUsers(), "ALICE"), Is.True, "ALICE user created via CreateDirectory");

                driver.Dispose(); // flush to the image
            }

            // --- Re-mount read-only: the new user must have persisted ---
            using (FileStream stream2 = new FileStream(imagePath, FileMode.Open, FileAccess.Read))
            {
                NdfsDriver verify = new NdfsDriver();
                verify.Mount(stream2, readOnly: true);

                INdfsFileSystem? ndfs = verify.GetFileSystem();
                Assert.That(ndfs, Is.Not.Null);
                Assert.That(HasUser(ndfs!.GetUsers(), "ALICE"), Is.True, "ALICE persisted to the image");

                verify.Dispose();
            }
        }

        [Test]
        public void CreateDirectory_OnReadOnlyMount_Throws()
        {
            string imagePath = Path.Combine(_testDir, "ro.img");
            NdfsImageCreator.CreateImageFromTemplate(
                imagePath,
                NdfsImageTemplate.Floppy360KB,
                directoryName: "RO-TEST",
                bootLoaderType: NdfsBootLoaderType.Floppy);

            using FileStream stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read);
            NdfsDriver driver = new NdfsDriver();
            driver.Mount(stream, readOnly: true);

            // Creating on a read-only mount must fail rather than silently no-op.
            Assert.That(() => driver.CreateDirectory("/BOB"),
                Throws.TypeOf<System.InvalidOperationException>());

            driver.Dispose();
        }
    }
}
