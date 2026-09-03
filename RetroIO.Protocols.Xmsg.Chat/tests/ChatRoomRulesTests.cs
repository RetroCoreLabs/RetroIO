using RetroIO.Protocols.Xmsg.Chat;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Chat.Tests
{
    /// <summary>
    /// The room rules on their own, with no transport at all.
    /// </summary>
    /// <remarks>
    /// These rules are reached by two completely different doors - a port-to-port conversation and
    /// somebody typing at a SINTRAN terminal connected to this node. They are tested here once so
    /// that both doors are known to behave the same, which is the whole reason they were pulled out
    /// of the server.
    /// </remarks>
    public sealed class ChatRoomRulesTests
    {
        /// <summary>
        /// The ordinary path.
        /// </summary>
        [Fact]
        public void SomebodyCanJoinAndIsThenKnown()
        {
            ChatRoom room = new ChatRoom();

            string refusal;
            Assert.True(room.TryJoin(1, "RONNY", out refusal));
            Assert.Equal(string.Empty, refusal);
            Assert.Equal(1, room.Count);

            string nickname;
            Assert.True(room.TryGetNickname(1, out nickname));
            Assert.Equal("RONNY", nickname);
        }

        /// <summary>
        /// A name somebody already answers to is refused, whatever case it is typed in.
        /// </summary>
        [Fact]
        public void ATakenNameIsRefusedIgnoringCase()
        {
            ChatRoom room = new ChatRoom();

            string refusal;
            room.TryJoin(1, "RONNY", out refusal);

            Assert.False(room.TryJoin(2, "ronny", out refusal));
            Assert.Equal("that nickname is taken", refusal);
            Assert.Equal(1, room.Count);
        }

        /// <summary>
        /// The same member joining twice is refused rather than duplicated.
        /// </summary>
        [Fact]
        public void JoiningTwiceIsRefused()
        {
            ChatRoom room = new ChatRoom();

            string refusal;
            room.TryJoin(1, "RONNY", out refusal);

            Assert.False(room.TryJoin(1, "SOMEBODY", out refusal));
            Assert.Equal("already joined", refusal);
        }

        /// <summary>
        /// An empty name is refused.
        /// </summary>
        [Fact]
        public void AnEmptyNameIsRefused()
        {
            ChatRoom room = new ChatRoom();

            string refusal;
            Assert.False(room.TryJoin(1, string.Empty, out refusal));
            Assert.Equal("a nickname is required", refusal);
        }

        /// <summary>
        /// A rename reports the name that was given up, so the room can be told both.
        /// </summary>
        [Fact]
        public void ARenameReportsThePreviousName()
        {
            ChatRoom room = new ChatRoom();

            string refusal;
            room.TryJoin(1, "ANNA", out refusal);

            string previous;
            Assert.True(room.TryRename(1, "ANNIKA", out previous, out refusal));
            Assert.Equal("ANNA", previous);
            Assert.Equal(string.Empty, refusal);

            string nickname;
            room.TryGetNickname(1, out nickname);
            Assert.Equal("ANNIKA", nickname);
        }

        /// <summary>
        /// Asking for the name you already have is not a change and NOT an error.
        /// </summary>
        /// <remarks>
        /// The caller tells the two apart by the refusal being empty. Reporting it as an error
        /// would put a complaint on somebody's screen for doing nothing wrong.
        /// </remarks>
        [Fact]
        public void RenamingToTheSameNameIsNeitherAChangeNorAnError()
        {
            ChatRoom room = new ChatRoom();

            string refusal;
            room.TryJoin(1, "ANNA", out refusal);

            string previous;
            Assert.False(room.TryRename(1, "anna", out previous, out refusal));
            Assert.Equal(string.Empty, refusal);
        }

        /// <summary>
        /// A rename to a taken name is refused and the old name stands.
        /// </summary>
        [Fact]
        public void ARenameToATakenNameIsRefused()
        {
            ChatRoom room = new ChatRoom();

            string refusal;
            room.TryJoin(1, "RONNY", out refusal);
            room.TryJoin(2, "ANNA", out refusal);

            string previous;
            Assert.False(room.TryRename(2, "RONNY", out previous, out refusal));
            Assert.Equal("that nickname is taken", refusal);

            string nickname;
            room.TryGetNickname(2, out nickname);
            Assert.Equal("ANNA", nickname);
        }

        /// <summary>
        /// Somebody who is not in the room cannot rename.
        /// </summary>
        [Fact]
        public void AStrangerCannotRename()
        {
            ChatRoom room = new ChatRoom();

            string previous;
            string refusal;
            Assert.False(room.TryRename(99, "ANYTHING", out previous, out refusal));
            Assert.Equal("you are not in the room", refusal);
        }

        /// <summary>
        /// Leaving reports the name that left and frees it for somebody else.
        /// </summary>
        [Fact]
        public void LeavingFreesTheName()
        {
            ChatRoom room = new ChatRoom();

            string refusal;
            room.TryJoin(1, "RONNY", out refusal);

            string nickname;
            Assert.True(room.TryLeave(1, out nickname));
            Assert.Equal("RONNY", nickname);
            Assert.Equal(0, room.Count);

            // Free again.
            Assert.True(room.TryJoin(2, "RONNY", out refusal));
        }

        /// <summary>
        /// Leaving when you were never in is not an event.
        /// </summary>
        [Fact]
        public void LeavingWhenNotPresentIsNotAnEvent()
        {
            ChatRoom room = new ChatRoom();

            string nickname;
            Assert.False(room.TryLeave(1, out nickname));
        }

        /// <summary>
        /// The member list can be walked while members are being removed.
        /// </summary>
        /// <remarks>
        /// Announcing something to the room and reacting to it - somebody being dropped, say -
        /// happens on the same list. Copying is what makes that safe.
        /// </remarks>
        [Fact]
        public void TheMemberListCanBeWalkedWhileRemoving()
        {
            ChatRoom room = new ChatRoom();

            string refusal;
            room.TryJoin(1, "ONE", out refusal);
            room.TryJoin(2, "TWO", out refusal);

            long[] ids = room.CopyMemberIds();
            for (int i = 0; i < ids.Length; i++)
            {
                string gone;
                room.TryLeave(ids[i], out gone);
            }

            Assert.Equal(0, room.Count);
        }

        /// <summary>
        /// A nickname longer than the SINTRAN server can store is refused.
        /// </summary>
        /// <remarks>
        /// Not a style rule. CHATSV.PLNC keeps names in a fixed sixteen bytes per seat and PLANC
        /// checks no array bound, so a longer name written in there runs through the other seats.
        /// That program drops the message; this one refuses it with a reason, and the important
        /// part - that the name is never admitted - is the same on both doors.
        /// </remarks>
        [Fact]
        public void ANicknameLongerThanSixteenIsRefused()
        {
            ChatRoom room = new ChatRoom();

            string refusal;
            Assert.False(room.TryJoin(1, new string('R', ChatRoom.MaxNicknameLength + 1), out refusal));
            Assert.Equal("a nickname is at most 16 characters", refusal);
            Assert.Equal(0, room.Count);
        }

        /// <summary>
        /// Exactly sixteen characters is allowed - the limit is inclusive.
        /// </summary>
        /// <remarks>
        /// The boundary matters because the PLANC guard is written <c>nameLength > maxNameLen</c>.
        /// An off-by-one on either side would leave one door admitting a name the other drops, and
        /// a sixteen-character name is the only one that would show it.
        /// </remarks>
        [Fact]
        public void ANicknameOfExactlySixteenIsAllowed()
        {
            ChatRoom room = new ChatRoom();

            string refusal;
            Assert.True(room.TryJoin(1, new string('R', ChatRoom.MaxNicknameLength), out refusal));
            Assert.Equal(1, room.Count);
        }

        /// <summary>
        /// A rename to an over-long name is refused too.
        /// </summary>
        /// <remarks>
        /// The join door is not the only way a name reaches storage - CHATSV copies the new name
        /// into the same fixed row on a rename. Guarding only the join would leave the shorter path
        /// in and the longer one out.
        /// </remarks>
        [Fact]
        public void ARenameToAnOverLongNicknameIsRefused()
        {
            ChatRoom room = new ChatRoom();

            string refusal;
            Assert.True(room.TryJoin(1, "RONNY", out refusal));

            string previous;
            Assert.False(room.TryRename(1, new string('R', ChatRoom.MaxNicknameLength + 1), out previous, out refusal));
            Assert.Equal("a nickname is at most 16 characters", refusal);

            // And the name they had is untouched.
            string current;
            Assert.True(room.TryGetNickname(1, out current));
            Assert.Equal("RONNY", current);
        }
    }
}