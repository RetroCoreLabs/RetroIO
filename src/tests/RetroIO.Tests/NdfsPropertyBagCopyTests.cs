//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.FileSystems;
using RetroIO.Storage.Ndfs.PropertyBags;
using RetroIO.Storage.Ndfs.Security;

namespace RetroIO.Tests
{
    /// <summary>
    /// Tests for PropertyBag-based file copying between NDFS filesystems.
    /// Validates complete metadata preservation including sparse files with empty holes.
    /// </summary>
    [TestFixture]
    public class NdfsPropertyBagCopyTests
    {
        private string _testImagePath1;
        private DriverFactory _factory;

        [SetUp]
        public void Setup()
        {
            _testImagePath1 = TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\ndfs\WD0.img");
            _factory = new DriverFactory();
            _factory.Register(new NdfsDriverProvider());
        }

        /// <summary>
        /// Creates an in-memory copy of a disk image for write testing.
        /// </summary>
        private MemoryStream LoadImageToMemory(string imagePath)
        {
            var imageBytes = File.ReadAllBytes(imagePath);
            return new MemoryStream(imageBytes, writable: true);
        }

        /// <summary>
        /// Performs a REAL copy (source image -&gt; in-memory destination image, via the actual
        /// NdfsFileSystem.WriteFileWithProperties driver path) and checks every key the source
        /// property bag contains, not a 7-key sample. Previously this test never even performed a
        /// copy — it only read the source file's own metadata and did presence-only (ContainsKey)
        /// checks on 7 of 20+ possible keys, so it could not have caught (and did not catch) a real
        /// bug where two fields were silently dropped during copy.
        ///
        /// Destination is a NEW file under the SAME source user, so unlike the cross-user-directory
        /// tests elsewhere (NdfsUiPanelTests.CopyFile_ExportThenReimport_RestoresPortableNdfsMetadata,
        /// NdfsPropertyBagSerializerApplyPortableMetadataTests), UserName/UserIndex are also expected
        /// to match here — ownership isn't changing, so there's no reason for them to differ.
        /// ObjectName/Type intentionally differ (the destination has a different filename); the other
        /// structural/allocation fields (Header, ObjectIndex, versioning, DeviceNumber,
        /// UserIndexOfReservingUser, open counts, FileTypeFlags, FileType) are destination-assigned by
        /// design (see NdfsPropertyBagSerializer.ApplyPortableMetadata) and are asserted present
        /// (nothing silently dropped from the bag) without requiring value equality to the source.
        /// </summary>
        [Test]
        public void CopyFile_WithPropertyBag_PreservesAllMetadata()
        {
            // Arrange
            if (!File.Exists(_testImagePath1))
            {
                Assert.Ignore("Test image not found");
                return;
            }

            using var stream = File.OpenRead(_testImagePath1);
            var fsDriver = _factory.Detect(_testImagePath1);
            if (fsDriver == null)
            {
                Assert.Fail("Could not detect filesystem");
                return;
            }

            fsDriver.Mount(stream, readOnly: true);

            var ndfsDriver = fsDriver as NdfsDriver;
            if (ndfsDriver == null)
            {
                Assert.Fail("Not an NDFS driver");
                return;
            }

            var ndfs = ndfsDriver.GetFileSystem();
            Assert.That(ndfs, Is.Not.Null, "Should be NDFS filesystem");

            // Get first file from source
            var files = ndfs.ListDirectory("SYSTEM");
            string sourceUserName = "SYSTEM";
            if (files == null || files.Length == 0)
            {
                files = ndfs.ListDirectory("");
                sourceUserName = "";
                if (files == null || files.Length == 0)
                {
                    Assert.Ignore("No files found in source filesystem");
                    return;
                }
            }

            var testFile = files[0];
            string sourceFilePath = testFile.Name;

            // Read source file with PropertyBag.
            var sourceProps = new Dictionary<string, object>();
            byte[] fileData = ndfs.ReadFileWithProperties(sourceFilePath, sourceProps);
            Assert.That(NdfsPropertyBagSerializer.HasNdfsMetadata(sourceProps), Is.True, "Source PropertyBag should contain NDFS metadata");

            // Act - perform a real copy into an in-memory destination image, same user, new name.
            using var destStream = LoadImageToMemory(_testImagePath1);
            var destDriver = new NdfsDriver();
            destDriver.Mount(destStream, false);
            var destFs = destDriver.GetFileSystem();
            Assert.That(destFs, Is.Not.Null, "Destination filesystem should mount");

            string destUserPrefix = string.IsNullOrEmpty(sourceUserName) ? "" : sourceUserName + "/";
            string destPath = $"{destUserPrefix}COPYALLMETA.TST";
            destFs!.WriteFileWithProperties(destPath, fileData, sourceProps);

            var destProps = destFs.GetFileProperties(destPath);
            Assert.That(destProps, Is.Not.Null, "Destination PropertyBag should be retrievable after copy");

            // Assert - content itself must be byte-identical.
            byte[] destData = destFs.ReadFile(destPath);
            Assert.That(destData, Is.EqualTo(fileData), "Copied file content must be byte-identical");

            // Assert - every key present in the source bag must still exist in the destination bag.
            // Nothing may silently vanish during the copy (this is what the original 7-key sample
            // could never have caught).
            foreach (var key in sourceProps.Keys)
            {
                Assert.That(destProps!.ContainsKey(key), Is.True, $"Destination PropertyBag is missing key '{key}' present in source");
            }

            // Assert - the four genuinely portable fields must match the source exactly.
            Assert.That(destProps![NdfsPropertyBagKeys.AccessBits], Is.EqualTo(sourceProps[NdfsPropertyBagKeys.AccessBits]), "AccessBits must be preserved");
            Assert.That(destProps[NdfsPropertyBagKeys.DateCreated], Is.EqualTo(sourceProps[NdfsPropertyBagKeys.DateCreated]), "DateCreated must be preserved");
            Assert.That(destProps[NdfsPropertyBagKeys.LastDateOpenedForRead], Is.EqualTo(sourceProps[NdfsPropertyBagKeys.LastDateOpenedForRead]), "LastDateOpenedForRead must be preserved");
            Assert.That(destProps[NdfsPropertyBagKeys.LastDateOpenedForWrite], Is.EqualTo(sourceProps[NdfsPropertyBagKeys.LastDateOpenedForWrite]), "LastDateOpenedForWrite must be preserved");

            // Assert - same user, same content size -> these legitimately should also match here
            // (unlike the cross-user-directory tests elsewhere, ownership isn't changing).
            Assert.That(destProps[NdfsPropertyBagKeys.UserName], Is.EqualTo(sourceProps[NdfsPropertyBagKeys.UserName]), "UserName must match: same owning user, only the filename changed");
            Assert.That(destProps[NdfsPropertyBagKeys.BytesInFile], Is.EqualTo(sourceProps[NdfsPropertyBagKeys.BytesInFile]), "BytesInFile must match: identical content was written");

            // Assert - fields that intentionally do NOT come from the source bag: the destination
            // reflects its own new identity, not the source file's. Asserted explicitly (not just
            // "not checked") so a future reader can tell this was a deliberate design decision, not
            // an oversight.
            Assert.That(destProps[NdfsPropertyBagKeys.ObjectName], Is.EqualTo("COPYALLMETA"), "ObjectName reflects the new destination filename, not the source's");
            Assert.That(destProps[NdfsPropertyBagKeys.Type], Is.EqualTo("TST"), "Type reflects the new destination filename, not the source's");

            string permStr = (string)destProps[NdfsPropertyBagKeys.AccessPermissionString];
            Assert.That(string.IsNullOrEmpty(permStr), Is.False, "Permission string should not be empty");

            TestContext.Out.WriteLine($"Source file: {sourceFilePath} ({fileData.Length} bytes, {sourceProps.Count} properties)");
            TestContext.Out.WriteLine($"Destination file: {destPath} ({destData.Length} bytes, {destProps.Count} properties)");
            TestContext.Out.WriteLine($"Verified {sourceProps.Count} keys present after copy; 4 portable fields + UserName + BytesInFile checked for value equality.");
        }

