//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Chat;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Chat.Tests
{
    /// <summary>
    /// The administrative message kinds, and the line between them and the room's own.
    /// </summary>
    /// <remarks>
    /// <para><b>Why admin shares the room's enum</b></para>
    /// <para>
    /// A separate admin enum would mean a second decoder bound. This one has already been left
    /// behind TWICE by a new kind added above it, and each time the effect was silent - the new kind
    /// decoded as malformed and was dropped, so one end sent it and the other never saw it, with
    /// nothing failing anywhere. One vocabulary is one place to forget rather than two.
    /// </para>
    /// <para><b>But one vocabulary is NOT one authority</b></para>
    /// <para>
    /// Admin kinds arrive on the server's command port. <c>CHAT-LOBBY</c> is a connection port and
    /// every arrival there spends one of the server's free connections - a seat a human then cannot
    /// have. So an admin kind on the lobby port must be refused, and a room kind on the command port
    /// must be refused too. These tests pin the predicate that decides which is which.
    /// </para>
    /// </remarks>
    public sealed class ChatAdminKindTests
    {
        /// <summary>
        /// Every room kind is below the admin line, and every admin kind is on or above it.
        /// </summary>
        /// <remarks>
        /// Written against the NAMES rather than the numbers on purpose. Asserting
        /// <c>IsAdmin(12) == true</c> would pass just as well if somebody renumbered the enum, which
        /// is exactly the change that would need catching.
        /// </remarks>
        [Fact]
        public void TheAdminLineFallsBetweenWhoAndAdminStatus()
        {
            Assert.False(ChatMessageKinds.IsAdmin(ChatMessageKind.None));
            Assert.False(ChatMessageKinds.IsAdmin(ChatMessageKind.Join));
            Assert.False(ChatMessageKinds.IsAdmin(ChatMessageKind.Say));
            Assert.False(ChatMessageKinds.IsAdmin(ChatMessageKind.Rename));
            Assert.False(ChatMessageKinds.IsAdmin(ChatMessageKind.Who));
            Assert.False(ChatMessageKinds.IsAdmin(ChatMessageKind.Map));

            Assert.True(ChatMessageKinds.IsAdmin(ChatMessageKind.AdminStatus));
            Assert.True(ChatMessageKinds.IsAdmin(ChatMessageKind.AdminStatusReply));
            Assert.True(ChatMessageKinds.IsAdmin(ChatMessageKind.AdminStop));
        }

        /// <summary>
        /// The admin block starts at a reserved base with room below it for more room kinds.
        /// </summary>
        /// <remarks>
        /// This used to assert the opposite - that admin began immediately after the last room
        /// kind, with NO gap. That was wrong, and the very next kind added proved it: Map is a ROOM
        /// kind and would have landed at 15, inside a contiguous admin block starting at 12, where
        /// <see cref="ChatMessageKinds.IsAdmin"/> would have misread it as administrative.
        /// A reserved base gives both halves room to grow - TRUNK and UNTRUNK join admin later
        /// without pushing anything, and new room kinds fill 13..31.
        /// </remarks>
        [Fact]
        public void TheAdminBlockStartsAtAReservedBaseWithRoomBelow()
        {
            Assert.Equal(32, ChatMessageKinds.LowestAdmin);
            Assert.True((byte)ChatMessageKind.Map < ChatMessageKinds.LowestAdmin);
            Assert.False(ChatMessageKinds.IsAdmin(ChatMessageKind.Map));
        }

        /// <summary>
        /// The decoder bound still covers the highest kind, now that admin kinds are the highest.
        /// </summary>
        /// <remarks>
        /// <c>ChatWhoTests.TheDecoderBoundCoversEveryKind</c> checks the same thing by walking the
        /// enum. This one states the expected ANSWER, so a reader can see what the bound should be
        /// without running anything - and so the two disagree loudly if only one is updated.
        /// </remarks>
        [Fact]
        public void TheDecoderBoundReachesTheHighestKind()
        {
            // TrunkRelay, not AdminStop. The trunk kinds sit ABOVE the admin block - 48 and up,
            // left clear of it so a future admin verb cannot collide with a trunk one - so the
            // highest kind is no longer an admin kind at all.
            //
            // THIS TEST EARNED ITS KEEP, TWICE. Ten kinds went into the PLANC server between
            // 2026-08-20 and 2026-08-23 and this constant was left behind for all of them; the
            // test failed the moment the enum caught up. Everything above the stale bound had
            // been decoding as malformed and being dropped in silence.
            //
            // It caught the same mistake again on 2026-08-25 when TrunkRelay (52) went in above
            // TrunkSaid - which is precisely the case the decoder's own comment warns about, and
            // it would otherwise have made every relayed message vanish in transit while
            // everything else kept working.
            //
            // And a THIRD time on the same day, when TrunkRelayId (53) went in above TrunkRelay
            // for dedup. Three catches for one assertion; do not be tempted to delete it as
            // busywork just because updating it is a chore on every new kind - that chore IS the
            // check.
            //
            // A FOURTH on 2026-08-30, when TrunkDirectBad (55) went into the registry. The
            // registry test failed first and the bound was moved with it, so this one failed
            // second - which is the order that shows the two checks are guarding different
            // things: one that the enum matches the registry, this one that the DECODER can
            // reach the top of the enum.
            Assert.Equal((byte)ChatMessageKind.TrunkDirectBad, ChatMessageKinds.Highest);
            Assert.True(ChatMessageKinds.Highest >= ChatMessageKinds.LowestAdmin);
        }

        /// <summary>
        /// The trunk kinds sit above the admin block, with a gap.
        /// </summary>
        /// <remarks>
        /// Server-to-server kinds start at 48, not at 35, so the admin block can grow without
        /// running into them - which it since has: START-TRUNK, STOP-TRUNK, LIST-TRUNKS and
        /// INITIALIZE took 35 to 38 and touched nothing.
        /// </remarks>
        [Fact]
        public void TheTrunkKindsSitAboveTheAdminBlockWithAGap()
        {
            Assert.True((byte)ChatMessageKind.TrunkHello > (byte)ChatMessageKind.AdminInitialize);
            Assert.Equal(48, (byte)ChatMessageKind.TrunkHello);

            // They are server-to-server, so they are NOT room kinds - IsAdmin says true for them
            // because it is a "not an ordinary member" test, and that is what the room port uses
            // to refuse them.
            Assert.True(ChatMessageKinds.IsAdmin(ChatMessageKind.TrunkHello));
            Assert.True(ChatMessageKinds.IsAdmin(ChatMessageKind.TrunkSaid));
        }

        /// <summary>
        /// The two kinds a client receives but never sends stay below the admin block.
        /// </summary>
        /// <remarks>
        /// <c>AllWho</c> and <c>History</c> are answers, not requests - a client sends
        /// <c>Who</c> and <c>Join</c> and the server decides. They are ROOM kinds because that is
        /// the port they arrive on, and putting them above 32 would have the room port refuse the
        /// server's own replies.
        /// </remarks>
        [Fact]
        public void TheReceiveOnlyRoomKindsAreNotAdmin()
        {
            Assert.False(ChatMessageKinds.IsAdmin(ChatMessageKind.AllWho));
            Assert.False(ChatMessageKinds.IsAdmin(ChatMessageKind.History));
            Assert.True((byte)ChatMessageKind.History < ChatMessageKinds.LowestAdmin);
        }

        /// <summary>
        /// Every admin kind survives the wire format unchanged.
        /// </summary>
        /// <remarks>
        /// The admin kinds carry no new fields - a status question has an empty text, and the answer
        /// puts readable text where a <c>Said</c> would put what somebody said. That is the whole
        /// reason they cost no wire change, and this proves it rather than assuming it.
        /// </remarks>
        [Fact]
        public void EveryAdminKindSurvivesTheWireFormat()
        {
            ChatMessageKind[] admin = new ChatMessageKind[]
            {
                ChatMessageKind.AdminStatus,
                ChatMessageKind.AdminStatusReply,
                ChatMessageKind.AdminStop,
            };

            for (int i = 0; i < admin.Length; i++)
            {
                ChatMessage sent = new ChatMessage(admin[i], "OPER", "peers=0 trunks=0");
                byte[] wire = new byte[sent.ByteCount];
                int written = sent.Encode(wire);

                Assert.Equal(wire.Length, written);
                Assert.True(ChatMessage.TryDecode(wire, out ChatMessage back));
                Assert.Equal(admin[i], back.Kind);
                Assert.Equal("OPER", back.Nickname);
                Assert.Equal("peers=0 trunks=0", back.Text);
            }
        }
    }
}
