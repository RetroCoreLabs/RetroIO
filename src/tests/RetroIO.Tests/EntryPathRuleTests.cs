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
