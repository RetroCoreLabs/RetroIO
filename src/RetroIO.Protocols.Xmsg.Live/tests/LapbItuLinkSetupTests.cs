//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Protocols.Xmsg.Hdlc;
using Xunit;

namespace RetroIO.Protocols.Xmsg.Live.Tests
{
    /// <summary>
    /// Link set-up in the ITU dialect of <see cref="LapbLayer"/>, by the X.25 (10/96) rules, when the
    /// station's own SABM is refused or crosses the peer's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 2.4.4.1: a station that answers a SABM with UA resets V(S) and V(R) and the link is set up.
    /// 2.4.5.11.1 (both stations send the same command): each sends the UA, then enters the
    /// information transfer phase when it receives the UA or when its T1 runs out. Neither rule
    /// ever sends the SABM again into a link that is set up.
    /// </para>
    /// <para>
    /// Measured 2026-11-16, x25switch (DTE) against the SINTRAN K COSMOS X.25 link on port 2362: the
    /// switch's SABM was refused with DM, SINTRAN then sent its own SABM, the switch answered UA,
    /// the link came up and the X.25 restart completed - and 3 s later the switch's T1 sent its old
    /// SABM again without resetting its own counters, so its next poll carried N(R)=1 into a link
    /// SINTRAN had just reset, and SINTRAN answered FRMR (Z, invalid N(R)).
    /// </para>
    /// </remarks>
    public class LapbItuLinkSetupTests
    {
        // ITU DTE (the switch's role): commands carry B, responses A; the DCE's commands carry A,
        // its responses B (2.4.2).
        private const byte FromDceCommand = LapbLayer.AddressItuA;
        private const byte FromDceResponse = LapbLayer.AddressItuB;

        private static LapbOptions ItuDte() => new LapbOptions(variant: LapbVariant.Itu, isDte: true);

        private static bool IsSabm(byte[] frame) => frame.Length >= 2 && (frame[1] & 0xEF) == 0x2F;

        private static int CountSabms(List<byte[]> sent)
        {
            int n = 0;
            for (int i = 0; i < sent.Count; i++)
            {
                if (IsSabm(sent[i]))
                {
                    n++;
                }
            }

            return n;
        }

        [Fact]
        public void OurSabmRefusedWithDm_ThenThePeersSabm_SetsTheLinkUp_AndNoSabmIsSentAgain()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent, ItuDte());

            link.Connect(0);
            LapbTestKit.DeliverAt(link, 100, FromDceResponse, 0x1F);   // DM, F=1: our SABM is refused
            LapbTestKit.DeliverAt(link, 200, FromDceCommand, 0x2F);    // the peer's SABM, P=0

            // 2.4.4.1: the UA answers the peer's SABM and the link is set up. No SABM of ours is
            // outstanding any more: the DM answered it.
            Assert.Equal(LapbLayerState.Connected, link.State);
            Assert.True(link.IsUp);

