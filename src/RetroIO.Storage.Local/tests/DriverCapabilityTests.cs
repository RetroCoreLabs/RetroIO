//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests that the capability flags and write flag of a driver follow how the image was mounted.
    /// </summary>
    [TestFixture]
    public class DriverCapabilityTests
    {
        private static CommodoreDiskDriver MountCbm(CbmDiskType type, bool readOnly)
        {
            byte[] image = CbmImageFactory.CreateBlank(type, "TEST", "01");
            CommodoreDiskDriver driver = new CommodoreDiskDriver();
            driver.Mount(new MemoryStream(image, writable: true), readOnly);
            return driver;
        }

        [Test]
        public void CommodoreDiskDriver_D81_AdvertisesSubdirectories()
        {
            CommodoreDiskDriver driver = MountCbm(CbmDiskType.D81, false);

            Assert.That(driver.Capabilities.HasFlag(DriverCapabilities.Subdirectories), Is.True);
        }

        [Test]
        public void CommodoreDiskDriver_D64_DoesNotAdvertiseSubdirectories()
        {
            CommodoreDiskDriver driver = MountCbm(CbmDiskType.D64, false);

            Assert.That(driver.Capabilities.HasFlag(DriverCapabilities.Subdirectories), Is.False);
        }

        [Test]
        public void PcFloppyDriver_ReadOnlyMount_ReportsNoWrite()
        {
            byte[] image = PcImageFactory.CreateBlankFloppy(PcFloppySize.Size1_44M, "TESTDISK");
            PcFloppyDriver driver = new PcFloppyDriver();
            driver.Mount(new MemoryStream(image, writable: true), readOnly: true);

            Assert.That(driver.CanWrite, Is.False);
            DriverCapabilities caps = driver.Capabilities;
            Assert.That(caps.HasFlag(DriverCapabilities.Write), Is.False);
            Assert.That(caps.HasFlag(DriverCapabilities.Delete), Is.False);
            Assert.That(caps.HasFlag(DriverCapabilities.Rename), Is.False);
            Assert.That(caps.HasFlag(DriverCapabilities.CreateDirectory), Is.False);
            Assert.That(caps.HasFlag(DriverCapabilities.BlockWrite), Is.False);
            Assert.That(caps.HasFlag(DriverCapabilities.Read), Is.True);
        }

        [Test]
        public void PcFloppyDriver_ReadWriteMount_ReportsWrite()
        {
            byte[] image = PcImageFactory.CreateBlankFloppy(PcFloppySize.Size1_44M, "TESTDISK");
            PcFloppyDriver driver = new PcFloppyDriver();
            driver.Mount(new MemoryStream(image, writable: true), readOnly: false);

            Assert.That(driver.CanWrite, Is.True);
            Assert.That(driver.Capabilities.HasFlag(DriverCapabilities.Write), Is.True);
        }
    }
}
