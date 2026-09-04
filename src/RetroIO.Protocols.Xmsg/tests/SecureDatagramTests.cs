using RetroIO.Protocols.Xmsg;
using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Tests for the XFSEC secure-datagram delivery state machine (send + receive sides).
    /// </summary>
    public sealed class SecureDatagramTests
    {
        [Fact]
        public void Send_Timeout_ResendThreeTimes_ThenReturned()
        {
            // timeout = 10 ticks (XTUs), default 3 resends.
            SecureDatagramSession session = new SecureDatagramSession(timeoutTicks: 10);

            int resendCalls = 0;
            int returnedCalls = 0;
            XmsgError returnedReason = XmsgError.XENOT;
            session.OnResend += delegate (ushort seq, int attempt) { resendCalls++; };
            session.OnReturned += delegate (ushort seq, XmsgError reason)
            {
                returnedCalls++;
                returnedReason = reason;
            };

            session.Send(0x0007, currentTicks: 0);
            Assert.Equal(SecureDatagramState.AwaitingAck, session.State);

            // Before the timeout elapses nothing happens.
            Assert.False(session.Tick(5));
            Assert.Equal(SecureDatagramState.AwaitingAck, session.State);

            // Resend #1, #2, #3 at successive timeout boundaries.
            Assert.True(session.Tick(10));
            Assert.Equal(1, session.ResendCount);
            Assert.True(session.Tick(20));
            Assert.Equal(2, session.ResendCount);
            Assert.True(session.Tick(30));
            Assert.Equal(3, session.ResendCount);
            Assert.Equal(SecureDatagramState.AwaitingAck, session.State);

            // Fourth timeout with no resends left -> Returned.
            Assert.True(session.Tick(40));
            Assert.Equal(SecureDatagramState.Returned, session.State);
            Assert.True(session.IsTerminal);

            Assert.Equal(3, resendCalls);
            Assert.Equal(1, returnedCalls);
            Assert.Equal(XmsgError.XENTO, returnedReason);
            Assert.Equal(XmsgError.XENTO, session.ReturnReason);
            Assert.Equal(XmsgMessageType.XMTRE, session.ReturnMessageType);
        }

        [Fact]
        public void Send_AckMatchingSequence_Delivered()
        {
            SecureDatagramSession session = new SecureDatagramSession(timeoutTicks: 10);

            bool deliveredRaised = false;
            session.OnDelivered += delegate (ushort seq) { deliveredRaised = true; };

            session.Send(0x0041, currentTicks: 0);

            // A non-matching ACK leaves it outstanding.
            Assert.False(session.AckReceived(0x0040));
            Assert.Equal(SecureDatagramState.AwaitingAck, session.State);

            // The matching ACK delivers.
            Assert.True(session.AckReceived(0x0041));
            Assert.Equal(SecureDatagramState.Delivered, session.State);
            Assert.True(deliveredRaised);

            // Further timeouts do nothing once terminal.
            Assert.False(session.Tick(1000));
        }

        [Fact]
        public void Nack_RequestsRetransmit_ThenReturnsOnExhaustion()
        {
            SecureDatagramSession session = new SecureDatagramSession(timeoutTicks: 10, maxResends: 2);

            session.Send(0x0009, currentTicks: 0);

            Assert.True(session.Nack(1));   // resend #1
            Assert.Equal(1, session.ResendCount);
            Assert.True(session.Nack(2));   // resend #2
            Assert.Equal(2, session.ResendCount);

            // Budget exhausted: XEREJ return.
            Assert.False(session.Nack(3));
            Assert.Equal(SecureDatagramState.Returned, session.State);
            Assert.Equal(XmsgError.XEREJ, session.ReturnReason);
        }

        [Fact]
        public void Receiver_DeliversAndEmitsAck_EchoingFlags1()
        {
            // A data frame arrives 100 -> 102 with datagram sequence 7.
            XmsgFrame data = new XmsgFrameBuilder()
                .Between(102, 100)
                .WithDatagramSequence(7)
                .WithProtocol(SintranProtocolId.Routing)
                .WithEndpoints(102, 0x02C1, 100, 0x02AD)
                .WithControlService(0x0100014B)
                .WithBody(new XroutMessageBuilder().WithSerial(1).WithService(XroutService.XSNUL).Build())
                .Build();

            SecureDatagramReceiver receiver = new SecureDatagramReceiver(initialCounter: 0x2D);

            ushort deliveredSeq = 0;
            bool received = false;
            receiver.OnReceived += delegate (ushort seq) { received = true; deliveredSeq = seq; };

            // The channel is passed EXPLICITLY. It used to be read off the incoming frame's byte 12,
            // which worked only because the builder fabricated word 6 from WithProtocol(). Once
            // Build() started deriving the real checksum (2026-08-07) that byte stopped being a
            // channel - it never was one on the wire - so the test now states what it means.
            XmsgFrame ack = receiver.ReceiveDataFrame(data, SintranProtocolId.Routing);

            Assert.True(received);
            Assert.Equal(7, deliveredSeq);

            // ACK properties per XMSG-PROTOCOL.md section 6.
            Assert.Equal(SintranPacketSubtype.Ack, ack.Header.Subtype);
            Assert.Equal(7, ack.Header.Flags1);                 // echoes the datagram sequence
            Assert.Equal(0x0001, ack.Header.Flags2);
            Assert.Equal(100, ack.Header.DestinationNode);      // opposite direction
            Assert.Equal(102, ack.Header.SourceNode);
            Assert.Equal((byte)SintranProtocolId.Routing, ack.Header.ChecksumHigh);

            // Trailing byte is the receiver's per-direction counter, which decrements.
            // CORRECTED 2026-08-04: an ACK is fourteen bytes with no trailing byte. What the old
            // model called the "trailing counter" is the LOW half of header word 6.
            Assert.Empty(ack.TrailingBytes);
            Assert.Equal(0x2D, (byte)(ack.Header.Checksum & 0xFF));
            Assert.Equal(0x2C, receiver.Counter);
        }

        // Builds a minimal 102->100 data frame carrying the given datagram sequence (Flags1). The
        // body/ports are irrelevant to the session ACK, which derives its Counter+channel from Flags1.
        private static XmsgFrame DataWithFlags1(ushort flags1)
        {
            return new XmsgFrameBuilder()
                .Between(102, 100)
                .WithDatagramSequence(flags1)
                .WithProtocol(SintranProtocolId.Dc)
                .WithEndpoints(102, 0x0211, 100, 0x02E3)
                .WithControlService(0x01080000)
                .WithBody(new XroutMessageBuilder().WithSerial(1).WithService(XroutService.XSNUL).Build())
                .Build();
        }

        [Fact]
        public void Receiver_SessionAckModel_MatchesCaptureAcrossDeToDdWrap()
        {
            // CAPTURE-VERIFIED (multiple-connect, 100-102 seed 0x14 -> ACK seed 0x1F): the TAD-session
            // ACK is a STATELESS closed form over the acknowledged Flags1. These are the real 102 ACK
            // Counter+channel rows, including the DE->DD wrap at F1 0x1F and the session-3 crash case
            // F1 0x2A (where our old per-connect re-seed wrongly emitted DE 0xF4 and 100 PERF_CONNCT'd).
            SecureDatagramReceiver receiver = new SecureDatagramReceiver(initialCounter: 0x00);
            receiver.UseSessionAckModel(0x14);

            ushort[] f1 = { 0x0016, 0x001E, 0x001F, 0x0020, 0x002A };
            SintranProtocolId[] channel =
            {
                SintranProtocolId.Routing, SintranProtocolId.Routing, // DE, DE (last DE)
                SintranProtocolId.Tad, SintranProtocolId.Tad, SintranProtocolId.Tad, // DD, DD, DD
            };
            byte[] counter = { 0x08, 0x00, 0xFF, 0xFE, 0xF4 };

            for (int i = 0; i < f1.Length; i++)
            {
                // ackChannel non-null marks this as a session frame; its value is ignored (derived).
                XmsgFrame ack = receiver.ReceiveDataFrame(DataWithFlags1(f1[i]), SintranProtocolId.Routing);

                Assert.Equal(f1[i], ack.Header.Flags1);            // echoes Flags1
                Assert.Equal((byte)channel[i], ack.Header.ChecksumHigh);   // derived channel (DE / DD)
                Assert.Equal(counter[i], (byte)(ack.Header.Checksum & 0xFF));  // checksum low half
                Assert.Equal(0x0001, ack.Header.Flags2);           // always class 0x0001
            }
        }

        [Fact]
        public void Receiver_SessionAckModel_IsStateless_SameFlags1SameAck()
        {
            // The session ACK holds NO per-ACK state: acking the same Flags1 twice yields identical
            // bytes, and there is no per-connect re-seed to reset (that reset was the third-connect
            // crash). F1 0x2A is past the ACK baseLow 0x1E, so it must ride DD 0xF4 every time.
            SecureDatagramReceiver receiver = new SecureDatagramReceiver(initialCounter: 0x00);
            receiver.UseSessionAckModel(0x14);

            XmsgFrame a = receiver.ReceiveDataFrame(DataWithFlags1(0x002A), SintranProtocolId.Routing);
            XmsgFrame b = receiver.ReceiveDataFrame(DataWithFlags1(0x002A), SintranProtocolId.Routing);

            Assert.Equal((byte)SintranProtocolId.Tad, a.Header.ChecksumHigh);   // DD
            Assert.Equal(0xF4, (byte)(a.Header.Checksum & 0xFF));
            Assert.Equal(a.Header.ChecksumHigh, b.Header.ChecksumHigh);
            Assert.Equal(a.Header.Checksum, b.Header.Checksum);
        }

        [Fact]
        public void Receiver_LegacyModel_EchoesChannelAndDecrements_WhenNoSessionModel()
        {
            // The reachability / list-route path is UNTOUCHED: with no session model it carries the
            // decrementing legacy counter and echoes the channel it was GIVEN.
            //
            // The channel is now passed explicitly. It used to be read off the incoming frame's
            // byte 12, which only worked because the builder fabricated word 6 from WithProtocol().
            // Byte 12 is the checksum's high half and never was a channel on the wire, so once
            // Build() started deriving it (2026-08-07) there was nothing there to echo. The legacy
            // ECHO behaviour under test is unchanged - only how the test states its input.
            SecureDatagramReceiver receiver = new SecureDatagramReceiver(initialCounter: 0x2D);

            XmsgFrame ack1 = receiver.ReceiveDataFrame(DataWithFlags1(0x0007), SintranProtocolId.Dc);
            Assert.Equal((byte)SintranProtocolId.Dc, ack1.Header.ChecksumHigh);   // echoes the data channel
            Assert.Equal(0x2D, (byte)(ack1.Header.Checksum & 0xFF));

            XmsgFrame ack2 = receiver.ReceiveDataFrame(DataWithFlags1(0x0008), SintranProtocolId.Dc);
            Assert.Equal(0x2C, (byte)(ack2.Header.Checksum & 0xFF));      // decremented
        }
    }
}
