//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Tests
{
    [TestFixture]
    public class NdfsRealDiskTests
    {
        private const string REAL_DISK_IMAGE = @"..\..\..\..\demo\test-images\ndfs\BIGDISK0-L.IMG";
        private string _imagePath;
        private DriverFactory _factory;

        [SetUp]
        public void Setup()
        {
            // Try both relative and absolute paths
            _imagePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, REAL_DISK_IMAGE);
            if (!File.Exists(_imagePath))
            {
                // Try absolute Windows path
                _imagePath = TestPaths.Resolve(@"E:\Dev\Ronny\RetroFS\demo\test-images\ndfs\BIGDISK0-L.IMG");
            }
            if (!File.Exists(_imagePath))
            {
                // Try WSL mount
                _imagePath = "/mnt/e/Dev/Ronny/RetroFS/demo/test-images/ndfs/BIGDISK0-L.IMG";
            }

            _factory = new DriverFactory();
            _factory.Register(new NdfsDriverProvider());

            if (!File.Exists(_imagePath))
            {
                Assert.Ignore($"Real NDFS disk image not found at: {_imagePath}");
            }
        }

        [Test]
        public void Test_RealDisk_ExamineMasterBlock()
        {
            // Read first page (2048 bytes) to examine master block
            using var stream = File.OpenRead(_imagePath);
            var firstPage = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            stream.ReadExactly(firstPage);

            // Parse master block
            var masterBlock = MasterBlock.FromBytes(firstPage);

            Console.WriteLine("=== NDFS Master Block Analysis ===");
            Console.WriteLine($"Directory Name: '{masterBlock.DirectoryName}'");
            Console.WriteLine($"Object File Pointer: {masterBlock.ObjectFilePointer?.BlockID:X8}");
            Console.WriteLine($"User File Pointer: {masterBlock.UserFilePointer?.BlockID:X8}");
            Console.WriteLine($"Bit File Pointer: {masterBlock.BitFilePointer?.BlockID:X8}");
            Console.WriteLine($"Unreserved Pages: {masterBlock.UnreservedPages}");
            Console.WriteLine($"Image Size: {masterBlock.ImageSize}");
            Console.WriteLine($"Directory Size: {masterBlock.DirectorySize}");
            Console.WriteLine($"Valid: {masterBlock.IsValid()}");

            // Extended info
            if (masterBlock.ExtValid)
            {
                Console.WriteLine("\n=== Extended Information ===");
                Console.WriteLine($"Pages Available: {masterBlock.ExtPagesAvailable}");
                Console.WriteLine($"Last System Number: {masterBlock.ExtLastSystemNumber}");
                Console.WriteLine($"Flag Word: {masterBlock.ExtFlagWord:X4}");
                Console.WriteLine($"CheckSum: {masterBlock.ExtCheckSum:X4}");
            }

            // Hex dump of master block area
            Console.WriteLine("\n=== Master Block Hex Dump (offset 0x7E0) ===");
            int offset = NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET;
            for (int i = 0; i < 32; i += 16)
            {
                Console.Write($"{offset + i:X4}: ");
                for (int j = 0; j < 16; j++)
                {
                    Console.Write($"{firstPage[offset + i + j]:X2} ");
                }
                Console.Write("  ");
                for (int j = 0; j < 16; j++)
                {
                    byte b = firstPage[offset + i + j];
                    Console.Write(b >= 32 && b < 127 ? (char)b : '.');
                }
                Console.WriteLine();
            }

            Assert.That(masterBlock.IsValid(), Is.True, "Master block should be valid");
        }

        [Test]
        public void Test_RealDisk_CanMount()
        {
            // Try to mount the real disk
            using var stream = File.OpenRead(_imagePath);
            var driver = _factory.Detect(_imagePath);

            Assert.That(driver, Is.Not.Null, "Should detect NDFS format");
            Assert.That(driver.GetType().Name, Is.EqualTo("NdfsDriver"));

            driver.Mount(stream, readOnly: true);

            Console.WriteLine($"FileSystem Type: {driver.FileSystemType}");
            Console.WriteLine($"Volume Label: {driver.VolumeLabel}");

            driver.Dispose();
        }

        [Test]
        public void Test_RealDisk_ListDirectory()
        {
            using var stream = File.OpenRead(_imagePath);
            var driver = new NdfsDriver();
            driver.Mount(stream, readOnly: true);

            // List root directory
            var files = driver.ListDirectory("/");

            Console.WriteLine($"\n=== Directory Listing ({files.Length} files) ===");
            foreach (var file in files)
            {
                Console.WriteLine($"  {file.Name,-20} {file.Metadata?.Size,10} bytes");
            }

            // Get filesystem info
            var ndfs = driver.GetFileSystem();
            if (ndfs != null)
            {
                var info = ndfs.DumpFileSystemInfo();
                Console.WriteLine("\n=== Filesystem Information ===");
                Console.WriteLine(info);
            }

            // Previously this test only printed the listing — it would still pass if
            // ListDirectory silently returned garbage or an empty array. Assert on what was
            // actually observed against this real fixture image (BIGDISK0-L.IMG is a known,
            // populated multi-user NDFS disk, not an empty one).
            Assert.That(files, Is.Not.Null);
            Assert.That(files.Length, Is.GreaterThan(0), "Root of a real, populated NDFS disk should list at least one entry");
            foreach (var file in files)
            {
                Assert.That(file.Name, Is.Not.Null.And.Not.Empty, "Every listed entry must have a non-empty name");
            }
            Assert.That(ndfs, Is.Not.Null, "GetFileSystem() should return a real filesystem instance for a mounted driver");
            Assert.That(ndfs!.DumpFileSystemInfo(), Is.Not.Null.And.Not.Empty);

            driver.Dispose();
        }

        [Test]
        public void Test_RealDisk_ExamineUserFile()
        {
            using var stream = File.OpenRead(_imagePath);

            // Read first page to get pointers
            var firstPage = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            stream.ReadExactly(firstPage);
            var masterBlock = MasterBlock.FromBytes(firstPage);

            // Previously this whole test was a no-op (guarded by "if UserFilePointer != null"
            // with no else, and no assertions inside the branch either) — it always passed, even
            // if the master block failed to parse the user file pointer at all. Fail loudly
            // instead: a real, mountable NDFS disk must have a user file pointer.
            Assert.That(masterBlock.UserFilePointer, Is.Not.Null, "Real NDFS disk must have a UserFilePointer in its master block");

            // Read user file page
            uint userPageNum = masterBlock.UserFilePointer!.BlockID;
            stream.Position = userPageNum * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            var userPage = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            stream.ReadExactly(userPage);

            Console.WriteLine($"\n=== User File at page {userPageNum} ===");

            // Hex dump first 256 bytes of user file
            for (int i = 0; i < 256 && i < userPage.Length; i += 16)
            {
                Console.Write($"{i:X4}: ");
                for (int j = 0; j < 16 && i + j < userPage.Length; j++)
                {
                    Console.Write($"{userPage[i + j]:X2} ");
                }
                Console.Write("  ");
                for (int j = 0; j < 16 && i + j < userPage.Length; j++)
                {
                    byte b = userPage[i + j];
                    Console.Write(b >= 32 && b < 127 ? (char)b : '.');
                }
                Console.WriteLine();
            }

            // Try to parse user count (first 2 bytes)
            ushort userCount = (ushort)((userPage[0] << 8) | userPage[1]);
            Console.WriteLine($"\nUser Count (first 2 bytes): {userCount}");

            // NOT asserting on userCount's value: empirically checked against this real fixture
            // (BIGDISK0-L.IMG, which Test_RealDisk_ListDirectory confirms has 16 real root
            // entries) and the "first 2 bytes = user count" guess reads back 0 — i.e. it's
            // wrong, or the field means something else, or is at a different offset. Asserting
            // a value on top of an unverified byte-layout guess would just encode the guess as
            // if it were confirmed fact. Left as a diagnostic printout only, same as before, but
            // with this note instead of silently pretending the guess was ever checked.
            Assert.That(userPage.Length, Is.EqualTo(NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE));
        }

        [Test]
        public void Test_RealDisk_ExamineObjectFile()
        {
            using var stream = File.OpenRead(_imagePath);

            // Read first page to get pointers
            var firstPage = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            stream.ReadExactly(firstPage);
            var masterBlock = MasterBlock.FromBytes(firstPage);

            // Previously this whole test was a no-op (guarded by "if ObjectFilePointer != null"
            // with no else, no assertions inside) — always passed even if the pointer failed to
            // parse. Fail loudly instead.
            Assert.That(masterBlock.ObjectFilePointer, Is.Not.Null, "Real NDFS disk must have an ObjectFilePointer in its master block");

            // Read object file page
            uint objPageNum = masterBlock.ObjectFilePointer!.BlockID;
            stream.Position = objPageNum * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            var objPage = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            stream.ReadExactly(objPage);

            Console.WriteLine($"\n=== Object File at page {objPageNum} ===");

            // Hex dump first 256 bytes of object file
            for (int i = 0; i < 256 && i < objPage.Length; i += 16)
            {
                Console.Write($"{i:X4}: ");
                for (int j = 0; j < 16 && i + j < objPage.Length; j++)
                {
                    Console.Write($"{objPage[i + j]:X2} ");
                }
                Console.Write("  ");
                for (int j = 0; j < 16 && i + j < objPage.Length; j++)
                {
                    byte b = objPage[i + j];
                    Console.Write(b >= 32 && b < 127 ? (char)b : '.');
                }
                Console.WriteLine();
            }

            // Try to parse object count (first 2 bytes)
            ushort objCount = (ushort)((objPage[0] << 8) | objPage[1]);
            Console.WriteLine($"\nObject Count (first 2 bytes): {objCount}");

            // NOT asserting on objCount's value — same reasoning as Test_RealDisk_ExamineUserFile:
            // the "first 2 bytes = count" byte-layout is an unverified guess in the original code
            // (comment literally says "Try to parse"), and asserting on it would encode the guess
            // as fact without having actually confirmed the real object-file structure. Only
            // asserting what's structurally verifiable.
            Assert.That(objPage.Length, Is.EqualTo(NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE));
        }

        [Test]
        public void Test_RealDisk_ExamineBitmap()
        {
            using var stream = File.OpenRead(_imagePath);

            // Read first page to get pointers
            var firstPage = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            stream.ReadExactly(firstPage);
            var masterBlock = MasterBlock.FromBytes(firstPage);

            // Previously this whole test was a no-op (guarded by "if BitFilePointer != null"
            // with no else, no assertions inside) — always passed even if the pointer failed to
            // parse. Fail loudly instead.
            Assert.That(masterBlock.BitFilePointer, Is.Not.Null, "Real NDFS disk must have a BitFilePointer in its master block");

            // Read bitmap page
            uint bitmapPageNum = masterBlock.BitFilePointer!.BlockID;
            stream.Position = bitmapPageNum * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            var bitmapPage = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            stream.ReadExactly(bitmapPage);

            Console.WriteLine($"\n=== Bitmap at page {bitmapPageNum} ===");

            // Analyze bitmap usage
            int usedPages = 0;
            int totalBits = bitmapPage.Length * 8;

            for (int i = 0; i < bitmapPage.Length; i++)
            {
                byte b = bitmapPage[i];
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((b & (1 << bit)) != 0)
                        usedPages++;
                }
            }

            Console.WriteLine($"Used pages: {usedPages} out of {totalBits} bits");
            Console.WriteLine($"Free pages: {totalBits - usedPages}");

            // Show first 128 bytes of bitmap
            Console.WriteLine("\nFirst 128 bytes of bitmap:");
            for (int i = 0; i < 128 && i < bitmapPage.Length; i += 16)
            {
                Console.Write($"{i:X4}: ");
                for (int j = 0; j < 16; j++)
                {
                    Console.Write($"{bitmapPage[i + j]:X2} ");
                }
                Console.WriteLine();
            }

            // Unlike the user/object "count" fields, this arithmetic is a plain bit-count over
            // the page we just read — not a guessed byte-layout — so it's safe to assert on
            // directly. A real, populated, non-full disk should have both used and free pages.
            Assert.That(usedPages, Is.GreaterThan(0), "A populated NDFS disk should have at least one page marked used in its bitmap");
            Assert.That(usedPages, Is.LessThan(totalBits), "A disk with any free space should have at least one page marked free");
        }
    }
}
