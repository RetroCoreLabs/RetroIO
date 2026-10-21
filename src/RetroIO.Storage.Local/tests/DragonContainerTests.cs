//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.Streams;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests of the VDK and DSK/JVC container rules: compressed VDK, VDK backwards compatibility byte, partial last track, JVC header size modulo
    /// 128, JVC attribute bytes, and the 720 KB headerless rule.
    /// </summary>
    /// <remarks>
    /// The rules follow the XRoar emulator (vdisk.c): it refuses a compressed VDK and a backwards compatibility version above 10H, it takes the
    /// JVC header size as the file size modulo 128, it adds one more track when the rest after the last whole track holds at least one sector and
    /// reads the missing sectors as zero, and it takes a single sided image of 88 tracks or more as double sided. The images here are built in
    /// the test; the real images are covered by the other Dragon tests.
    /// </remarks>
    [TestFixture]
    public class DragonContainerTests
    {
        private static byte[] Vdk(byte[] sectors, int tracks, int heads, byte compat = 0x10, byte compression = 0, int headerLength = 12)
        {
            byte[] file = new byte[headerLength + sectors.Length];
            file[0] = (byte)'d';
            file[1] = (byte)'k';
            file[2] = (byte)(headerLength & 0xFF);
            file[3] = (byte)(headerLength >> 8);
            file[4] = 0x10;
            file[5] = compat;
            file[6] = (byte)'X';
            file[7] = 0x10;
            file[8] = (byte)tracks;
            file[9] = (byte)heads;
            file[10] = 0;
            file[11] = compression;
            Array.Copy(sectors, 0, file, headerLength, sectors.Length);
            return file;
        }

        private static byte[] Jvc(byte[] sectors, params byte[] header)
        {
            byte[] file = new byte[header.Length + sectors.Length];
            Array.Copy(header, 0, file, 0, header.Length);
            Array.Copy(sectors, 0, file, header.Length, sectors.Length);
            return file;
        }

        private static byte[] Truncate(byte[] bytes, int length)
        {
            byte[] shorter = new byte[length];
            Array.Copy(bytes, 0, shorter, 0, length);
            return shorter;
        }

        // A formatted disk with one file in it, so that a partial image can be read back through the file system.
        private static byte[] DiskWithFile(int tracks, int heads, byte[] content)
        {
            byte[] image = DragonDosTestDisk.Format(tracks, heads);
            using (GenericDeviceHolder holder = new GenericDeviceHolder(image, tracks, heads))
            {
                holder.Fs.WriteFile("HELLO.BIN", content);
                holder.Device.Flush();
            }

            return image;
        }

        private sealed class GenericDeviceHolder : IDisposable
        {
            public readonly RetroIO.Storage.Local.BlockDevices.GenericBlockDevice Device;
            public readonly RetroIO.Storage.Local.FileSystems.DragonDosFileSystem Fs;

            public GenericDeviceHolder(byte[] image, int tracks, int heads)
            {
                Fs = DragonDosTestDisk.Mount(image, tracks, heads, out Device);
            }

            public void Dispose()
            {
            }
        }

        // ------------------------------------------------------------------ VDK

        [TestCase(1)]
        [TestCase(7)]
        [TestCase(2)]
        public void Vdk_Compressed_IsRefusedWithAClearError(byte flags)
        {
            byte[] file = Vdk(DragonDosTestDisk.Format(40, 1), 40, 1, compression: flags);
            DragonVdkDriver driver = new DragonVdkDriver();

            Assert.That(() => driver.Mount(new MemoryStream(file), true),
                Throws.InstanceOf<NotSupportedException>().With.Message.Contains("Compressed VDK"));
        }

        [Test]
        public void Vdk_DiskNameLengthInTheHighBitsOfByte11_IsNotCompression()
        {
            // Bits 3 to 7 of byte 11 are the disk name length; only bits 0 to 2 are compression flags.
            byte[] file = Vdk(DragonDosTestDisk.Format(40, 1), 40, 1, compression: 0x28);
            DragonVdkDriver driver = new DragonVdkDriver();

            Assert.That(() => driver.Mount(new MemoryStream(file), true), Throws.Nothing);
            Assert.That(driver.FileSystemType, Is.EqualTo("DragonDOS"));
        }

        [Test]
        public void Vdk_NewerBackwardsCompatibilityVersion_IsRefused()
        {
            byte[] file = Vdk(DragonDosTestDisk.Format(40, 1), 40, 1, compat: 0x11);
            DragonVdkDriver driver = new DragonVdkDriver();

            Assert.That(() => driver.Mount(new MemoryStream(file), true),
                Throws.InstanceOf<NotSupportedException>().With.Message.Contains("11H"));
        }

        [Test]
        public void Vdk_PartialLastTrack_MountsAndTheMissingSectorsAreZero()
        {
            byte[] content = DragonDosTestDisk.Pattern(2000, 4);
            byte[] sectors = DiskWithFile(40, 1, content);
            // Cut 5 sectors and a half sector off the end: the last cylinder is partly there.
            byte[] file = Vdk(Truncate(sectors, sectors.Length - (5 * 256) - 128), 40, 1);
            DragonVdkDriver driver = new DragonVdkDriver();

            driver.Mount(new MemoryStream(file), true);

            Assert.That(driver.GetBlockDeviceInfo()!.CylinderCount, Is.EqualTo(40));
            Assert.That(driver.ReadFile("HELLO.BIN"), Is.EqualTo(content));
            byte[] sector = new byte[256];
            driver.ReadBlock(BlockAddress.FromLBA(719), sector);
            Assert.That(sector, Is.All.EqualTo((byte)0), "the last sector is missing in the file and reads as zero");
        }

        [Test]
        public void Vdk_PartialLastTrack_KeepsTheSectorsThatAreThere()
        {
            byte[] sectors = DragonDosTestDisk.Format(40, 1);
            for (int i = 0; i < 256; i++)
            {
                sectors[(712 * 256) + i] = 0xA5;
                sectors[(715 * 256) + i] = 0x5A;
            }

            byte[] file = Vdk(Truncate(sectors, 716 * 256), 40, 1);
            DragonVdkDriver driver = new DragonVdkDriver();

            driver.Mount(new MemoryStream(file), true);
            byte[] sector = new byte[256];
            driver.ReadBlock(BlockAddress.FromLBA(712), sector);
            Assert.That(sector, Is.All.EqualTo((byte)0xA5));
            driver.ReadBlock(BlockAddress.FromLBA(715), sector);
            Assert.That(sector, Is.All.EqualTo((byte)0x5A));
            driver.ReadBlock(BlockAddress.FromLBA(716), sector);
            Assert.That(sector, Is.All.EqualTo((byte)0));
        }

        [Test]
        public void Vdk_WriteIntoTheMissingPart_GrowsTheFileToTheFullLength()
        {
            byte[] sectors = DragonDosTestDisk.Format(40, 1);
            byte[] file = Vdk(Truncate(sectors, 710 * 256), 40, 1);
            MemoryStream stream = new MemoryStream();
            stream.Write(file, 0, file.Length);
            DragonVdkDriver driver = new DragonVdkDriver();
            driver.Mount(stream, false);
            byte[] data = new byte[256];
            Array.Fill(data, (byte)0x77);

            driver.WriteBlock(BlockAddress.FromLBA(715), data);

            byte[] after = stream.ToArray();
            Assert.That(after.Length, Is.EqualTo(12 + (720 * 256)), "the image is complete now");
            Assert.That(after[12 + (715 * 256)], Is.EqualTo(0x77));
            Assert.That(after[12 + (712 * 256)], Is.EqualTo(0), "the gap in between is zero");
        }

        [Test]
        public void Vdk_ShortByAWholeCylinder_IsRefused()
        {
            byte[] sectors = DragonDosTestDisk.Format(40, 1);
            byte[] file = Vdk(Truncate(sectors, 39 * 18 * 256), 40, 1);
            DragonVdkDriver driver = new DragonVdkDriver();

            Assert.That(() => driver.Mount(new MemoryStream(file), true), Throws.InstanceOf<InvalidDataException>());
        }

        [Test]
        public void Vdk_LongerThanTheHeaderSays_IsRefused()
        {
            byte[] sectors = DragonDosTestDisk.Format(40, 1);
            byte[] longer = new byte[sectors.Length + 256];
            Array.Copy(sectors, longer, sectors.Length);
            DragonVdkDriver driver = new DragonVdkDriver();

            Assert.That(() => driver.Mount(new MemoryStream(Vdk(longer, 40, 1)), true), Throws.InstanceOf<InvalidDataException>());
        }

        [Test]
        public void Vdk_PartialDoubleSidedLastCylinder_Mounts()
        {
            byte[] sectors = DragonDosTestDisk.Format(40, 2);
            byte[] file = Vdk(Truncate(sectors, sectors.Length - (20 * 256)), 40, 2);
            DragonVdkDriver driver = new DragonVdkDriver();

            driver.Mount(new MemoryStream(file), true);

            Assert.That(driver.GetBlockDeviceInfo()!.HeadCount, Is.EqualTo(2));
        }

        // ------------------------------------------------------------------ JVC / DSK

        [Test]
        public void Jvc_HeaderIsTheFileSizeModulo128_NotModulo256()
        {
            // 5 byte header. The data is cut by half a sector: the file size modulo 256 is 133, modulo 128 it is 5.
            byte[] sectors = DiskWithFile(40, 1, DragonDosTestDisk.Pattern(1500, 2));
            byte[] file = Jvc(Truncate(sectors, sectors.Length - 128), 18, 1, 1, 1, 0);
            Assert.That(file.Length % 256, Is.EqualTo(133));
            Assert.That(file.Length % 128, Is.EqualTo(5));
            DragonDskDriver driver = new DragonDskDriver();

            driver.Mount(new MemoryStream(file), true);

            Assert.That(driver.GetBlockDeviceInfo()!.CylinderCount, Is.EqualTo(40));
            Assert.That(driver.ReadFile("HELLO.BIN"), Is.EqualTo(DragonDosTestDisk.Pattern(1500, 2)));
        }

        [Test]
        public void Jvc_FiveByteHeader_DoubleSided_Mounts()
        {
            byte[] sectors = DiskWithFile(40, 2, DragonDosTestDisk.Pattern(5000, 9));
            byte[] file = Jvc(sectors, 18, 2, 1, 1, 0);
            DragonDskDriver driver = new DragonDskDriver();

            driver.Mount(new MemoryStream(file), true);

            Assert.That(driver.GetBlockDeviceInfo()!.HeadCount, Is.EqualTo(2));
            Assert.That(driver.ReadFile("HELLO.BIN"), Is.EqualTo(DragonDosTestDisk.Pattern(5000, 9)));
        }

        [Test]
        public void Jvc_PartialLastTrack_MountsAndTheMissingSectorsAreZero()
        {
            byte[] content = DragonDosTestDisk.Pattern(3000, 5);
            byte[] sectors = DiskWithFile(40, 1, content);
            byte[] file = Truncate(sectors, sectors.Length - (3 * 256));
            Assert.That(file.Length % 128, Is.EqualTo(0));
            DragonDskDriver driver = new DragonDskDriver();

            driver.Mount(new MemoryStream(file), true);

            Assert.That(driver.GetBlockDeviceInfo()!.CylinderCount, Is.EqualTo(40));
            Assert.That(driver.ReadFile("HELLO.BIN"), Is.EqualTo(content));
            byte[] sector = new byte[256];
            driver.ReadBlock(BlockAddress.FromLBA(719), sector);
            Assert.That(sector, Is.All.EqualTo((byte)0));
        }

        [Test]
        public void Jvc_RestOfLessThanOneSector_IsRefused()
        {
            byte[] sectors = DragonDosTestDisk.Format(40, 1);
            byte[] longer = new byte[sectors.Length + 128];
            Array.Copy(sectors, longer, sectors.Length);
            DragonDskDriver driver = new DragonDskDriver();

            Assert.That(() => driver.Mount(new MemoryStream(longer), true), Throws.InstanceOf<InvalidDataException>());
        }

        [Test]
        public void Jvc_34TracksAndHalfASector_IsNotAPartial35thTrack()
        {
            byte[] sectors = DragonDosTestDisk.Format(40, 1);
            byte[] file = new byte[(34 * 18 * 256) + 128];
            Array.Copy(sectors, file, file.Length);
            DragonDskDriver driver = new DragonDskDriver();

            Assert.That(() => driver.Mount(new MemoryStream(file), true), Throws.InstanceOf<InvalidDataException>());
        }

        [Test]
        public void Jvc_AttributeFlag_IsRefusedWithAClearError()
        {
            byte[] file = Jvc(DragonDosTestDisk.Format(40, 1), 18, 1, 1, 1, 1);
            DragonDskDriver driver = new DragonDskDriver();

            Assert.That(() => driver.Mount(new MemoryStream(file), true),
                Throws.InstanceOf<NotSupportedException>().With.Message.Contains("attribute"));
        }

        [Test]
        public void Jvc_ZeroSectorsPerTrackInTheHeader_IsRefused_NotADivideByZero()
        {
            byte[] file = Jvc(DragonDosTestDisk.Format(40, 1), 0);
            DragonDskDriver driver = new DragonDskDriver();

            Assert.That(() => driver.Mount(new MemoryStream(file), true), Throws.InstanceOf<InvalidDataException>());
        }

        [Test]
        public void Jvc_SingleSidedImageOf160Tracks_IsTakenAsDoubleSided80()
        {
            // 737280 bytes without a header: 160 tracks single sided is implausible, so XRoar (and this driver) read 80 tracks double sided.
            byte[] file = DragonDosTestDisk.Format(80, 2);
            Assert.That(file.Length, Is.EqualTo(737280));
            DragonDskDriver driver = new DragonDskDriver();

            driver.Mount(new MemoryStream(file), true);

            BlockDeviceInfo info = driver.GetBlockDeviceInfo()!;
            Assert.That(info.HeadCount, Is.EqualTo(2));
            Assert.That(info.CylinderCount, Is.EqualTo(80));
            Assert.That(driver.FileSystemType, Is.EqualTo("DragonDOS"));
        }

        [Test]
        public void Jvc_FileWithAnExplicitSingleSidedHeaderAnd160Tracks_IsAlsoTakenAsDoubleSided()
        {
            byte[] file = Jvc(DragonDosTestDisk.Format(80, 2), 18, 1);
            DragonDskDriver driver = new DragonDskDriver();

            driver.Mount(new MemoryStream(file), true);

            Assert.That(driver.GetBlockDeviceInfo()!.HeadCount, Is.EqualTo(2));
        }

        [Test]
        public void PaddedSubStream_JunkBehindTheRealData_IsNeverShown_AndIsClearedByAWriteBehindIt()
        {
            // The file has a stub of 4 junk bytes behind the whole sectors (a half sector at the end of a cut file).
            MemoryStream baseStream = new MemoryStream();
            baseStream.Write(new byte[] { 9, 9, 1, 2, 3, 4, 0xEE, 0xEE, 0xEE, 0xEE }, 0, 10);
            PaddedSubStream padded = new PaddedSubStream(baseStream, 2, 4, 10);
            byte[] buffer = new byte[10];

            Assert.That(padded.Read(buffer, 0, 10), Is.EqualTo(10));
            Assert.That(buffer, Is.EqualTo(new byte[] { 1, 2, 3, 4, 0, 0, 0, 0, 0, 0 }), "the junk is not shown");

            padded.Position = 7;
            padded.Write(new byte[] { 7, 8 }, 0, 2);
            padded.Position = 0;
            Assert.That(padded.Read(buffer, 0, 10), Is.EqualTo(10));
            Assert.That(buffer, Is.EqualTo(new byte[] { 1, 2, 3, 4, 0, 0, 0, 7, 8, 0 }), "the gap between the real data and the write is zero, not junk");
            Assert.That(baseStream.ToArray()[6], Is.EqualTo(0), "the junk in the base stream is cleared too");
        }

        [Test]
        public void PaddedSubStream_ReadsZeroBehindTheRealData_AndWritesFillTheGap()
        {
            MemoryStream baseStream = new MemoryStream();
            baseStream.Write(new byte[] { 9, 9, 1, 2, 3, 4 }, 0, 6);
            PaddedSubStream padded = new PaddedSubStream(baseStream, 2, 4, 10);
            byte[] buffer = new byte[10];

            Assert.That(padded.Length, Is.EqualTo(10));
            Assert.That(padded.Read(buffer, 0, 10), Is.EqualTo(10));
            Assert.That(buffer, Is.EqualTo(new byte[] { 1, 2, 3, 4, 0, 0, 0, 0, 0, 0 }));

            padded.Position = 7;
            padded.Write(new byte[] { 7, 8 }, 0, 2);

            Assert.That(baseStream.ToArray(), Is.EqualTo(new byte[] { 9, 9, 1, 2, 3, 4, 0, 0, 0, 7, 8 }));
            padded.Position = 0;
            Assert.That(padded.Read(buffer, 0, 10), Is.EqualTo(10));
            Assert.That(buffer, Is.EqualTo(new byte[] { 1, 2, 3, 4, 0, 0, 0, 7, 8, 0 }));
            Assert.That(() => { padded.Position = 9; padded.Write(new byte[2], 0, 2); }, Throws.InstanceOf<IOException>());
        }
    }
}
