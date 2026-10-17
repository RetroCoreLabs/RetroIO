//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Xunit;

namespace RetroIO.Protocols.Xmsg.Ethernet.Tests
{
    /// <summary>
    /// The disconnect-request family is recognised by its HIGH NIBBLE, so <c>0x60</c> is handled
    /// rather than logged as an unknown frame kind.
    /// </summary>
    /// <remarks>
    /// D100 sends <c>0x60</c> repeatedly once it gives up on a conversation. Before 2026-08-04 it
    /// fell through to "unknown ND frame kind 0x60" and filled the runner log while the link stayed
    /// nominally up. What the LOW nibble distinguishes is UNVERIFIED - only the high nibble is
    /// acted on. See <c>NdLinkFrameKind.DisconnectRequest60</c>.
    /// </remarks>
    public sealed class NdLinkDisconnect60Tests
    {
        /// <summary>
        /// Both observed members of the family classify as a disconnect request.
        /// </summary>
        /// <param name="kind">
        /// The ND frame kind byte.
        /// </param>
        [Theory]
        [InlineData((byte)0x60)]
        [InlineData((byte)0x6F)]
        public void HighNibbleSix_IsADisconnectRequest(byte kind)
        {
            NdLinkHeader header = HeaderWithKind(kind);

            Assert.True(header.IsDisconnectRequest);
            Assert.False(header.IsData);
            Assert.False(header.IsAcknowledge);
            Assert.False(header.IsConnectionRequest);
        }

        /// <summary>
        /// The other captured kinds are NOT swept into the family by the nibble test.
        /// </summary>
        /// <param name="kind">
        /// The ND frame kind byte.
        /// </param>
        [Theory]
        [InlineData((byte)0x0F)]   // CR
        [InlineData((byte)0x20)]   // DT
        [InlineData((byte)0x3F)]   // AK
        public void OtherKinds_AreNotDisconnectRequests(byte kind)
        {
            Assert.False(HeaderWithKind(kind).IsDisconnectRequest);
        }

        /// <summary>
        /// Builds a header carrying a given kind byte.
        /// </summary>
        /// <param name="kind">
        /// The kind byte to place in the header.
        /// </param>
        /// <returns>
        /// The header.
        /// </returns>
        private static NdLinkHeader HeaderWithKind(byte kind)
        {
            // A disconnect request carries no payload and both link ids are real on an established
            // link, but only the kind byte matters to the classification under test.
            NdLinkHeader header = new NdLinkHeader(kind, sequence: 0, senderLinkId: 1, receiverLinkId: 2, payloadLength: 0);
            Assert.Equal(kind, header.Kind);
            return header;
        }
    }
}
