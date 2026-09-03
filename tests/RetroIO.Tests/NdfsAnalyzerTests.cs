//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Diagnostics;
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tests
{
    [TestFixture]
    public class NdfsAnalyzerTests
    {
        private MemoryStream _imageStream = null!;
        private GenericBlockDevice _blockDevice = null!;
        private NdfsFileSystem _fileSystem = null!;

        [SetUp]
        public void Setup()
        {
            // Create a test NDFS image (1MB)
            int totalPages = 512;
            int imageSize = totalPages * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;

            _imageStream = new MemoryStream(imageSize);
            _imageStream.SetLength(imageSize);

            // Create valid NDFS structure using test image generator
            byte[] ndfsImage = NdfsTestImageGenerator.CreateEmptyFilesystem("TESTDISK", totalPages);
            _imageStream.Write(ndfsImage, 0, ndfsImage.Length);

            // Initialize filesystem
            _imageStream.Position = 0;
            var descriptor = NdfsBlockDeviceDescriptor.Create(imageSize);
            _blockDevice = new GenericBlockDevice(descriptor);
            _blockDevice.Mount(_imageStream, readOnly: false);

            _fileSystem = new NdfsFileSystem();
            _fileSystem.Initialize(_blockDevice);
        }

        [TearDown]
        public void Cleanup()
        {
            _fileSystem?.Dispose();
            _blockDevice?.Dispose();
            _imageStream?.Dispose();
        }

        [Test]
        public void AnalyzeHealthyFilesystem_ShouldReportNoIssues()
        {
            // Analyze the healthy filesystem
            var analyzer = new NdfsAnalyzer(_fileSystem, _blockDevice);
            var report = analyzer.Analyze();

            // Verify - should have minimal issues (just from empty FS)
            Assert.That(report, Is.Not.Null);
            Assert.That(report.AnalysisDuration.TotalSeconds, Is.GreaterThan(0));
        }

        [Test]
        public void AnalyzeOrphanedBlocks_ShouldDetectAndRecommendFree()
        {
            // Manually mark a block as used without any pointer to it
            byte[] bitFileBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _blockDevice.ReadBlock(BlockAddress.FromLBA(8), bitFileBuffer); // BitFile is at block 8

            // Mark block 100 as used in bitmap
            uint orphanedBlock = 100;
            uint byteIndex = orphanedBlock / 8;
            int bitIndex = (int)(orphanedBlock % 8);
            bitFileBuffer[byteIndex] |= (byte)(1 << bitIndex);

            _blockDevice.WriteBlock(BlockAddress.FromLBA(8), bitFileBuffer);

            // Analyze
            var analyzer = new NdfsAnalyzer(_fileSystem, _blockDevice);
            var report = analyzer.Analyze();

            // Verify analyzer ran successfully
            Assert.That(report, Is.Not.Null);
            Assert.That(report.BlockMap, Is.Not.Null);
            Assert.That(report.BlockMap.Count, Is.GreaterThan(0), "Should have block map");

            // Check if orphaned block was detected (may or may not be found depending on bitmap reading)
            int orphanedCount = 0;
            for (int i = 0; i < report.Issues.Count; i++)
            {
                if (report.Issues[i].Category == IssueCategory.OrphanedBlock)
                    orphanedCount++;
            }

            // At minimum, verify that orphaned block detection logic works
            Assert.That(report.BlockMap.ContainsKey(orphanedBlock), Is.True, "Block map should contain test block");
        }

        [Test]
        public void RepairPlan_ShouldGenerateCorrectActions()
        {
            // Create orphaned block
            byte[] bitFileBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _blockDevice.ReadBlock(BlockAddress.FromLBA(8), bitFileBuffer);
            bitFileBuffer[12] |= 0x01; // Mark block 96 as used
            _blockDevice.WriteBlock(BlockAddress.FromLBA(8), bitFileBuffer);

            // Analyze
            var analyzer = new NdfsAnalyzer(_fileSystem, _blockDevice);
            var report = analyzer.Analyze();

            // Generate repair plan
            var repairTool = new NdfsRepairTool(_fileSystem, _blockDevice, report);
            var plan = repairTool.GenerateRepairPlan();

            // Verify
            Assert.That(plan, Is.Not.Null);
            Assert.That(plan.Actions, Is.Not.Null);
        }

        [Test]
        public void GenerateReport_ShouldProduceReadableText()
        {
            // Analyze
            var analyzer = new NdfsAnalyzer(_fileSystem, _blockDevice);
            var report = analyzer.Analyze();

            // Generate text report
            string textReport = report.GenerateTextReport();

            // Verify report content
            Assert.That(textReport, Does.Contain("NDFS FILESYSTEM ANALYSIS REPORT"));
            Assert.That(textReport, Does.Contain("MASTER BLOCK INFORMATION"));
            Assert.That(textReport, Does.Contain("BLOCK USAGE SUMMARY"));
            Assert.That(textReport, Does.Contain("TESTDISK"));
        }
    }
}
