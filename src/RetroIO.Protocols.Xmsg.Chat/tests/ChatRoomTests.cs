//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Chat;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Chat.Tests
{
    /// <summary>
    /// Drives a chat room the way users would: join by name, talk, leave, and fill the room up.
    /// </summary>
    /// <remarks>
    /// These tests are as much about the XMSG model as about the chat code. The interesting
    /// assertions are the ones that show XROUT behaving like a letterbox: a client that is refused
    /// never learns the server's address, and the room's capacity is enforced before a join ever
    /// reaches the server.
    /// </remarks>
    public sealed class ChatRoomTests
    {
        /// <summary>
        /// The ordinary path: two users join, one speaks, both see it.
        /// </summary>
        [Fact]
        public void TwoUsers_JoinAndTalk()
        {
            Room room = new Room(seats: 4);

            ChatClient ronny = room.NewUser("RONNY");
            Assert.Equal(XroutError.XRSOK, ronny.Join(Room.Name));
            room.Server.Poll();

            IReadOnlyList<ChatMessage> welcome = ronny.Poll();
            Assert.Single(welcome);
            Assert.Equal(ChatMessageKind.Welcome, welcome[0].Kind);
            Assert.True(ronny.IsJoined);

            ChatClient anna = room.NewUser("ANNA");
            Assert.Equal(XroutError.XRSOK, anna.Join(Room.Name));
            room.Server.Poll();
            anna.Poll();

            // Ronny is told somebody arrived.
            IReadOnlyList<ChatMessage> notice = ronny.Poll();
            Assert.Single(notice);
            Assert.Equal(ChatMessageKind.Joined, notice[0].Kind);
            Assert.Equal("ANNA", notice[0].Nickname);

            Assert.True(anna.Say("god morgen"));
            room.Server.Poll();

            // Both hear it, including the speaker.
            AssertSaid(anna.Poll(), "ANNA", "god morgen");
            AssertSaid(ronny.Poll(), "ANNA", "god morgen");
        }

        /// <summary>
        /// A user who has not been welcomed cannot speak, because there is nowhere to speak to.
        /// </summary>
        [Fact]
        public void Say_BeforeWelcome_IsRefusedLocally()
        {
            Room room = new Room(seats: 2);
            ChatClient user = room.NewUser("EARLY");

            Assert.Equal(XroutError.XRSOK, user.Join(Room.Name));

            // The server has not run yet, so no welcome has come back.
            Assert.False(user.IsJoined);
            Assert.False(user.Say("anyone there?"));
        }

        /// <summary>
        /// The room's size is enforced by XROUT, before the server is involved at all.
        /// </summary>
        /// <remarks>
        /// This is the whole reason a chat service wants a connection port. The third join fails
        /// with XRNSP - no free service points - and the server never sees it.
        /// </remarks>
        [Fact]
        public void RoomFull_IsRefusedByXrout_NotByTheServer()
        {
            Room room = new Room(seats: 2);

            ChatClient first = room.NewUser("ONE");
            ChatClient second = room.NewUser("TWO");
            Assert.Equal(XroutError.XRSOK, first.Join(Room.Name));
            Assert.Equal(XroutError.XRSOK, second.Join(Room.Name));
            room.Server.Poll();
            first.Poll();
            second.Poll();
            Assert.Equal(2, room.Server.MemberCount);

            ChatClient third = room.NewUser("THREE");
            Assert.Equal(XroutError.XRNSP, third.Join(Room.Name));

            // Nothing reached the server, and the newcomer learned nothing about it.
            Assert.Equal(0, room.Server.Poll());
            Assert.Empty(third.Poll());
            Assert.False(third.IsJoined);
        }

        /// <summary>
        /// Leaving gives the seat back, so the next user can get in.
        /// </summary>
        [Fact]
        public void Leaving_ReturnsTheSeat()
        {
            Room room = new Room(seats: 1);

            ChatClient first = room.NewUser("FIRST");
            Assert.Equal(XroutError.XRSOK, first.Join(Room.Name));
            room.Server.Poll();
            first.Poll();

            ChatClient waiting = room.NewUser("SECOND");
            Assert.Equal(XroutError.XRNSP, waiting.Join(Room.Name));

            first.Leave();
            room.Server.Poll();
            Assert.Equal(0, room.Server.MemberCount);

            // The seat is back, so the same call now succeeds.
            Assert.Equal(XroutError.XRSOK, waiting.Join(Room.Name));
            room.Server.Poll();

            // A successful Join only means the letter was forwarded. The client is not in the room
            // until it collects the welcome - which is the distinction the API is meant to make
            // obvious, so the test has to honour it too.
            waiting.Poll();
            Assert.True(waiting.IsJoined);
        }

        /// <summary>
        /// Two users cannot hold the same nickname, and the refused one gets its seat back.
        /// </summary>
        [Fact]
        public void DuplicateNickname_IsRejectedAndTheSeatIsReturned()
        {
            Room room = new Room(seats: 2);

            ChatClient original = room.NewUser("RONNY");
            original.Join(Room.Name);
            room.Server.Poll();
            original.Poll();

            ChatClient impostor = room.NewUser("RONNY");
            Assert.Equal(XroutError.XRSOK, impostor.Join(Room.Name));
            room.Server.Poll();

            IReadOnlyList<ChatMessage> answer = impostor.Poll();
            Assert.Single(answer);
            Assert.Equal(ChatMessageKind.Reject, answer[0].Kind);
            Assert.False(impostor.IsJoined);
            Assert.Equal(1, room.Server.MemberCount);

            // The rejected join must not cost the room a seat permanently: a third user still fits.
            ChatClient third = room.NewUser("ANNA");
            Assert.Equal(XroutError.XRSOK, third.Join(Room.Name));
        }

        /// <summary>
        /// Leaving tells the others.
        /// </summary>
        [Fact]
        public void Leaving_IsAnnouncedToTheRoom()
        {
            Room room = new Room(seats: 4);

            ChatClient stays = room.NewUser("STAYS");
            ChatClient goes = room.NewUser("GOES");
            stays.Join(Room.Name);
            goes.Join(Room.Name);
            room.Server.Poll();
            stays.Poll();
            goes.Poll();

            goes.Leave();
            room.Server.Poll();

            IReadOnlyList<ChatMessage> notice = stays.Poll();
            Assert.Single(notice);
            Assert.Equal(ChatMessageKind.Left, notice[0].Kind);
            Assert.Equal("GOES", notice[0].Nickname);
        }

        /// <summary>
        /// A message from a port that never joined is ignored without a reply.
        /// </summary>
        /// <remarks>
        /// Answering would tell an unknown caller that this port is a chat server. Silence is the
        /// same thing XROUT does with a letter for a name it does not hold.
        /// </remarks>
        [Fact]
        public void SayFromAStranger_IsIgnored()
        {
            Room room = new Room(seats: 2);

            ChatClient member = room.NewUser("MEMBER");
            member.Join(Room.Name);
            room.Server.Poll();
            member.Poll();

            // A port that never joined, writing straight at the server.
            XmsgPortNumber strangerPort;
            room.Kernel.OpenPort(out strangerPort);
            XmsgMagicNumber serverMagic;
            room.Kernel.ConvertPortToMagic(room.ServerPort, out serverMagic);

            ChatMessage intrusion = new ChatMessage(ChatMessageKind.Say, "GHOST", "hello");
            byte[] bytes = new byte[intrusion.ByteCount];
            intrusion.Encode(bytes);

            XmsgMessageIdentifier buffer;
            room.Kernel.ReserveBuffer(bytes.Length, XmsgBufferOptions.None, out buffer);
            int written;
            room.Kernel.Write(buffer, bytes, 0, false, out written);
            room.Kernel.Send(serverMagic, strangerPort, XmsgSendFlags.None);

            room.Server.Poll();

            // The member heard nothing, and the room is unchanged.
            Assert.Empty(member.Poll());
            Assert.Equal(1, room.Server.MemberCount);
        }

        private static void AssertSaid(IReadOnlyList<ChatMessage> messages, string nickname, string text)
        {
            Assert.Single(messages);
            Assert.Equal(ChatMessageKind.Said, messages[0].Kind);
            Assert.Equal(nickname, messages[0].Nickname);
            Assert.Equal(text, messages[0].Text);
        }

        /// <summary>
        /// A room and the users in it, all on one system.
        /// </summary>
        private sealed class Room
        {
            internal const string Name = "*CHAT";

            internal Room(int seats)
            {
                Directory = new XroutDirectory();
                Kernel = new XmsgKernel(100, 0x1111, null);
                Server = new ChatServer(Kernel, Directory);

                XroutError opened = Server.Open(Name, seats, "welcome to the room");
                Assert.Equal(XroutError.XRSOK, opened);

                // The server's port is the first one opened on this kernel.
                ServerPort = new XmsgPortNumber(1);
            }

            internal XroutDirectory Directory { get; }

            internal XmsgKernel Kernel { get; }

            internal ChatServer Server { get; }

            internal XmsgPortNumber ServerPort { get; }

            internal ChatClient NewUser(string nickname)
            {
                return new ChatClient(Kernel, Directory, nickname);
            }
        }
    }
}
