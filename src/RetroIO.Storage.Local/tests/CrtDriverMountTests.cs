//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests that <see cref="CrtDriver"/> can be mounted more than once on the same instance.
    /// The cartridges are built in memory: a 64-byte header, then CHIP packets.
    /// </summary>
    [TestFixture]
    public class CrtDriverMountTests
    {
        private static void WriteBe(byte[] data, int offset, uint value, int bytes)
        {
            for (int i = 0; i < bytes; i++)
                data[offset + i] = (byte)(value >> (8 * (bytes - 1 - i)));
        }

        // A cartridge with the given name and one 16-byte CHIP packet per entry of banks.
        private static Stream Cartridge(string name, params ushort[] banks)
        {
            byte[] image = new byte[0x40 + banks.Length * 0x20];
            Encoding.ASCII.GetBytes("C64 CARTRIDGE   ").CopyTo(image, 0);
            WriteBe(image, 0x10, 0x40, 4);                         // header length
            Encoding.ASCII.GetBytes(name).CopyTo(image, 0x20);     // cartridge name
            for (int i = 0; i < banks.Length; i++)
            {
                int at = 0x40 + i * 0x20;
                Encoding.ASCII.GetBytes("CHIP").CopyTo(image, at);
                WriteBe(image, at + 0x04, 0x20, 4);                // packet length: 16 header + 16 data
                WriteBe(image, at + 0x08, 0, 2);                   // chip type
                WriteBe(image, at + 0x0A, banks[i], 2);            // bank
                WriteBe(image, at + 0x0C, 0x8000, 2);              // load address
                WriteBe(image, at + 0x0E, 0x10, 2);                // rom size
                for (int b = 0; b < 16; b++)
                    image[at + 0x10 + b] = (byte)(0xA0 + banks[i]);
            }
            return new MemoryStream(image);
        }

        [Test]
        public void Mount_TheSameCartridgeTwice_ListsEachChipOnce()
        {
            // Regression: the chip list was never cleared, so a second Mount on the same instance
            // listed every chip again.
            using CrtDriver driver = new CrtDriver();
            driver.Mount(Cartridge("TWICE", 0, 1), readOnly: true);
            driver.Mount(Cartridge("TWICE", 0, 1), readOnly: true);

            Assert.That(driver.ListDirectory("/").Length, Is.EqualTo(2));
        }

        [Test]
        public void Mount_ADifferentCartridge_ListsOnlyTheNewOnesChips()
        {
            using CrtDriver driver = new CrtDriver();
            driver.Mount(Cartridge("FIRST", 0, 1, 2), readOnly: true);
            driver.Mount(Cartridge("SECOND", 7), readOnly: true);

            DirectoryEntry[] entries = driver.ListDirectory("/");
            Assert.That(entries.Length, Is.EqualTo(1));
            Assert.That(entries[0].Name, Does.Contain("B07"));
            Assert.That(driver.VolumeLabel, Is.EqualTo("SECOND"));
        }

        [Test]
        public void Mount_ThatFails_LeavesThePreviousCartridgeIntact()
        {
            using CrtDriver driver = new CrtDriver();
            driver.Mount(Cartridge("KEEPME", 0, 1), readOnly: true);

            Assert.Throws<InvalidDataException>(() => driver.Mount(new MemoryStream(new byte[100]), readOnly: true));

            Assert.That(driver.VolumeLabel, Is.EqualTo("KEEPME"));
            DirectoryEntry[] entries = driver.ListDirectory("/");
            Assert.That(entries.Length, Is.EqualTo(2));
            byte[] expected = new byte[16];
            Array.Fill(expected, (byte)0xA0);   // bank 0 of the first cartridge
            Assert.That(driver.ReadFile(entries[0].Name), Is.EqualTo(expected), "the first cartridge's data must still be there");
        }
    }
}