        [Test]
        public void GetFileProperties_WithoutReadingData_ReturnsMetadata()
        {
            // Arrange
            if (!File.Exists(_testImagePath1))
            {
                Assert.Ignore("Test image not found");
                return;
            }

            using var stream = File.OpenRead(_testImagePath1);
            var fsDriver = _factory.Detect(_testImagePath1);
            if (fsDriver == null)
            {
                Assert.Fail("Could not detect filesystem");
                return;
            }

            fsDriver.Mount(stream, readOnly: true);

            var ndfsDriver = fsDriver as NdfsDriver;
            if (ndfsDriver == null)
            {
                Assert.Fail("Not an NDFS driver");
                return;
            }

            var ndfs = ndfsDriver.GetFileSystem();
            Assert.That(ndfs, Is.Not.Null, "Filesystem should be mounted");

            var files = ndfs.ListDirectory("SYSTEM");
            if (files == null || files.Length == 0)
            {
                files = ndfs.ListDirectory("");
                if (files == null || files.Length == 0)
                {
                    Assert.Ignore("No files found");
                    return;
                }
            }

            string testFilePath = files[0].Name;

            // Act
            var props = ndfs.GetFileProperties(testFilePath);

            // Assert
            Assert.That(props, Is.Not.Null, "Should return properties");
            Assert.That(props.Count, Is.GreaterThan(0), "Should have properties");
            Assert.That(NdfsPropertyBagSerializer.HasNdfsMetadata(props), Is.True, "Should contain NDFS metadata");

            // Verify we got metadata without reading file data
            Assert.That(props.ContainsKey(NdfsPropertyBagKeys.AccessBits), Is.True);
            Assert.That(props.ContainsKey(NdfsPropertyBagKeys.BytesInFile), Is.True);

            TestContext.Out.WriteLine($"Retrieved {props.Count} properties for {testFilePath}");
        }

