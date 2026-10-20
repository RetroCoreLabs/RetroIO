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
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tests
{
    [TestFixture]
    public class NdfsObjectDirectorySubIndexedGrowthTests
    {
        private string _testDir = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "RetroFS_NdfsObjDirGrowth_" + Path.GetRandomFileName());
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

        // Custom-sized image: 30,000 empty files each cost exactly 1 real page (their own index
        // block — empty content is a sparse hole, so no data block), plus ~938 object-directory
        // pages (30000/32), plus a handful of group-index/sub-index/user-file pages. 40,000 pages
        // leaves comfortable headroom.
        private string MakeBigImage(string name)
        {
            // Users are NOT created via NdfsImageCreationOptions.Users — that path is currently an
            // unimplemented stub (NdfsImageCreator.CreateUserFile is a no-op, see its own TODO
            // comment). Users are created at runtime via CreateDirectory, same as every other NDFS
            // test in this project.
            string path = Path.Combine(_testDir, name);
            NdfsImageCreator.CreateImage(path, new NdfsImageCreationOptions
            {
                Template = NdfsImageTemplate.Custom,
                CustomNdfsPages = 90000,
                DirectoryName = "BIGDIR-TEST",
                BootLoaderType = NdfsBootLoaderType.Winchester
            });
            return path;
        }

        [Test]
        public void CreateDirectory_UsersBeyond64_PastOldObjectFileCapacity_DoesNotThrow()
        {
            // The old plain-Indexed object file capped out at 64 users (512 index-pointer slots /
            // 8 reserved per user) — well before any file count limit is even reached. This alone
            // used to throw for user #64 onward.
            string image = MakeBigImage("many-users.img");

            using FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite);
            NdfsDriver driver = new NdfsDriver();
            driver.Mount(stream, readOnly: false);

            for (int i = 0; i < 80; i++)
            {
                Assert.DoesNotThrow(() => driver.CreateDirectory($"/USER{i}"),
                    $"creating user #{i} (past the old 64-user object-file ceiling) must not throw");
                driver.WriteFile($"/USER{i}/FILE:DAT", new byte[] { 1 });
            }

            // A new image has the SYSTEM user (as after SINTRAN's @CREATE-DIRECTORY), so the 80
            // users created above sit alongside it.
            INdfsFileSystem ndfs = driver.GetFileSystem()!;
            Assert.That(ndfs.GetUsers().Count, Is.EqualTo(81));
        }

        [Test]
        public void Write30000Files_Across120Users_AllSurviveRoundTripWithCorrectOwnership()
        {
            // 30,000 files spread across 120 users (250 each — well under the 256-file-per-user
            // cap) forces the object file well past its old 16,384-file/64-user ceiling: pageIdx
            // reaches roughly 120*8=960, requiring 2 SubIndexed groups (each covering 512 pages).
            const int userCount = 120;
            const int filesPerUser = 250;
            const int totalFiles = userCount * filesPerUser;

            string image = MakeBigImage("thirty-thousand.img");

            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);

                for (int u = 0; u < userCount; u++)
                {
                    driver.CreateDirectory($"/USER{u}");
                    for (int f = 0; f < filesPerUser; f++)
                    {
                        driver.WriteFile($"/USER{u}/F{f}:DAT", new byte[] { (byte)(u % 251), (byte)(f % 251) });
                    }
                }

                driver.Dispose();
            }

            // Remount fresh (not reusing in-memory state) and verify everything survived,
            // including that the read-side subindex fix produced correct, non-colliding
            // ObjectIndex/ownership for files in groups beyond the first.
            using (FileStream stream = new FileStream(image, FileMode.Open, FileAccess.Read))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: true);
                INdfsFileSystem ndfs = driver.GetFileSystem()!;

                var objects = ndfs.GetObjectFile().GetObjects();
                Assert.That(objects.Count, Is.EqualTo(totalFiles));

                // Spot-check users from the FIRST group (pageIdx < 512, e.g. user 0), the group
                // BOUNDARY (around pageIdx 512, i.e. user ~64), and the SECOND group (e.g. user 119)
                // — this is exactly the scenario the subindex read-side bug would have corrupted.
                foreach (int userToCheck in new[] { 0, 64, 119 })
                {
                    var user = ndfs.GetUser(FindUserIndex(ndfs, $"USER{userToCheck}"));
                    Assert.That(user, Is.Not.Null, $"USER{userToCheck} must exist");

                    int filesForThisUser = 0;
                    for (int i = 0; i < objects.Count; i++)
                    {
                        if (objects[i].UserIndex == user!.UserIndex)
                        {
                            filesForThisUser++;
                            // Ownership must decode correctly from the ObjectIndex's high byte —
                            // this is exactly what the ignored-subindex read bug would break.
                            int ownerFromObjectIndex = objects[i].ObjectIndex >> 8;
                            Assert.That(ownerFromObjectIndex, Is.EqualTo(user.UserIndex),
                                $"USER{userToCheck}'s file ObjectIndex high byte must equal their UserIndex, not collide with another user's region");
                        }
                    }
                    Assert.That(filesForThisUser, Is.EqualTo(filesPerUser), $"USER{userToCheck} must have exactly {filesPerUser} files");
                }

                // Byte-level content check on one file from each of the 3 spot-checked users.
                Assert.That(driver.ReadFile("/USER0/F0:DAT"), Is.EqualTo(new byte[] { 0, 0 }));
                Assert.That(driver.ReadFile("/USER64/F100:DAT"), Is.EqualTo(new byte[] { 64 % 251, 100 }));
                Assert.That(driver.ReadFile("/USER119/F249:DAT"), Is.EqualTo(new byte[] { 119, 249 % 251 }));
            }
        }

        [Test]
        public void DeleteAndRecreate_FileInSecondGroup_WorksCorrectly()
        {
            // Exercises delete/recreate for a user whose directory page lives in the second
            // SubIndexed group (not just create-only). USER69's pageIdx (69*8=552..559) is past
            // the first group's 512-page cap.
            const int userCount = 70;
            string image = MakeBigImage("delete-second-group.img");

            using FileStream stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite);
            NdfsDriver driver = new NdfsDriver();
            driver.Mount(stream, readOnly: false);

            for (int i = 0; i < userCount; i++)
            {
                driver.CreateDirectory($"/USER{i}");
            }

            driver.WriteFile("/USER69/FILE:DAT", new byte[] { 1, 2, 3 });
            driver.DeleteFile("/USER69/FILE:DAT");
            Assert.DoesNotThrow(() => driver.WriteFile("/USER69/FILE:DAT", new byte[] { 4, 5, 6 }));
            Assert.That(driver.ReadFile("/USER69/FILE:DAT"), Is.EqualTo(new byte[] { 4, 5, 6 }));
        }

        private static int FindUserIndex(INdfsFileSystem ndfs, string userName)
        {
            var users = ndfs.GetUsers();
            for (int i = 0; i < users.Count; i++)
            {
                if (string.Equals(users[i].UserName, userName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return users[i].UserIndex;
                }
            }
            Assert.Fail($"User '{userName}' not found");
            return -1;
        }
    }
}
