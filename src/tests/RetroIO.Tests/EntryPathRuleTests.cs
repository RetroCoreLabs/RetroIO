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
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Ndfs.Drivers;

namespace RetroIO.Tests
{
    /// <summary>
    /// The entry-path rule, checked on a blank image of every layout: every entry a folder lists has
    /// the path of that folder plus its name, and that path finds the same entry again. A file is
    /// written in a new folder when the format has folders, so a listing below the root is checked.
    /// </summary>
    [TestFixture]
    public class EntryPathRuleTests
    {
        private static IEnumerable<TestCaseData> Layouts()
        {
            IFileSystemDriverProvider[] providers =
            {
                new CommodoreDiskDriverProvider(), new CpmDriverProvider(), new DragonDmkDriverProvider(),
                new DragonDskDriverProvider(), new DragonVdkDriverProvider(), new PcFloppyDriverProvider(),
                new NdfsDriverProvider(),
            };
            for (int p = 0; p < providers.Length; p++)
            {
                IBlankImageProvider blank = (IBlankImageProvider)providers[p];
                IReadOnlyList<BlankImageLayout> layouts = blank.BlankLayouts;
                for (int i = 0; i < layouts.Count; i++)
                {
                    yield return new TestCaseData(blank, layouts[i].Id).SetName("Paths " + layouts[i].Id);
                }
            }
        }

        [TestCaseSource(nameof(Layouts))]
        public void EveryListedEntry_HasItsFolderPathAndIsFoundByIt(IBlankImageProvider blank, string layoutId)
        {
            MemoryStream stream = new MemoryStream();
            byte[] image = blank.CreateBlank(layoutId, new Dictionary<string, string>());
            stream.Write(image, 0, image.Length);
            stream.Position = 0;
            IFileSystemDriver driver = blank.Mount(stream, false, layoutId);

            string folder = FirstFolder(driver);
            string name = "A" + driver.ExtensionSeparator + "TXT";
            driver.WriteFile(folder + name, new byte[] { 1, 2, 3 });
            try
            {
                driver.CreateDirectory(folder + "SUB");
                driver.WriteFile(folder + "SUB/" + name, new byte[] { 4, 5, 6 });
            }
            catch (Exception e) when (e is NotSupportedException or UnauthorizedAccessException or InvalidOperationException or IOException)
            {
                // This format or this folder has no subfolders; the listings it has are still checked.
            }

            string wrong = string.Empty;
            Check(driver, "/", ref wrong);
            Assert.That(wrong, Is.Empty);
        }

        [Test]
        public void Ndfs_DoubledSlash_DoesNotBecomePartOfTheName()
        {
            // A folder given with a trailing slash and a name joined to it ("/SYSTEM/" + "/" + name)
            // stored the file as "/TPE:BPUN", slash included; FLOMON and SINTRAN cannot find it.
            NdfsDriverProvider ndfs = new NdfsDriverProvider();
            MemoryStream stream = new MemoryStream();
            byte[] image = ndfs.CreateBlank("ndfs-floppy-1200k", new Dictionary<string, string>());
            stream.Write(image, 0, image.Length);
            stream.Position = 0;
            IFileSystemDriver driver = ndfs.Mount(stream, false, "ndfs-floppy-1200k");

            driver.WriteFile("/SYSTEM//TPE:BPUN", new byte[] { 1, 2, 3 });

            DirectoryEntry[] entries = driver.ListDirectory("/SYSTEM/");
            Assert.That(entries.Length, Is.EqualTo(1));
            Assert.That(entries[0].Name, Is.EqualTo("TPE:BPUN"));
            Assert.That(driver.ReadFile("/SYSTEM/TPE:BPUN"), Is.EqualTo(new byte[] { 1, 2, 3 }));
        }

        [Test]
        public void Ndfs_FileWrite_KeepsTheBytesAfterTheMapInTheBitFilePage()
        {
            // A FLOMON floppy keeps part of FLOMON after the free-page map, in the same page: FLOMON
            // reads that whole page into its own memory at boot (measured on nd100x with the TPE
            // floppy 210523I01-XX-01D). Rewriting the page from zeros made the floppy unbootable.
            NdfsDriverProvider ndfs = new NdfsDriverProvider();
            byte[] image = ndfs.CreateBlank("ndfs-floppy-1200k", new Dictionary<string, string>());
            int bitFilePage = (image[2042] << 8) | image[2043];
            int tail = (bitFilePage * 2048) + 78;
            for (int i = tail; i < (bitFilePage + 1) * 2048; i++)
            {
                image[i] = (byte)(i * 13 + 5);
            }

            byte[] before = image.AsSpan(tail, 2048 - 78).ToArray();
            MemoryStream stream = new MemoryStream();
            stream.Write(image, 0, image.Length);
            stream.Position = 0;
            IFileSystemDriver driver = ndfs.Mount(stream, false, "ndfs-floppy-1200k");
            driver.WriteFile("/SYSTEM/A:DATA", new byte[5000]);
            driver.DeleteFile("/SYSTEM/A:DATA");
            if (driver is IDisposable disposable)
            {
                disposable.Dispose();
            }

            byte[] after = stream.ToArray().AsSpan(tail, 2048 - 78).ToArray();
            Assert.That(bitFilePage, Is.EqualTo(306), "the 1.2 MB floppy's bit file is page 462 octal");
            Assert.That(after, Is.EqualTo(before));
        }