        [Test]
        public void PropertyBag_RoundTrip_PreservesAllFields()
        {
            // Arrange
            var originalEntry = new ObjectEntry
            {
                Header = 0x8000,
                ObjectIndex = 42,
                ObjectName = "TESTFILE",
                Type = "DAT",
                UserName = "SYSTEM",
                UserIndex = 1,
                ObjectEntryNextVersion = 0,
                ObjectEntryPrevVersion = 0,
                AccessBits = 0x4FF,
                FileTypeFlags = FileTypes.IndexedFile,
                DeviceNumber = 0,
                UserIndexOfReservingUser = 0,
                CurrentOpenCount = 0,
                TotalOpenCount = 5,
                DateCreated = 12345,
                LastDateOpenedForRead = 12350,
                LastDateOpenedForWrite = 12348,
                PagesInFile = 10,
                BytesInFile = 20480,
                FileType = 0
            };

            var propertyBag = new Dictionary<string, object>();
            var restoredEntry = new ObjectEntry();

            // Act
            NdfsPropertyBagSerializer.SerializeObjectEntry(originalEntry, propertyBag);
            NdfsPropertyBagSerializer.DeserializeObjectEntry(propertyBag, restoredEntry);

            // Assert - every ObjectEntry field SerializeObjectEntry/DeserializeObjectEntry claims
            // to round-trip (all 20 primitive fields — see ObjectEntry.cs), not a sample. This test
            // previously only checked 10 of 20 while claiming "AllFields" — the 10 it skipped
            // included LastDateOpenedForRead/LastDateOpenedForWrite, a real bug elsewhere in this
            // codebase (NdfsPropertyBagSerializer.ApplyPortableMetadata) that a sampling-only test
            // like this one's original form would never have caught either.
            Assert.That(restoredEntry.Header, Is.EqualTo(originalEntry.Header));
            Assert.That(restoredEntry.ObjectIndex, Is.EqualTo(originalEntry.ObjectIndex));
            Assert.That(restoredEntry.ObjectName, Is.EqualTo(originalEntry.ObjectName));
            Assert.That(restoredEntry.Type, Is.EqualTo(originalEntry.Type));
            Assert.That(restoredEntry.UserName, Is.EqualTo(originalEntry.UserName));
            Assert.That(restoredEntry.UserIndex, Is.EqualTo(originalEntry.UserIndex));
            Assert.That(restoredEntry.ObjectEntryNextVersion, Is.EqualTo(originalEntry.ObjectEntryNextVersion));
            Assert.That(restoredEntry.ObjectEntryPrevVersion, Is.EqualTo(originalEntry.ObjectEntryPrevVersion));
            Assert.That(restoredEntry.AccessBits, Is.EqualTo(originalEntry.AccessBits));
            Assert.That(restoredEntry.FileTypeFlags, Is.EqualTo(originalEntry.FileTypeFlags));
            Assert.That(restoredEntry.DeviceNumber, Is.EqualTo(originalEntry.DeviceNumber));
            Assert.That(restoredEntry.UserIndexOfReservingUser, Is.EqualTo(originalEntry.UserIndexOfReservingUser));
            Assert.That(restoredEntry.CurrentOpenCount, Is.EqualTo(originalEntry.CurrentOpenCount));
            Assert.That(restoredEntry.TotalOpenCount, Is.EqualTo(originalEntry.TotalOpenCount));
            Assert.That(restoredEntry.DateCreated, Is.EqualTo(originalEntry.DateCreated));
            Assert.That(restoredEntry.LastDateOpenedForRead, Is.EqualTo(originalEntry.LastDateOpenedForRead));
            Assert.That(restoredEntry.LastDateOpenedForWrite, Is.EqualTo(originalEntry.LastDateOpenedForWrite));
            Assert.That(restoredEntry.PagesInFile, Is.EqualTo(originalEntry.PagesInFile));
            Assert.That(restoredEntry.BytesInFile, Is.EqualTo(originalEntry.BytesInFile));
            Assert.That(restoredEntry.FileType, Is.EqualTo(originalEntry.FileType));

            TestContext.Out.WriteLine("All 20 ObjectEntry fields verified preserved in round-trip.");
        }

