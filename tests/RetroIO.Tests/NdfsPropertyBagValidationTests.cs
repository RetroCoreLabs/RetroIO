//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.FileSystems;
using RetroIO.Storage.Ndfs.PropertyBags;
using RetroIO.Storage.Ndfs.Security;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Tests
{
    /// <summary>
    /// Validates PropertyBag preservation with real file copies including sparse files.
    /// </summary>
    [TestFixture]
    public class NdfsPropertyBagValidationTests
    {
        private string _testImagePath;
        private DriverFactory _factory;
        private FileStream? _stream;
        private INdfsFileSystem? _ndfs;

        [SetUp]
        public void Setup()
        {
            _testImagePath = TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\ndfs\WD0.img");
            _factory = new DriverFactory();
            _factory.Register(new NdfsDriverProvider());

            // Mount filesystem once for all test methods
            if (File.Exists(_testImagePath))
            {
                var fsDriver = _factory.Detect(_testImagePath);
                if (fsDriver != null && fsDriver is NdfsDriver ndfsDriver)
                {
                    _stream = File.OpenRead(_testImagePath);  // Keep stream open
                    ndfsDriver.Mount(_stream, true); // read-only
                    _ndfs = ndfsDriver.GetFileSystem();
                }
            }
        }

        [TearDown]
        public void TearDown()
        {
            // Dispose stream after test completes
            _stream?.Dispose();
            _stream = null;
            _ndfs = null;
        }

        private INdfsFileSystem GetNdfsFileSystem()
        {
            if (_ndfs == null)
                Assert.Inconclusive("NDFS test image not available at: " + _testImagePath);
            return _ndfs!;
        }

        [Test]
        public void ValidatePropertyBag_AllFieldsPresent()
        {
            // Arrange
            var ndfs = GetNdfsFileSystem();

            // List files from SYSTEM user directory
            var files = ndfs.ListDirectory("SYSTEM");
            if (files == null || files.Length == 0)
            {
                // Fallback to root if SYSTEM doesn't exist
                files = ndfs.ListDirectory("");
                if (files == null || files.Length == 0)
                {
                    Assert.Inconclusive("No files found");
                    return;
                }
            }

            string testFile = files[0].Name;

            // Act
            var props = ndfs.GetFileProperties(testFile);

            // Assert - Verify ALL expected properties are present
            Assert.That(props, Is.Not.Null, "PropertyBag should not be null");

            var expectedKeys = new[]
            {
                // Core identification
                NdfsPropertyBagKeys.Header,
                NdfsPropertyBagKeys.ObjectIndex,
                NdfsPropertyBagKeys.ObjectName,
                NdfsPropertyBagKeys.Type,
                NdfsPropertyBagKeys.UserName,
                NdfsPropertyBagKeys.UserIndex,

                // Versioning
                NdfsPropertyBagKeys.ObjectEntryNextVersion,
                NdfsPropertyBagKeys.ObjectEntryPrevVersion,
                NdfsPropertyBagKeys.IsVersioned,

                // Access control
                NdfsPropertyBagKeys.AccessBits,
                NdfsPropertyBagKeys.AccessPermissionString,

                // File type
                NdfsPropertyBagKeys.FileTypeFlags,
                NdfsPropertyBagKeys.FileTypeString,
                NdfsPropertyBagKeys.FileType,

                // Device and tracking
                NdfsPropertyBagKeys.DeviceNumber,
                NdfsPropertyBagKeys.UserIndexOfReservingUser,
                NdfsPropertyBagKeys.CurrentOpenCount,
                NdfsPropertyBagKeys.TotalOpenCount,

                // Timestamps
                NdfsPropertyBagKeys.DateCreated,
                NdfsPropertyBagKeys.LastDateOpenedForRead,
                NdfsPropertyBagKeys.LastDateOpenedForWrite,

                // Size
                NdfsPropertyBagKeys.PagesInFile,
                NdfsPropertyBagKeys.BytesInFile
            };

            TestContext.Out.WriteLine($"Validating PropertyBag for file: {testFile}");
            TestContext.Out.WriteLine($"Expected keys: {expectedKeys.Length}");
            TestContext.Out.WriteLine($"Actual keys: {props.Count}");
            TestContext.Out.WriteLine("");

            int missingCount = 0;
            for (int i = 0; i < expectedKeys.Length; i++)
            {
                var key = expectedKeys[i];
                if (!props.ContainsKey(key))
                {
                    TestContext.Out.WriteLine($"MISSING: {key}");
                    missingCount++;
                }
                else
                {
                    TestContext.Out.WriteLine($"✓ {key} = {props[key]}");
                }
            }

            Assert.That(missingCount, Is.EqualTo(0), $"{missingCount} keys missing from PropertyBag");
        }

        [Test]
        public void CompareFileData_ReadWithAndWithoutPropertyBag()
        {
            // Arrange
            var ndfs = GetNdfsFileSystem();

            // List files from SYSTEM user directory
            var files = ndfs.ListDirectory("SYSTEM");
            if (files == null || files.Length == 0)
            {
                // Fallback to root if SYSTEM doesn't exist
                files = ndfs.ListDirectory("");
                if (files == null || files.Length == 0)
                {
                    Assert.Inconclusive("No files found");
                    return;
                }
            }

            string testFile = files[0].Name;

            // Act
            var props = new Dictionary<string, object>();
            byte[] dataWithProps = ndfs.ReadFileWithProperties(testFile, props);
            byte[] dataWithoutProps = ndfs.ReadFile(testFile);

            // Assert - File data should be identical
            Assert.That(dataWithProps, Is.Not.Null, "Data with PropertyBag should not be null");
            Assert.That(dataWithoutProps, Is.Not.Null, "Data without PropertyBag should not be null");
            Assert.That(dataWithProps.Length, Is.EqualTo(dataWithoutProps.Length), "Data length should match");

            bool identical = true;
            for (int i = 0; i < dataWithProps.Length; i++)
            {
                if (dataWithProps[i] != dataWithoutProps[i])
                {
                    identical = false;
                    TestContext.Out.WriteLine($"Mismatch at byte {i}: {dataWithProps[i]:X2} vs {dataWithoutProps[i]:X2}");
                    break;
                }
            }

            Assert.That(identical, Is.True, "File data should be identical with or without PropertyBag");

            // And PropertyBag should be populated
            Assert.That(props.Count, Is.GreaterThan(0), "PropertyBag should be populated");
            Assert.That(NdfsPropertyBagSerializer.HasNdfsMetadata(props), Is.True, "Should have NDFS metadata");

            TestContext.Out.WriteLine($"✓ File data identical ({dataWithProps.Length} bytes)");
            TestContext.Out.WriteLine($"✓ PropertyBag populated ({props.Count} properties)");
        }

        [Test]
        public void ValidateSparseFile_EmptyHolesFilledWithZeros()
        {
            // Arrange
            var fs = GetNdfsFileSystem();

            // Get device via reflection
            var fsType = fs.GetType();
            var deviceField = fsType.GetField("_device", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (deviceField == null)
            {
                Assert.Inconclusive("Cannot access internal device");
                return;
            }

            var device = deviceField.GetValue(fs) as IBlockDevice;
            if (device == null)
            {
                Assert.Inconclusive("Device is null");
                return;
            }

            var objectFile = fs.GetObjectFile();
            var objects = objectFile.GetObjects();

            // Find a sparse file
            ObjectEntry? sparseFile = null;
            int firstHoleIndex = -1;

            for (int i = 0; i < objects.Count; i++)
            {
                var obj = objects[i];

                if (obj.FilePointer != null &&
                    obj.FilePointer.Type == BlockPointer.PointerType.Indexed &&
                    obj.PagesInFile > 1)
                {
                    var indexBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
                    device.ReadBlock(BlockAddress.FromLBA(obj.FilePointer.BlockID), indexBuffer);

                    for (int j = 0; j < (int)System.Math.Min(obj.PagesInFile, 512); j++)
                    {
                        var bp = BlockPointer.FromBytes(indexBuffer, j * 4);
                        if (bp != null && bp.BlockID == 0)
                        {
                            sparseFile = obj;
                            firstHoleIndex = j;
                            break;
                        }
                    }

                    if (sparseFile != null)
                        break;
                }
            }

            if (sparseFile == null)
            {
                Assert.Inconclusive("No sparse files found in image");
                return;
            }

            // Act - Read the sparse file
            string filePath = $"{sparseFile.UserName}/{sparseFile.ObjectName}.{sparseFile.Type}";
            byte[] fileData = fs.ReadFile(filePath);

            // Assert - Verify empty hole is filled with zeros
            Assert.That(fileData, Is.Not.Null, "File data should not be null");

            int holeStart = firstHoleIndex * 2048;
            int holeEnd = holeStart + 2048;

            if (holeEnd <= fileData.Length)
            {
                bool allZeros = true;
                int nonZeroCount = 0;

                for (int i = holeStart; i < holeEnd; i++)
                {
                    if (fileData[i] != 0)
                    {
                        allZeros = false;
                        nonZeroCount++;
                    }
                }

                TestContext.Out.WriteLine($"Sparse file: {filePath}");
                TestContext.Out.WriteLine($"Empty hole at page {firstHoleIndex} (bytes {holeStart}-{holeEnd})");
                TestContext.Out.WriteLine($"All zeros: {allZeros}");
                TestContext.Out.WriteLine($"Non-zero bytes: {nonZeroCount}");

                Assert.That(allZeros, Is.True, $"Empty hole should be filled with zeros, found {nonZeroCount} non-zero bytes");
            }
            else
            {
                Assert.Inconclusive($"File size ({fileData.Length}) smaller than expected hole end ({holeEnd})");
            }
        }

        [Test]
        public void ValidateAccessPermissions_ParsedCorrectly()
        {
            // Arrange
            var ndfs = GetNdfsFileSystem();

            // List files from SYSTEM user directory
            var files = ndfs.ListDirectory("SYSTEM");
            if (files == null || files.Length == 0)
            {
                // Fallback to root if SYSTEM doesn't exist
                files = ndfs.ListDirectory("");
                if (files == null || files.Length == 0)
                {
                    Assert.Inconclusive("No files found");
                    return;
                }
            }

            // Act - Get properties for multiple files
            int filesChecked = 0;
            int filesWithPermissions = 0;

            for (int i = 0; i < System.Math.Min(files.Length, 10); i++)
            {
                var props = ndfs.GetFileProperties(files[i].Name);
                if (props == null)
                    continue;

                filesChecked++;

                if (props.ContainsKey(NdfsPropertyBagKeys.AccessBits))
                {
                    uint accessBits = (uint)props[NdfsPropertyBagKeys.AccessBits];
                    var permissions = new AccessPermissions((ushort)accessBits);

                    string? permStr = props[NdfsPropertyBagKeys.AccessPermissionString] as string;

                    TestContext.Out.WriteLine($"File: {files[i].Name}");
                    TestContext.Out.WriteLine($"  AccessBits: 0x{accessBits:X4}");
                    TestContext.Out.WriteLine($"  {permStr}");
                    TestContext.Out.WriteLine("");

                    // Verify permission string format
                    Assert.That(permStr, Is.Not.Null, "Permission string should not be null");
                    Assert.That(permStr, Does.Contain("Own:"), "Should contain Own permissions");
                    Assert.That(permStr, Does.Contain("Friend:"), "Should contain Friend permissions");
                    Assert.That(permStr, Does.Contain("Public:"), "Should contain Public permissions");

                    filesWithPermissions++;
                }
            }

            Assert.That(filesChecked, Is.GreaterThan(0), "Should have checked some files");
            Assert.That(filesWithPermissions, Is.GreaterThan(0), "Should have found files with permissions");

            TestContext.Out.WriteLine($"Validated permissions for {filesWithPermissions} files");
        }

        [Test]
        public void ValidateFileTypeFlags_ParsedCorrectly()
        {
            // Arrange
            var ndfs = GetNdfsFileSystem();

            // List files from SYSTEM user directory
            var files = ndfs.ListDirectory("SYSTEM");
            if (files == null || files.Length == 0)
            {
                // Fallback to root if SYSTEM doesn't exist
                files = ndfs.ListDirectory("");
                if (files == null || files.Length == 0)
                {
                    Assert.Inconclusive("No files found");
                    return;
                }
            }

            // Act - Check file types
            var fileTypeCounts = new Dictionary<string, int>();

            for (int i = 0; i < System.Math.Min(files.Length, 20); i++)
            {
                var props = ndfs.GetFileProperties(files[i].Name);
                if (props == null)
                    continue;

                if (props.ContainsKey(NdfsPropertyBagKeys.FileTypeString))
                {
                    string? fileTypeStr = props[NdfsPropertyBagKeys.FileTypeString] as string;

                    if (!string.IsNullOrEmpty(fileTypeStr))
                    {
                        if (!fileTypeCounts.ContainsKey(fileTypeStr))
                            fileTypeCounts[fileTypeStr] = 0;

                        fileTypeCounts[fileTypeStr]++;
                    }
                }
            }

            // Assert
            TestContext.Out.WriteLine("File Type Distribution:");
            foreach (var kvp in fileTypeCounts)
            {
                TestContext.Out.WriteLine($"  {kvp.Key}: {kvp.Value} files");
            }

            Assert.That(fileTypeCounts.Count, Is.GreaterThan(0), "Should have found file types");

            // Most NDFS files should be IndexedFile
            if (fileTypeCounts.ContainsKey("Indexed"))
            {
                TestContext.Out.WriteLine($"\n✓ Found {fileTypeCounts["Indexed"]} indexed files (most common type)");
            }
        }

        [Test]
        public void ValidateMetadata_SizeFieldsConsistent()
        {
            // Arrange
            var ndfs = GetNdfsFileSystem();

            // List files from SYSTEM user directory
            var files = ndfs.ListDirectory("SYSTEM");
            if (files == null || files.Length == 0)
            {
                // Fallback to root if SYSTEM doesn't exist
                files = ndfs.ListDirectory("");
                if (files == null || files.Length == 0)
                {
                    Assert.Inconclusive("No files found");
                    return;
                }
            }

            // Act - Validate size consistency
            int filesChecked = 0;
            int inconsistencies = 0;

            for (int i = 0; i < System.Math.Min(files.Length, 20); i++)
            {
                var props = ndfs.GetFileProperties(files[i].Name);
                if (props == null)
                    continue;

                filesChecked++;

                uint pagesInFile = (uint)props[NdfsPropertyBagKeys.PagesInFile];
                ulong bytesInFile = (ulong)props[NdfsPropertyBagKeys.BytesInFile];

                // Verify: bytes should be <= pages * 2048
                ulong maxBytes = (ulong)pagesInFile * 2048;

                if (bytesInFile > maxBytes)
                {
                    TestContext.Out.WriteLine($"INCONSISTENCY in {files[i].Name}:");
                    TestContext.Out.WriteLine($"  Pages: {pagesInFile}");
                    TestContext.Out.WriteLine($"  Bytes: {bytesInFile}");
                    TestContext.Out.WriteLine($"  Max possible bytes: {maxBytes}");
                    inconsistencies++;
                }
            }

            Assert.That(filesChecked, Is.GreaterThan(0), "Should have checked files");
            Assert.That(inconsistencies, Is.EqualTo(0), $"Found {inconsistencies} size inconsistencies");

            TestContext.Out.WriteLine($"✓ Validated size consistency for {filesChecked} files");
        }
    }
}
