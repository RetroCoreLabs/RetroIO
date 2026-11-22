//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using Xunit;

namespace RetroIO.Protocols.Slip.Tests
{
    public class SlipLinkTests
    {
        private readonly SlipLink _link = new SlipLink();
        private readonly List<byte[]> _packets = new List<byte[]>();
        private readonly List<SlipError> _errors = new List<SlipError>();
        private readonly List<byte> _line = new List<byte>();

        public SlipLinkTests()
        {
            _link.OnPacket += packet => _packets.Add(packet.ToArray());
            _link.OnError += (error, count) => _errors.Add(error);
            _link.OnLineOutput += bytes =>
            {
                for (int i = 0; i < bytes.Length; i++)
                {
                    _line.Add(bytes[i]);
                }
            };
        }

        [Fact]
        public void SendPacket_EscapesEndAndEsc_AndFramesWithEnd()
        {
            _link.SendPacket(new byte[] { 0x45, 0xC0, 0x01, 0xDB, 0x02 });

            Assert.Equal(new byte[] { 0xC0, 0x45, 0xDB, 0xDC, 0x01, 0xDB, 0xDD, 0x02, 0xC0 }, _line.ToArray());
        }

        [Fact]
        public void ReceiveFromLine_UndoesEscapes()
        {
            _link.ReceiveFromLine(new byte[] { 0xC0, 0x45, 0xDB, 0xDC, 0x01, 0xDB, 0xDD, 0x02, 0xC0 });

            Assert.Single(_packets);
            Assert.Equal(new byte[] { 0x45, 0xC0, 0x01, 0xDB, 0x02 }, _packets[0]);
            Assert.Empty(_errors);
        }

        [Fact]
        public void ReceiveFromLine_TakesAnySplit_EvenBetweenEscAndItsByte()
        {
            byte[] line = { 0x45, 0xDB, 0xDC, 0x07, 0xC0, 0x46, 0xC0 };
            for (int i = 0; i < line.Length; i++)
            {
                _link.ReceiveFromLine(line.AsSpan(i, 1));
            }

            Assert.Equal(2, _packets.Count);
            Assert.Equal(new byte[] { 0x45, 0xC0, 0x07 }, _packets[0]);
            Assert.Equal(new byte[] { 0x46 }, _packets[1]);
        }

        [Fact]
        public void ReceiveFromLine_IgnoresEmptyPackets()
        {
            _link.ReceiveFromLine(new byte[] { 0xC0, 0xC0, 0xC0, 0x45, 0xC0 });

            Assert.Single(_packets);
            Assert.Empty(_errors);
        }

        [Fact]
        public void BadEscape_DropsThePacket_ReportsIt_AndTheNextPacketIsGood()
        {
            _link.ReceiveFromLine(new byte[] { 0x45, 0xDB, 0x41, 0x46, 0xC0, 0x47, 0xC0 });

            Assert.Equal(new[] { SlipError.BadEscape }, _errors.ToArray());
            Assert.Equal(1, _link.ErrorCount);
            Assert.Single(_packets);
            Assert.Equal(new byte[] { 0x47 }, _packets[0]);
        }

        [Fact]
        public void EscThenEnd_IsABadEscape()
        {
            _link.ReceiveFromLine(new byte[] { 0x45, 0xDB, 0xC0 });

            Assert.Equal(new[] { SlipError.BadEscape }, _errors.ToArray());
            Assert.Empty(_packets);
        }

        [Fact]
        public void TooLongPacket_IsDroppedAndReported()
        {
            SlipLink small = new SlipLink(4);
            int good = 0;
            SlipError seen = SlipError.None;
            small.OnPacket += packet => good++;
            small.OnError += (error, count) => seen = error;

            small.ReceiveFromLine(new byte[] { 1, 2, 3, 4, 5, 0xC0, 1, 2, 3, 4, 0xC0 });

            Assert.Equal(SlipError.TooLong, seen);
            Assert.Equal(1, small.ErrorCount);
            Assert.Equal(1, good);
        }

        [Fact]
        public void SendThenReceive_GivesTheSamePacket_ForEveryByteValue()
        {
            byte[] packet = new byte[256];
            for (int i = 0; i < packet.Length; i++)
            {
                packet[i] = (byte)i;
            }

            SlipLink wide = new SlipLink(256);
            byte[]? got = null;
            wide.OnPacket += p => got = p.ToArray();
            wide.OnLineOutput += bytes => wide.ReceiveFromLine(bytes);

            wide.SendPacket(packet);

            Assert.Equal(packet, got);
        }

        [Fact]
        public void SendPacket_RefusesEmptyAndTooLong()
        {
            Assert.Throws<ArgumentException>(() => _link.SendPacket(ReadOnlySpan<byte>.Empty));
            Assert.Throws<ArgumentException>(() => _link.SendPacket(new byte[SlipLink.DefaultMaxPacket + 1]));
        }
    }
}
