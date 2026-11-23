//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Local.FileSystems;

namespace RetroIO.Tests
{
    /// <summary>
    /// The bytes a host file name becomes on a Commodore disk: the same bytes a C64 in its start-up
    /// character set stores when the name is typed, so the name shows as letters and LOAD finds it.
    /// </summary>
    /// <remarks>
    /// Letter keys typed without Shift give PETSCII 41h to 5Ah, shown as capitals in the start-up set;
    /// C1h to DAh are the shifted keys, shown as graphics there (sta.c64.org/cbm64pettoscr.html).
    /// </remarks>
    [TestFixture]
    public class CbmNameBytesTests
    {
        private static CbmRawDirectoryEntry WriteAndRead(string hostName)
        {
            CommodoreDiskDriverProvider provider = new CommodoreDiskDriverProvider();
            byte[] image = provider.CreateBlank("cbm-d64", new Dictionary<string, string>());
            MemoryStream stream = new MemoryStream();
            stream.Write(image, 0, image.Length);
            stream.Position = 0;
            CommodoreDiskDriver disk = (CommodoreDiskDriver)provider.Mount(stream, false, "cbm-d64");
            disk.WriteFile("/" + hostName, new byte[] { 1 });
            CbmRawDirectoryEntry[] raw = disk.ReadRawDirectory();
            Assert.That(raw.Length, Is.EqualTo(1));
            return raw[0];
        }

        [TestCase("HELLO")]
        [TestCase("hello")]
        [TestCase("HeLLo")]
        public void Letters_AreStoredAsTypedOnAC64(string hostName)
        {
            byte[] name = WriteAndRead(hostName).Name;
            Assert.That(name[0..5], Is.EqualTo(new byte[] { 0x48, 0x45, 0x4C, 0x4C, 0x4F }));
            Assert.That(name[5], Is.EqualTo(0xA0), "padded with shifted spaces");
        }

        [Test]
        public void EveryLetter_AToZ()
        {
            byte[] name = WriteAndRead("ABCDEFGHIJKLMNOP").Name;
            for (int i = 0; i < 16; i++)
            {
                Assert.That(name[i], Is.EqualTo(0x41 + i), "letter " + (char)('A' + i));
            }

            name = WriteAndRead("qrstuvwxyz").Name;
            for (int i = 0; i < 10; i++)
            {
                Assert.That(name[i], Is.EqualTo(0x51 + i), "letter " + (char)('q' + i));
            }
        }

        [TestCase("HELLO")]
        [TestCase("hello 2-1.5")]
        public void EncodeText_GivesTheBytesAFileNameGets(string text)
        {
            CbmRawDirectoryEntry written = WriteAndRead(text);
            CommodoreDiskDriver disk = (CommodoreDiskDriver)new CommodoreDiskDriverProvider().Mount(
                new MemoryStream(new CommodoreDiskDriverProvider().CreateBlank("cbm-d64", new Dictionary<string, string>())), true, "cbm-d64");
            byte[] encoded = ((RetroIO.Storage.Core.IRawDirectoryNames)disk).EncodeText(text);
            Assert.That(encoded, Is.EqualTo(written.Name[0..text.Length]));
        }

        [Test]
        public void EncodeText_RefusesACharacterWithNoByte()
        {
            CommodoreDiskDriver disk = (CommodoreDiskDriver)new CommodoreDiskDriverProvider().Mount(
                new MemoryStream(new CommodoreDiskDriverProvider().CreateBlank("cbm-d64", new Dictionary<string, string>())), true, "cbm-d64");
            System.ArgumentException e = Assert.Throws<System.ArgumentException>(() => disk.EncodeText("A~B"))!;
            Assert.That(e.Message, Does.Contain("'~'"));
        }

        [Test]
        public void DigitsAndSymbols_Unchanged()
        {
            byte[] name = WriteAndRead("GAME 2-1.5").Name;
            Assert.That(name[0..10], Is.EqualTo(new byte[] { 0x47, 0x41, 0x4D, 0x45, 0x20, 0x32, 0x2D, 0x31, 0x2E, 0x35 }));
        }
    }
}
