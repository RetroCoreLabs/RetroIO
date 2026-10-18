//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Storage.Local.Streams;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Tests that <see cref="SubStream"/> follows the argument rules of <see cref="Stream"/>.
    /// </summary>
    [TestFixture]
    public class SubStreamTests
    {
        private static SubStream Window()
        {
            byte[] data = new byte[100];
            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)i;
            return new SubStream(new MemoryStream(data), 10, 20);
        }

        [Test]
        public void Read_IntoAnEmptyBuffer_ReturnsZero()
        {
            // Regression: offset >= buffer.Length was rejected, so a valid empty buffer at offset 0 threw.
            using SubStream stream = Window();

            Assert.That(stream.Read(Array.Empty<byte>(), 0, 0), Is.EqualTo(0));
            Assert.That(stream.Position, Is.EqualTo(0));
        }

        [Test]
        public void Read_ZeroBytesAtTheEndOfTheBuffer_ReturnsZero()
        {
            using SubStream stream = Window();
            byte[] buffer = new byte[4];

            Assert.That(stream.Read(buffer, 4, 0), Is.EqualTo(0));
        }

        [Test]
        public void Read_StillRejectsAnOffsetPastTheBuffer_AndACountThatDoesNotFit()
        {
            using SubStream stream = Window();
            byte[] buffer = new byte[4];

            Assert.That(() => stream.Read(buffer, 5, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => stream.Read(buffer, 2, 3), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => stream.Read(buffer, -1, 1), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void Read_ReturnsTheWindowNotTheWholeStream()
        {
            using SubStream stream = Window();
            byte[] buffer = new byte[3];

            Assert.That(stream.Read(buffer, 0, 3), Is.EqualTo(3));
            Assert.That(buffer, Is.EqualTo(new byte[] { 10, 11, 12 }));
        }

        [Test]
        public void Write_FromAnEmptyBuffer_WritesNothing()
        {
            using SubStream stream = Window();

            Assert.DoesNotThrow(() => stream.Write(Array.Empty<byte>(), 0, 0));
            Assert.That(stream.Position, Is.EqualTo(0));
        }

        [Test]
        public void Write_StillRejectsAnOffsetPastTheBuffer()
        {
            using SubStream stream = Window();

            Assert.Throws<ArgumentOutOfRangeException>(() => stream.Write(new byte[4], 5, 0));
        }
    }
}
