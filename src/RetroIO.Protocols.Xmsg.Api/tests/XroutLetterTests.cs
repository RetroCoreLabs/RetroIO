//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;

using RetroIO.Protocols.Xmsg.Api;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Api.Tests
{
    /// <summary>
    /// A letter is the XSLET header followed by the sender's data, exactly as a SINTRAN server
    /// receives it from XROUT.
    /// </summary>
    public sealed class XroutLetterTests
    {
        // The join measured on D100 on 2026-08-18 (NDInsight SINTRAN/XMSG/SINTRAN-CHAT/CHATSV.PLNC,
        // receive loop): 123 65 0 12 | 255 10 'CHAT-LOBBY' | 1 5 'RONNY' 0 0. Data starts at 16.
        private static readonly byte[] MeasuredJoin = BuildMeasuredJoin();

        /// <summary>
        /// Building the letter gives the measured bytes.
        /// </summary>
        [Fact]
        public void Build_GivesTheMeasuredJoin()
        {
            byte[] data = { 1, 5, (byte)'R', (byte)'O', (byte)'N', (byte)'N', (byte)'Y', 0, 0 };

            byte[] letter = XroutLetter.Build(123, "CHAT-LOBBY", data);

            Assert.Equal(MeasuredJoin, letter);
        }

        /// <summary>
        /// The data starts at 4 + the length word, which is 16 in the measured join.
        /// </summary>
        [Fact]
        public void UserDataOffset_OfTheMeasuredJoin_Is16()
        {
            Assert.Equal(16, XroutLetter.UserDataOffset(MeasuredJoin));
        }

        /// <summary>
        /// The name is string parameter 1.
        /// </summary>
        [Fact]
        public void TryReadName_OfTheMeasuredJoin_IsTheRoomName()
        {
            string name;
            Assert.True(XroutLetter.TryReadName(MeasuredJoin, out name));
            Assert.Equal("CHAT-LOBBY", name);
        }

        /// <summary>
        /// A letter shorter than the header has no data offset.
        /// </summary>
        [Fact]
        public void UserDataOffset_OfAShortLetter_IsMinusOne()
        {
            Assert.Equal(-1, XroutLetter.UserDataOffset(new byte[] { 123, 65, 0 }));
        }

        /// <summary>
        /// The in-process name table hands the server the whole letter, header first.
        /// </summary>
        [Fact]
        public void Directory_DeliversTheHeaderInFrontOfTheData()
        {
            XmsgKernel kernel = new XmsgKernel(100, 0x2B67, null);
            XmsgPortNumber port;
            Assert.False(kernel.OpenPort(out port).IsError);
            XmsgMagicNumber magic;
            Assert.False(kernel.ConvertPortToMagic(port, out magic).IsError);

            XroutDirectory directory = new XroutDirectory();
            Assert.Equal(XroutError.XRSOK, directory.RegisterName("*CHAT", kernel, magic));

            byte[] data = { 1, 5, (byte)'R', (byte)'O', (byte)'N', (byte)'N', (byte)'Y', 0, 0 };
            Assert.Equal(XroutError.XRSOK, directory.SendLetter("*CHAT", magic, data));

            XmsgReceiveResult arrived = kernel.Receive(port, XmsgWaitOptions.None);
            Assert.True(arrived.Received);
            byte[] buffer = new byte[64];
            int read;
            kernel.Read(arrived.Message, buffer, 0, out read);

            byte[] got = new byte[read];
            Array.Copy(buffer, got, read);
            int start = XroutLetter.UserDataOffset(got);
            string name;
            Assert.True(XroutLetter.TryReadName(got, out name));
            Assert.Equal("*CHAT", name);
            Assert.Equal(data, got.AsSpan(start).ToArray());
        }

        private static byte[] BuildMeasuredJoin()
        {
            byte[] bytes = new byte[25];
            bytes[0] = 123;
            bytes[1] = 65;
            bytes[2] = 0;
            bytes[3] = 12;
            bytes[4] = 255;
            bytes[5] = 10;
            Encoding.ASCII.GetBytes("CHAT-LOBBY").CopyTo(bytes, 6);
            bytes[16] = 1;
            bytes[17] = 5;
            Encoding.ASCII.GetBytes("RONNY").CopyTo(bytes, 18);
            bytes[23] = 0;
            bytes[24] = 0;
            return bytes;
        }
    }
}
