//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Ndfs.Creation;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.FileSystems;
using RetroIO.Storage.Ndfs.PropertyBags;

namespace RetroIO.Tests
{
    /// <summary>
    /// Tests that the .xat property bag records WHERE a sparse file's holes are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sparse file has pages that were never allocated. Nothing in the sidecar said where they
    /// were: <c>PagesInFile</c> and <c>BytesInFile</c> together reveal THAT a file is sparse, but
    /// not the positions, so a file copied out to a host and back returned with a different layout
    /// from the one it left with. The holes key records them.
    /// </para>
    /// <para>
    /// The behaviour pinned here was measured on real hardware, not invented. The file
    /// <c>(SYSTEM)S3-CONFIG-E01:PROG</c> on a live SINTRAN pack has 89 allocated pages spread over a
    /// 99-page extent, with holes at pages 54 to 63. Transferring it between two ND-100s sends 89
    /// messages carrying page numbers 0 to 53 and then 64 to 98 - the machine skips the holes rather
    /// than sending zeros, which is why their positions have to be recorded to reproduce the file.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class NdfsSparseHoleXatTests
    {
        private string _testDir = string.Empty;

        /// <summary>
        /// Creates the scratch directory for the images these tests build.
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "RetroFS_NdfsSparseHoleTests");
            Directory.CreateDirectory(_testDir);
        }

        /// <summary>
        /// Removes the scratch directory.
        /// </summary>
        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }

        /// <summary>
        /// One NDFS page in bytes.
        /// </summary>
        private const int PageSize = 2048;

        /// <summary>
        /// Builds file contents spanning <paramref name="pageCount"/> pages, non-zero only on the
        /// pages named in <paramref name="filledPages"/>.
        /// </summary>
        /// <param name="pageCount">
        /// How many pages the file should span.
        /// </param>
        /// <param name="filledPages">
        /// Pages to fill with a non-zero byte; every other page is left zero so it holes out.
        /// </param>
        /// <returns>
        /// The file contents.
        /// </returns>
        private static byte[] SparseContent(int pageCount, int[] filledPages)
        {
            byte[] data = new byte[pageCount * PageSize];
            for (int f = 0; f < filledPages.Length; f++)
            {
                int start = filledPages[f] * PageSize;
                for (int i = start; i < start + PageSize; i++)
                {
                    data[i] = 0xA5;
                }
            }

            return data;
        }

        /// <summary>
        /// Reads the holes recorded in a property bag.
        /// </summary>
        /// <param name="bag">
        /// The property bag produced for a file.
        /// </param>
        /// <returns>
        /// The hole page indices held in <paramref name="bag"/>, or <see langword="null"/> when the
        /// key is absent.
        /// </returns>
        private static int[]? HolesFrom(IDictionary<string, object> bag)
        {
            object? value;
            if (!bag.TryGetValue(NdfsPropertyBagKeys.Holes, out value))
            {
                return null;
            }

            return (int[])value!;
        }

        /// <summary>
        /// Creates a fresh image named <paramref name="imageName"/>, writes
        /// <paramref name="contents"/> to <paramref name="path"/>, and returns the resulting
        /// property bag.
        /// </summary>
        /// <param name="imageName">
        /// File name for the scratch image.
        /// </param>
        /// <param name="path">
        /// NDFS path to write.
        /// </param>
        /// <param name="contents">
        /// The file contents.
        /// </param>
        /// <returns>
        /// The property bag for the written file.
        /// </returns>
        private IDictionary<string, object> WriteAndDescribe(string imageName, string path, byte[] contents)
        {
            string imagePath = Path.Combine(_testDir, imageName);
            NdfsImageCreator.CreateImageFromTemplate(
                imagePath,
                NdfsImageTemplate.Floppy360KB,
                directoryName: "HOLE-TEST",
                bootLoaderType: NdfsBootLoaderType.Floppy);

            using (FileStream stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);

                INdfsFileSystem? ndfs = driver.GetFileSystem();
                Assert.That(ndfs, Is.Not.Null, "driver must expose the NDFS filesystem");

                // A freshly created image has no users, and a file needs an owner.
                Assert.That(ndfs!.AddUser("SYSTEM", 64), Is.True, "SYSTEM user must be created");

                ndfs.WriteFile(path, contents);

                IDictionary<string, object>? bag = ndfs.GetFileProperties(path);
                Assert.That(bag, Is.Not.Null, "the file must have properties");

                driver.Dispose();
                return bag!;
            }
        }

        /// <summary>
        /// A file with no holes reports an empty list, which is a different claim from absent.
        /// </summary>
        [Test]
        public void SolidFile_RecordsAnEmptyHoleList()
        {
            byte[] contents = new byte[2 * PageSize];
            for (int i = 0; i < contents.Length; i++)
            {
                contents[i] = 0x11;
            }

            IDictionary<string, object> bag = WriteAndDescribe("solid.img", "SYSTEM/SOLID:DATA", contents);

            int[]? holes = HolesFrom(bag);
            Assert.That(holes, Is.Not.Null, "the holes key must be present for a file that was checked");
            Assert.That(holes!.Length, Is.EqualTo(0));
        }

        /// <summary>
        /// A sparse file records the page indices that hole out.
        /// </summary>
        [Test]
        public void SparseFile_RecordsWhereTheHolesAre()
        {
            // Six pages with real data only on the first and last, so pages 1 to 4 hole out.
            byte[] contents = SparseContent(6, new int[] { 0, 5 });

            IDictionary<string, object> bag = WriteAndDescribe("sparse.img", "SYSTEM/SPARSE:DATA", contents);

            int[]? holes = HolesFrom(bag);
            Assert.That(holes, Is.Not.Null);
            Assert.That(holes, Is.EqualTo(new int[] { 1, 2, 3, 4 }));
        }

        /// <summary>
        /// Several separate gaps are all recorded, in ascending order and inside the extent.
        /// </summary>
        [Test]
        public void SeveralGaps_AreAllRecordedInOrder()
        {
            byte[] contents = SparseContent(8, new int[] { 0, 3, 7 });

            IDictionary<string, object> bag = WriteAndDescribe("gappy.img", "SYSTEM/GAPPY:DATA", contents);

            int[]? holes = HolesFrom(bag);
            Assert.That(holes, Is.Not.Null);
            Assert.That(holes, Is.EqualTo(new int[] { 1, 2, 4, 5, 6 }));

            for (int i = 1; i < holes!.Length; i++)
            {
                Assert.That(holes[i], Is.GreaterThan(holes[i - 1]), "holes must be ascending");
            }

            for (int i = 0; i < holes.Length; i++)
            {
                Assert.That(holes[i], Is.GreaterThanOrEqualTo(0));
                Assert.That(holes[i], Is.LessThan(8), "every hole must sit inside the file extent");
            }
        }

        /// <summary>
        /// The holes survive being written to and read back from a .xat file.
        /// </summary>
        [Test]
        public void Holes_SurviveTheXatJsonRoundTrip()
        {
            byte[] contents = SparseContent(6, new int[] { 0, 5 });

            IDictionary<string, object> bag = WriteAndDescribe("roundtrip.img", "SYSTEM/RTRIP:DATA", contents);

            string json = NdfsXatSerializer.SerializeToJson(bag);
            Assert.That(json, Does.Contain("ndfs.holes"));

            IDictionary<string, object> restored = NdfsXatSerializer.DeserializeFromJson(json);

            object? value;
            Assert.That(restored.TryGetValue(NdfsPropertyBagKeys.Holes, out value), Is.True,
                "the holes key must survive the round trip");
            Assert.That(value, Is.Not.Null);
        }

        /// <summary>
        /// The holes still read back as zeros, so recording them changes no file contents.
        /// </summary>
        [Test]
        public void RecordingHoles_DoesNotChangeTheFileContents()
        {
            byte[] contents = SparseContent(6, new int[] { 0, 5 });
            string imagePath = Path.Combine(_testDir, "contents.img");

            NdfsImageCreator.CreateImageFromTemplate(
                imagePath,
                NdfsImageTemplate.Floppy360KB,
                directoryName: "HOLE-TEST",
                bootLoaderType: NdfsBootLoaderType.Floppy);

            using (FileStream stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite))
            {
                NdfsDriver driver = new NdfsDriver();
                driver.Mount(stream, readOnly: false);

                INdfsFileSystem ndfs = driver.GetFileSystem()!;
                Assert.That(ndfs.AddUser("SYSTEM", 64), Is.True, "SYSTEM user must be created");
                ndfs.WriteFile("SYSTEM/BACK:DATA", contents);

                byte[] readBack = ndfs.ReadFile("SYSTEM/BACK:DATA");

                Assert.That(readBack.Length, Is.EqualTo(contents.Length));
                for (int i = 0; i < contents.Length; i++)
                {
                    Assert.That(readBack[i], Is.EqualTo(contents[i]), "byte " + i + " changed");
                }

                driver.Dispose();
            }
        }
    }
}
