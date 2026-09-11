//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tests
{
    /// <summary>
    /// Tests for the SINTRAN III initial-command buffer read/write API
    /// (<see cref="SintranInitialCommands"/> + NdfsFileSystem entry points). The pure parser/encoder
    /// cases mirror the nd100x glass-UI unit test (template-glass/js/tests/test_initial_commands.js),
    /// whose expected values were captured on a live L07 system (incl. an offline write + reboot).
    /// The end-to-end cases run against the repo's real BIGDISK0-L.IMG SINTRAN L pack.
    /// </summary>
    [TestFixture]
    public class NdfsInitialCommandsTests
    {
        // "|" marks an explicit NUL pad byte in a fixture string (matches the JS test's buf() helper).
        private static byte[] Buf(string str)
        {
            var outBytes = new List<byte>();
            for (int i = 0; i < str.Length; i++)
                outBytes.Add(str[i] == '|' ? (byte)0 : (byte)str[i]);
            while (outBytes.Count < 64) outBytes.Add(0);
            return outBytes.ToArray();
        }

        // ---------------------------------------------------------------
        // 1. Buffer parser: text + 0x27, word-aligned, lone 0x27 terminates
        // ---------------------------------------------------------------

        [Test]
        public void ParseBuffer_TwoCommandsWithPad()
        {
            var cmds = SintranInitialCommands.ParseBuffer(Buf("AB'|CD'|'"), 0, 64);
            Assert.That(cmds, Is.EqualTo(new[] { "AB", "CD" }));
        }

        [Test]
        public void ParseBuffer_EvenCommandNeedsNoPad()
        {
            var cmds = SintranInitialCommands.ParseBuffer(Buf("SET-AVAIL'CC HELLO'|'"), 0, 64);
            Assert.That(cmds, Is.EqualTo(new[] { "SET-AVAIL", "CC HELLO" }));
        }

        [Test]
        public void ParseBuffer_LoneQuoteTerminatesImmediately()
        {
            var cmds = SintranInitialCommands.ParseBuffer(Buf("'"), 0, 64);
            Assert.That(cmds, Is.Empty);
        }

        [Test]
        public void ParseBuffer_ParityBitStripped()
        {
            // 0xC1 0xC2 => 'A' 'B' after &0x7F, terminated by 0x27
            var cmds = SintranInitialCommands.ParseBuffer(new byte[] { 0xC1, 0xC2, 0x27, 0x00, 0x27 }, 0, 5);
            Assert.That(cmds, Is.EqualTo(new[] { "AB" }));
        }

        [Test]
        public void ParseBuffer_NonPrintablePayloadRejected()
        {
            var cmds = SintranInitialCommands.ParseBuffer(new byte[] { 0x01, 0x02, 0x27, 0x27 }, 0, 4);
            Assert.That(cmds, Is.Null);
        }

        // ---------------------------------------------------------------
        // 2. Version symbol table (INIBU) and region layout
        // ---------------------------------------------------------------

        [Test]
        public void Inibu_SymbolsMatchSymbolLists()
        {
            Assert.That(SintranInitialCommands.Inibu['K'], Is.EqualTo(Convert.ToInt32("67172", 8)));
            Assert.That(SintranInitialCommands.Inibu['L'], Is.EqualTo(Convert.ToInt32("74123", 8)));
            Assert.That(SintranInitialCommands.Inibu['M'], Is.EqualTo(Convert.ToInt32("102327", 8)));
        }

        [Test]
        public void RegionLayout_Constants()
        {
            Assert.That(SintranInitialCommands.CapBytes, Is.EqualTo(262));        // INIBU..INCOM = 131 words
            Assert.That(SintranInitialCommands.LengthCellBytes, Is.EqualTo(260)); // length cell at INIBU+130 words
            Assert.That(SintranInitialCommands.MaxTextBytes, Is.EqualTo(254));    // NEXIN literal 252 -> (252+2)&~1
        }

        // ---------------------------------------------------------------
        // 3. Encoder: textLen EXCLUDES the terminator (this is the length cell).
        //    Values below were observed on the real L07 system.
        // ---------------------------------------------------------------

        private static readonly string[] Three =
        {
            "ENTER-DIR,,DI-75-1,0", "ENTER-DIR,,DI-74-1,0", "SET-AVAIL"
        };

        [Test]
        public void Encode_ThreeRealCommands_TextLen54()
        {
            // Matches the live 0x36 length cell.
            Assert.That(SintranInitialCommands.EncodeBuffer(Three).TextLen, Is.EqualTo(54));
        }

        [Test]
        public void Encode_PlusCcHello_TextLen64()
        {
            var list = new List<string>(Three) { "CC HELLO" };
            Assert.That(SintranInitialCommands.EncodeBuffer(list).TextLen, Is.EqualTo(64));
        }

        [Test]
        public void Encode_PlusOfflineWriteOk_TextLen74()
        {
            // The exact command set that booted OK after an offline write on live L07.
            var list = new List<string>(Three) { "CC OFFLINE WRITE OK" };
            Assert.That(SintranInitialCommands.EncodeBuffer(list).TextLen, Is.EqualTo(74));
        }

        [Test]
        public void Encode_Uppercases()
        {
            var enc = SintranInitialCommands.EncodeBuffer(new[] { "cc hi" });
            // First 5 bytes should be "CC HI"
            var head = System.Text.Encoding.ASCII.GetString(enc.Bytes, 0, 5);
            Assert.That(head, Is.EqualTo("CC HI"));
        }

        [Test]
        public void Encode_254BytesAccepted_AtTheLimit()
        {
            // 253 'C' + 1 quote = 254, even.
            var enc = SintranInitialCommands.EncodeBuffer(new[] { new string('C', 253) });
            Assert.That(enc.TextLen, Is.EqualTo(254));
        }

        [Test]
        public void Encode_256BytesRefused_OverTheLimit()
        {
            Assert.Throws<InvalidOperationException>(
                () => SintranInitialCommands.EncodeBuffer(new[] { new string('C', 255) }));
        }

        [Test]
        public void Encode_EmbeddedQuoteRefused()
        {
            Assert.Throws<ArgumentException>(
                () => SintranInitialCommands.EncodeBuffer(new[] { "BAD'CMD" }));
        }

        [Test]
        public void ReEncodeMatches_RejectsCoincidentalParse()
        {
            // The exact false positive seen on a corrupted tor-disk pack: a garbage region whose length
            // cell happened to read 2 and whose first two bytes were the printable "B,". Re-encoding "B,"
            // yields 4 bytes ('B' ',' 0x27 pad), not 2 — so it must be rejected as NOT self-consistent.
            Assert.That(SintranInitialCommands.ReEncodeMatches(new[] { "B," }, 2), Is.False);

            // A real buffer round-trips exactly: the 3 stock commands re-encode to their stored length 54.
            Assert.That(SintranInitialCommands.ReEncodeMatches(Three, 54), Is.True);
        }

        [Test]
        public void Encode_Then_Parse_RoundTrip()
        {
            var list = new List<string>(Three) { "CC OFFLINE WRITE OK" };
            var enc = SintranInitialCommands.EncodeBuffer(list);
            var parsed = SintranInitialCommands.ParseBuffer(enc.Bytes, 0, enc.TextLen);
            Assert.That(parsed, Is.EqualTo(list));
        }

        // ---------------------------------------------------------------
        // 4. End-to-end against the real BIGDISK0-L.IMG SINTRAN L pack.
        //    Located purely via symbol + segment table - no byte searching.
        // ---------------------------------------------------------------

        private static string? ResolveRealImage()
        {
            string[] candidates =
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\demo\test-images\ndfs\BIGDISK0-L.IMG"),
                @"E:\Dev\Ronny\RetroFS\demo\test-images\ndfs\BIGDISK0-L.IMG",
                "/mnt/e/Dev/Ronny/RetroFS/demo/test-images/ndfs/BIGDISK0-L.IMG",
            };
            for (int i = 0; i < candidates.Length; i++)
                if (File.Exists(candidates[i])) return candidates[i];
            return null;
        }

        private static NdfsFileSystem MountReadOnly(string imagePath, out GenericBlockDevice device, out Stream stream)
        {
            stream = File.Open(imagePath, FileMode.Open, FileAccess.Read);
            var descriptor = NdfsBlockDeviceDescriptor.Create((int)stream.Length);
            device = new GenericBlockDevice(descriptor);
            device.Mount(stream, readOnly: true);
            var fs = new NdfsFileSystem();
            fs.Initialize(device);
            return fs;
        }

        [Test]
        public void RealPack_Locate_MatchesLiveL07()
        {
            string? image = ResolveRealImage();
            if (image == null) Assert.Ignore("Real NDFS disk image BIGDISK0-L.IMG not found");

            using var fs = MountReadOnly(image!, out var device, out var stream);
            using (device)
            using (stream)
            {
                SintranInitialCommandBuffer? buffer = fs.ReadSintranInitialCommands();

                Assert.That(buffer, Is.Not.Null, "should locate the initial-command buffer on an L pack");
                Assert.That(buffer!.VersionLetter, Is.EqualTo('L'));
                Assert.That(buffer.Commands, Is.EqualTo(Three));
                // These exact values are asserted in the nd100x live-verified test.
                Assert.That(buffer.ByteOffset, Is.EqualTo(2920614));                  // 0x2C90A6
                Assert.That(buffer.LengthCellOffset, Is.EqualTo(2920614 + 260));
                Assert.That(buffer.TextLength, Is.EqualTo(54));                        // 0x36
                Assert.That(buffer.SegfilPath.ToUpperInvariant(), Does.Contain("SEGFIL0"));
            }
        }

        [Test]
        public void RealPack_WriteRoundTrip_PersistsToDisk()
        {
            string? image = ResolveRealImage();
            if (image == null) Assert.Ignore("Real NDFS disk image BIGDISK0-L.IMG not found");

            // Work on a throwaway copy so the fixture image is never modified.
            string temp = Path.Combine(Path.GetTempPath(), "retrofs-initcmd-" + Guid.NewGuid().ToString("N") + ".img");
            File.Copy(image!, temp, true);
            try
            {
                var newCommands = new List<string>(Three) { "CC OFFLINE WRITE OK" };

                // Write on a read-write mount, then dispose to flush + release the file.
                using (var stream = File.Open(temp, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create((int)stream.Length);
                    using var device = new GenericBlockDevice(descriptor);
                    device.Mount(stream, readOnly: false);
                    using var fs = new NdfsFileSystem();
                    fs.Initialize(device);

                    bool ok = fs.WriteSintranInitialCommands(newCommands);
                    Assert.That(ok, Is.True, "write should succeed on a locatable L pack");
                }

                // Re-open from scratch and confirm the new buffer is on disk with the right length cell.
                using (var fs2 = MountReadOnly(temp, out var device2, out var stream2))
                using (device2)
                using (stream2)
                {
                    var buffer = fs2.ReadSintranInitialCommands();
                    Assert.That(buffer, Is.Not.Null);
                    Assert.That(buffer!.Commands, Is.EqualTo(newCommands));
                    Assert.That(buffer.TextLength, Is.EqualTo(74));  // live-verified value for these 4 commands
                }
            }
            finally
            {
                try { File.Delete(temp); } catch { /* best effort cleanup */ }
            }
        }

        [Test]
        public void WriteToReadOnly_Throws()
        {
            string? image = ResolveRealImage();
            if (image == null) Assert.Ignore("Real NDFS disk image BIGDISK0-L.IMG not found");

            using var fs = MountReadOnly(image!, out var device, out var stream);
            using (device)
            using (stream)
            {
                Assert.Throws<InvalidOperationException>(
                    () => fs.WriteSintranInitialCommands(new[] { "SET-AVAIL" }));
            }
        }
    }
}