            sent.Clear();
            link.Tick(3500);
            link.Tick(7000);
            Assert.Equal(0, CountSabms(sent));
        }

        [Fact]
        public void CrossingSabms_TheUaToOursSetsTheLinkUp()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent, ItuDte());

            link.Connect(0);
            LapbTestKit.DeliverAt(link, 100, FromDceCommand, 0x3F);    // the peer's SABM crosses ours

            // 2.4.5.11.1: send the UA, then wait for the UA to our SABM (or T1).
            Assert.Equal(new byte[] { LapbLayer.AddressItuA, 0x73 }, sent[1]);
            Assert.False(link.IsUp);

            LapbTestKit.DeliverAt(link, 200, FromDceResponse, 0x73);   // the UA to ours
            Assert.True(link.IsUp);
        }

        [Fact]
        public void CrossingSabms_WithNoUa_T1SetsTheLinkUp_AndDoesNotSendTheSabmAgain()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent, ItuDte());

            link.Connect(0);
            LapbTestKit.DeliverAt(link, 100, FromDceCommand, 0x3F);
            sent.Clear();

            // 2.4.5.11.1: "or after timeout of T1" - enter information transfer, no new SABM.
            link.Tick(3500);

            Assert.True(link.IsUp);
            Assert.Equal(0, CountSabms(sent));
        }

        [Fact]
        public void CrossingSabms_NoRrIsSentWhileOurSabmIsUnanswered()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent, ItuDte());

            link.Connect(0);
            LapbTestKit.DeliverAt(link, 100, FromDceCommand, 0x3F);    // the peer's SABM crosses ours

            // X.25 2.4.4.1: frames other than UA and DM "will be sent only after the data link is
            // set up and if no outstanding SABM ... exists". Our SABM is outstanding, so the UA is
            // the only answer: SABM, UA and nothing more.
            Assert.Equal(2, sent.Count);
            Assert.True(IsSabm(sent[0]));
            Assert.Equal(new byte[] { LapbLayer.AddressItuA, 0x73 }, sent[1]);
        }

        /// <summary>
        /// The peer's SABM crossed ours and was answered, and our own SABM is still unanswered. An I frame the
        /// peer sends now (it got our UA, so for it the link is up) must be thrown away: not handed up, not
        /// acknowledged. X.25 (10/96) 2.4.4.1: "The DCE, having sent the SABM ... command, will ignore and
        /// discard any frames except an SABM ... or DISC command, or a UA or DM response", and frames other than
        /// UA and DM "will be sent only after the data link is set up and if no outstanding SABM ... exists".
        /// Measured 2026-11-18 on the real serial trunk (COM10/COM18): the station handed the peer's X.25
        /// Restart Request up while it still said "not up", the switch port dropped it, the RR said it had
        /// arrived, and the X.25 restart never finished at that end, so every call over the trunk was ignored.
        /// </summary>
        [Fact]
        public void CrossingSabms_AnIFrameBeforeOurSabmIsAnswered_IsDiscardedAndNotAcknowledged()
        {
            List<byte[]> sent = new List<byte[]>();
            List<byte[]> got = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent, ItuDte());
            link.OnInformation += delegate (ReadOnlyMemory<byte> info) { got.Add(info.ToArray()); };

            link.Connect(0);
            LapbTestKit.DeliverAt(link, 100, FromDceCommand, 0x3F);    // the peer's SABM crosses ours
            sent.Clear();

            // I frame N(S)=0 N(R)=0 carrying an X.25 Restart Indication.
            LapbTestKit.DeliverAt(link, 150, FromDceCommand, 0x00, 0x10, 0x00, 0xFB, 0x07, 0x00);
            Assert.Empty(got);
            Assert.Empty(sent);

            // T1 sets the link up (2.4.4.5.1). The peer sends the frame again (its timer recovery) and this time
            // it is taken, once, with N(S)=0 because nothing was accepted before.
            link.Tick(3500);
            Assert.True(link.IsUp);
            LapbTestKit.DeliverAt(link, 3600, FromDceCommand, 0x10, 0x10, 0x00, 0xFB, 0x07, 0x00);
            Assert.Single(got);
        }

        /// <summary>
        /// X.25 (10/96) 2.4.5.9: when T1 runs out with an I frame unacknowledged the station enters the timer
        /// recovery condition and polls; when the answer with F=1 comes back it sets V(S) to the N(R) received
        /// and retransmits from there. Before this the poll was sent and the F=1 answer changed nothing, so an I
        /// frame the peer had thrown away was never sent again and the link failed after N2 polls.
        /// </summary>
        [Fact]
        public void TimerRecovery_AnFOneAnswerWithAnOldNr_RetransmitsFromThatNr()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent, ItuDte());
            link.Connect(0);
            LapbTestKit.DeliverAt(link, 10, FromDceResponse, 0x73);    // the UA to our SABM: link up
            Assert.True(link.IsUp);

            link.SendInformation(new byte[] { 0x10, 0x00, 0xFB, 0x00, 0x00 }, 20);
            sent.Clear();

            link.Tick(3500);                                           // T1: poll, RR P=1
            Assert.Contains(sent, f => f.Length == 2 && f[1] == 0x11);
            sent.Clear();

            LapbTestKit.DeliverAt(link, 3600, FromDceResponse, 0x11);  // RR F=1 N(R)=0: nothing arrived
            Assert.Contains(sent, f => f.Length > 2 && (f[1] & 0x01) == 0 && ((f[1] >> 1) & 0x07) == 0);
        }

        /// <summary>
        /// Two ITU stations joined back to back with a wire that delivers each frame at once, inside the
        /// send (as an in-process codec bridge does): a DTE's SABM brings the link up on both and the
        /// exchange ends. Found 2026-11-17: the first version of the unsolicited-UA reset made the two
        /// stations answer each other for ever (stack overflow in CodecLinkEndToEndTests).
        /// </summary>
        [Fact]
        public void BackToBackStations_ASabmBringsTheLinkUp_AndTheExchangeEnds()
        {
            LapbLayer dte = new LapbLayer(0, ItuDte());
            LapbLayer dce = new LapbLayer(0, new LapbOptions(variant: LapbVariant.Itu, isDte: false));
            List<string> wire = new List<string>();
            int depth = 0;

            void Wire(LapbLayer to, string from, byte[] body)
            {
                wire.Add(from + " " + BitConverter.ToString(body));
                depth++;
                if (depth > 20)
                {
                    throw new InvalidOperationException("the stations never stop answering each other: " + string.Join(" | ", wire));
                }

                byte[] frame = new byte[body.Length + 2];
                Array.Copy(body, frame, body.Length);
                to.OnFrameReceived(LapbFrame.FromCapture(frame), 0);
                depth--;
            }

            dte.OnTransmit += delegate (byte[] body) { Wire(dce, "DTE", body); };
            dce.OnTransmit += delegate (byte[] body) { Wire(dte, "DCE", body); };

            dte.Connect(0);

            Assert.True(dte.IsUp, string.Join(" | ", wire));
            Assert.True(dce.IsUp, string.Join(" | ", wire));
            Assert.Equal(0L, dte.LinkResets);
            Assert.Equal(0L, dce.LinkResets);
        }

        [Fact]
        public void UnsolicitedUaWhileConnected_StartsTheLinkResetting()
        {
            List<byte[]> sent = new List<byte[]>();
            LapbLayer link = LapbTestKit.NewLink(0x0066, sent, ItuDte());

            link.Connect(0);
            LapbTestKit.DeliverAt(link, 100, FromDceResponse, 0x73);   // the UA to our SABM: link up
            Assert.True(link.IsUp);
            sent.Clear();

            // A second UA, with no SABM or DISC of ours outstanding: X.25 2.4.7.3 says the station
            // "may either initiate the data link resetting procedures itself ... or return a DM
            // response". We take the first: a new SABM, and the link counts a reset.
            LapbTestKit.DeliverAt(link, 200, FromDceResponse, 0x73);

            Assert.Equal(1, CountSabms(sent));
            Assert.Equal(1L, link.LinkResets);
            Assert.False(link.IsUp);

            // The peer accepts the SABM: the link is up again.
            LapbTestKit.DeliverAt(link, 300, FromDceResponse, 0x73);
            Assert.True(link.IsUp);
            Assert.Equal(1L, link.LinkResets);
        }
    }
}
