//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Local.BSD.Structures;

namespace RetroIO.Tests
{
    [TestFixture]
    public class BsdSuperblockParsingTests
    {
        private const string SunDiskImagePath = @"D:\ND\HDD\sun2-sunos-3.2.img";

        [Test]
        [Category("SunOS")]
        [Category("Debug")]
        public void ParseSuperblock_AtOffset8192_DebugOutput()
        {
            // Arrange
            if (!File.Exists(SunDiskImagePath))
            {
                Assert.Ignore($"Test skipped: Sun disk image not found at {SunDiskImagePath}");
                return;
            }

            using var fs = File.OpenRead(SunDiskImagePath);
            fs.Seek(8192, SeekOrigin.Begin);
            byte[] buffer = new byte[1024];
            fs.ReadExactly(buffer, 0, 1024);

            // Act
            var sb = BsdSuperblock.Parse(buffer);

            // Assert & Debug
            TestContext.Out.WriteLine("=== BSD Superblock at offset 8192 ===");
            TestContext.Out.WriteLine($"ISize: {sb.ISize}");
            TestContext.Out.WriteLine($"FSize: {sb.FSize}");
            TestContext.Out.WriteLine($"NFree: {sb.NFree}");
            TestContext.Out.WriteLine($"NInode: {sb.NInode}");
            TestContext.Out.WriteLine($"FMod: {sb.FMod}");
            TestContext.Out.WriteLine($"FsName: {sb.FsName}");
            TestContext.Out.WriteLine($"FsPack: {sb.FsPack}");
            TestContext.Out.WriteLine($"TotalInodes: {sb.TotalInodes}");
            TestContext.Out.WriteLine($"TFree: {sb.TFree}");
            TestContext.Out.WriteLine($"TInode: {sb.TInode}");
            TestContext.Out.WriteLine();
            TestContext.Out.WriteLine($"Validates: {sb.Validate()}");

            if (!sb.Validate())
            {
                TestContext.Out.WriteLine();
                TestContext.Out.WriteLine("Validation failed. Checking why:");
                if (sb.ISize == 0 || sb.FSize == 0)
                    TestContext.Out.WriteLine($"  - ISize ({sb.ISize}) or FSize ({sb.FSize}) is zero");
                if (sb.ISize >= sb.FSize)
                    TestContext.Out.WriteLine($"  - ISize ({sb.ISize}) >= FSize ({sb.FSize})");
                if (sb.NFree < 0 || sb.NFree > 50)
                    TestContext.Out.WriteLine($"  - NFree ({sb.NFree}) out of range [0-50]");
                if (sb.NInode < 0 || sb.NInode > 100)
                    TestContext.Out.WriteLine($"  - NInode ({sb.NInode}) out of range [0-100]");
            }

            // Show first 128 bytes of buffer
            TestContext.Out.WriteLine();
            TestContext.Out.WriteLine("First 128 bytes of superblock:");
            for (int i = 0; i < 128; i += 16)
            {
                string hex = "";
                string ascii = "";
                for (int j = 0; j < 16; j++)
                {
                    byte b = buffer[i + j];
                    hex += $"{b:X2} ";
                    ascii += (b >= 32 && b < 127) ? (char)b : '.';
                }
                TestContext.Out.WriteLine($"  {i:X4}: {hex}| {ascii}");
            }
        }

        [Test]
        [Category("SunOS")]
        [Category("Debug")]
        public void ParseSuperblock_AtOffset0_DebugOutput()
        {
            // Arrange
            if (!File.Exists(SunDiskImagePath))
            {
                Assert.Ignore($"Test skipped: Sun disk image not found at {SunDiskImagePath}");
                return;
            }

            using var fs = File.OpenRead(SunDiskImagePath);
            byte[] buffer = new byte[1024];
            fs.ReadExactly(buffer, 0, 1024);

            // Act
            var sb = BsdSuperblock.Parse(buffer);

            // Assert & Debug
            TestContext.Out.WriteLine("=== BSD Superblock at offset 0 ===");
            TestContext.Out.WriteLine($"Validates: {sb.Validate()}");

            if (sb.Validate())
            {
                TestContext.Out.WriteLine($"ISize: {sb.ISize}");
                TestContext.Out.WriteLine($"FSize: {sb.FSize}");
                TestContext.Out.WriteLine($"FsName: {sb.FsName}");
            }
        }
    }
}
