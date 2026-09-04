using System;

using RetroIO.Protocols.Xmsg.Node.Seam;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Gate for the relay's hop count: it COUNTS, and a datagram out of hops is dropped.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect these tests pin</b></para>
    /// <para>
    /// <c>MakeRelayed</c> used to ASSIGN <c>0x12</c> to byte 1. For the only case ever exercised -
    /// one hop, <c>0x13</c> to <c>0x12</c> - assigning and decrementing are the same, so nothing
    /// caught it. Beyond one hop they differ completely: a twice-forwarded datagram stayed at
    /// <c>0x12</c>, so the count could never fall and a datagram going round a loop would circulate
    /// for ever. <c>IsRelayed</c> compared <c>== 0x12</c> and so reported a twice-forwarded
    /// datagram as fresh.
    /// </para>
    /// <para>
    /// Byte 1 is a hop count - the kernel calls word 0 <c>XDROU</c>, "NETWORK INFO (VERSION,
    /// PROTOCOL, HOP COUNT)" - so counting is the entire purpose of the field.
    /// </para>
    /// </remarks>
    public sealed class RelayHopCountTests
    {
        /// <summary>
        /// Builds a well-formed datagram with a correct header checksum.
        /// </summary>
        /// <returns>
        /// The datagram bytes.
        /// </returns>
        private static byte[] FreshDatagram()
        {
            byte[] d = new byte[SintranDatagramRelay.HeaderSize + 2];
            d[SintranDatagramRelay.VersionProtocolOffset] = SintranDatagramRelay.VersionProtocolValue;
            d[SintranDatagramRelay.HopCountOffset] = SintranDatagramRelay.InitialHopCount;
            d[2] = 0x00; d[3] = 0x0E;   // packet type / subtype
            d[4] = 0x00; d[5] = 0x64;   // to 100
            d[6] = 0x4E; d[7] = 0x1F;   // from 19999
            d[8] = 0x00; d[9] = 0x10;   // flags1
            d[10] = 0x00; d[11] = 0x22; // flags2
            SintranDatagramRelay.WriteChecksum(d);
            return d;
        }

        /// <summary>
        /// The first hop takes the count from its initial value to one below.
        /// </summary>
        [Fact]
        public void TheFirstHopDecrementsTheCount()
        {
            byte[] d = FreshDatagram();

            Assert.True(SintranDatagramRelay.MakeRelayed(d));

            Assert.Equal(SintranDatagramRelay.InitialHopCount - 1, d[SintranDatagramRelay.HopCountOffset]);
            Assert.True(SintranDatagramRelay.IsRelayed(d));
            Assert.Equal(1, SintranDatagramRelay.HopsTaken(d));
        }

        /// <summary>
        /// The SECOND hop decrements again. Assigning a constant left it stuck at the first value.
        /// </summary>
        [Fact]
        public void TheSecondHopDecrementsAgain()
        {
            byte[] d = FreshDatagram();

            Assert.True(SintranDatagramRelay.MakeRelayed(d));
            Assert.True(SintranDatagramRelay.MakeRelayed(d));

            Assert.Equal(SintranDatagramRelay.InitialHopCount - 2, d[SintranDatagramRelay.HopCountOffset]);
            Assert.Equal(2, SintranDatagramRelay.HopsTaken(d));
        }

        /// <summary>
        /// A twice-forwarded datagram still reads as relayed - the old equality test said no.
        /// </summary>
        [Fact]
        public void ATwiceForwardedDatagramStillReadsAsRelayed()
        {
            byte[] d = FreshDatagram();
            SintranDatagramRelay.MakeRelayed(d);
            SintranDatagramRelay.MakeRelayed(d);

            Assert.True(SintranDatagramRelay.IsRelayed(d));
        }

        /// <summary>
        /// A datagram with no hops left is refused and left BYTE FOR BYTE untouched.
        /// </summary>
        /// <remarks>
        /// Without the guard, <c>byte</c> arithmetic would turn 0 into 255 and hand a looping
        /// datagram 255 more hops - the opposite of what the field is for.
        /// </remarks>
        [Fact]
        public void ADatagramOutOfHopsIsRefusedAndUntouched()
        {
            byte[] d = FreshDatagram();
            d[SintranDatagramRelay.HopCountOffset] = 0;
            SintranDatagramRelay.WriteChecksum(d);
            byte[] before = (byte[])d.Clone();

            Assert.False(SintranDatagramRelay.MakeRelayed(d));

            Assert.Equal(before, d);
        }

        /// <summary>
        /// Every hop leaves the header checksum correct, so the peer still accepts the datagram.
        /// </summary>
        [Fact]
        public void TheChecksumStaysCorrectAcrossSeveralHops()
        {
            byte[] d = FreshDatagram();

            for (int hop = 0; hop < 5; hop++)
            {
                Assert.True(SintranDatagramRelay.MakeRelayed(d));

                // Recomputing from scratch must not change a byte: the relay already wrote the
                // checksum its own decremented header calls for.
                byte[] recomputed = (byte[])d.Clone();
                Assert.True(SintranDatagramRelay.WriteChecksum(recomputed));
                Assert.Equal(recomputed, d);
            }
        }

        /// <summary>
        /// Everything except word 0 and word 6 is passed through byte for byte.
        /// </summary>
        [Fact]
        public void OnlyWordZeroAndWordSixChange()
        {
            byte[] d = FreshDatagram();
            byte[] before = (byte[])d.Clone();

            SintranDatagramRelay.MakeRelayed(d);

            for (int i = 0; i < d.Length; i++)
            {
                bool mayChange = i == SintranDatagramRelay.HopCountOffset
                    || i == SintranDatagramRelay.ChecksumHighOffset
                    || i == SintranDatagramRelay.ChecksumLowOffset;
                if (!mayChange)
                {
                    Assert.Equal(before[i], d[i]);
                }
            }
        }
    }
}
