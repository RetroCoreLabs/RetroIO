//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.IO;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Hosting.Tests
{
    /// <summary>
    /// <see cref="FaConnectionNumbers.Reserve"/>: a block per process, persisted, wrapping inside
    /// the range a real machine was seen to accept.
    /// </summary>
    public sealed class FaConnectionNumbersTests
    {
        [Fact]
        public void AFirstRunStartsAt0x42_AndRecordsWhereTheNextStarts()
        {
            using TempFolder folder = new TempFolder();
            string path = XmsgStateFiles.PathOf(folder.Path, XmsgStateFiles.FaConnection);

            ushort start = FaConnectionNumbers.Reserve(path, null);

            Assert.Equal(0x0042, start);
            Assert.Equal((0x0042 + FaConnectionNumbers.ConnectionNumberBlock).ToString(), File.ReadAllText(path));
        }

        [Fact]
        public void TheNextRunContinuesFromTheFile()
        {
            using TempFolder folder = new TempFolder();
            string path = XmsgStateFiles.PathOf(folder.Path, XmsgStateFiles.FaConnection);

            FaConnectionNumbers.Reserve(path, null);
            ushort second = FaConnectionNumbers.Reserve(path, null);

            Assert.Equal(0x0042 + FaConnectionNumbers.ConnectionNumberBlock, second);
        }

        [Fact]
        public void ABlockThatWouldRunPast0xFF_IsHandedOut_ButTheNextRunWrapsTo0x42()
        {
            using TempFolder folder = new TempFolder();
            string path = XmsgStateFiles.PathOf(folder.Path, XmsgStateFiles.FaConnection);
            File.WriteAllText(path, "194"); // 0xC2: 0xC2 + 64 = 0x102, past the last number

            ushort start = FaConnectionNumbers.Reserve(path, null);

            Assert.Equal(0x00C2, start);
            Assert.Equal("66", File.ReadAllText(path)); // 0x42
        }

        [Fact]
        public void AStaleNumberFromTheClimbingDaysStartsOver()
        {
            using TempFolder folder = new TempFolder();
            string path = XmsgStateFiles.PathOf(folder.Path, XmsgStateFiles.FaConnection);
            File.WriteAllText(path, "3586"); // 0x0E02, the number a live D100 ignored

            ushort start = FaConnectionNumbers.Reserve(path, null);

            Assert.Equal(0x0042, start);
        }
    }
}
