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
    /// The <see cref="IRawDirectoryNames"/> rule, on a blank image of every writable layout whose
    /// driver offers it: raw names, separators and the disk header read back as the bytes written,
    /// the lengths agree with the driver's own <see cref="RawDirectoryLayout"/>, and the files keep
    /// their place and their data.
    /// </summary>
    [TestFixture]
    public class RawDirectoryNamesRuleTests
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
                    yield return new TestCaseData(blank, layouts[i].Id).SetName("RawNames " + layouts[i].Id);
                }
            }
        }

        private static IFileSystemDriver Mount(IBlankImageProvider blank, string layoutId)
        {
            MemoryStream stream = new MemoryStream();
            byte[] image = blank.CreateBlank(layoutId, new Dictionary<string, string>());
            stream.Write(image, 0, image.Length);
            stream.Position = 0;
            return blank.Mount(stream, false, layoutId);
        }

        private static byte[] Bytes(int length, byte first)
        {
            byte[] b = new byte[length];
            for (int i = 0; i < length; i++)
            {
                b[i] = (byte)(first + i);
            }

            return b;
        }

        [TestCaseSource(nameof(Layouts))]
        public void RawNamesSeparatorsAndHeader_ReadBackAsWritten(IBlankImageProvider blank, string layoutId)
        {
            IFileSystemDriver driver = Mount(blank, layoutId);
            if (driver is not IRawDirectoryNames raw)
            {
                driver.Dispose();
                Assert.Pass(layoutId + " does not offer raw directory names");
                return;
            }

            try
            {
                RawDirectoryLayout layout = raw.RawLayout;
                Assert.That(layout.NameLength, Is.GreaterThan(0));
                Assert.That(layout.CharacterSet, Is.Not.Empty);
                Assert.That(layout.SeparatorTypeName, Is.Not.Empty);
                Assert.That(layout.Description, Is.Not.Empty);
                Assert.That(raw.ReadRawVolumeName().Length, Is.EqualTo(layout.VolumeNameLength));
                Assert.That(raw.ReadRawVolumeId().Length, Is.EqualTo(layout.VolumeIdLength));

                string sep = driver.ExtensionSeparator;
                driver.WriteFile("/ONE" + sep + "PRG", new byte[] { 1, 2, 3 });
                driver.WriteFile("/TWO" + sep + "PRG", new byte[] { 4 });
                int files = raw.ReadRawDirectory().Length;

                // Separators at the start, between the files and at the end.
                byte[] a = Bytes(layout.NameLength, 0xB0);
                byte[] b = Bytes(layout.NameLength, 0x60);
                byte[] c = Bytes(layout.NameLength, 0xC0);
                raw.InsertSeparator(0, a);
                raw.InsertSeparator(2, b);
                raw.InsertSeparator(files + 2, c);

                RawDirectoryEntry[] entries = raw.ReadRawDirectory();
                Assert.That(entries.Length, Is.EqualTo(files + 3));
                for (int i = 0; i < entries.Length; i++)
                {
                    Assert.That(entries[i].Index, Is.EqualTo(i));
                    Assert.That(entries[i].Name.Length, Is.EqualTo(layout.NameLength));
                }

                Assert.That(entries[0].Name, Is.EqualTo(a));
                Assert.That(entries[2].Name, Is.EqualTo(b));
                Assert.That(entries[files + 2].Name, Is.EqualTo(c));
                Assert.That(entries[0].IsSeparator && entries[2].IsSeparator && entries[files + 2].IsSeparator, Is.True);
                Assert.That(entries[0].TypeName, Is.EqualTo(layout.SeparatorTypeName));
                Assert.That(entries[1].IsSeparator || entries[3].IsSeparator, Is.False, "the files are not separators");

                // A name written over an existing separator.
                byte[] d = Bytes(layout.NameLength, 0x20);
                raw.WriteRawName(2, d);
                Assert.That(raw.ReadRawDirectory()[2].Name, Is.EqualTo(d));

                // The disk header.
                byte[] name = Bytes(layout.VolumeNameLength, 0xA1);
                byte[] id = Bytes(layout.VolumeIdLength, 0x30);
                raw.WriteRawVolumeHeader(name, id);
                Assert.That(raw.ReadRawVolumeName(), Is.EqualTo(name));
                Assert.That(raw.ReadRawVolumeId(), Is.EqualTo(id));

                // Wrong lengths are refused.
                Assert.Throws<ArgumentException>(() => raw.InsertSeparator(0, new byte[layout.NameLength + 1]));
                Assert.Throws<ArgumentException>(() => raw.WriteRawName(0, new byte[layout.NameLength - 1]));

                // The files keep their data.
                Assert.That(driver.ReadFile("/ONE" + sep + "PRG"), Is.EqualTo(new byte[] { 1, 2, 3 }));
                Assert.That(driver.ReadFile("/TWO" + sep + "PRG"), Is.EqualTo(new byte[] { 4 }));
            }
            finally
            {
                driver.Dispose();
            }
        }

        [Test]
        public void AtLeastOneDriverOffersIt()
        {
            int offered = 0;
            foreach (TestCaseData data in Layouts())
            {
                IFileSystemDriver driver = Mount((IBlankImageProvider)data.Arguments[0]!, (string)data.Arguments[1]!);
                if (driver is IRawDirectoryNames)
                {
                    offered++;
                }

                driver.Dispose();
            }

            Assert.That(offered, Is.GreaterThan(0), "no driver offers raw directory names, so the rule tested nothing");
        }
    }
}