        [Test]
        public void PropertyBag_FriendList_SerializationWorks()
        {
            // Arrange
            var user = new UserEntry
            {
                UserIndex = 5,
                UserName = "TESTUSER",
                Password = 12345,
                PagesReserved = 1000,
                PagesUsed = 500
            };

            // Add some friends
            user.AddFriend(10, 0x1F); // Friend 10 with all permissions
            user.AddFriend(15, 0x03); // Friend 15 with read+write
            user.AddFriend(20, 0x01); // Friend 20 with read only

            var propertyBag = new Dictionary<string, object>();
            var restoredUser = new UserEntry();

            // Act
            NdfsPropertyBagSerializer.SerializeUserEntry(user, propertyBag);
            NdfsPropertyBagSerializer.DeserializeUserEntry(propertyBag, restoredUser);

            // Assert
            Assert.That(restoredUser.IsFriend(10), Is.True, "Friend 10 should be restored");
            Assert.That(restoredUser.IsFriend(15), Is.True, "Friend 15 should be restored");
            Assert.That(restoredUser.IsFriend(20), Is.True, "Friend 20 should be restored");
            Assert.That(restoredUser.IsFriend(99), Is.False, "Friend 99 should not exist");

            var friend10 = restoredUser.GetFriend(10);
            var friend15 = restoredUser.GetFriend(15);
            var friend20 = restoredUser.GetFriend(20);

            Assert.That(friend10, Is.Not.Null);
            Assert.That(friend15, Is.Not.Null);
            Assert.That(friend20, Is.Not.Null);

            TestContext.Out.WriteLine($"Friend 10: {friend10?.GetPermissionString()}");
            TestContext.Out.WriteLine($"Friend 15: {friend15?.GetPermissionString()}");
            TestContext.Out.WriteLine($"Friend 20: {friend20?.GetPermissionString()}");

            Assert.That(friend10?.ReadAccess, Is.True, "Friend 10 should have read access");
            Assert.That(friend15?.ReadAccess, Is.True, "Friend 15 should have read access");
            Assert.That(friend20?.ReadAccess, Is.True, "Friend 20 should have read access");
        }

