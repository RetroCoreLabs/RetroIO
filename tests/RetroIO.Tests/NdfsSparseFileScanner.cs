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
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.FileSystems;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Tests
{
    /// <summary>
    /// Scans NDFS image for files with sparse holes (BlockID=0 in index).
    /// </summary>
    [TestFixture]
    public class NdfsSparseFileScanner
    {
        private string _testImagePath;
        private DriverFactory _factory;

        [SetUp]
        public void Setup()
        {
            _testImagePath = TestPaths.Resolve(@"C:\Users\ronny\OneDrive\Documents\RetroFS\test-images\ndfs\WD0.img");
            _factory = new DriverFactory();
            _factory.Register(new NdfsDriverProvider());
        }

        [Test]
        public void ScanForSparseFiles_FindFilesWithEmptyHoles()
        {
            // Arrange
            if (!File.Exists(_testImagePath))
            {
                Assert.Ignore("Test image not found");
                return;
            }

            var fsDriver = _factory.Detect(_testImagePath);
            if (fsDriver == null)
            {
                Assert.Fail($"No driver detected for {_testImagePath}");
                return;
            }

            var ndfsDriver = fsDriver as NdfsDriver;
            if (ndfsDriver == null)
            {
                Assert.Fail($"Driver is {fsDriver.GetType().Name}, not NdfsDriver");
                return;
            }

            // Mount the filesystem
            using (var stream = File.OpenRead(_testImagePath))
            {
                ndfsDriver.Mount(stream, true); // read-only
            }

            var ndfs = ndfsDriver.GetFileSystem();
            if (ndfs == null)
            {
                Assert.Fail("NDFS filesystem is null after mount");
                return;
            }

            var objectFile = ndfs.GetObjectFile();
            var objects = objectFile.GetObjects();

            var sparseFiles = new List<(ObjectEntry obj, List<int> holeIndices)>();
            int totalFilesScanned = 0;
            int totalIndexedFiles = 0;

            // Need access to underlying device for low-level block reading
            // Get device through reflection as it's internal
            var fsType = ndfs.GetType();
            var deviceField = fsType.GetField("_device", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (deviceField == null)
            {
                Assert.Inconclusive("Cannot access internal device for block-level scanning");
                return;
            }

            var device = deviceField.GetValue(ndfs) as IBlockDevice;
            if (device == null)
            {
                Assert.Inconclusive("Device is null");
                return;
            }

            // Act - Scan all files for sparse holes
            for (int i = 0; i < objects.Count; i++)
            {
                var obj = objects[i];
                totalFilesScanned++;

                if (obj.FilePointer == null)
                    continue;

                if (obj.FilePointer.Type != BlockPointer.PointerType.Indexed)
                    continue;

                totalIndexedFiles++;

                if (obj.PagesInFile <= 1)
                    continue; // Skip single-page files

                // Read index block
                var indexBuffer = new byte[2048];
                device.ReadBlock(BlockAddress.FromLBA(obj.FilePointer.BlockID), indexBuffer);

                // Check for empty holes (BlockID=0)
                var holes = new List<int>();
                int maxPointers = (int)System.Math.Min(obj.PagesInFile, 512); // 512 pointers max in one index block

                for (int j = 0; j < maxPointers; j++)
                {
                    var bp = BlockPointer.FromBytes(indexBuffer, j * 4);
                    if (bp != null && bp.BlockID == 0)
                    {
                        holes.Add(j);
                    }
                }

                if (holes.Count > 0)
                {
                    sparseFiles.Add((obj, holes));
                }
            }

            // Assert
            TestContext.Out.WriteLine($"=== SPARSE FILE SCAN RESULTS ===");
            TestContext.Out.WriteLine($"Total files scanned: {totalFilesScanned}");
            TestContext.Out.WriteLine($"Indexed files: {totalIndexedFiles}");
            TestContext.Out.WriteLine($"Sparse files found: {sparseFiles.Count}");
            TestContext.Out.WriteLine("");

            if (sparseFiles.Count > 0)
            {
                TestContext.Out.WriteLine("Files with empty holes:");
                TestContext.Out.WriteLine("========================================");

                int displayLimit = System.Math.Min(sparseFiles.Count, 20);
                for (int i = 0; i < displayLimit; i++)
                {
                    var (obj, holes) = sparseFiles[i];
                    string fileName = $"{obj.UserName}/{obj.ObjectName}.{obj.Type}";

                    TestContext.Out.WriteLine($"[{i + 1}] {fileName}");
                    TestContext.Out.WriteLine($"    Pages: {obj.PagesInFile}, Bytes: {obj.BytesInFile}");

                    // Build hole list string manually (no LINQ)
                    string holeList = "";
                    int showHoles = System.Math.Min(holes.Count, 10);
                    for (int h = 0; h < showHoles; h++)
                    {
                        if (h > 0) holeList += ", ";
                        holeList += holes[h].ToString();
                    }

                    TestContext.Out.WriteLine($"    Empty holes at indices: {holeList}");
                    if (holes.Count > 10)
                    {
                        TestContext.Out.WriteLine($"    ... and {holes.Count - 10} more holes");
                    }
                    TestContext.Out.WriteLine($"    Hole count: {holes.Count} / {obj.PagesInFile} pages ({(holes.Count * 100.0 / obj.PagesInFile):F1}% sparse)");
                    TestContext.Out.WriteLine("");
                }

                if (sparseFiles.Count > 20)
                {
                    TestContext.Out.WriteLine($"... and {sparseFiles.Count - 20} more sparse files");
                }
            }
            else
            {
                TestContext.Out.WriteLine("No sparse files found in image.");
                TestContext.Out.WriteLine("This might be expected if the test image doesn't use sparse allocation.");
            }

            Assert.That(totalFilesScanned, Is.GreaterThan(0), "Should have scanned some files");
        }

        [Test]
        public void AnalyzeSparseFile_ShowDetailedStructure()
        {
            // Arrange
            if (!File.Exists(_testImagePath))
            {
                Assert.Ignore("Test image not found");
                return;
            }

            var fsDriver = _factory.Detect(_testImagePath);
            if (fsDriver == null)
            {
                Assert.Fail($"No driver detected for {_testImagePath}");
                return;
            }

            var ndfsDriver = fsDriver as NdfsDriver;
            if (ndfsDriver == null)
            {
                Assert.Fail($"Driver is {fsDriver.GetType().Name}, not NdfsDriver");
                return;
            }

            // Mount the filesystem
            using (var stream = File.OpenRead(_testImagePath))
            {
                ndfsDriver.Mount(stream, true); // read-only
            }

            var ndfs = ndfsDriver.GetFileSystem();
            if (ndfs == null)
            {
                Assert.Fail("NDFS filesystem is null after mount");
                return;
            }

            var objectFile = ndfs.GetObjectFile();
            var objects = objectFile.GetObjects();

            // Get device via reflection
            var fsType = ndfs.GetType();
            var deviceField = fsType.GetField("_device", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (deviceField == null)
            {
                Assert.Inconclusive("Cannot access internal device");
                return;
            }

            var device = deviceField.GetValue(ndfs) as IBlockDevice;
            if (device == null)
            {
                Assert.Inconclusive("Device is null");
                return;
            }

            // Find first sparse file
            ObjectEntry? sparseFile = null;

            for (int i = 0; i < objects.Count; i++)
            {
                var obj = objects[i];

                if (obj.FilePointer != null &&
                    obj.FilePointer.Type == BlockPointer.PointerType.Indexed &&
                    obj.PagesInFile > 1)
                {
                    var indexBuffer = new byte[2048];
                    device.ReadBlock(BlockAddress.FromLBA(obj.FilePointer.BlockID), indexBuffer);

                    // Check for at least one empty hole
                    bool hasHole = false;
                    int maxCheck = (int)System.Math.Min(obj.PagesInFile, 512);

                    for (int j = 0; j < maxCheck; j++)
                    {
                        var bp = BlockPointer.FromBytes(indexBuffer, j * 4);
                        if (bp != null && bp.BlockID == 0)
                        {
                            hasHole = true;
                            break;
                        }
                    }

                    if (hasHole)
                    {
                        sparseFile = obj;
                        break;
                    }
                }
            }

            if (sparseFile == null)
            {
                Assert.Inconclusive("No sparse files found in image");
                return;
            }

            // Act - Analyze structure
            string filePath = $"{sparseFile.UserName}/{sparseFile.ObjectName}.{sparseFile.Type}";

            TestContext.Out.WriteLine($"=== SPARSE FILE DETAILED ANALYSIS ===");
            TestContext.Out.WriteLine($"File: {filePath}");
            TestContext.Out.WriteLine($"Pages: {sparseFile.PagesInFile}");
            TestContext.Out.WriteLine($"Bytes: {sparseFile.BytesInFile}");
            TestContext.Out.WriteLine($"Index Block: {sparseFile.FilePointer?.BlockID}");
            TestContext.Out.WriteLine("");

            // Read index and show structure
            var indexBuf = new byte[2048];
            Assert.That(sparseFile.FilePointer, Is.Not.Null);
            device.ReadBlock(BlockAddress.FromLBA(sparseFile.FilePointer!.BlockID), indexBuf);

            TestContext.Out.WriteLine("Block Pointer Structure:");
            TestContext.Out.WriteLine("Index | BlockID | Type      | Status");
            TestContext.Out.WriteLine("------|---------|-----------|----------");

            int displayCount = (int)System.Math.Min(sparseFile.PagesInFile, 20);

            for (int i = 0; i < displayCount; i++)
            {
                var bp = BlockPointer.FromBytes(indexBuf, i * 4);
                if (bp != null)
                {
                    string status = bp.BlockID == 0 ? "EMPTY HOLE" : "DATA";
                    TestContext.Out.WriteLine($"{i,5} | {bp.BlockID,7} | {bp.Type,-9} | {status}");
                }
            }

            if (sparseFile.PagesInFile > 20)
            {
                TestContext.Out.WriteLine($"... {sparseFile.PagesInFile - 20} more pages");
            }

            Assert.Pass($"Analyzed sparse file: {filePath}");
        }
    }
}
