//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using RetroIO.Storage.Local.Drivers;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Writes a file onto real NitrOS-9 DriveWire images whose file is shorter than the volume, through the DSK driver, and checks that the
    /// file grows exactly as it does when Toolshed <c>os9 copy</c> writes the same file, and that Toolshed reads the result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The DriveWire images say DD.TOT 524268 in sector 0, but the file stops after the last sector in use. Measured by hand on a copy of
    /// ArcadePack_dw.dsk on 2026-10-21: <c>os9 copy</c> of a 100000 byte file took the file from 256768 to 357120 bytes (its descriptor at
    /// sector 1003, the first free sector, and 391 data sectors behind it).
    /// </para>
    /// <para>
    /// For every image two copies are made. Toolshed copies the payload onto one, the DSK driver writes it onto the other. The two files must
    /// end up the same length. Then Toolshed checks our copy: <c>os9 dcheck</c> finds the structure intact with nothing new out of place,
    /// <c>os9 free</c> gives the same free space as on Toolshed's copy, and <c>os9 copy</c> gives the payload back byte for byte. The original
    /// image is hashed before and after, so it is proven unchanged.
    /// </para>
    /// <para>
    /// Images: ArcadePack_dw.dsk from the folder in RETROIO_NITROS9_IMAGES (the NitrOS-9 snapshot images), and the DriveWire images in the
    /// os9 test-image folder (RETROIO_DRAGON_TEST_IMAGES). A missing image, or a missing Toolshed, skips the case.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class DriveWireShortImageWriteTests
    {
        private const int SectorSize = 256;
        private const int PayloadBytes = 100000;

        private string _folder = string.Empty;

        /// <summary>
        /// The DriveWire images, by where they are found.
        /// </summary>
        /// <returns>
        /// One case per image.
        /// </returns>
        public static IEnumerable<TestCaseData> Images()
        {
            yield return new TestCaseData("nitros9", "ArcadePack_dw.dsk").SetArgDisplayNames("ArcadePack_dw.dsk");
            yield return new TestCaseData("os9", "NOS9_6809_L1_v030300_coco1_dw.dsk").SetArgDisplayNames("NOS9_6809_L1_v030300_coco1_dw.dsk");
            yield return new TestCaseData("os9", "built_l1_coco_dw.dsk").SetArgDisplayNames("built_l1_coco_dw.dsk");
            yield return new TestCaseData("os9", "built_l2_coco3_dw.dsk").SetArgDisplayNames("built_l2_coco3_dw.dsk");
        }

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "dwshort-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }

        private static string Source(string where, string name)
        {
            string root;
            if (where == "nitros9")
            {
                string? images = Environment.GetEnvironmentVariable("RETROIO_NITROS9_IMAGES");
                if (string.IsNullOrEmpty(images))
                {
                    Assert.Ignore("RETROIO_NITROS9_IMAGES is not set, so " + name + " is not there");
                }

                root = images!;
            }
            else
            {
                string? fromEnvironment = Environment.GetEnvironmentVariable("RETROIO_DRAGON_TEST_IMAGES");
                string top = !string.IsNullOrEmpty(fromEnvironment)
                    ? fromEnvironment
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents", "RetroFS", "test-images");
                root = Path.Combine(top, "os9");
            }

            string path = Path.Combine(root, name);
            if (!File.Exists(path))
            {
                Assert.Ignore("Test image not found: " + path);
            }

            return path;
        }

        private static string Hash(string path)
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }

        private static string Toolshed(params string[] arguments)
        {
            string? text = StressToolshed.Run("os9", arguments);
            if (text == null)
            {
                Assert.Ignore("Toolshed os9 is not available through WSL Ubuntu");
            }

            return text!;
        }

        [TestCaseSource(nameof(Images))]
        public void WriteAFile_GrowsTheShortFileAsToolshedDoes_AndToolshedReadsTheResult(string where, string name)
        {
            string source = Source(where, name);
            string hashBefore = Hash(source);

            string ours = Path.Combine(_folder, "ours.dsk");
            string theirs = Path.Combine(_folder, "theirs.dsk");
            string payloadPath = Path.Combine(_folder, "payload.bin");
            File.Copy(source, ours);
            File.Copy(source, theirs);
            long originalLength = new FileInfo(source).Length;

            byte[] id = new byte[SectorSize];
            using (FileStream stream = File.OpenRead(source))
            {
                stream.ReadExactly(id, 0, SectorSize);
            }

            long volumeSectors = (id[0] << 16) | (id[1] << 8) | id[2];
            Assert.That(originalLength, Is.LessThan(volumeSectors * SectorSize), "the image file is shorter than its volume");

            byte[] payload = new byte[PayloadBytes];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)((i * 7) + 1);
            }

            File.WriteAllBytes(payloadPath, payload);

            // What Toolshed sees before anything is written: the clusters it already reports as out of place.
            string checkBefore = Toolshed("dcheck", StressToolshed.WslPath(ours));
            long unreferencedBefore = StressToolshed.NumberBefore(checkBefore, " clusters in allocation map but not in file structure");

            // Toolshed writes the payload onto its copy.
            Toolshed("copy", StressToolshed.WslPath(payloadPath), StressToolshed.WslPath(theirs) + ",PAYLOAD");
            long theirLength = new FileInfo(theirs).Length;

            // We write it onto ours, through the driver the application uses.
            using (FileStream stream = new FileStream(ours, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                DragonDskDriver driver = new DragonDskDriver();
                driver.Mount(stream, false);
                Assert.That(driver.FileSystemType, Is.EqualTo("OS-9"));
                Assert.That(driver.CanWrite, Is.True, "a short DriveWire image can be written");
                driver.WriteFile("/PAYLOAD", payload);
                driver.Dispose();
            }

            long ourLength = new FileInfo(ours).Length;
            Assert.That(theirLength, Is.GreaterThan(originalLength), "Toolshed grew the file");
            Assert.That(ourLength, Is.EqualTo(theirLength), "our write grows the file exactly as far as Toolshed's write does");
            Assert.That(ourLength, Is.LessThan(volumeSectors * SectorSize), "the file did not become the whole volume");

            // Toolshed reads our result.
            string check = Toolshed("dcheck", StressToolshed.WslPath(ours));
            Assert.That(check, Does.Contain("file structure is intact"), check);
            Assert.That(check, Does.Contain("0 previously allocated cluster found"), check);
            Assert.That(check, Does.Contain("0 clusters in file structure but not in allocation map"), check);
            Assert.That(check, Does.Contain("0 bad file decriptor sector"), check);
            Assert.That(StressToolshed.NumberBefore(check, " clusters in allocation map but not in file structure"), Is.EqualTo(unreferencedBefore),
                "nothing new is marked used without a file\n" + check);

            string ourFree = Toolshed("free", StressToolshed.WslPath(ours));
            string theirFree = Toolshed("free", StressToolshed.WslPath(theirs));
            long ourFreeSectors = StressToolshed.NumberBefore(ourFree, " Free sectors");
            Assert.That(ourFreeSectors, Is.GreaterThan(0), ourFree);
            Assert.That(ourFreeSectors, Is.EqualTo(StressToolshed.NumberBefore(theirFree, " Free sectors")), ourFree + theirFree);

            string back = Path.Combine(_folder, "back.bin");
            Toolshed("copy", StressToolshed.WslPath(ours) + ",PAYLOAD", StressToolshed.WslPath(back));
            Assert.That(File.ReadAllBytes(back), Is.EqualTo(payload), "Toolshed copies the payload back out byte for byte");

            // And our own reader, on a fresh mount of the grown file.
            using (FileStream stream = File.OpenRead(ours))
            {
                DragonDskDriver driver = new DragonDskDriver();
                driver.Mount(stream, true);
                Assert.That(driver.ReadFile("/PAYLOAD"), Is.EqualTo(payload));
                Assert.That(driver.GetBlocksFree(), Is.EqualTo(ourFreeSectors));
                driver.Dispose();
            }

            Assert.That(Hash(source), Is.EqualTo(hashBefore), "the original image is unchanged");
        }
    }
}
