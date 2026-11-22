//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using Xunit;

namespace RetroIO.Protocols.Tn3270.Tests
{
    // RFC 2355 sections 8.1 and 10.4.1.
    public class Tn3270EHeaderTests
    {
        [Fact]
        public void TheHeaderIsFiveBytesWithABigEndianSequenceNumber()
        {
            byte[] record = { 0x00, 0x00, 0x02, 0x12, 0x34, Command3270.EraseWrite, 0x02 };

            Assert.True(Tn3270EHeader.TryRead(record, out Tn3270EHeader h));

            Assert.Equal(Tn3270EDataType.Data3270, h.DataType);
            Assert.Equal(Tn3270EResponseFlag.AlwaysResponse, h.ResponseFlag);
            Assert.Equal(0x1234, h.SequenceNumber);

            byte[] back = new byte[5];
            h.Write(back);
            Assert.Equal(record.AsSpan(0, 5).ToArray(), back);
        }

        [Fact]
        public void AShortRecordHasNoHeader()
        {
            Assert.False(Tn3270EHeader.TryRead(new byte[] { 0x00, 0x00, 0x00, 0x00 }, out _));
        }

        [Fact]
        public void ANegativeResponseCarriesTheSequenceNumberAndOneStatusByte()
        {
            byte[] r = new byte[6];

            int n = Tn3270EHeader.WriteResponse(0x0102, positive: false, Tn3270EResponseStatus.OperationCheck, r);

            Assert.Equal(6, n);
            Assert.Equal(new byte[] { 0x02, 0x00, 0x01, 0x01, 0x02, 0x02 }, r);
        }

        [Fact]
        public void ProcessOutboundSaysWhetherTheRecordFailed()
        {
            var d = new Display3270();
            d.ProtocolError += _ => { };

            Assert.True(d.ProcessOutbound(new byte[] { Command3270.EraseWrite, 0x02, 0xC1 }));
            Assert.False(d.ProcessOutbound(new byte[] { 0x99 }));
            Assert.True(d.ProcessOutbound(new byte[] { Command3270.Write, 0x02 }));
        }
    }
}
