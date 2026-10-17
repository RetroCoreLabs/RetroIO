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
    /// Aliases (a member changing the name the room knows them by) and channels (more than one
    /// room on a node, and finding out which).
    /// </summary>
    public sealed class ChatAliasAndChannelTests
    {
        /// <summary>
        /// One node, one name table, as many rooms as are opened on it.
        /// </summary>
        private sealed class Node
        {
            internal Node()
            {
                Directory = new XroutDirectory();
                Kernel = new XmsgKernel(100, 0x1111, null);
            }

            internal XroutDirectory Directory { get; }

            internal XmsgKernel Kernel { get; }

            internal ChatServer OpenRoom(string room, int seats)
            {
                ChatServer server = new ChatServer(Kernel, Directory);
                Assert.Equal(
                    XroutError.XRSOK,
                    server.Open(ChatRooms.NameFor(room), seats, "welcome to " + room));
                return server;
            }

            internal ChatClient NewUser(string nickname)
            {
                return new ChatClient(Kernel, Directory, nickname);
            }
        }

        private static ChatMessage Only(IReadOnlyList<ChatMessage> messages, ChatMessageKind kind)
        {
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Kind == kind)
                {
                    return messages[i];
                }
            }

            Assert.Fail("no " + kind + " message arrived");
            return default!;
        }

        /// <summary>
        /// A rename is announced to the whole room, with both names.
        /// </summary>
        /// <remarks>
        /// The old name matters as much as the new one: everybody has a transcript on screen under
        /// the old name and no way to connect the two without it.
        /// </remarks>
        [Fact]
        public void ARenameIsAnnouncedWithBothNames()
        {
            Node node = new Node();
            ChatServer server = node.OpenRoom("LOBBY", 4);

            ChatClient ronny = node.NewUser("RONNY");
            ronny.Join(ChatRooms.NameFor("LOBBY"));
            server.Poll();
            ronny.Poll();

            ChatClient anna = node.NewUser("ANNA");
            anna.Join(ChatRooms.NameFor("LOBBY"));
            server.Poll();
            anna.Poll();
            ronny.Poll();

            Assert.True(anna.Rename("ANNIKA"));
            server.Poll();

            ChatMessage seenByRonny = Only(ronny.Poll(), ChatMessageKind.Renamed);
            Assert.Equal("ANNIKA", seenByRonny.Nickname);
            Assert.Equal("ANNA", seenByRonny.Text);

            // The asker hears it too - that is the confirmation.
            Only(anna.Poll(), ChatMessageKind.Renamed);
            Assert.Equal("ANNIKA", anna.Nickname);
        }

        /// <summary>
        /// After a rename, what the member says is attributed to the NEW name.
        /// </summary>
        [Fact]
        public void AfterARenameTheNewNameIsUsed()
        {
            Node node = new Node();
            ChatServer server = node.OpenRoom("LOBBY", 4);

            ChatClient anna = node.NewUser("ANNA");
            anna.Join(ChatRooms.NameFor("LOBBY"));
            server.Poll();
            anna.Poll();

            anna.Rename("ANNIKA");
            server.Poll();
            anna.Poll();

            anna.Say("still me");
            server.Poll();

            ChatMessage said = Only(anna.Poll(), ChatMessageKind.Said);
            Assert.Equal("ANNIKA", said.Nickname);
        }

        /// <summary>
        /// A name somebody else already answers to is refused, and nothing changes.
        /// </summary>
        [Fact]
        public void ATakenNameIsRefusedAndTheOldOneStands()
        {
            Node node = new Node();
            ChatServer server = node.OpenRoom("LOBBY", 4);

            ChatClient ronny = node.NewUser("RONNY");
            ronny.Join(ChatRooms.NameFor("LOBBY"));
            server.Poll();
            ronny.Poll();

            ChatClient anna = node.NewUser("ANNA");
            anna.Join(ChatRooms.NameFor("LOBBY"));
            server.Poll();
            anna.Poll();

            anna.Rename("RONNY");
            server.Poll();

            ChatMessage reject = Only(anna.Poll(), ChatMessageKind.Reject);
            Assert.Equal("RONNY", reject.Nickname);
            Assert.Equal("ANNA", anna.Nickname);
        }

        /// <summary>
        /// A refused rename does NOT hand back a seat.
        /// </summary>
        /// <remarks>
        /// Every other refusal in the server returns a seat, because every other refusal happens to
        /// somebody who is not in the room. A rename happens to a member who still is. Copying the
        /// pattern would let the room admit one person more than it holds, and the fault would
        /// surface much later as XROUT refusing joins for no visible reason.
        /// </remarks>
        [Fact]
        public void ARefusedRenameDoesNotReturnASeat()
        {
            Node node = new Node();
            ChatServer server = node.OpenRoom("LOBBY", 2);

            ChatClient ronny = node.NewUser("RONNY");
            ronny.Join(ChatRooms.NameFor("LOBBY"));
            server.Poll();
            ronny.Poll();

            ChatClient anna = node.NewUser("ANNA");
            anna.Join(ChatRooms.NameFor("LOBBY"));
            server.Poll();
            anna.Poll();

            int before = node.Directory.FreeConnections(ChatRooms.NameFor("LOBBY"));

            anna.Rename("RONNY");
            server.Poll();
            anna.Poll();

            Assert.Equal(before, node.Directory.FreeConnections(ChatRooms.NameFor("LOBBY")));
        }

        /// <summary>
        /// Asking for the name you already have is quietly ignored.
        /// </summary>
        [Fact]
        public void RenamingToTheSameNameSaysNothing()
        {
            Node node = new Node();
            ChatServer server = node.OpenRoom("LOBBY", 4);

            ChatClient anna = node.NewUser("ANNA");
            anna.Join(ChatRooms.NameFor("LOBBY"));
            server.Poll();
            anna.Poll();

            anna.Rename("ANNA");
            server.Poll();

            Assert.Empty(anna.Poll());
        }

        /// <summary>
        /// Somebody who never joined cannot rename anybody.
        /// </summary>
        [Fact]
        public void AStrangerCannotRename()
        {
            Node node = new Node();
            ChatServer server = node.OpenRoom("LOBBY", 4);

            ChatClient inside = node.NewUser("RONNY");
            inside.Join(ChatRooms.NameFor("LOBBY"));
            server.Poll();
            inside.Poll();

            ChatClient outside = node.NewUser("NOBODY");

            // Never joined, so there is nothing to rename and nothing to answer.
            Assert.False(outside.Rename("RONNY"));
            server.Poll();

            Assert.Empty(inside.Poll());
        }

        /// <summary>
        /// Several rooms live on one node and are listed with their free seats.
        /// </summary>
        [Fact]
        public void RoomsAreListedWithTheirFreeSeats()
        {
            Node node = new Node();
            node.OpenRoom("LOBBY", 4);
            node.OpenRoom("NORSK", 2);

            IReadOnlyList<XroutNameEntry> rooms = ChatRooms.List(node.Directory);

            Assert.Equal(2, rooms.Count);

            int lobby = -1;
            int norsk = -1;
            for (int i = 0; i < rooms.Count; i++)
            {
                if (rooms[i].Name == "LOBBY") { lobby = i; }
                if (rooms[i].Name == "NORSK") { norsk = i; }
            }

            Assert.True(lobby >= 0, "LOBBY was not listed");
            Assert.True(norsk >= 0, "NORSK was not listed");
            Assert.Equal(4, rooms[lobby].FreeConnections);
            Assert.Equal(2, rooms[norsk].FreeConnections);
            Assert.True(rooms[lobby].HasRoom);
        }

        /// <summary>
        /// The listing follows the seats as people join, so a full room shows as full.
        /// </summary>
        [Fact]
        public void AFullRoomIsListedWithNoRoomLeft()
        {
            Node node = new Node();
            ChatServer server = node.OpenRoom("SMALL", 1);

            ChatClient ronny = node.NewUser("RONNY");
            ronny.Join(ChatRooms.NameFor("SMALL"));
            server.Poll();
            ronny.Poll();

            IReadOnlyList<XroutNameEntry> rooms = ChatRooms.List(node.Directory);

            Assert.Single(rooms);
            Assert.Equal(0, rooms[0].FreeConnections);
            Assert.False(rooms[0].HasRoom);
        }

        /// <summary>
        /// Services that are not chat rooms are not listed as rooms.
        /// </summary>
        /// <remarks>
        /// The name table holds every server on the node. A menu of rooms that offered the file
        /// server as somewhere to talk would be worse than no menu.
        /// </remarks>
        [Fact]
        public void NonChatServicesAreNotListedAsRooms()
        {
            Node node = new Node();
            node.OpenRoom("LOBBY", 4);

            // Something else on the same node, registered the way any other server would be.
            ChatServer notARoom = new ChatServer(node.Kernel, node.Directory);
            Assert.Equal(XroutError.XRSOK, notARoom.Open("*FA-SERVER", 30, "files"));

            IReadOnlyList<XroutNameEntry> rooms = ChatRooms.List(node.Directory);

            Assert.Single(rooms);
            Assert.Equal("LOBBY", rooms[0].Name);
        }

        /// <summary>
        /// EVERY message kind survives being encoded and decoded.
        /// </summary>
        /// <remarks>
        /// Written after adding Rename and Renamed, because the decoder rejected anything above the
        /// kind that used to be last. The effect was silence: a rename decoded as malformed and was
        /// dropped, so the server never saw the request and the room never heard the answer, with
        /// nothing failing loudly anywhere. This test fails the moment a kind is added and the
        /// bound is not moved with it.
        /// </remarks>
        [Theory]
        [InlineData(ChatMessageKind.Join)]
        [InlineData(ChatMessageKind.Welcome)]
        [InlineData(ChatMessageKind.Reject)]
        [InlineData(ChatMessageKind.Say)]
        [InlineData(ChatMessageKind.Said)]
        [InlineData(ChatMessageKind.Leave)]
        [InlineData(ChatMessageKind.Joined)]
        [InlineData(ChatMessageKind.Left)]
        [InlineData(ChatMessageKind.Rename)]
        [InlineData(ChatMessageKind.Renamed)]
        public void EveryKindSurvivesARoundTrip(ChatMessageKind kind)
        {
            ChatMessage original = new ChatMessage(kind, "RONNY", "hei pa deg");

            byte[] buffer = new byte[256];
            int written = original.Encode(buffer);

            ChatMessage decoded;
            Assert.True(
                ChatMessage.TryDecode(new ReadOnlySpan<byte>(buffer, 0, written), out decoded),
                kind + " did not decode - has the bound in TryDecode been moved to the last kind?");

            Assert.Equal(kind, decoded.Kind);
            Assert.Equal("RONNY", decoded.Nickname);
            Assert.Equal("hei pa deg", decoded.Text);
        }
    }
}
