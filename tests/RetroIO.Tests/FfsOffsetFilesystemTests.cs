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
using RetroIO.Storage.Local;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.FFS.Core;
using RetroIO.Storage.Local.FFS.Structures;

namespace RetroIO.Tests
{
    /// <summary>
    /// Tests for FFS images whose filesystem does NOT start at byte 0 and which carry no
    /// partition table to say where it does start.
    ///
    /// Reference image: an NDIX (Norsk Data UNIX) root filesystem on a di70 pack. The NDIX
    /// disk driver (kernel/MASTER/io/di.c) sets blkzero = nspc = nsect(18) * ntrak(5) = 90
    /// and adds it to every block address, so NDIX logical block 0 lands on physical sector
    /// 90. Physical cylinder 0 is skipped because it belongs to SINTRAN (label / boot /
    /// bad-track area). With DEV_BSIZE = 1024 that puts the filesystem base at byte 92160
    /// and the primary superblock at 92160 + 8192 = 100352.
    /// </summary>
    [TestFixture]
    public class FfsOffsetFilesystemTests
    {
        private const string NdixImagePath = @"E:\Dev\Ronny\NDIX-C\rootfs_mkproto.img";

        /// <summary>Filesystem base: 90 sectors of 1024 bytes (one di70 cylinder).</summary>
        private const long NdixFilesystemBase = 90L * 1024L;

        /// <summary>Primary superblock: filesystem base + SBLOCK_OFFSET.</summary>
        private const long NdixSuperblockOffset = NdixFilesystemBase + 8192L;

        [Test]
        [Category("FFS")]
        [Category("Detection")]
        public void Locator_NdixImage_FindsFilesystemBaseAtCylinderOne()
        {
            if (!File.Exists(NdixImagePath))
            {
                Assert.Ignore($"Test skipped: NDIX image not found at {NdixImagePath}");
                return;
            }

            using var stream = File.OpenRead(NdixImagePath);

            bool found = FfsSuperblockLocator.TryLocate(stream, out long baseOffset, out FfsSuperblock? sb);

            Assert.That(found, Is.True, "Locator should find the FFS superblock");
            Assert.That(baseOffset, Is.EqualTo(NdixFilesystemBase),
                "Filesystem base should be sector 90 * 1024 bytes (one di70 cylinder)");
            Assert.That(sb, Is.Not.Null);
            Assert.That(sb!.fs_magic, Is.EqualTo(FfsConstants.FS_MAGIC));

            TestContext.Out.WriteLine($"Filesystem base : {baseOffset} (superblock at {baseOffset + 8192})");
            TestContext.Out.WriteLine($"fs_bsize/fs_fsize/fs_frag : {sb.fs_bsize}/{sb.fs_fsize}/{sb.fs_frag}");
            TestContext.Out.WriteLine($"fs_size/fs_ncg/fs_ipg     : {sb.fs_size}/{sb.fs_ncg}/{sb.fs_ipg}");
        }

        [Test]
        [Category("FFS")]
        [Category("Detection")]
        public void Locator_NdixImage_SuperblockLandsAtDocumentedOffset()
        {
            if (!File.Exists(NdixImagePath))
            {
                Assert.Ignore($"Test skipped: NDIX image not found at {NdixImagePath}");
                return;
            }

            using var stream = File.OpenRead(NdixImagePath);

            FfsSuperblockLocator.TryLocate(stream, out long baseOffset, out _);

            // The kernel reads the superblock at devaddr 98 => byte 0x18800 = 100352,
            // and fs_magic sits 1372 bytes into the structure => 101724 (0x18D5C).
            Assert.That(baseOffset + 8192L, Is.EqualTo(NdixSuperblockOffset),
                "Superblock should be at byte 100352 (0x18800)");

            byte[] magic = new byte[4];
            stream.Position = NdixSuperblockOffset + 1372L;
            stream.ReadExactly(magic, 0, 4);

            uint value = ((uint)magic[0] << 24) | ((uint)magic[1] << 16) | ((uint)magic[2] << 8) | magic[3];
            Assert.That(value, Is.EqualTo(FfsConstants.FS_MAGIC),
                "Big-endian fs_magic should be at byte 101724 (0x18D5C)");
        }

