using System;

using Xunit;
using Xunit.Abstractions;
using static RetroIO.Protocols.Xmsg.TestSupport.TestHex;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Rejoins a fragment pair taken off the wire, byte for byte.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists alongside SintranFragmentReassemblerTests</b></para>
    /// Those tests build their frames in memory with a helper, so they check the reassembler against
    /// OUR idea of what a fragment looks like. They passed while a live write from D100 failed: both
    /// halves of the client's data message arrived, nothing was stored, and D100 retransmitted the
    /// pair 39 seconds later.
    /// <para>
    /// These frames are instead parsed from the hex a real SINTRAN client put on the wire, so the
    /// only thing they can agree with is the machine.
    /// </para>
    /// <para><b>Source</b></para>
    /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-write.txt</c> lines 60 and 61 - one of the
    /// 18 pairs in that capture, a real client writing a file to a real SINTRAN file server.
    /// </remarks>
    public sealed class SintranFragmentCaptureTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public SintranFragmentCaptureTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// The first fragment: 622 bytes, 28 of header and sub-header plus a 594-byte body.
        /// </summary>
        /// <remarks>
        /// <c>Flags2</c> is <c>0x0408</c> = 1032, the TOTAL length of the message being sent. 622 is
        /// also the largest frame the scheme can produce, which is what
        /// <c>LapbLayer.MaxInformationLength</c> is derived from.
        /// </remarks>
        private const string FirstFragmentHex =
            "2113000A0066006402620408D7AE21008284006606B600640853040807F000440400D7618D0AC0284E442D5041D4C348"
            + "2D53C94E2D3A535953D4A94EC5D72D535953D4A0D7D2C9D4C52D4DC553534147C5A041534B8D8D0A0D0A4D756C746946"
            + "756E6374696F6E2050726F6772616D204D41592032372C20313938380D0A24C3E86563EB69EEE7A0535953D4C54DA0E7"
            + "65EE6572E174696FEEA069EE666F72EDE174696FEEA02DA077E169742E2E2E8D0D0A436865636B696E67205359535445"
            + "4D2067656E65726174696F6E20696E666F726D6174696F6E202D20776169742E2E2E0D0AC08D8D0AC0284E442D5041D4"
            + "C3482D53C94E2D3A535953D4A94EC5D72D535953D4A0C348C5C34B2DD7CFD24B4DCF44C5A041534B8D8D0A0D0A4D756C"
            + "746946756E6374696F6E2050726F6772616D204D41592032372C20313938380D0A0D0A53796D626F6C732066696C6520"
            + "286465663D284E2D2D2D3A532953594D424F4C533A4641444D293A20284E2D2D2D3A535953D4A953594D42CFCC533AC6"
            + "41444D8D0D0A53796D626F6C2D312D6C6973742066696C6520286465663D53594D424F4C2D312D4C4953543A53594D42"
            + "293A2053594D42CFCC2DB12DCCC953D48D0D0A59C5538D8D0AC0284E442D5041D4C3482D53C94E2D3A535953D4A94EC5"
            + "D72D535953D4A0D7D2C9D4C52D4DC553534147C5A041534B8D8D0A0D0A4D756C746946756E6374696F6E2050726F6772"
            + "616D204D41592032372C20313938380D0A2444F57269EEE7A0F0E17463E869EEE7A06F66A05369EE7472E1EEA074E865"
            + "A0F0E17463E82D66696C65A0F3F9F37465EDA077696C6CA0F0726FE4F56365A06FF574F0F5748D0D0A447572696E";

        /// <summary>
        /// The continuation: 452 bytes, 14 of header and then a 438-byte body.
        /// </summary>
        /// <remarks>
        /// <para><b>It carries NO sub-header</b></para>
        /// Its body starts at offset 14, not 28 - see
        /// <c>SintranMessageFragment.ContinuationBodyOffset</c>. 594 + 438 = 1032, the total the
        /// first fragment declared.
        /// <para>
        /// <c>Flags2</c> is <c>0x0252</c> = 594, the offset it resumes at, and <c>Flags1</c> is
        /// <c>0x0262</c> - the same as the first fragment, which is what pairs them.
        /// </para>
        /// </remarks>
        private const string ContinuationHex =
            "2113000C0066006402620252D96267207061746368696E67206F662053696E7472616E207468652070617463682D6669"
            + "6C652073797374656D2077696C6C2070726F64756365206F75747075740D0A746FA074E865A066696C65F33A8D746F20"
            + "7468652066696C65733A0D0A2E2E2E2E2E50E17463E82D66696C653A6FF5748D2E2E2E2E2E50617463682D66696C653A"
            + "6F75740D0A2E2E2E2E2E50E17463E865F33A6FF5748D2E2E2E2E2E506174636865733A6F75740D0AC9EEA063E1F365A0"
            + "6F66A0E1E2EE6F72EDE16CA0746572ED69EEE174696FEEA06F66A074E865A0F0E17463E869EEE7A028692E65A06966A0"
            + "F96FF5A0726574F572EE8D496E2063617365206F662061626E6F726D616C207465726D696E6174696F6E206F66207468"
            + "65207061746368696E672028692E6520696620796F752072657475726E0D0A746FA05369EE7472E1EEA0636FEDEDE1EE"
            + "E4A0ED6FE465A0776974E86FF574A0E1A0ED65F3F3E1E765A074656C6C69EEE7A0F96FF5A077E86574E86572A074E865"
            + "8D746F2053696E7472616E20636F6D6D616E64206D6F646520776974686F75742061206D6573736167652074656C6C69"
            + "6E6720796F752077686574686572207468650D0A";

        /// <summary>
        /// The captured frames are the sizes the capture recorded.
        /// </summary>
        /// <remarks>
        /// Guards the hex constants themselves, so a dropped byte fails here and names the real
        /// problem rather than failing the rejoin for the wrong reason.
        /// </remarks>
        [Fact]
        public void TheCapturedFramesAreTheSizesTheCaptureRecorded()
        {
            Assert.Equal(622, FromHex(FirstFragmentHex).Length);
            Assert.Equal(452, FromHex(ContinuationHex).Length);
        }

        /// <summary>
        /// A pair taken off the wire parses into the subtypes and flags the scheme depends on.
        /// </summary>
        [Fact]
        public void TheCapturedPairParsesAsAFragmentPair()
        {
            XmsgFrame first = XmsgFrame.Parse(FromHex(FirstFragmentHex));
            XmsgFrame continuation = XmsgFrame.Parse(FromHex(ContinuationHex));

            Assert.Equal(SintranPacketSubtype.MessageFirstFragment, first.Header!.Subtype);
            Assert.Equal(SintranPacketSubtype.MessageContinuation, continuation.Header!.Subtype);

            // Same Flags1 is what pairs them.
            Assert.Equal(first.Header.Flags1, continuation.Header.Flags1);

            // The first declares the TOTAL, the continuation declares where it resumes.
            Assert.Equal(1032, first.Header.Flags2);
            Assert.Equal(594, continuation.Header.Flags2);
        }

        /// <summary>
        /// The bodies are the lengths the resume offset implies.
        /// </summary>
        /// <remarks>
        /// This is the assertion that separates "our idea of a fragment" from the wire. The first
        /// fragment's body sits after a header AND a sub-header; the continuation's sits after a
        /// header alone. Get that boundary wrong and the two lengths stop adding up to 1032, which
        /// is exactly the check <c>SintranFragmentReassembler</c> refuses a join on.
        /// </remarks>
        [Fact]
        public void TheCapturedBodiesAddUpToTheDeclaredTotal()
        {
            XmsgFrame first = XmsgFrame.Parse(FromHex(FirstFragmentHex));
            XmsgFrame continuation = XmsgFrame.Parse(FromHex(ContinuationHex));

            byte[] head = first.GetBodyBytes();
            byte[] tail = continuation.GetBodyBytes();

            _output.WriteLine("first body " + head.Length + ", continuation body " + tail.Length);

            Assert.Equal(SintranMessageFragment.FirstFragmentBodyLength, head.Length);
            Assert.Equal(continuation.Header!.Flags2, head.Length);
            Assert.Equal(first.Header!.Flags2, head.Length + tail.Length);
        }

        /// <summary>
        /// The reassembler rejoins a real pair into one data frame.
        /// </summary>
        /// <remarks>
        /// The joined message must be 1032 bytes and open with the FA data-message prefix
        /// <c>07F0</c>, because that is what the layer above recognises a write block by.
        /// </remarks>
        [Fact]
        public void TheCapturedPairRejoinsIntoOneDataMessage()
        {
            SintranFragmentReassembler reassembler = new SintranFragmentReassembler();
            reassembler.Log += line => _output.WriteLine(line);

            XmsgFrame first = XmsgFrame.Parse(FromHex(FirstFragmentHex));
            XmsgFrame continuation = XmsgFrame.Parse(FromHex(ContinuationHex));

            Assert.Null(reassembler.Accept(first));

            XmsgFrame? joined = reassembler.Accept(continuation);

            Assert.NotNull(joined);
            Assert.Equal(SintranPacketSubtype.Data, joined!.Header!.Subtype);

            byte[] body = joined.GetBodyBytes();
            Assert.Equal(1032, body.Length);

            // The FA data-message prefix, and then raw file content.
            Assert.Equal(0x07, body[0]);
            Assert.Equal(0xF0, body[1]);

            Assert.Equal(0, reassembler.PendingCount);
        }
    }
}
