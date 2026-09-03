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
using RetroIO.Storage.Ndfs.Drivers;

namespace RetroIO.Tests
{
    /// <summary>
    /// Tests for the filesystem-agnostic volume-info contract that drives the shared
    /// disk-info window. Verifies both the NDFS provider implementation and the generic
    /// fallback that keeps the UI useful for drivers with no volume-info support.
    /// </summary>
    [TestFixture]
    public class VolumeInfoTests
    {
        private string _testImageDir = string.Empty;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            _testImageDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                         @"..\..\..\..\demo\test-images\ndfs");
            Directory.CreateDirectory(_testImageDir);

            byte[] image = NdfsTestImageGenerator.CreateFilesystemWithFiles();
            File.WriteAllBytes(Path.Combine(_testImageDir, "volinfo.ndfs"), image);
        }

        [Test]
        [Category("VolumeInfo")]
        public void NdfsDriver_ImplementsVolumeInfoProvider()
        {
            using var driver = new NdfsDriver();
            Assert.That(driver, Is.InstanceOf<IVolumeInfoProvider>(),
                "NdfsDriver should expose volume info to generic callers");
        }

        [Test]
        [Category("VolumeInfo")]
        public void NdfsDriver_MountedImage_ReportsVolumeSections()
        {
            string imagePath = Path.Combine(_testImageDir, "volinfo.ndfs");
            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not generated at {imagePath}");
                return;
            }

            using var stream = File.OpenRead(imagePath);
            using var driver = new NdfsDriver();
            driver.Mount(stream, readOnly: true);

            VolumeInfo info = VolumeInfoBuilder.Build(driver, imagePath, isReadOnly: true);

            Assert.That(info, Is.Not.Null);
            Assert.That(info.FileSystemType, Does.Contain("NDFS"));
            Assert.That(info.IsReadOnly, Is.True);
            Assert.That(info.ImagePath, Is.EqualTo(imagePath));

            Assert.That(FindSection(info, "Volume"), Is.True, "Should report a Volume section");
            Assert.That(FindSection(info, "Extended Info"), Is.True, "Should report Extended Info");
            Assert.That(FindSection(info, "Users"), Is.True, "Should report Users");

            // Capabilities are appended by the builder for every driver, provider or not.
            Assert.That(FindSection(info, "Capabilities"), Is.True, "Builder should append Capabilities");

            for (int s = 0; s < info.Sections.Count; s++)
            {
                TestContext.Out.WriteLine($"== {info.Sections[s].Title} ==");
                for (int f = 0; f < info.Sections[s].Fields.Count; f++)
                {
                    var fld = info.Sections[s].Fields[f];
                    TestContext.Out.WriteLine($"   {fld.Label,-24}{fld.Value}");
                }
            }
        }

        [Test]
        [Category("VolumeInfo")]
        public void NdfsDriver_MountedImage_ProducesAllocationMap()
        {
            string imagePath = Path.Combine(_testImageDir, "volinfo.ndfs");
            if (!File.Exists(imagePath))
            {
                Assert.Ignore($"Test image not generated at {imagePath}");
                return;
            }

            using var stream = File.OpenRead(imagePath);
            using var driver = new NdfsDriver();
            driver.Mount(stream, readOnly: true);

            VolumeInfo info = VolumeInfoBuilder.Build(driver, imagePath, isReadOnly: true);

            Assert.That(info.AllocationMap, Is.Not.Null, "NDFS should produce a page allocation map");
            Assert.That(info.AllocationMap!.Units.Length, Is.GreaterThan(0));
            Assert.That(info.AllocationMap.UnitName, Is.EqualTo("page"));
            Assert.That(info.AllocationMap.UnitSizeBytes, Is.EqualTo(2048),
                "NDFS pages are 2048 bytes, not 512-byte sectors");

            // A mounted filesystem always has metadata pages allocated, so an all-free map
            // would mean the bitmap was not actually consulted.
            int used = 0;
            for (int i = 0; i < info.AllocationMap.Units.Length; i++)
            {
                if (info.AllocationMap.Units[i] == VolumeAllocationState.Used)
                {
                    used++;
                }
            }

            Assert.That(used, Is.GreaterThan(0), "A mounted NDFS volume must have used pages");
            TestContext.Out.WriteLine($"Allocation map: {info.AllocationMap.Units.Length:N0} pages, {used:N0} used");
        }

        [Test]
        [Category("VolumeInfo")]
        public void VolumeInfoBuilder_NullDriver_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => VolumeInfoBuilder.Build(null!, "x.img", true));
        }

        [Test]
        [Category("VolumeInfo")]
        public void VolumeInfoSection_AddChains_AndPreservesOrder()
        {
            var info = new VolumeInfo();
            info.AddSection("First", "A").Add("one", "1").Add("two", "2", "detail");
            info.AddSection("Second");

            Assert.That(info.Sections.Count, Is.EqualTo(2));
            Assert.That(info.Sections[0].Title, Is.EqualTo("First"));
            Assert.That(info.Sections[0].Icon, Is.EqualTo("A"));
            Assert.That(info.Sections[0].Fields.Count, Is.EqualTo(2));
            Assert.That(info.Sections[0].Fields[0].Label, Is.EqualTo("one"));
            Assert.That(info.Sections[0].Fields[1].Detail, Is.EqualTo("detail"));
            Assert.That(info.Sections[1].Title, Is.EqualTo("Second"));
        }

        /// <summary>
        /// True when a section with the given title is present.
        /// </summary>
        private static bool FindSection(VolumeInfo info, string title)
        {
            for (int i = 0; i < info.Sections.Count; i++)
            {
                if (string.Equals(info.Sections[i].Title, title, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