        [Test]
        [Category("FFS")]
        [Category("Detection")]
        public void Locator_NdixImage_FilesystemFitsImageExactly()
        {
            if (!File.Exists(NdixImagePath))
            {
                Assert.Ignore($"Test skipped: NDIX image not found at {NdixImagePath}");
                return;
            }

            using var stream = File.OpenRead(NdixImagePath);
            long imageLength = stream.Length;

            FfsSuperblockLocator.TryLocate(stream, out long baseOffset, out FfsSuperblock? sb);

            Assert.That(sb, Is.Not.Null);

            // base + fs_size * fs_fsize must account for the whole image. This size check is
            // what stops a backup superblock being mistaken for the filesystem base.
            long declared = baseOffset + (long)sb!.fs_size * sb.fs_fsize;
            Assert.That(declared, Is.EqualTo(imageLength),
                "Filesystem extent should exactly match the image size");
        }

        [Test]
        [Category("FFS")]
        [Category("Detection")]
        public void DetectFfs_NdixImage_ReturnsHighConfidence()
        {
            if (!File.Exists(NdixImagePath))
            {
                Assert.Ignore($"Test skipped: NDIX image not found at {NdixImagePath}");
                return;
            }

            var driverFactory = new DriverFactory();
            driverFactory.Register(new FfsDriverProvider());

            var confidences = driverFactory.GetDetectionConfidences(NdixImagePath, "FFS");

            TestContext.Out.WriteLine($"FFS confidence: {confidences["FFS"]:P0}");

            Assert.That(confidences["FFS"], Is.GreaterThan(0.5f),
                "FFS confidence should be > 50% for an offset filesystem with no partition table");
        }

        [Test]
        [Category("FFS")]
        [Category("Integration")]
        public void MountFfs_NdixImage_AutoDiscoversBaseAndListsRoot()
        {
            if (!File.Exists(NdixImagePath))
            {
                Assert.Ignore($"Test skipped: NDIX image not found at {NdixImagePath}");
                return;
            }

            using var driver = new FfsDriver();
            driver.SetRawMode();

            using var stream = File.OpenRead(NdixImagePath);
            driver.Mount(stream, readOnly: true);

            Assert.That(driver.FilesystemOffsetBytes, Is.EqualTo(NdixFilesystemBase),
                "Mount should have discovered the filesystem base by scanning");

            DirectoryEntry[] entries = driver.ListDirectory("/");

            TestContext.Out.WriteLine($"Root entries ({entries.Length}):");
            for (int i = 0; i < entries.Length; i++)
            {
                TestContext.Out.WriteLine($"  {entries[i].Name}");
            }

            // Root of this mkproto-built filesystem: lost+found, etc, dev.
            Assert.That(FindEntry(entries, "lost+found"), Is.True, "Root should contain lost+found");
            Assert.That(FindEntry(entries, "etc"), Is.True, "Root should contain etc");
            Assert.That(FindEntry(entries, "dev"), Is.True, "Root should contain dev");
        }

        [Test]
        [Category("FFS")]
        [Category("Integration")]
        public void MountFfs_NdixImage_ExplicitOffsetMatchesDiscovered()
        {
            if (!File.Exists(NdixImagePath))
            {
                Assert.Ignore($"Test skipped: NDIX image not found at {NdixImagePath}");
                return;
            }

            using var driver = new FfsDriver();
            driver.SetFilesystemOffset(NdixFilesystemBase);

            using var stream = File.OpenRead(NdixImagePath);
            driver.Mount(stream, readOnly: true);

            Assert.That(driver.FilesystemOffsetBytes, Is.EqualTo(NdixFilesystemBase));

            DirectoryEntry[] entries = driver.ListDirectory("/");
            Assert.That(FindEntry(entries, "etc"), Is.True,
                "Explicitly pinned offset should mount the same filesystem");
        }

