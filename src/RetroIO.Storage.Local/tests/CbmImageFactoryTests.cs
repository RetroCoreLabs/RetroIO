//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests CbmImageFactory.CreateBlank: it must produce a correctly-sized, empty, c1541-valid disk
    /// image with the right free-block count, for D64/D71/D81.
    /// </summary>
    [TestFixture]
    [Category("CBM")]
    public class CbmImageFactoryTests
    {
        private static string? FindC1541()
        {
            string? env = Environment.GetEnvironmentVariable("C1541");
            if (!string.IsNullOrEmpty(env) && File.Exists(env))
            {
                return env;
            }
            string[] candidates = { @"F:\Prog\GTK3VICE-3.5-win64\bin\c1541.exe" };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i]))
                {
                    return candidates[i];
                }
            }
            return null;
        }

        private static void Check(CbmDiskType type, int expectedSize, int dirTrack, int bamTrack,
            int freshFree, BlockDeviceDescriptor descriptor)
        {
            byte[] image = CbmImageFactory.CreateBlank(type, "TESTDISK", "01");
            Assert.That(image.Length, Is.EqualTo(expectedSize), "image is the correct size");

            // Mount the produced bytes and confirm it is a valid empty disk.
            using (MemoryStream ms = new MemoryStream(image, writable: false))
            {
                GenericBlockDevice device = new GenericBlockDevice(descriptor);
                device.Mount(ms, readOnly: true);
                CbmDosFileSystem fs = new CbmDosFileSystem(dirTrack, bamTrack);
                fs.Initialize(device);
                Assert.That(fs.ListDirectory("/").Length, Is.EqualTo(0), "blank image is empty");
                Assert.That(fs.GetBlocksFree(), Is.EqualTo(freshFree), "blank image free count");
                device.Dispose();
            }

            // c1541 must accept the produced image.
            string? c1541 = FindC1541();
            if (c1541 != null)
            {
                string temp = Path.Combine(Path.GetTempPath(), "rfs_fac_" + Path.GetRandomFileName() + CbmImageFactory.ExtensionFor(type));
                try
                {
                    File.WriteAllBytes(temp, image);
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = c1541,
                        Arguments = $"-attach \"{temp}\" -list",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using Process p = Process.Start(psi)!;
                    string listing = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                    Assert.That(p.ExitCode, Is.EqualTo(0), "c1541 accepts the factory image");
                    Assert.That(listing, Does.Contain($"{freshFree} blocks free"), "c1541 agrees on the free count");
                }
                finally
                {
                    File.Delete(temp);
                }
            }
        }

        [Test]
        public void CreateBlank_D64_IsValidEmptyImage()
        {
            Check(CbmDiskType.D64, 174848, 18, 18, 664, BlockDeviceDescriptor.D64_35Track);
        }

        [Test]
        public void CreateBlank_D71_IsValidEmptyImage()
        {
            Check(CbmDiskType.D71, 349696, 18, 18, 1328, BlockDeviceDescriptor.D71);
        }

        [Test]
        public void CreateBlank_D81_IsValidEmptyImage()
        {
            Check(CbmDiskType.D81, 819200, 40, 40, 3160, BlockDeviceDescriptor.D81);
        }
    }
}
