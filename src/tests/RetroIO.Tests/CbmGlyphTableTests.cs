//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;

namespace RetroIO.Tests
{
    /// <summary>
    /// The Commodore driver's PETSCII to screen code table, checked entry by entry against the
    /// published conversion table.
    /// </summary>
    [TestFixture]
    public class CbmGlyphTableTests
    {
        // The rows of https://sta.c64.org/cbm64pettoscr.html (read 2026-11-23), copied as the
        // first and last PETSCII code of each row and the screen code of the first:
        //   0-31 -> 128-159, 32-63 -> 32-63, 64-95 -> 0-31, 96-127 -> 64-95, 128-159 -> 192-223,
        //   160-191 -> 96-127, 192-223 -> 64-95, 224-254 -> 96-126, 255 -> 94.
        private static readonly int[][] s_rows =
        {
            new[] { 0, 31, 128 },
            new[] { 32, 63, 32 },
            new[] { 64, 95, 0 },
            new[] { 96, 127, 64 },
            new[] { 128, 159, 192 },
            new[] { 160, 191, 96 },
            new[] { 192, 223, 64 },
            new[] { 224, 254, 96 },
            new[] { 255, 255, 94 },
        };

        [Test]
        public void EveryByte_GetsThePublishedScreenCode()
        {
            CommodoreDiskDriverProvider provider = new CommodoreDiskDriverProvider();
            MemoryStream stream = new MemoryStream(provider.CreateBlank("cbm-d64", new Dictionary<string, string>()));
            using IFileSystemDriver driver = provider.Mount(stream, true, "cbm-d64");
            byte[] table = ((IRawDirectoryNames)driver).RawLayout.GlyphIndex;

            Assert.That(table.Length, Is.EqualTo(256));
            int checkedCount = 0;
            for (int r = 0; r < s_rows.Length; r++)
            {
                for (int p = s_rows[r][0]; p <= s_rows[r][1]; p++)
                {
                    Assert.That(table[p], Is.EqualTo(s_rows[r][2] + (p - s_rows[r][0])), "PETSCII " + p.ToString("X2"));
                    checkedCount++;
                }
            }

            Assert.That(checkedCount, Is.EqualTo(256), "the rows cover every byte once");
        }
    }
}