        [Test]
        public void Ndfs_NewFloppy_CarriesAllOfFlomon()
        {
            // FLOMON is in three places on a bootable floppy (measured on nd100x with the TPE
            // floppy 210523I01-XX-01D): page 0, bytes 2000 to 2015 of page 0 (its load addresses
            // 3533 and 5533 octal and its text), and the bit file page after the map, which it
            // reads whole into its memory at 3533 octal. A floppy without the last two prints nothing.
            NdfsDriverProvider ndfs = new NdfsDriverProvider();
            byte[] image = ndfs.CreateBlank("ndfs-floppy-1200k", new Dictionary<string, string>());

            Assert.That((image[2000] << 8) | image[2001], Is.EqualTo(Convert.ToInt32("125003", 8)));
            Assert.That((image[2002] << 8) | image[2003], Is.EqualTo(Convert.ToInt32("3533", 8)));
            Assert.That((image[2004] << 8) | image[2005], Is.EqualTo(Convert.ToInt32("5533", 8)));

            int bitFilePage = (image[2042] << 8) | image[2043];
            int nonZero = 0;
            for (int i = (bitFilePage * 2048) + 78; i < (bitFilePage + 1) * 2048; i++)
            {
                if (image[i] != 0)
                {
                    nonZero++;
                }
            }

            Assert.That(nonZero, Is.GreaterThan(1000), "FLOMON's code after the map");
        }

        [Test]
        public void Ndfs_ZeroPages_AreStored_UnlessTheXatListsThemAsHoles()
        {
            // FLOMON cannot load a file with holes, and the ND-made TPE floppy stores its zero
            // pages. So a plain write stores every page; only a write that carries a hole list
            // (from a .xat side file) makes those pages holes again.
            byte[] data = new byte[5 * 2048];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (i / 2048) == 1 || (i / 2048) == 3 ? (byte)0 : (byte)(i + 1);
            }

            NdfsDriverProvider ndfs = new NdfsDriverProvider();
            byte[] image = ndfs.CreateBlank("ndfs-floppy-1200k", new Dictionary<string, string>());
            MemoryStream stream = new MemoryStream();
            stream.Write(image, 0, image.Length);
            stream.Position = 0;
            NdfsDriver driver = (NdfsDriver)ndfs.Mount(stream, false, "ndfs-floppy-1200k");

            driver.WriteFile("/SYSTEM/PLAIN:DATA", data);
            Assert.That((int[])driver.GetFileProperties("/SYSTEM/PLAIN:DATA")![RetroIO.Storage.Ndfs.PropertyBags.NdfsPropertyBagKeys.Holes], Is.Empty, "plain write");

            IDictionary<string, object> fromXat = RetroIO.Storage.Ndfs.PropertyBags.NdfsXatSerializer.DeserializeFromJson(
                RetroIO.Storage.Ndfs.PropertyBags.NdfsXatSerializer.SerializeToJson(new Dictionary<string, object>
                {
                    { RetroIO.Storage.Ndfs.PropertyBags.NdfsPropertyBagKeys.Holes, new[] { 3 } },
                }));
            driver.WriteFileWithProperties("/SYSTEM/SPARSE:DATA", data, fromXat);
            Assert.That((int[])driver.GetFileProperties("/SYSTEM/SPARSE:DATA")![RetroIO.Storage.Ndfs.PropertyBags.NdfsPropertyBagKeys.Holes], Is.EqualTo(new[] { 3 }), "only the listed zero page");
            Assert.That(driver.ReadFile("/SYSTEM/SPARSE:DATA"), Is.EqualTo(data));
            Assert.That(driver.ReadFile("/SYSTEM/PLAIN:DATA"), Is.EqualTo(data));
        }

        private static void Check(IFileSystemDriver driver, string folder, ref string wrong)
        {
            DirectoryEntry[] entries = driver.ListDirectory(folder);
            for (int i = 0; i < entries.Length; i++)
            {
                string expected = folder + entries[i].Name;
                if (entries[i].Metadata.Path != expected)
                {
                    wrong += " listed " + expected + " has path " + entries[i].Metadata.Path + ";";
                }

                FileMetadata? found = driver.GetMetadata(expected);
                if (found == null || found.Name != entries[i].Name || found.IsDirectory != entries[i].Metadata.IsDirectory)
                {
                    wrong += " " + expected + " is not found by its path;";
                }
                else if (found.Path != expected)
                {
                    wrong += " " + expected + " looked up has path " + found.Path + ";";
                }

                if (entries[i].Metadata.IsDirectory)
                {
                    Check(driver, expected + "/", ref wrong);
                }
            }
        }

        private static string FirstFolder(IFileSystemDriver driver)
        {
            DirectoryEntry[] entries = driver.ListDirectory("/");
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Metadata.IsDirectory)
                {
                    return "/" + entries[i].Name + "/";
                }
            }

            return "/";
        }
    }
}
