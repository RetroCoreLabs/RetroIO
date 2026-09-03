using System;
using System.IO;

using NUnit.Framework;

using RetroIO.Storage.Ndfs.Creation;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tests
{
    /// <summary>
    /// The <c>GiveObjectBlocks</c> API and the two limits a copy loop can hit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GiveObjectBlocks</c> mirrors the SINTRAN operator command
    /// <c>@GIVE-OBJECT-BLOCKS &lt;user&gt;,&lt;count&gt;</c>. The behaviour asserted here was
    /// measured on a live SINTRAN III K pack: user BIGMAN was a default user with a reported
    /// MAXIMUM NUMBER OF FILES of 256, and after <c>@GIVE-OBJECT-BLOCKS BIGMAN,3</c> that
    /// became 1024 — so the command ADDS blocks rather than setting a total.
    /// </para>
    /// <para>
    /// See <c>docs/NDFS-Object-Blocks.md</c>.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class NdfsGiveObjectBlocksTests
    {
        private const int FilesPerObjectBlock = 256;
        private const int MaxObjectBlocks = 16;

        /// <summary>
        /// The user area these tests create and operate on.
        /// </summary>
        private const string TestUserName = "OBJUSER";

        private string _testDir = string.Empty;

        /// <summary>
        /// Creates a scratch directory for the images these tests build.
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "retrofs-give-obj-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);
        }

        /// <summary>
        /// Removes the scratch directory.
        /// </summary>
        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }

        /// <summary>
        /// Builds a small image and mounts it read-write.
        /// </summary>
        /// <param name="name">
        /// Distinguishes the image file so tests do not collide.
        /// </param>
        /// <param name="driver">
        /// Receives the mounted driver, which the caller must dispose.
        /// </param>
        /// <returns>
        /// The mounted file system.
        /// </returns>
        private INdfsFileSystem Mount(string name, out NdfsDriver driver)
        {
            string imagePath = Path.Combine(_testDir, name + ".img");
            NdfsImageCreator.CreateImageFromTemplate(
                imagePath,
                NdfsImageTemplate.Floppy360KB,
                directoryName: "OBJBLK",
                bootLoaderType: NdfsBootLoaderType.Floppy);

            FileStream stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite);
            driver = new NdfsDriver();
            driver.Mount(stream, readOnly: false);

            INdfsFileSystem? fs = driver.GetFileSystem();
            Assert.That(fs, Is.Not.Null, "driver must expose the NDFS filesystem");

            // A freshly created image has no user areas at all, and object blocks are a
            // per-user structure - so make one to operate on. On NDFS a "directory" IS a user.
            driver.CreateDirectory("/" + TestUserName);
            return fs!;
        }

        /// <summary>
        /// Returns the first user on a freshly created image.
        /// </summary>
        /// <param name="fs">
        /// The mounted file system.
        /// </param>
        /// <returns>
        /// The user entry to operate on.
        /// </returns>
        private static UserEntry FirstUser(INdfsFileSystem fs)
        {
            Assert.That(fs.GetUsers().Count, Is.GreaterThan(0), "image must have at least one user");
            return fs.GetUsers()[0];
        }

        // ---------------------------------------------------------------
        // Granting
        // ---------------------------------------------------------------

        /// <summary>
        /// The command adds blocks to the maximum; it does not set the total.
        /// </summary>
        [Test]
        public void GiveObjectBlocksAddsToTheMaximum()
        {
            NdfsDriver driver;
            INdfsFileSystem fs = Mount("add", out driver);
            using (driver)
            {
                UserEntry user = FirstUser(fs);
                Assert.That(user.MaxObjectBlocks, Is.EqualTo(1), "a fresh user has one block");

                int newMax = fs.GiveObjectBlocks(user.UserName, 3);

                Assert.That(newMax, Is.EqualTo(4));
                Assert.That(user.MaxObjectBlocks, Is.EqualTo(4));
                // 4 blocks x 256 = 1024, the figure the live machine reported for BIGMAN.
                Assert.That(fs.GetObjectBlockInfo(user.UserName).MaxFiles, Is.EqualTo(1024));
            }
        }

        /// <summary>
        /// Granting moves only the ceiling — blocks are allocated on demand, as SINTRAN does.
        /// </summary>
        [Test]
        public void GiveObjectBlocksDoesNotAllocateThem()
        {
            NdfsDriver driver;
            INdfsFileSystem fs = Mount("noalloc", out driver);
            using (driver)
            {
                UserEntry user = FirstUser(fs);

                fs.GiveObjectBlocks(user.UserName, 5);

                Assert.That(user.MaxObjectBlocks, Is.EqualTo(6));
                Assert.That(user.AllocatedObjectBlocks, Is.EqualTo(1), "granting must not pre-allocate");
            }
        }

        /// <summary>
        /// Defaults to a single block when no count is supplied.
        /// </summary>
        [Test]
        public void GiveObjectBlocksDefaultsToOne()
        {
            NdfsDriver driver;
            INdfsFileSystem fs = Mount("defaultone", out driver);
            using (driver)
            {
                UserEntry user = FirstUser(fs);
                Assert.That(fs.GiveObjectBlocks(user.UserName), Is.EqualTo(2));
            }
        }

        /// <summary>
        /// A user may also be named by index, since that is what a listing shows.
        /// </summary>
        [Test]
        public void GiveObjectBlocksAcceptsAUserIndex()
        {
            NdfsDriver driver;
            INdfsFileSystem fs = Mount("byindex", out driver);
            using (driver)
            {
                UserEntry user = FirstUser(fs);
                int newMax = fs.GiveObjectBlocks(user.UserIndex.ToString(), 2);
                Assert.That(newMax, Is.EqualTo(3));
            }
        }

        /// <summary>
        /// 16 blocks — 4096 files — is the SINTRAN version-K ceiling.
        /// </summary>
        [Test]
        public void GiveObjectBlocksRefusesToPassSixteen()
        {
            NdfsDriver driver;
            INdfsFileSystem fs = Mount("ceiling", out driver);
            using (driver)
            {
                UserEntry user = FirstUser(fs);
                fs.GiveObjectBlocks(user.UserName, 15);
                Assert.That(user.MaxObjectBlocks, Is.EqualTo(MaxObjectBlocks));

                Assert.Throws<ArgumentOutOfRangeException>(
                    () => fs.GiveObjectBlocks(user.UserName, 1));
            }
        }

        /// <summary>
        /// Asking for more than remains fails outright rather than silently clamping.
        /// </summary>
        /// <remarks>
        /// Clamping would leave the caller believing they received what they asked for, and the
        /// shortfall would only surface much later as a premature "user is full".
        /// </remarks>
        [Test]
        public void GiveObjectBlocksRejectsAGrantThatWouldOvershoot()
        {
            NdfsDriver driver;
            INdfsFileSystem fs = Mount("overshoot", out driver);
            using (driver)
            {
                UserEntry user = FirstUser(fs);

                ArgumentOutOfRangeException? err = Assert.Throws<ArgumentOutOfRangeException>(
                    () => fs.GiveObjectBlocks(user.UserName, 20));

                Assert.That(err!.Message, Does.Contain("At most 15 more"));
                Assert.That(user.MaxObjectBlocks, Is.EqualTo(1), "a refused grant must change nothing");
            }
        }

        /// <summary>
        /// A count below one, or an unknown user, is rejected.
        /// </summary>
        [Test]
        public void GiveObjectBlocksValidatesItsArguments()
        {
            NdfsDriver driver;
            INdfsFileSystem fs = Mount("validate", out driver);
            using (driver)
            {
                UserEntry user = FirstUser(fs);

                Assert.Throws<ArgumentOutOfRangeException>(() => fs.GiveObjectBlocks(user.UserName, 0));
                Assert.Throws<ArgumentException>(() => fs.GiveObjectBlocks("NO-SUCH-USER", 1));
            }
        }

        /// <summary>
        /// Byte 47 must reach the image, not merely the in-memory entry.
        /// </summary>
        [Test]
        public void GiveObjectBlocksPersistsThroughARemount()
        {
            string imagePath = Path.Combine(_testDir, "persist.img");
            NdfsImageCreator.CreateImageFromTemplate(
                imagePath,
                NdfsImageTemplate.Floppy360KB,
                directoryName: "OBJBLK",
                bootLoaderType: NdfsBootLoaderType.Floppy);

            string userName = TestUserName;
            using (FileStream stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);
                INdfsFileSystem fs = driver.GetFileSystem()!;
                driver.CreateDirectory("/" + userName);
                fs.GiveObjectBlocks(userName, 3);
                driver.Dispose();
            }

            using (FileStream stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read))
            {
                NdfsDriver verify = new NdfsDriver();
                verify.Mount(stream, readOnly: true);
                INdfsFileSystem fs = verify.GetFileSystem()!;

                Assert.That(fs.GetObjectBlockInfo(userName).MaxObjectBlocks, Is.EqualTo(4),
                    "the grant must survive a remount");
                verify.Dispose();
            }
        }

        // ---------------------------------------------------------------
        // Taking blocks back - library only, no SINTRAN equivalent
        // ---------------------------------------------------------------

        /// <summary>
        /// Lowering the maximum works while no file depends on the blocks removed.
        /// </summary>
        [Test]
        public void TakeObjectBlocksLowersTheMaximum()
        {
            NdfsDriver driver;
            INdfsFileSystem fs = Mount("take", out driver);
            using (driver)
            {
                UserEntry user = FirstUser(fs);
                fs.GiveObjectBlocks(user.UserName, 3);

                Assert.That(fs.TakeObjectBlocks(user.UserName, 2), Is.EqualTo(2));
                Assert.That(user.MaxObjectBlocks, Is.EqualTo(2));
            }
        }

        /// <summary>
        /// The maximum may never fall below the blocks actually allocated.
        /// </summary>
        /// <remarks>
        /// File numbers are derived from the block a file sits in, so dropping an allocated block
        /// would orphan every file in it.
        /// </remarks>
        [Test]
        public void TakeObjectBlocksRefusesToOrphanFiles()
        {
            NdfsDriver driver;
            INdfsFileSystem fs = Mount("orphan", out driver);
            using (driver)
            {
                UserEntry user = FirstUser(fs);
                fs.GiveObjectBlocks(user.UserName, 3);
                user.AllocatedObjectBlocks = 2;     // as if a 257th file had opened block 1

                ArgumentOutOfRangeException? err = Assert.Throws<ArgumentOutOfRangeException>(
                    () => fs.TakeObjectBlocks(user.UserName, 3));

                Assert.That(err!.Message, Does.Contain("orphan"));
                Assert.That(user.MaxObjectBlocks, Is.EqualTo(4), "a refused reduction must change nothing");
            }
        }

        // ---------------------------------------------------------------
        // The two limits, and telling them apart
        // ---------------------------------------------------------------

        /// <summary>
        /// A soft limit names the remedy: grant more blocks.
        /// </summary>
        [Test]
        public void SoftLimitMessageNamesTheRemedy()
        {
            ObjectBlockLimitException err = new ObjectBlockLimitException("BIGMAN", 2, 2, false);

            Assert.That(err.IsHardLimit, Is.False);
            Assert.That(err.Message, Does.Contain("GiveObjectBlocks"));
            Assert.That(err.Message, Does.Contain("GIVE-OBJECT-BLOCKS"));
            Assert.That(err.Message, Does.Contain("512 files"), "2 blocks x 256");
        }

        /// <summary>
        /// A hard limit must not point at a command that cannot help.
        /// </summary>
        [Test]
        public void HardLimitMessageOffersNoRemedy()
        {
            ObjectBlockLimitException err = new ObjectBlockLimitException(
                "BIGMAN", MaxObjectBlocks, MaxObjectBlocks, true);

            Assert.That(err.IsHardLimit, Is.True);
            Assert.That(err.Message, Does.Contain("hard limit"));
            Assert.That(err.Message, Does.Contain("4096 files"));
            Assert.That(err.Message, Does.Not.Contain("GiveObjectBlocks"));
        }

        /// <summary>
        /// Existing callers catch <see cref="InvalidOperationException"/>; the typed error must
        /// not break them.
        /// </summary>
        [Test]
        public void ObjectBlockLimitExceptionIsStillAnInvalidOperationException()
        {
            Assert.That(
                typeof(InvalidOperationException).IsAssignableFrom(typeof(ObjectBlockLimitException)),
                Is.True);
            Assert.That(
                typeof(InvalidOperationException).IsAssignableFrom(typeof(ObjectBlockCollisionException)),
                Is.True);
        }

        /// <summary>
        /// The report a user interface needs in order to explain a refusal.
        /// </summary>
        [Test]
        public void GetObjectBlockInfoReportsTheWholePosition()
        {
            NdfsDriver driver;
            INdfsFileSystem fs = Mount("info", out driver);
            using (driver)
            {
                UserEntry user = FirstUser(fs);
                fs.GiveObjectBlocks(user.UserName, 3);

                ObjectBlockInfo info = fs.GetObjectBlockInfo(user.UserName);

                Assert.That(info.UserName, Is.EqualTo(user.UserName));
                Assert.That(info.UserIndex, Is.EqualTo(user.UserIndex));
                Assert.That(info.MaxObjectBlocks, Is.EqualTo(4));
                Assert.That(info.AllocatedObjectBlocks, Is.EqualTo(1));
                Assert.That(info.MaxFiles, Is.EqualTo(4 * FilesPerObjectBlock));
                Assert.That(info.AllocatedFiles, Is.EqualTo(FilesPerObjectBlock));
                Assert.That(info.CanGrant, Is.True);
                Assert.That(info.GrantableBlocks, Is.EqualTo(12));
                Assert.That(info.IsFull, Is.False);
            }
        }

        /// <summary>
        /// A grant on a read-only mount is refused rather than silently lost.
        /// </summary>
        /// <remarks>
        /// It would change only the in-memory entry and vanish on unmount. Every port
        /// refuses it.
        /// </remarks>
        [Test]
        public void GiveObjectBlocksRefusesOnAReadOnlyMount()
        {
            string imagePath = Path.Combine(_testDir, "readonly.img");
            NdfsImageCreator.CreateImageFromTemplate(
                imagePath,
                NdfsImageTemplate.Floppy360KB,
                directoryName: "OBJBLK",
                bootLoaderType: NdfsBootLoaderType.Floppy);

            using (FileStream rw = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver setup = new NdfsDriver();
                setup.Mount(rw, readOnly: false);
                setup.CreateDirectory("/" + TestUserName);
                setup.Dispose();
            }

            using FileStream stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read);
            NdfsDriver driver = new NdfsDriver();
            driver.Mount(stream, readOnly: true);
            using (driver)
            {
                INdfsFileSystem fs = driver.GetFileSystem()!;
                Assert.Throws<InvalidOperationException>(() => fs.GiveObjectBlocks(TestUserName, 1));
                Assert.Throws<InvalidOperationException>(() => fs.TakeObjectBlocks(TestUserName, 1));
            }
        }

        /// <summary>
        /// At the ceiling the report says nothing more can be granted.
        /// </summary>
        [Test]
        public void GetObjectBlockInfoReportsTheCeiling()
        {
            NdfsDriver driver;
            INdfsFileSystem fs = Mount("infomax", out driver);
            using (driver)
            {
                UserEntry user = FirstUser(fs);
                fs.GiveObjectBlocks(user.UserName, 15);

                ObjectBlockInfo info = fs.GetObjectBlockInfo(user.UserName);

                Assert.That(info.MaxObjectBlocks, Is.EqualTo(MaxObjectBlocks));
                Assert.That(info.MaxFiles, Is.EqualTo(4096));
                Assert.That(info.CanGrant, Is.False);
                Assert.That(info.GrantableBlocks, Is.EqualTo(0));
            }
        }
    }
}
