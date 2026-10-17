//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Xmsg;
using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Spot-checks that generated enum values match the authoritative JSON constants.
    /// </summary>
    public sealed class EnumValueTests
    {
        [Fact]
        public void XfSec_IsBit9_Mask512()
        {
            // option_bits values are BIT NUMBERS; XFSEC bit 9 -> mask 1<<9 = 512.
            Assert.Equal(512, (int)XmsgOption.XFSEC);
            Assert.Equal(1 << 9, (int)XmsgOption.XFSEC);
        }

        [Fact]
        public void XfSnd_Is12()
        {
            Assert.Equal(12, (int)XmsgFunction.XFSND);
        }

        [Fact]
        public void XsGsy_Is75()
        {
            Assert.Equal(75, (int)XroutService.XSGSY);
        }

        [Fact]
        public void XeIma_IsNegative19()
        {
            Assert.Equal(-19, (int)XmsgError.XEIMA);
        }

        [Fact]
        public void XrNro_Is12()
        {
            Assert.Equal(12, (int)XroutError.XRNRO);
        }

        [Fact]
        public void ProtocolAndSubtype_MatchWire()
        {
            Assert.Equal(0xDE, (int)SintranProtocolId.Routing);
            Assert.Equal(0x03, (int)SintranPacketSubtype.Ack);
            Assert.Equal(0x0E, (int)SintranPacketSubtype.Data);
            Assert.Equal(0x13, (int)SintranPacketSubtype.ReachabilityReply);
            Assert.Equal(0x19, (int)SintranPacketSubtype.ReachabilityRequest);
        }

        [Fact]
        public void ConnectionType_And_LinkState_Values()
        {
            Assert.Equal(2, (int)XroutConnectionType.Via);
            Assert.Equal(4, (int)XroutConnectionType.Local);
            Assert.Equal(4, (int)XmsgLinkState.Run);
        }

        /// <summary>
        /// The six link states, as ND publish them.
        /// </summary>
        /// <remarks>
        /// <para><b>Why these are pinned</b></para>
        /// These numbers had no ND source until 2026-08-07. They came from this project's own
        /// <c>XMSG-API.md</c>, and neither version-L symbol file defines a link-state symbol at all -
        /// so they were prose we had written and believed.
        /// <para>
        /// ND's X-MESSAGE version-L program description (210373L) states them verbatim, twice: in
        /// section 7.2 for <c>XSLKI</c> and 7.3 for <c>XSNET</c>, both reading
        /// "Link state (0=Dead, 1=Init, 2=Call, 3=Conn, 4=Run, 5=Kill)". They needed no change.
        /// </para>
        /// <para>
        /// Pinned here because a VERIFIED claim has to be falsifiable. Anyone renumbering this enum
        /// now fails a test that names the document to check against.
        /// </para>
        /// </remarks>
        [Fact]
        public void LinkStates_MatchThePublishedTable()
        {
            Assert.Equal(0, (int)XmsgLinkState.Dead);
            Assert.Equal(1, (int)XmsgLinkState.Init);
            Assert.Equal(2, (int)XmsgLinkState.Call);
            Assert.Equal(3, (int)XmsgLinkState.Conn);
            Assert.Equal(4, (int)XmsgLinkState.Run);
            Assert.Equal(5, (int)XmsgLinkState.Kill);
        }

        [Fact]
        public void Subservices_AreGroupedWithoutCollision()
        {
            // Different parent services reuse parameter-1 values 1..5; the split enums
            // keep them distinct.
            Assert.Equal(1, (int)XroutSetCrashInfoSubservice.XSDAR);
            Assert.Equal(1, (int)XroutGetAttributeSubservice.XSGXV);
            Assert.Equal(1, (int)XroutDefineAttributeSubservice.XSDFR);
        }
    }
}
