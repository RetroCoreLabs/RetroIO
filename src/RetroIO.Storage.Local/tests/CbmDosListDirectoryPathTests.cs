//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests what <see cref="CbmDosFileSystem.ListDirectory(string)"/> does with its path argument.
    /// A 1541 disk has one flat directory, so only the root may be listed.
    /// </summary>
    [TestFixture]
    [Category("CBM")]
    public class CbmDosListDirectoryPathTests
    {
        // A blank 35-track D64 held in memory. Its directory sector is all zero, so the directory is empty.
        private static CbmDosFileSystem MountBlankD64()
        {
            GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.D64_35Track);
            device.Mount(new MemoryStream(new byte[174848]), false);

            CbmDosFileSystem fs = new CbmDosFileSystem(18, 18);
            fs.Initialize(device);
            return fs;
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("/")]
        public void ListDirectory_RootPath_ListsTheDirectory(string? path)
        {
            CbmDosFileSystem fs = MountBlankD64();

            Assert.That(fs.ListDirectory(path!).Length, Is.EqualTo(0));
        }

        [TestCase("/GAMES")]
        [TestCase("GAMES")]
        [TestCase("/GAMES/")]
        [TestCase("/a/b")]
        public void ListDirectory_AnyOtherPath_ThrowsDirectoryNotFound(string path)
        {
            CbmDosFileSystem fs = MountBlankD64();

            Assert.Throws<DirectoryNotFoundException>(() => fs.ListDirectory(path));
        }
    }
}
