using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Chat;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Chat.Tests
{
    /// <summary>
    /// One service, many rooms: that a room hears its own traffic and nobody else's.
    /// </summary>
    /// <remarks>
    /// <para><b>Why these exist, and they are not hypothetical</b></para>
    /// On 2026-08-20 two clients sat in one room on D100 and neither heard a word the other said.
    /// The cause was in <c>CHATSV.PLNC</c>: <c>broadcast</c> took ONE slot number that named both
    /// the room to send to and the member to leave out, guarded on it being zero meaning "no room",
    /// while every Say passed zero meaning "leave nobody out". So every Say returned before sending
    /// anything.
    /// <para>
    /// What made it expensive was that <c>/who</c> and the Joined notice kept working - they are the
    /// two paths that do not pass a zero - so the room table looked right and the fault looked like
    /// delivery. Nothing in the C# suite would have caught it either, because every existing test
    /// joins without naming a room and so never exercises the room filter at all.
    /// </para>
    /// <para><b>Two clients are the minimum, and that is the point</b></para>
    /// A single client cannot show isolation: it cannot distinguish "delivered to my room" from
    /// "delivered to everybody". Every test here uses at least two, and the ones about leaking use
    /// two rooms.
    /// </remarks>
    public sealed class ChatRoomIsolationTests
    {
        /// <summary>
        /// The registered service name. ONE name for every room - see <see cref="ChatClient.Join"/>.
        /// </summary>
        private const string Service = "*CHAT";

        /// <summary>
        /// One node, one name table, one chat service on it.
        /// </summary>
        private sealed class Node
        {
            internal Node(int seats)
            {
                Directory = new XroutDirectory();
                Kernel = new XmsgKernel(100, 0x1111, null);
                Server = new ChatServer(Kernel, Directory);

                Assert.Equal(XroutError.XRSOK, Server.Open(Service, seats, "welcome"));
            }

            internal XroutDirectory Directory { get; }

            internal XmsgKernel Kernel { get; }

            internal ChatServer Server { get; }

            /// <summary>
            /// Creates a client, joins it to a room, and settles the exchange.
            /// </summary>
            /// <param name="nickname">
            /// The name to join as.
            /// </param>
            /// <param name="room">
            /// The room to join.
            /// </param>
            /// <returns>
            /// The joined client.
            /// </returns>
            internal ChatClient Arrive(string nickname, string room)
            {
                ChatClient client = new ChatClient(Kernel, Directory, nickname);
                Assert.Equal(XroutError.XRSOK, client.Join(Service, room));
                Server.Poll();
                client.Poll();
                return client;
            }
        }

        /// <summary>
        /// Counts the messages of one kind whose text matches.
        /// </summary>
        /// <param name="messages">
        /// What a client's poll returned.
        /// </param>
        /// <param name="kind">
        /// The kind to count.
        /// </param>
        /// <param name="text">
        /// The text to match exactly.
        /// </param>
        /// <returns>
        /// How many matched.
        /// </returns>
        private static int Count(IReadOnlyList<ChatMessage> messages, ChatMessageKind kind, string text)
        {
            int found = 0;
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Kind == kind && string.Equals(messages[i].Text, text, StringComparison.Ordinal))
                {
                    found++;
                }
            }

            return found;
        }

        /// <summary>
        /// The baseline: two people in ONE room hear each other. Without this the isolation tests
        /// below would pass on a server that delivers nothing at all - which is exactly the bug
        /// they were written for.
        /// </summary>
        [Fact]
        public void TwoInTheSameRoomHearEachOther()
        {
            Node node = new Node(16);
            ChatClient olav = node.Arrive("OLAV", "LOBBY");
            ChatClient anna = node.Arrive("ANNA", "LOBBY");

            olav.Poll();
            anna.Poll();

            Assert.True(olav.Say("hello"));
            node.Server.Poll();

            Assert.Equal(1, Count(anna.Poll(), ChatMessageKind.Said, "hello"));
        }

        /// <summary>
        /// The speaker hears their own line back. That is their confirmation it left the machine,
        /// and it is what the PLANC server does - it broadcasts a Say to the whole room including
        /// the person who said it.
        /// </summary>
        [Fact]
        public void TheSpeakerHearsTheirOwnLine()
        {
            Node node = new Node(16);
            ChatClient olav = node.Arrive("OLAV", "LOBBY");
            olav.Poll();

            Assert.True(olav.Say("hello"));
            node.Server.Poll();

            Assert.Equal(1, Count(olav.Poll(), ChatMessageKind.Said, "hello"));
        }

        /// <summary>
        /// THE ONE THAT MATTERS: a line said in one room does not reach another.
        /// </summary>
        [Fact]
        public void ARoomDoesNotHearAnotherRoom()
        {
            Node node = new Node(16);
            ChatClient olav = node.Arrive("OLAV", "LOBBY");
            ChatClient anna = node.Arrive("ANNA", "GENERAL");

            olav.Poll();
            anna.Poll();

            Assert.True(olav.Say("lobby only"));
            node.Server.Poll();

            Assert.Equal(1, Count(olav.Poll(), ChatMessageKind.Said, "lobby only"));
            Assert.Equal(0, Count(anna.Poll(), ChatMessageKind.Said, "lobby only"));
        }

        /// <summary>
        /// And the other way round, so the test cannot pass because one room is simply deaf.
        /// </summary>
        [Fact]
        public void IsolationHoldsInBothDirections()
        {
            Node node = new Node(16);
            ChatClient olav = node.Arrive("OLAV", "LOBBY");
            ChatClient anna = node.Arrive("ANNA", "GENERAL");

            olav.Poll();
            anna.Poll();

            Assert.True(anna.Say("general only"));
            node.Server.Poll();

            Assert.Equal(1, Count(anna.Poll(), ChatMessageKind.Said, "general only"));
            Assert.Equal(0, Count(olav.Poll(), ChatMessageKind.Said, "lobby only"));
            Assert.Equal(0, Count(olav.Poll(), ChatMessageKind.Said, "general only"));
        }

        /// <summary>
        /// /who lists the asker's room only. On one port every member of every room is reachable
        /// from the same place, so this is a filter that has to be applied rather than a property
        /// that comes free.
        /// </summary>
        [Fact]
        public void WhoAnswersWithTheAskersRoomOnly()
        {
            Node node = new Node(16);
            ChatClient olav = node.Arrive("OLAV", "LOBBY");
            ChatClient ivar = node.Arrive("IVAR", "LOBBY");
            ChatClient anna = node.Arrive("ANNA", "GENERAL");

            olav.Poll();
            ivar.Poll();
            anna.Poll();

            Assert.True(olav.Who());
            node.Server.Poll();

            IReadOnlyList<ChatMessage> answer = olav.Poll();
            string list = string.Empty;
            for (int i = 0; i < answer.Count; i++)
            {
                if (answer[i].Kind == ChatMessageKind.Who)
                {
                    list = answer[i].Text;
                }
            }

            Assert.Contains("OLAV", list);
            Assert.Contains("IVAR", list);
            Assert.DoesNotContain("ANNA", list);
        }

        /// <summary>
        /// A Joined notice reaches the joiner's room and no other.
        /// </summary>
        [Fact]
        public void AJoinedNoticeStaysInItsRoom()
        {
            Node node = new Node(16);
            ChatClient olav = node.Arrive("OLAV", "LOBBY");
            ChatClient anna = node.Arrive("ANNA", "GENERAL");

            olav.Poll();
            anna.Poll();

            // A third arrival, in LOBBY. OLAV should be told; ANNA should not.
            node.Arrive("IVAR", "LOBBY");

            int toldOlav = 0;
            int toldAnna = 0;
            IReadOnlyList<ChatMessage> forOlav = olav.Poll();
            for (int i = 0; i < forOlav.Count; i++)
            {
                if (forOlav[i].Kind == ChatMessageKind.Joined && forOlav[i].Nickname == "IVAR")
                {
                    toldOlav++;
                }
            }

            IReadOnlyList<ChatMessage> forAnna = anna.Poll();
            for (int i = 0; i < forAnna.Count; i++)
            {
                if (forAnna[i].Kind == ChatMessageKind.Joined && forAnna[i].Nickname == "IVAR")
                {
                    toldAnna++;
                }
            }

            Assert.Equal(1, toldOlav);
            Assert.Equal(0, toldAnna);
        }

        /// <summary>
        /// A nickname is unique across the SERVER, not within a room - which is what the PLANC
        /// server does, its <c>findByName</c> walking one flat seat table. If the two disagreed, a
        /// name would mean different people on the two sides of a trunk.
        /// </summary>
        [Fact]
        public void ANicknameIsTakenEvenInAnotherRoom()
        {
            Node node = new Node(16);
            node.Arrive("OLAV", "LOBBY");

            ChatClient other = new ChatClient(node.Kernel, node.Directory, "OLAV");
            Assert.Equal(XroutError.XRSOK, other.Join(Service, "GENERAL"));
            node.Server.Poll();

            IReadOnlyList<ChatMessage> answer = other.Poll();
            int refused = 0;
            for (int i = 0; i < answer.Count; i++)
            {
                if (answer[i].Kind == ChatMessageKind.Reject)
                {
                    refused++;
                }
            }

            Assert.Equal(1, refused);
            Assert.False(other.IsJoined);
        }

        /// <summary>
        /// A Join naming no room lands in the default room, so a client that knows nothing about
        /// rooms still works. The PLANC falls back the same way in <c>copyRoomIn</c>.
        /// </summary>
        [Fact]
        public void AJoinWithNoRoomLandsInTheDefaultRoom()
        {
            Node node = new Node(16);
            ChatClient olav = node.Arrive("OLAV", string.Empty);
            ChatClient anna = node.Arrive("ANNA", ChatServer.DefaultRoom);

            olav.Poll();
            anna.Poll();

            Assert.True(olav.Say("still together"));
            node.Server.Poll();

            Assert.Equal(1, Count(anna.Poll(), ChatMessageKind.Said, "still together"));
        }

        /// <summary>
        /// Rooms come and go with their members. Nothing registers a room, so an empty one is not
        /// a room - the same as on the ND, which has no room objects at all.
        /// </summary>
        [Fact]
        public void ARoomExistsOnlyWhileSomebodyIsInIt()
        {
            Node node = new Node(16);
            Assert.Empty(node.Server.Rooms());

            ChatClient olav = node.Arrive("OLAV", "LOBBY");
            olav.Poll();
            Assert.Single(node.Server.Rooms());

            olav.Leave();
            node.Server.Poll();

            Assert.Empty(node.Server.Rooms());
        }

        /// <summary>
        /// The count that the live load run checks itself against: every line said in a room
        /// reaches everybody in that room, so the total is the sum over rooms of members squared
        /// times lines - never the square of the whole population.
        /// </summary>
        [Fact]
        public void TheTotalIsPerRoomNotPerServer()
        {
            Node node = new Node(16);
            ChatClient a = node.Arrive("AA", "ONE");
            ChatClient b = node.Arrive("BB", "ONE");
            ChatClient c = node.Arrive("CC", "TWO");

            a.Poll();
            b.Poll();
            c.Poll();

            Assert.True(a.Say("x"));
            Assert.True(b.Say("x"));
            Assert.True(c.Say("x"));
            node.Server.Poll();

            // Room ONE has two members saying one line each: 2 x 1 x 2 = 4 deliveries.
            // Room TWO has one member saying one line:      1 x 1 x 1 = 1 delivery.
            int heard = Count(a.Poll(), ChatMessageKind.Said, "x")
                + Count(b.Poll(), ChatMessageKind.Said, "x")
                + Count(c.Poll(), ChatMessageKind.Said, "x");

            Assert.Equal(5, heard);
        }
    }
}
