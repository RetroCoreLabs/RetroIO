//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Ndfs.Drivers;

namespace RetroIO.Tests
{
    /// <summary>
    /// The blank-image rule, checked over every file-system provider: a provider that writes offers
    /// blank images, and every blank image it offers is detected, mounts and takes a file.
    /// </summary>
    [TestFixture]
    public class BlankImageRuleTests
    {
        private static IFileSystemDriverProvider[] AllProviders()
        {
            return new IFileSystemDriverProvider[]
            {
                new V7DriverProvider(),
                new CommodoreDiskDriverProvider(),
                new CpmDriverProvider(),
                new CrtDriverProvider(),
                new DragonDmkDriverProvider(),
                new DragonDskDriverProvider(),
                new DragonVdkDriverProvider(),
                new FfsDriverProvider(),
                new P00DriverProvider(),
                new PcFloppyDriverProvider(),
                new PrgDriverProvider(),
                new T64DriverProvider(),
                new TapDriverProvider(),
                new NdfsDriverProvider(),
            };
        }

        private static IEnumerable<TestCaseData> Layouts()
        {
            IFileSystemDriverProvider[] providers = AllProviders();
            for (int p = 0; p < providers.Length; p++)
            {
                if (providers[p] is IBlankImageProvider blank)
                {
                    IReadOnlyList<BlankImageLayout> layouts = blank.BlankLayouts;
                    for (int i = 0; i < layouts.Count; i++)
                    {
                        yield return new TestCaseData(providers[p].FileSystemType, layouts[i].Id).SetName("Blank " + layouts[i].Id);
                    }
                }
            }
        }

        [Test]
        public void EveryProviderThatWrites_OffersBlankImages()
        {
            IFileSystemDriverProvider[] providers = AllProviders();
            string missing = string.Empty;
            for (int p = 0; p < providers.Length; p++)
            {
                bool offers = providers[p] is IBlankImageProvider blank && blank.BlankLayouts.Count > 0;
                if (providers[p].SupportsWrite != offers)
                {
                    missing += " " + providers[p].FileSystemType + (offers ? " (offers blanks but says it cannot write)" : " (writes, no blank images)");
                }
            }

            Assert.That(missing, Is.Empty);
        }

        [Test]
        public void LayoutIds_AreUnique_AndSameBytesAsNamesRealLayouts()
        {
            HashSet<string> ids = new HashSet<string>();
            List<BlankImageLayout> all = new List<BlankImageLayout>();
            IFileSystemDriverProvider[] providers = AllProviders();
            for (int p = 0; p < providers.Length; p++)
            {
                if (providers[p] is IBlankImageProvider blank)
                {
                    for (int i = 0; i < blank.BlankLayouts.Count; i++)
                    {
                        Assert.That(ids.Add(blank.BlankLayouts[i].Id), Is.True, "duplicate layout id " + blank.BlankLayouts[i].Id);
                        all.Add(blank.BlankLayouts[i]);
                    }
                }
            }

            for (int i = 0; i < all.Count; i++)
            {
                for (int s = 0; s < all[i].SameBytesAs.Count; s++)
                {
                    Assert.That(ids.Contains(all[i].SameBytesAs[s]), Is.True, all[i].Id + " names " + all[i].SameBytesAs[s]);
                }
            }
        }

        [TestCaseSource(nameof(Layouts))]
        public void BlankImage_IsDetectedMountsAndTakesAFile(string fileSystemType, string layoutId)
        {
            IFileSystemDriverProvider[] providers = AllProviders();
            IBlankImageProvider? blank = null;
            BlankImageLayout? layout = null;
            DriverFactory factory = new DriverFactory();
            for (int p = 0; p < providers.Length; p++)
            {
                factory.Register(providers[p]);
                if (providers[p].FileSystemType == fileSystemType)
                {
                    blank = (IBlankImageProvider)providers[p];
                }
            }

            for (int i = 0; i < blank!.BlankLayouts.Count; i++)
            {
                if (blank.BlankLayouts[i].Id == layoutId)
                {
                    layout = blank.BlankLayouts[i];
                }
            }

            byte[] image = blank.CreateBlank(layoutId, new Dictionary<string, string>());
            Assert.That(image.Length, Is.EqualTo(layout!.SizeBytes), "size");

            byte[] data = new byte[3000];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i * 7 + 1);
            }

            // Mounted as the named layout: empty, takes a file, reads it back on a fresh mount.
            MemoryStream namedStream = NewStream(image);
            byte[] named = WriteOneFile(blank.Mount(namedStream, false, layoutId), data, namedStream);
            IFileSystemDriver reread = blank.Mount(new MemoryStream(named, writable: false), true, layoutId);
            Assert.That(reread.ReadFile(FindTestFile(reread)).AsSpan(0, data.Length).SequenceEqual(data), Is.True, "file read back");

            // Detected the way a program opens a file: by its path and extension.
            string folder = Path.Combine(Path.GetTempPath(), "retroio-blank-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "blank" + layout.Extension);
                File.WriteAllBytes(path, image);
                IFileSystemDriver? detected = factory.Detect(path);
                Assert.That(detected, Is.Not.Null, "detected");
                Assert.That(detected!.FileSystemType, Is.EqualTo(((IFileSystemDriverProvider)blank).CreateDriver().FileSystemType), "detected as its own file system");
                if (layout.SameBytesAs.Count == 0)
                {
                    // Plain detection must pick this exact layout: a file written through it reads
                    // back through the named mount.
                    MemoryStream detectedStream = NewStream(image);
                    detected.Mount(detectedStream, false);
                    byte[] viaDetect = WriteOneFile(detected, data, detectedStream);

                    // Compared by reading, not byte for byte: a file system may stamp the time.
                    IFileSystemDriver check = blank.Mount(new MemoryStream(viaDetect, writable: false), true, layoutId);
                    Assert.That(check.ReadFile(FindTestFile(check)).AsSpan(0, data.Length).SequenceEqual(data), Is.True, "detection mounts the same layout");
                }
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        private static MemoryStream NewStream(byte[] image)
        {
            MemoryStream stream = new MemoryStream();
            stream.Write(image, 0, image.Length);
            stream.Position = 0;
            return stream;
        }

        private static byte[] WriteOneFile(IFileSystemDriver driver, byte[] data, MemoryStream stream)
        {
            // A blank disk holds no files. Folders the format always has (for example the SYSTEM
            // user of an NDFS disk) are allowed, but they must be empty too.
            string folder = "/";
            Assert.That(CountFiles(driver, "/", ref folder), Is.EqualTo(0), "a blank disk holds no files");
            driver.WriteFile(folder + TestFile(driver), data);
            if (driver is IDisposable disposable)
            {
                disposable.Dispose();
            }

            return stream.ToArray();
        }

        private static string TestFile(IFileSystemDriver driver) => "TEST" + driver.ExtensionSeparator + "DAT";

        private static string FindTestFile(IFileSystemDriver driver)
        {
            string folder = "/";
            CountFiles(driver, "/", ref folder);
            return folder + TestFile(driver);
        }

        private static int CountFiles(IFileSystemDriver driver, string path, ref string firstFolder)
        {
            DirectoryEntry[] entries = driver.ListDirectory(path);
            int files = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Metadata.IsDirectory)
                {
                    string sub = path + entries[i].Name + "/";
                    if (firstFolder == "/")
                    {
                        firstFolder = sub;
                    }

                    files += CountFiles(driver, sub, ref firstFolder);
                }
                else
                {
                    files++;
                }
            }

            return files;
        }
    }
}