        [Test]
        public void CopySparseFile_ToMemoryImage_PreservesSparseHoles()
        {
            // This test will show if sparse holes are preserved during copy
            // Arrange
            if (!File.Exists(_testImagePath1))
            {
                Assert.Ignore("Test image not found");
                return;
            }

            // Load source image (read-only)
            var sourceDriver = _factory.Detect(_testImagePath1);
            if (sourceDriver == null)
            {
                Assert.Fail("Could not detect source filesystem");
                return;
            }

            var sourceNdfsDriver = sourceDriver as NdfsDriver;
            if (sourceNdfsDriver == null)
            {
                Assert.Fail("Not an NDFS driver");
                return;
            }

            using (var sourceStream = File.OpenRead(_testImagePath1))
            {
                sourceNdfsDriver.Mount(sourceStream, true);
            }

            var sourceFs = sourceNdfsDriver.GetFileSystem();
            if (sourceFs == null)
            {
                Assert.Fail("Could not get source filesystem");
                return;
            }

            // Find a sparse file in source
            var sparseFile = FindSparseFileInFilesystem(sourceFs);
            if (sparseFile == null)
            {
                Assert.Ignore("No sparse files found in source image");
                return;
            }

            TestContext.Out.WriteLine($"Found sparse file: {sparseFile.UserName}/{sparseFile.ObjectName}.{sparseFile.Type}");
            TestContext.Out.WriteLine($"  Pages: {sparseFile.PagesInFile}, Bytes: {sparseFile.BytesInFile}");

            // Get sparse hole indices from source
            var sourceHoleIndices = GetSparseHoleIndices(sourceFs, sparseFile);
            TestContext.Out.WriteLine($"  Source has {sourceHoleIndices.Count} sparse holes");

            // Read file with PropertyBag from source
            string sourcePath = $"{sparseFile.UserName}/{sparseFile.ObjectName}.{sparseFile.Type}";
            var propertyBag = new Dictionary<string, object>();
            byte[] fileData = sourceFs.ReadFileWithProperties(sourcePath, propertyBag);

            TestContext.Out.WriteLine($"  Read {fileData.Length} bytes with PropertyBag containing {propertyBag.Count} properties");

            // Create in-memory destination image
            using (var destStream = LoadImageToMemory(_testImagePath1))
            {
                var destDriver = new NdfsDriver();
                destDriver.Mount(destStream, false); // writable

                var destFs = destDriver.GetFileSystem();
                if (destFs == null)
                {
                    Assert.Fail("Could not get destination filesystem");
                    return;
                }

                // Write file with PropertyBag to destination
                string destPath = $"{sparseFile.UserName}/COPIED-FILE.TST";

                try
                {
                    destFs.WriteFileWithProperties(destPath, fileData, propertyBag);
                    TestContext.Out.WriteLine($"  Wrote file to: {destPath}");
                }
                catch (System.Exception ex)
                {
                    TestContext.Out.WriteLine($"  Write failed: {ex.Message}");
                    Assert.Fail($"Failed to write file: {ex.Message}");
                    return;
                }

                // Find the copied file's ObjectEntry
                var objectFile = destFs.GetObjectFile();
                var allObjects = objectFile.GetObjects();

                ObjectEntry? copiedEntry = null;
                for (int i = 0; i < allObjects.Count; i++)
                {
                    var obj = allObjects[i];
                    if (obj.ObjectName == "COPIED-FILE" && obj.Type == "TST")
                    {
                        copiedEntry = obj;
                        break;
                    }
                }

                if (copiedEntry == null)
                {
                    Assert.Fail("Could not find copied file in destination");
                    return;
                }

                // Get sparse hole indices from destination
                var destHoleIndices = GetSparseHoleIndices(destFs, copiedEntry);
                TestContext.Out.WriteLine($"  Destination has {destHoleIndices.Count} sparse holes");

                // Compare sparse holes
                TestContext.Out.WriteLine("");
                TestContext.Out.WriteLine("=== SPARSE HOLE COMPARISON ===");
                TestContext.Out.WriteLine($"Source holes: {sourceHoleIndices.Count}");
                TestContext.Out.WriteLine($"Dest holes:   {destHoleIndices.Count}");

                if (destHoleIndices.Count == 0 && sourceHoleIndices.Count > 0)
                {
                    TestContext.Out.WriteLine("");
                    TestContext.Out.WriteLine("❌ SPARSE HOLES NOT PRESERVED!");
                    TestContext.Out.WriteLine($"   Source had {sourceHoleIndices.Count} sparse holes");
                    TestContext.Out.WriteLine("   Destination has 0 sparse holes (all blocks allocated)");
                    Assert.Fail("Sparse holes were not preserved during copy - file became fully allocated!");
                }
                else if (destHoleIndices.Count >= sourceHoleIndices.Count)
                {
                    TestContext.Out.WriteLine("");
                    TestContext.Out.WriteLine("✅ SPARSE HOLES PRESERVED!");
                    TestContext.Out.WriteLine($"   Source: {sourceHoleIndices.Count} sparse holes");
                    TestContext.Out.WriteLine($"   Dest:   {destHoleIndices.Count} sparse holes");
                    if (destHoleIndices.Count > sourceHoleIndices.Count)
                    {
                        TestContext.Out.WriteLine($"   🎉 BONUS: Found {destHoleIndices.Count - sourceHoleIndices.Count} additional zero pages to optimize!");
                    }
                    Assert.Pass("Sparse holes successfully preserved!");
                }
                else
                {
                    TestContext.Out.WriteLine("");
                    TestContext.Out.WriteLine($"⚠️  FEWER HOLES IN DESTINATION");
                    TestContext.Out.WriteLine($"   Source: {sourceHoleIndices.Count} sparse holes");
                    TestContext.Out.WriteLine($"   Dest:   {destHoleIndices.Count} sparse holes");
                    Assert.Fail($"Lost sparse holes during copy: source={sourceHoleIndices.Count}, dest={destHoleIndices.Count}");
                }
            }
        }

