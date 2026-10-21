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
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Local.Streams;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// The block device writes back only the blocks that changed, and a short image (a file that holds fewer blocks than its volume) grows
    /// only as far as the last block written.
    /// </summary>
    /// <remarks>
    /// The rules, as approved for the short NitrOS-9 DriveWire images (sector 0 says 524268 sectors, the file holds a few hundred):
    ///  - a write writes back only the blocks that changed;
    ///  - a write past the end of the file grows the file only as far as that block, with zero bytes in any gap;
    ///  - a read past the end of the file but below the volume size reads zero bytes;
    ///  - a read or write at or beyond the volume size is refused;
    ///  - the device holds only the part of the file that exists and the pages written, not the whole volume.
    /// </remarks>
    [TestFixture]
    public class BlockDeviceChangedSectorTests
    {
        private const int SectorSize = 256;

        private static BlockDeviceDescriptor Lba(uint blocks)
        {
            return new BlockDeviceDescriptor
            {
                Name = "LBA " + blocks,
                BlockSize = SectorSize,
                AddressMode = BlockAddressMode.LBA,
                Cylinders = 1,
                Heads = 1,
                UniformSectorsPerTrack = 1,
                TotalBlocks = blocks,
                ExpectedImageSize = (long)blocks * SectorSize,
                SupportsVariableGeometry = false
            };
        }

        private static byte[] Pattern(int length, int seed)
        {
            byte[] data = new byte[length];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)((i * 31) + seed);
            }

            return data;
        }

        private static byte[] Filled(byte value)
        {
            byte[] data = new byte[SectorSize];
            Array.Fill(data, value);
            return data;
        }

        [Test]
        public void Flush_WritesOnlyTheBlocksThatChanged()
        {
            byte[] image = Pattern(40 * SectorSize, 1);
            RecordingStream stream = new RecordingStream(image);
            GenericBlockDevice device = new GenericBlockDevice(Lba(40));
            device.Mount(stream, false);

            device.WriteBlock(BlockAddress.FromLBA(3), Filled(0xA3));
            device.WriteBlock(BlockAddress.FromLBA(4), Filled(0xA4));
            device.WriteBlock(BlockAddress.FromLBA(20), Filled(0xB4));
            device.Flush();

            // Two runs: blocks 3 and 4 together, then block 20.
            Assert.That(stream.Writes.Count, Is.EqualTo(2), "one write per run of changed blocks");
            Assert.That(stream.Writes[0], Is.EqualTo((3L * SectorSize, 2 * SectorSize)));
            Assert.That(stream.Writes[1], Is.EqualTo((20L * SectorSize, SectorSize)));

            byte[] expected = Pattern(40 * SectorSize, 1);
            Array.Fill(expected, (byte)0xA3, 3 * SectorSize, SectorSize);
            Array.Fill(expected, (byte)0xA4, 4 * SectorSize, SectorSize);
            Array.Fill(expected, (byte)0xB4, 20 * SectorSize, SectorSize);
            Assert.That(stream.ToArray(), Is.EqualTo(expected));

            // Nothing changed since: the next flush writes nothing.
            stream.Writes.Clear();
            device.Flush();
            Assert.That(stream.Writes.Count, Is.EqualTo(0));
            device.Dispose();
        }

        [Test]
        public void Flush_LeavesBytesItDidNotChange_EvenWhenTheStreamChangedUnderneath()
        {
            // Whole-image write-back would put the old bytes of block 7 back. Writing only changed blocks keeps what the stream holds there.
            byte[] image = Pattern(16 * SectorSize, 5);
            RecordingStream stream = new RecordingStream(image);
            GenericBlockDevice device = new GenericBlockDevice(Lba(16));
            device.Mount(stream, false);

            long position = stream.Position;
            stream.Position = 7 * SectorSize;
            stream.Write(Filled(0x77), 0, SectorSize);
            stream.Position = position;
            stream.Writes.Clear();

            device.WriteBlock(BlockAddress.FromLBA(2), Filled(0x22));
            device.Flush();

            byte[] after = stream.ToArray();
            Assert.That(after[7 * SectorSize], Is.EqualTo(0x77));
            Assert.That(after[2 * SectorSize], Is.EqualTo(0x22));
            device.Dispose();
        }

        [Test]
        public void ShortImage_ReadsZeroBehindTheFile_AndGrowsOnlyToTheLastBlockWritten()
        {
            // A volume of 100 blocks in a file of 4.
            MemoryStream file = new MemoryStream();
            byte[] start = Pattern(4 * SectorSize, 9);
            file.Write(start, 0, start.Length);
            GenericBlockDevice device = new GenericBlockDevice(Lba(100));
            device.Mount(new PaddedSubStream(file, 0, file.Length, 100L * SectorSize), false);

            byte[] read = new byte[SectorSize];
            device.ReadBlock(BlockAddress.FromLBA(50), read);
            Assert.That(read, Is.EqualTo(new byte[SectorSize]), "behind the file, below the volume size: zero bytes");
            device.ReadBlock(BlockAddress.FromLBA(3), read);
            Assert.That(read, Is.EqualTo(start.AsSpan(3 * SectorSize, SectorSize).ToArray()));

            device.WriteBlock(BlockAddress.FromLBA(10), Filled(0x5A));
            device.Flush();
            Assert.That(file.Length, Is.EqualTo(11 * SectorSize), "the file grows to the end of block 10 and no further");

            byte[] grown = file.ToArray();
            Assert.That(grown.AsSpan(0, 4 * SectorSize).ToArray(), Is.EqualTo(start), "the blocks the file held are unchanged");
            Assert.That(grown.AsSpan(4 * SectorSize, 6 * SectorSize).ToArray(), Is.EqualTo(new byte[6 * SectorSize]), "the gap is zero bytes");
            Assert.That(grown.AsSpan(10 * SectorSize, SectorSize).ToArray(), Is.EqualTo(Filled(0x5A)));

            // A write inside the file does not grow it.
            device.WriteBlock(BlockAddress.FromLBA(1), Filled(0x11));
            device.Flush();
            Assert.That(file.Length, Is.EqualTo(11 * SectorSize));
            device.Dispose();
        }

        [Test]
        public void ShortImage_ABlockAtOrBeyondTheVolumeSize_IsRefused()
        {
            MemoryStream file = new MemoryStream();
            file.Write(Pattern(4 * SectorSize, 2), 0, 4 * SectorSize);
            GenericBlockDevice device = new GenericBlockDevice(Lba(100));
            device.Mount(new PaddedSubStream(file, 0, file.Length, 100L * SectorSize), false);

            byte[] buffer = new byte[SectorSize];
            Assert.Throws<ArgumentException>(() => device.ReadBlock(BlockAddress.FromLBA(100), buffer));
            Assert.Throws<ArgumentException>(() => device.WriteBlock(BlockAddress.FromLBA(100), buffer));
            Assert.Throws<ArgumentException>(() => device.ReadBlock(BlockAddress.FromLBA(5000), buffer));
            device.ReadBlock(BlockAddress.FromLBA(99), buffer);
            Assert.That(file.Length, Is.EqualTo(4 * SectorSize), "a refused write does not touch the file");
            device.Dispose();
        }

        [Test]
        public void ShortImage_OfADriveWireSizedVolume_HoldsOnlyTheFileAndTheBlocksWritten()
        {
            // The DriveWire volume: 524268 sectors (134 MB). The file holds 1003 sectors (the size of ArcadePack_dw.dsk).
            const uint volume = 524268;
            const int fileSectors = 1003;
            MemoryStream file = new MemoryStream();
            file.Write(Pattern(fileSectors * SectorSize, 3), 0, fileSectors * SectorSize);
            GenericBlockDevice device = new GenericBlockDevice(Lba(volume));
            device.Mount(new PaddedSubStream(file, 0, file.Length, (long)volume * SectorSize), false);

            long fileBytes = fileSectors * SectorSize;
            Assert.That(device.HeldBytes, Is.LessThanOrEqualTo(fileBytes + 4096), "only the file is held after mount");

            byte[] buffer = new byte[SectorSize];
            for (uint lba = fileSectors; lba < volume; lba += 997)
            {
                device.ReadBlock(BlockAddress.FromLBA(lba), buffer);
            }

            Assert.That(device.HeldBytes, Is.LessThanOrEqualTo(fileBytes + 4096), "reading zero blocks holds nothing more");

            // A block far behind the file: one page more is held, and the file grows to the end of that block only.
            device.WriteBlock(BlockAddress.FromLBA(5000), Filled(0xEE));
            Assert.That(device.HeldBytes, Is.LessThanOrEqualTo(fileBytes + (2 * 4096)), "one written block holds one page more");
            device.ReadBlock(BlockAddress.FromLBA(5000), buffer);
            Assert.That(buffer, Is.EqualTo(Filled(0xEE)));
            device.Flush();
            Assert.That(file.Length, Is.EqualTo(5001L * SectorSize));
            device.Dispose();
        }

        [Test]
        public void ImageWithAnErrorTable_KeepsItsErrorBytes_WhenABlockIsWritten()
        {
            // A D64 with its 683 error bytes behind the blocks: they are not blocks, and a write of block 0 must leave them as they were.
            BlockDeviceDescriptor d64 = BlockDeviceDescriptor.D64_35Track;
            long blocks = d64.TotalBlocks;
            byte[] image = Pattern((int)(blocks * SectorSize) + (int)blocks, 4);
            RecordingStream stream = new RecordingStream(image);
            GenericBlockDevice device = new GenericBlockDevice(d64);
            device.Mount(stream, false);
            device.WriteBlock(BlockAddress.FromTrackSector(1, 0), Filled(0x01));
            device.Flush();

            Assert.That(stream.Writes.Count, Is.EqualTo(1));
            Assert.That(stream.Writes[0], Is.EqualTo((0L, SectorSize)));
            byte[] after = stream.ToArray();
            Assert.That(after.Length, Is.EqualTo(image.Length));
            byte[] original = Pattern((int)(blocks * SectorSize) + (int)blocks, 4);
            Assert.That(after.AsSpan((int)(blocks * SectorSize)).ToArray(), Is.EqualTo(original.AsSpan((int)(blocks * SectorSize)).ToArray()));
            device.Dispose();
        }

        // A growable memory stream that records the position and length of every write.
        private sealed class RecordingStream : MemoryStream
        {
            public RecordingStream(byte[] initial)
            {
                base.Write(initial, 0, initial.Length);
                Position = 0;
            }

            public List<(long Offset, int Count)> Writes { get; } = new List<(long Offset, int Count)>();

            public override void Write(byte[] buffer, int offset, int count)
            {
                Writes.Add((Position, count));
                base.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                Writes.Add((Position, buffer.Length));
                base.Write(buffer);
            }
        }
    }
}
