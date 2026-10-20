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
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.FileSystems;
using RetroIO.Storage.Local.Streams;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Pins where <c>DragonDosFileSystem</c> reads the disk geometry from, against the DragonDOS 1.2 source.
    /// </summary>
    /// <remarks>
    /// Source read: dosdefs.asm, section "Offsets in Sector 0 on Dir track": the bitmap uses bytes
    /// $00 to $FB of the first BAM sector, then DirTracks = $FC, DirSecPerTrk = $FD,
    /// DirTracks1s = $FE (complement of DirTracks) and DirSecPerTrk1s = $FF (complement of DirSecPerTrk).
    /// ddos12.asm, BuildBAM writes them and DosGetDiskGeometry checks them (it complements $FE and $FF
    /// and compares the pair with $FC and $FD).
    /// Neither listing has a volume name, and the bitmap covers $00 to $FB, so there is no label
    /// field in the BAM sector.
    /// Real images: the Dragon test images in the RetroFS test-image folder. Each real-image test
    /// skips when its image is missing. Set RETROIO_DRAGON_TEST_IMAGES to the folder that holds the
    /// vdk and dsk folders to use another place.
    /// </remarks>
    [TestFixture]
    public class DragonDosGeometryTests
    {
        private const int SectorSize = 256;
        private const int DirLsn = 18 * 20; // $0168, first sector of the directory track on a single sided disk
        private const int VdkHeaderSize = 12;

        private static string ImageFolder()
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable("RETROIO_DRAGON_TEST_IMAGES");
            if (!string.IsNullOrEmpty(fromEnvironment))
            {
                return fromEnvironment;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "OneDrive", "Documents", "RetroFS", "test-images");
        }

        private static string RealImage(string folder, string name)
        {
            string path = Path.Combine(ImageFolder(), folder, name);
            if (!File.Exists(path))
            {
                Assert.Ignore("Test image not found: " + name);
            }

            return path;
        }

        private static byte[] BlankDisk()
        {
            // 40 tracks, 1 side, 18 sectors per track, 256 byte sectors, no header.
            return new byte[40 * 18 * SectorSize];
        }

        private static void WriteGeometry(byte[] image, byte tracks, byte sectorsPerTrack)
        {
            int bam = DirLsn * SectorSize;
            image[bam + 0xFC] = tracks;
            image[bam + 0xFD] = sectorsPerTrack;
            image[bam + 0xFE] = (byte)(~tracks & 0xFF);
            image[bam + 0xFF] = (byte)(~sectorsPerTrack & 0xFF);
        }

        private static bool CanHandle(byte[] image)
        {
            GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.DragonVDK_SS40);
            device.Mount(new MemoryStream(image, writable: false), true);
            bool result = new DragonDosFileSystem().CanHandle(device);
            device.Dispose();
            return result;
        }

        private static string LabelOf(byte[] image)
        {
            DragonDskDriver driver = new DragonDskDriver();
            driver.Mount(new MemoryStream(image, writable: true), true);
            string label = driver.VolumeLabel;
            driver.Dispose();
            return label;
        }

        // ---- real images ----

        [Test]
        public void RealVdk_6809Tut1_BamGeometryIsRead_SoTheLabelIsNotTheVdkFallback()
        {
            string path = RealImage("vdk", "6809TUT1.VDK");

            DragonVdkDriver driver = new DragonVdkDriver();
            using (FileStream stream = File.OpenRead(path))
            {
                driver.Mount(stream, true);

                // The BAM holds 28 12 d7 ed at $FC to $FF: 40 tracks, 18 sectors per track.
                // Before the fix the check read $A2 to $A5 (all zero), failed, and gave "VDK DISK".
                Assert.That(driver.VolumeLabel, Is.EqualTo("DRAGON DISK"));
            }

            driver.Dispose();
        }

        [Test]
        public void RealVdk_6809Tut1_CanHandle()
        {
            string path = RealImage("vdk", "6809TUT1.VDK");

            GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.DragonVDK_SS40);
            using (FileStream stream = File.OpenRead(path))
            {
                device.Mount(new SubStream(stream, VdkHeaderSize, stream.Length - VdkHeaderSize), true);

                Assert.That(new DragonDosFileSystem().CanHandle(device), Is.True);
            }

            device.Dispose();
        }

        [Test]
        public void RealDsk_Walkabout_BamGeometryIsRead()
        {
            string path = RealImage("dsk", "WALKABOU.DSK");

            DragonDskDriver driver = new DragonDskDriver();
            using (FileStream stream = File.OpenRead(path))
            {
                driver.Mount(stream, true);

                Assert.That(driver.VolumeLabel, Is.EqualTo("DRAGON DISK"));
            }

            driver.Dispose();
        }

        [Test]
        public void RealVdk_GameImageWithoutDragonDosBam_IsNotAccepted()
        {
            // HTCHHKER.VDK has a 256 byte VDK header (size at bytes 2 and 3) and bytes 19 37 53 25 at
            // $FC to $FF of the directory track: not a valid pair, so it is not a DragonDOS volume and
            // the file system falls back to the VDK header.
            string path = RealImage("vdk", "HTCHHKER.VDK");

            GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.DragonVDK_SS40);
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] header = new byte[4];
                stream.ReadExactly(header, 0, 4);
                int headerSize = header[2] | (header[3] << 8);
                device.Mount(new SubStream(stream, headerSize, stream.Length - headerSize), true);

                Assert.That(new DragonDosFileSystem().CanHandle(device), Is.False);
            }

            device.Dispose();
        }

        [Test]
        public void RealVdk_6809Expr_CanHandle()
        {
            // 6809EXPR.VDK has a 12 byte VDK header and 28 12 d7 ed at $FC to $FF of the directory track.
            string path = RealImage("vdk", "6809EXPR.VDK");

            GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.DragonVDK_SS40);
            using (FileStream stream = File.OpenRead(path))
            {
                device.Mount(new SubStream(stream, VdkHeaderSize, stream.Length - VdkHeaderSize), true);

                Assert.That(new DragonDosFileSystem().CanHandle(device), Is.True);
            }

            device.Dispose();
        }

        [Test]
        public void RealVdk_Planetfall_IsNotAccepted()
        {
            // PLANETFALL.VDK has a 256 byte VDK header and 5d 52 2a 3e at $FC to $FF of the directory track:
            // not a valid pair, so it is not a DragonDOS volume.
            string path = RealImage("vdk", "PLANETFALL.VDK");

            GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.DragonVDK_SS40);
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] header = new byte[4];
                stream.ReadExactly(header, 0, 4);
                int headerSize = header[2] | (header[3] << 8);
                device.Mount(new SubStream(stream, headerSize, stream.Length - headerSize), true);

                Assert.That(new DragonDosFileSystem().CanHandle(device), Is.False);
            }

            device.Dispose();
        }

        [Test]
        public void RealOs9Disk_NitrOs9_IsNotAccepted()
        {
            // NITR09.DSK is an OS-9 (RBF) disk: its identification sector 0 holds 1440 total sectors, 18 sectors
            // per track and a 180 byte allocation map. LSN 360 is OS-9 file data (68 61 72 61 at $FC to $FF),
            // not a DragonDOS BAM, so DragonDOS must not claim it.
            string path = RealImage("dsk", "NITR09.DSK");

            GenericBlockDevice device = new GenericBlockDevice(BlockDeviceDescriptor.DragonVDK_DS40);
            using (FileStream stream = File.OpenRead(path))
            {
                device.Mount(new SubStream(stream, 0, stream.Length), true);

                Assert.That(new DragonDosFileSystem().CanHandle(device), Is.False);
            }

            device.Dispose();
        }

        // ---- built images, laid out as the source says ----

        [Test]
        public void BamWithGeometryAtFcToFf_IsAccepted()
        {
            byte[] image = BlankDisk();
            WriteGeometry(image, 40, 18);

            Assert.That(CanHandle(image), Is.True);
            Assert.That(LabelOf(image), Is.EqualTo("DRAGON DISK"));
        }

        [Test]
        public void BamWithGeometryOnlyAtTheOldOffsets_IsNotAccepted()
        {
            // The old code read $A2 to $A5 as sectors per track, its complement, tracks, its complement.
            // Those bytes are bitmap bytes in DragonDOS, so a valid-looking pattern there means nothing.
            byte[] image = BlankDisk();
            int bam = DirLsn * SectorSize;
            image[bam + 0xA2] = 18;
            image[bam + 0xA3] = 0xED;
            image[bam + 0xA4] = 40;
            image[bam + 0xA5] = 0xD7;

            Assert.That(CanHandle(image), Is.False);
            Assert.That(LabelOf(image), Is.EqualTo("VDK DISK"));
        }

        [Test]
        public void BamWithTheBytesInTheWrongOrder_IsNotAccepted()
        {
            // Source order is tracks, sectors per track, complement of tracks, complement of sectors per track.
            // Sectors per track first (12 28 ed d7) must fail.
            byte[] image = BlankDisk();
            int bam = DirLsn * SectorSize;
            image[bam + 0xFC] = 18;
            image[bam + 0xFD] = 40;
            image[bam + 0xFE] = 0xED;
            image[bam + 0xFF] = 0xD7;

            Assert.That(CanHandle(image), Is.False);
        }

        [Test]
        public void BamWithABadSectorsPerTrackComplement_IsNotAccepted()
        {
            byte[] image = BlankDisk();
            WriteGeometry(image, 40, 18);
            image[DirLsn * SectorSize + 0xFF] = 0x00;

            Assert.That(CanHandle(image), Is.False);
            Assert.That(LabelOf(image), Is.EqualTo("VDK DISK"));
        }

        [Test]
        public void BamWithABadTracksComplement_IsNotAccepted()
        {
            byte[] image = BlankDisk();
            WriteGeometry(image, 40, 18);
            image[DirLsn * SectorSize + 0xFE] = 0x00;

            Assert.That(CanHandle(image), Is.False);
            Assert.That(LabelOf(image), Is.EqualTo("VDK DISK"));
        }

        [Test]
        public void BitmapBytesAt0x90_AreNotTakenAsALabel()
        {
            // $90 is inside the bitmap ($00 to $FB). DragonDOS has no volume name.
            byte[] image = BlankDisk();
            WriteGeometry(image, 40, 18);
            byte[] text = System.Text.Encoding.ASCII.GetBytes("TEST DISK");
            Array.Copy(text, 0, image, DirLsn * SectorSize + 0x90, text.Length);

            Assert.That(LabelOf(image), Is.EqualTo("DRAGON DISK"));
        }
    }
}
