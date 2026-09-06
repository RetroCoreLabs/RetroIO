using System.Collections.Generic;
using Xunit;

namespace RetroIO.Protocols.Xmsg.Live.Tests
{
    /// <summary>
    /// Pins the seam the one-stack plan (Phase D1) opens in <see cref="LapbLayer"/>: today it speaks
    /// the ND dialect only. It will become the ONE LAPB behind every link in the repository, so it
    /// needs an ITU-T variant for the X.25 links (XOT, serial, TCP LAPB) whose peers are not ND
    /// machines. These tests record what happens today when an ITU peer talks to it, so that the
    /// variant flag is measured against something.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ITU-T X.25 (10/96) clause 2.4.2: address A = 0x03, B = 0x01; commands from the DCE carry A
    /// and responses from the DTE carry A. Clause 2.3.4.1 / 2.3.4.2: SABM and UA have no information
    /// field. The ND dialect (see the remarks on <see cref="LapbLayer.AddressData"/>) uses 0x01 for
    /// link setup, 0x09/0x89 for data, and carries a two-octet node number in SABM and UA.
    /// </para>
    /// <para>
    /// Measured 2026-09-06 on <c>LapbLayer</c> as committed at a9d125a.
    /// </para>
    /// </remarks>
    public class LapbItuVariantSeamTests
    {
        [Fact]
        public void ItuSabmWithAddress03AndNoInfo_IsAcceptedAndAnsweredInNdFormat_NO_ITU_VARIANT_YET()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent);

            // An ITU DCE's SABM: address A, control SABM P=1, no information field.
            LapbTestKit.Deliver(link, 0x03, 0x3F);

            // MEASURED: the address is not checked and the missing node number is not missed; the
            // link goes Connected and answers with an ND-format UA (address 0x01, two-octet node
            // number 00 66 as information) followed by an ND-format RR (address 0x09, node number).
            // An ITU peer receiving a UA WITH an information field must reject it (X.25 2.3.5.3,
            // FRMR reason X "information field not permitted"), so the link would never come up
            // against a real X.25 DCE. With the ITU variant selected the answer must be `03 73`
            // and nothing else; this test is rewritten when D1 lands.
            Assert.Equal(LapbLayerState.Connected, link.State);
            Assert.Equal(2, sent.Count);
            Assert.Equal(new byte[] { 0x01, 0x73, 0x00, 0x66 }, sent[0]);
            Assert.Equal(new byte[] { 0x09, 0x01, 0x00, 0x66 }, sent[1]);
        }

        [Fact]
        public void NdSabmWithNodeNumber_IsAcceptedAndAnsweredInNdFormat()
        {
            // The dialect the layer was built for, pinned so D1 cannot break it while adding ITU.
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent);

            LapbTestKit.Deliver(link, LapbLayer.AddressLinkSetup, 0x3F, LapbTestKit.Node100Hi, LapbTestKit.Node100Lo);

            Assert.Equal(LapbLayerState.Connected, link.State);
            Assert.Equal(2, sent.Count);
            Assert.Equal(new byte[] { 0x01, 0x73, 0x00, 0x66 }, sent[0]);
            Assert.Equal(new byte[] { 0x09, 0x01, 0x00, 0x66 }, sent[1]);
        }
    }
}
