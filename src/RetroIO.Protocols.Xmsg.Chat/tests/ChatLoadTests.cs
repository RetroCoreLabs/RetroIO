using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Chat;
using RetroIO.Protocols.Xmsg;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Chat.Tests
{
    /// <summary>
    /// Puts twenty simulated users in one room and makes them all talk at once.
    /// </summary>
    /// <remarks>
    /// <para><b>What this is for</b></para>
    /// The room had only ever been run with two people in it - one PLANC client on a terminal and
    /// one of something else - because the PLANC client's nickname was fixed and two of them could
    /// not both join. So nothing had ever answered the plain question: does it still work with
    /// twenty, and does anything fall over when they all speak at once.
    /// <para><b>Why it lives here and not in a program of its own</b></para>
    /// It drives the SAME <see cref="ChatServer"/>, <see cref="ChatClient"/> and
    /// <see cref="XroutDirectory"/> the live runner does. A separate load harness would be a second
    /// implementation of the client, and the thing most likely to break under load is the client.
    /// <para><b>What it does NOT cover</b></para>
    /// This is one process and no wire. It exercises the room rules, the seat accounting and the
    /// message encoding under volume; it does not exercise HDLC, XMSG framing or the PLANC server.
    /// The equivalent against a real CHATSV has to be run live - see
    /// <c>DOC\PLAN-CHAT-RT-SERVER-AND-REENTRANT-CLIENT.md</c>.
    /// </remarks>
    public sealed class ChatLoadTests
    {
        /// <summary>
        /// How many simulated users take part.
        /// </summary>
        private const int Users = 20;

        /// <summary>
        /// How many lines each of them says.
        /// </summary>
        private const int LinesEach = 25;

        /// <summary>
        /// Twenty users join, all talk, and everyone hears everyone.
        /// </summary>
        /// <remarks>
        /// The strong assertion is the arithmetic: every speaker's line reaches every member,
        /// including the speaker, so the room must deliver <c>Users * LinesEach * Users</c> Said
        /// messages in total. Counting them is what would catch a message quietly dropped when a
        /// queue filled up - the failure this test exists to find.
        /// </remarks>
        [Fact]
        public void TwentyUsersCanAllTalkAtOnce()
        {
            Fixture fixture = new Fixture(Users);
            List<ChatClient> users = new List<ChatClient>();

            for (int i = 0; i < Users; i++)
            {
                ChatClient user = fixture.Join(NameOf(i));
                Assert.True(user.IsJoined, "user " + NameOf(i) + " was not welcomed");
                users.Add(user);
            }

            Assert.Equal(Users, fixture.Server.MemberCount);

            // Everybody speaks, round by round, so the traffic interleaves the way it would with
            // real people rather than one user emptying the room's attention first.
            int said = 0;
            for (int round = 0; round < LinesEach; round++)
            {
                for (int i = 0; i < users.Count; i++)
                {
                    Assert.True(users[i].Say("line " + round + " from " + NameOf(i)));
                    said++;
                }

                fixture.Server.Poll();
            }

            Assert.Equal(Users * LinesEach, said);

            // Drain every client and count what actually arrived.
            int heardSaid = 0;
            for (int i = 0; i < users.Count; i++)
            {
                IReadOnlyList<ChatMessage> arrived = users[i].Poll();
                for (int m = 0; m < arrived.Count; m++)
                {
                    if (arrived[m].Kind == ChatMessageKind.Said)
                    {
                        heardSaid++;
                    }
                }
            }

            // A Said goes to EVERY member, speaker included - so the room moved this many.
            Assert.Equal(Users * LinesEach * Users, heardSaid);
        }

        /// <summary>
        /// The seat count is exactly right after twenty joins and twenty leaves.
        /// </summary>
        /// <remarks>
        /// The seat leak is the defect this project has spent the most time on, in two different
        /// programs. Twenty users arriving and leaving is where an off-by-one shows up as a number
        /// rather than as a room that mysteriously stops accepting people a week later.
        /// </remarks>
        [Fact]
        public void EverySeatComesBackWhenTwentyUsersLeave()
        {
            Fixture fixture = new Fixture(Users);
            List<ChatClient> users = new List<ChatClient>();

            for (int i = 0; i < Users; i++)
            {
                users.Add(fixture.Join(NameOf(i)));
            }

            Assert.Equal(0, fixture.FreeSeats);
            Assert.Equal(Users, fixture.Server.MemberCount);

            for (int i = 0; i < users.Count; i++)
            {
                users[i].Leave();
                fixture.Server.Poll();
            }

            Assert.Equal(0, fixture.Server.MemberCount);
            Assert.Equal(Users, fixture.FreeSeats);
        }

        /// <summary>
        /// A room the size of the PLANC one turns the extra users away instead of breaking.
        /// </summary>
        /// <remarks>
        /// <para>
        /// CHATSV opens <c>CHAT-LOBBY</c> with sixteen seats, and XROUT - not the server - is what
        /// refuses the seventeenth. This is the same shape on our side, and the point is that going
        /// over the limit is ORDINARY: the first sixteen are unaffected, and the room carries on
        /// working for them.
        /// </para>
        /// <para>
        /// Twenty into sixteen is the case a live run against D100 would hit, which is why the
        /// number matches the machine rather than being a round one.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheSeventeenthUserIsRefusedAndTheRoomKeepsWorking()
        {
            const int Seats = 16;

            Fixture fixture = new Fixture(Seats);
            List<ChatClient> seated = new List<ChatClient>();

            for (int i = 0; i < Seats; i++)
            {
                seated.Add(fixture.Join(NameOf(i)));
            }

            Assert.Equal(0, fixture.FreeSeats);

            // The ones that do not fit. XROUT refuses the letter outright - the server never sees
            // them, so this is not a rule the room applies but a door that is shut.
            int refused = 0;
            for (int i = Seats; i < Users; i++)
            {
                ChatClient late = fixture.NewUser(NameOf(i));
                if (late.Join(ChatRooms.NameFor(Fixture.Room)) != XroutError.XRSOK)
                {
                    refused++;
                }
            }

            Assert.Equal(Users - Seats, refused);
            Assert.Equal(Seats, fixture.Server.MemberCount);

            // AND THE ROOM IS STILL FINE. A refusal at the door must not disturb the people
            // already inside, which is the half of "handles overload" that is easy to miss.
            Assert.True(seated[0].Say("still here"));
            fixture.Server.Poll();

            int heard = 0;
            for (int i = 0; i < seated.Count; i++)
            {
                IReadOnlyList<ChatMessage> arrived = seated[i].Poll();
                for (int m = 0; m < arrived.Count; m++)
                {
                    if (arrived[m].Kind == ChatMessageKind.Said)
                    {
                        heard++;
                    }
                }
            }

            Assert.Equal(Seats, heard);
        }

        /// <summary>
        /// Long lines from every user at once do not corrupt anybody's text.
        /// </summary>
        /// <remarks>
        /// Volume finds dropped messages; length finds buffer arithmetic. The text length is two
        /// bytes big-endian on the wire, so a line over 255 characters is the one that catches a
        /// single-byte length - and twenty users sending them at once catches a shared buffer.
        /// </remarks>
        [Fact]
        public void LongLinesFromEveryUserSurviveIntact()
        {
            Fixture fixture = new Fixture(Users);
            List<ChatClient> users = new List<ChatClient>();

            for (int i = 0; i < Users; i++)
            {
                users.Add(fixture.Join(NameOf(i)));
            }

            for (int i = 0; i < users.Count; i++)
            {
                Assert.True(users[i].Say(new string((char)('A' + (i % 26)), 300)));
            }

            fixture.Server.Poll();

            // One client's view is enough to check the CONTENT - every line passed through the
            // same encode and decode, and a shared-buffer fault would show as one user's text
            // appearing under another user's name.
            IReadOnlyList<ChatMessage> arrived = users[0].Poll();
            int checkedLines = 0;

            for (int m = 0; m < arrived.Count; m++)
            {
                if (arrived[m].Kind != ChatMessageKind.Said)
                {
                    continue;
                }

                string who = arrived[m].Nickname;
                string text = arrived[m].Text;

                Assert.Equal(300, text.Length);

                // The speaker's name says which letter the line must be made of.
                int index = IndexOf(who);
                Assert.InRange(index, 0, Users - 1);
                Assert.Equal(new string((char)('A' + (index % 26)), 300), text);
                checkedLines++;
            }

            Assert.Equal(Users, checkedLines);
        }

        /// <summary>
        /// Builds the nickname for a simulated user.
        /// </summary>
        /// <param name="index">
        /// Zero-based user number.
        /// </param>
        /// <returns>
        /// A nickname that fits the room's sixteen-character limit.
        /// </returns>
        private static string NameOf(int index)
        {
            return "USER" + index.ToString("00");
        }

        /// <summary>
        /// Recovers the user number from a nickname built by <see cref="NameOf"/>.
        /// </summary>
        /// <param name="nickname">
        /// The nickname to read.
        /// </param>
        /// <returns>
        /// The zero-based user number, or -1 when the name is not one of ours.
        /// </returns>
        private static int IndexOf(string nickname)
        {
            if (nickname == null || nickname.Length != 6)
            {
                return -1;
            }

            int value;
            if (!int.TryParse(nickname.Substring(4), out value))
            {
                return -1;
            }

            return value;
        }

        /// <summary>
        /// One node, one room, and a way to put a welcomed user in it.
        /// </summary>
        private sealed class Fixture
        {
            internal const string Room = "LOBBY";

            private readonly XroutDirectory _directory;
            private readonly XmsgKernel _kernel;

            /// <summary>
            /// Opens a room with the given number of seats.
            /// </summary>
            /// <param name="seats">
            /// How many members the room admits.
            /// </param>
            internal Fixture(int seats)
            {
                _directory = new XroutDirectory();
                _kernel = new XmsgKernel(100, 0x1111, null);

                Server = new ChatServer(_kernel, _directory);
                Assert.Equal(
                    XroutError.XRSOK,
                    Server.Open(ChatRooms.NameFor(Room), seats, "welcome"));
            }

            /// <summary>
            /// Gets the room's server.
            /// </summary>
            internal ChatServer Server { get; }

            /// <summary>
            /// Gets how many connection seats XROUT still has for the room.
            /// </summary>
            internal int FreeSeats
            {
                get { return _directory.FreeConnections(ChatRooms.NameFor(Room)); }
            }

            /// <summary>
            /// Makes a client without joining it.
            /// </summary>
            /// <param name="nickname">
            /// The name to join under.
            /// </param>
            /// <returns>
            /// A client that has not yet sent anything.
            /// </returns>
            internal ChatClient NewUser(string nickname)
            {
                return new ChatClient(_kernel, _directory, nickname);
            }

            /// <summary>
            /// Makes a client, joins it, and settles the welcome.
            /// </summary>
            /// <param name="nickname">
            /// The name to join under.
            /// </param>
            /// <returns>
            /// A client that has been welcomed.
            /// </returns>
            internal ChatClient Join(string nickname)
            {
                ChatClient user = NewUser(nickname);
                Assert.Equal(XroutError.XRSOK, user.Join(ChatRooms.NameFor(Room)));

                // The server has to run before the welcome exists, and the client has to poll
                // before it knows it is in - the same two steps a live client takes.
                Server.Poll();
                user.Poll();
                return user;
            }
        }
    }
}
