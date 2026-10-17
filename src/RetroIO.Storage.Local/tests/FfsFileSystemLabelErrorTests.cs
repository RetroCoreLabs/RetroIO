//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using NUnit.Framework;
using RetroIO.Storage.Local.BSD.Structures;
using RetroIO.Storage.Local.FFS.FileSystems;
using RetroIO.Storage.Local.V7.Structures;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests what <see cref="FfsFileSystem.Initialize"/> does with a BSD disk label that names a
    /// partition it cannot use. The disk is built in memory, so no image file is needed.
    /// </summary>
    [TestFixture]
    public class FfsFileSystemLabelErrorTests
    {
        [Test]
        public void Initialize_PartitionNamedByTheLabelIsUnused_SaysSo()
        {
            // Regression: this exception was thrown inside a try whose bare catch swallowed it, so
            // Initialize fell through to raw mode and failed later with a misleading superblock error.
            using MemoryBlockDevice device = V7FileSystemLabelErrorTests.MakeDisk(new BsdPartition[]
            {
                default,
                default,
                new BsdPartition { Size = 1000, Offset = 100, FsType = V7Constants.FS_BSDFFS }
            });
            FfsFileSystem fs = new FfsFileSystem { PartitionLetter = 'a' };

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => fs.Initialize(device))!;

            Assert.That(ex.Message, Does.Contain("not in use"));
        }

        [Test]
        public void Initialize_PartitionNamedByTheLabelIsNotFfs_SaysSo()
        {
            using MemoryBlockDevice device = V7FileSystemLabelErrorTests.MakeDisk(new BsdPartition[]
            {
                new BsdPartition { Size = 1000, Offset = 100, FsType = V7Constants.FS_V71K }
            });
            FfsFileSystem fs = new FfsFileSystem { PartitionLetter = 'a' };

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => fs.Initialize(device))!;

            Assert.That(ex.Message, Does.Contain("not an FFS filesystem"));
        }
    }
}