        [Test]
        [Category("FFS")]
        [Category("Detection")]
        public void Locator_ImageWithoutFfs_ReturnsFalse()
        {
            // A buffer of plausible-looking noise must not be reported as a filesystem.
            byte[] noise = new byte[512 * 1024];
            for (int i = 0; i < noise.Length; i++)
            {
                noise[i] = (byte)(i * 31);
            }

            using var stream = new MemoryStream(noise);

            bool found = FfsSuperblockLocator.TryLocate(stream, out long baseOffset, out FfsSuperblock? sb);

            Assert.That(found, Is.False, "Should not find a filesystem in non-FFS data");
            Assert.That(baseOffset, Is.EqualTo(-1));
            Assert.That(sb, Is.Null);
        }

        [Test]
        [Category("FFS")]
        [Category("Integration")]
        public void VolumeInfo_NdixImage_ReportsBaseOffsetAndGeometry()
        {
            if (!File.Exists(NdixImagePath))
            {
                Assert.Ignore($"Test skipped: NDIX image not found at {NdixImagePath}");
                return;
            }

            using var driver = new FfsDriver();
            driver.SetRawMode();

            using var stream = File.OpenRead(NdixImagePath);
            driver.Mount(stream, readOnly: true);

            VolumeInfo info = VolumeInfoBuilder.Build(driver, NdixImagePath, isReadOnly: true);

            Assert.That(info, Is.Not.Null);
            Assert.That(info.FileSystemType, Does.StartWith("FFS"));
            Assert.That(info.Sections.Count, Is.GreaterThan(0), "Should report at least one section");

            // The whole point of the Location section: a filesystem that does not start at 0
            // must say so, otherwise a mis-detected volume looks identical to a correct one.
            string location = FindFieldValue(info, "Location", "Filesystem base");
            Assert.That(location, Does.Contain("92,160").Or.Contain("92160"),
                "Location section should report the 92160-byte base");

            string blockSize = FindFieldValue(info, "Geometry", "Block size");
            Assert.That(blockSize, Does.Contain("8,192").Or.Contain("8192"));

            string groups = FindFieldValue(info, "Cylinder Groups", "Group count");
            Assert.That(groups, Is.EqualTo("6"));

            for (int s = 0; s < info.Sections.Count; s++)
            {
                TestContext.Out.WriteLine($"== {info.Sections[s].Title} ==");
                for (int f = 0; f < info.Sections[s].Fields.Count; f++)
                {
                    var fld = info.Sections[s].Fields[f];
                    TestContext.Out.WriteLine($"   {fld.Label,-22}{fld.Value}   [{fld.Detail}]");
                }
            }
        }

        [Test]
        [Category("FFS")]
        public void VolumeInfo_UnmountedDriver_FallsBackToGenericDescription()
        {
            // A driver that cannot describe its volume must still produce usable info rather
            // than throwing, so the UI never ends up with a dead menu item.
            using var driver = new FfsDriver();

            VolumeInfo info = VolumeInfoBuilder.Build(driver, "nowhere.img", isReadOnly: true);

            Assert.That(info, Is.Not.Null);
            Assert.That(info.Sections.Count, Is.GreaterThan(0));
            Assert.That(FindFieldValue(info, "Capabilities", "Read"), Is.EqualTo("Yes"));
        }

        /// <summary>
        /// Returns the value of a named field in a named section, or an empty string.
        /// </summary>
        private static string FindFieldValue(VolumeInfo info, string sectionTitle, string label)
        {
            for (int s = 0; s < info.Sections.Count; s++)
            {
                if (!string.Equals(info.Sections[s].Title, sectionTitle, StringComparison.Ordinal))
                {
                    continue;
                }

                for (int f = 0; f < info.Sections[s].Fields.Count; f++)
                {
                    if (string.Equals(info.Sections[s].Fields[f].Label, label, StringComparison.Ordinal))
                    {
                        return info.Sections[s].Fields[f].Value;
                    }
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Case-sensitive name lookup. FFS is a case-sensitive filesystem.
        /// </summary>
        private static bool FindEntry(DirectoryEntry[] entries, string name)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (string.Equals(entries[i].Name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