        private ObjectEntry? FindSparseFileInFilesystem(INdfsFileSystem fs)
        {
            var objectFile = fs.GetObjectFile();
            var objects = objectFile.GetObjects();

            // Find first file with sparse holes
            for (int i = 0; i < objects.Count; i++)
            {
                var obj = objects[i];
                if (obj.PagesInFile > 1) // Must have multiple pages to have holes
                {
                    var holes = GetSparseHoleIndices(fs, obj);
                    if (holes.Count > 0)
                        return obj;
                }
            }

            return null;
        }

        private List<int> GetSparseHoleIndices(INdfsFileSystem fs, ObjectEntry obj)
        {
            var holes = new List<int>();

            if (obj.FilePointer == null)
                return holes;

            if (obj.FilePointer.Type != RetroIO.Storage.Ndfs.Structures.BlockPointer.PointerType.Indexed)
                return holes; // Only indexed files can have holes

            // Use reflection to access internal _device field
            var fsType = fs.GetType();
            var deviceField = fsType.GetField("_device", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (deviceField == null)
                return holes;

            var device = deviceField.GetValue(fs) as RetroIO.Storage.Core.IBlockDevice;
            if (device == null)
                return holes;

            // Read index block
            var indexBuffer = new byte[2048];
            device.ReadBlock(RetroIO.Storage.Core.BlockAddress.FromLBA(obj.FilePointer.BlockID), indexBuffer);

            // Check each pointer in index
            uint pointerCount = obj.PagesInFile;
            for (int i = 0; i < pointerCount; i++)
            {
                var bp = RetroIO.Storage.Ndfs.Structures.BlockPointer.FromBytes(indexBuffer, i * 4);
                if (bp != null && bp.BlockID == 0)
                {
                    holes.Add(i);
                }
            }

            return holes;
        }
    }
}
