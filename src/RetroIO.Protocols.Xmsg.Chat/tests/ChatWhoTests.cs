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
    /// The /who question and the room's answer.
    /// </summary>
    /// <remarks>
    /// <para><b>Where this came from</b></para>
    /// <para>
    /// A bug report on 2026-08-18: the PLANC client had no working /who. It had a handler, but the
    /// handler printed three lines saying the message format had no kind for it and that adding one
    /// would change both ends together. That was true, and these tests are the C# half of doing it:
    /// <see cref="ChatMessageKind.Who"/> = 11, sent by a member with an empty text and answered by
    /// the room with the names in the text.
    /// </para>
    /// <para><b>What is worth pinning, and what is not</b></para>
    /// <para>
    /// The separator and the empty name field are pinned here because the PLANC client parses
    /// exactly those - it prints the text after "CHAT: in (ROOM): " and does nothing else to it.
    /// The ORDER of the names is deliberately not pinned: it is whatever order the room holds its
    /// seats in, and nothing on either side depends on it.
    /// </para>
    /// </remarks>
    public sealed class ChatWhoTests
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

        private static bool Mentions(string list, string name)
        {
            string[] parts = list.Split(' ');
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.Equals(parts[i], name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The decoder's upper bound covers every kind the enum defines.
        /// </summary>
        /// <remarks>
        /// <para><b>This test exists because the same defect happened twice</b></para>
        /// <para>
        /// <c>ChatMessage.TryDecode</c> rejects a kind above the last one defined. That bound was
        /// written out by hand and left behind when <c>Rename</c>/<c>Renamed</c> were added, and
        /// again when <c>Who</c> was. Both times the new kind decoded as a malformed message and
        /// was dropped without a word - the only symptom was a feature that did nothing.
        /// </para>
        /// <para>
        /// Reflection is fine here: this runs once, in a test, and reading the enum itself is the
        /// entire point - a hand-written list would be the same defect wearing a different hat.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheDecoderBoundCoversEveryKind()
        {
            Array values = Enum.GetValues(typeof(ChatMessageKind));

            byte highest = 0;
            for (int i = 0; i < values.Length; i++)
            {
                byte value = (byte)(ChatMessageKind)values.GetValue(i)!;
                if (value > highest) { highest = value; }
            }

            Assert.Equal(ChatMessageKinds.Highest, highest);
        }

        /// <summary>
        /// Every kind the enum defines survives a round trip through the wire format.
        /// </summary>
        /// <remarks>
        /// The bound above is the mechanism; this is the behaviour it protects. A kind that cannot
        /// be decoded is a kind that silently does not exist between two machines.
        /// </remarks>
        [Fact]
        public void EveryKindSurvivesTheWireFormat()
        {
            Array values = Enum.GetValues(typeof(ChatMessageKind));

            for (int i = 0; i < values.Length; i++)
            {
                ChatMessageKind kind = (ChatMessageKind)values.GetValue(i)!;
                if (kind == ChatMessageKind.None)
                {
                    // None is rejected on purpose: it is what a zeroed buffer looks like.
                    continue;
                }

                byte[] buffer = new byte[64];
                int length = new ChatMessage(kind, "ANNA", "hello").Encode(buffer);

                ChatMessage back;
                Assert.True(ChatMessage.TryDecode(new ReadOnlySpan<byte>(buffer, 0, length), out back),
                    kind.ToString());
                Assert.Equal(kind, back.Kind);
                Assert.Equal("ANNA", back.Nickname);
                Assert.Equal("hello", back.Text);
            }
        }

        /// <summary>
        /// The room answers with every member's name, separated by single spaces.
        /// </summary>
        [Fact]
        public void TheRoomAnswersWithEveryMember()
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

            Assert.True(ronny.Who());
            server.Poll();

            ChatMessage answer = Only(ronny.Poll(), ChatMessageKind.Who);

            Assert.True(Mentions(answer.Text, "RONNY"), answer.Text);
            Assert.True(Mentions(answer.Text, "ANNA"), answer.Text);
            Assert.Equal(2, answer.Text.Split(' ').Length);
        }

        /// <summary>
        /// The answer's name field is EMPTY: the room is answering, not a member.
        /// </summary>
        /// <remarks>
        /// The PLANC client relies on this. It prints an ordinary arrival as "$NAME: text", and
        /// gives the Who answer its own branch precisely because there is no name to put there -
        /// the generic printer would open the line with a bare colon.
        /// </remarks>
        [Fact]
        public void TheAnswerCarriesNoSenderName()
        {
            Node node = new Node();
            ChatServer server = node.OpenRoom("LOBBY", 4);

            ChatClient ronny = node.NewUser("RONNY");
            ronny.Join(ChatRooms.NameFor("LOBBY"));
            server.Poll();
            ronny.Poll();

            ronny.Who();
            server.Poll();

            ChatMessage answer = Only(ronny.Poll(), ChatMessageKind.Who);
            Assert.Equal(string.Empty, answer.Nickname);
            Assert.Equal("RONNY", answer.Text);
        }

        /// <summary>
        /// Only the asker gets the answer - it is not broadcast.
        /// </summary>
        /// <remarks>
        /// Broadcasting it would put an unasked-for list on everybody's screen and spend one of the
        /// ten data transmit blocks per member, which is the resource measured to run out first.
        /// </remarks>
        [Fact]
        public void OnlyTheAskerIsAnswered()
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

            ronny.Who();
            server.Poll();

            Only(ronny.Poll(), ChatMessageKind.Who);

            IReadOnlyList<ChatMessage> annaHeard = anna.Poll();
            for (int i = 0; i < annaHeard.Count; i++)
            {
                Assert.NotEqual(ChatMessageKind.Who, annaHeard[i].Kind);
            }
        }

        /// <summary>
        /// A port that never joined is answered with silence.
        /// </summary>
        /// <remarks>
        /// Same rule as Say: replying would confirm to something that never joined that this port
        /// is a chat room, and would hand it the membership list as well.
        /// </remarks>
        [Fact]
        public void AStrangerIsNotAnswered()
        {
            Node node = new Node();
            ChatServer server = node.OpenRoom("LOBBY", 4);

            ChatClient ronny = node.NewUser("RONNY");
            ronny.Join(ChatRooms.NameFor("LOBBY"));
            server.Poll();
            ronny.Poll();

            // Never joined, so Who refuses to send anything at all - the client knows it is not in
            // a room. That is the first of the two guards; the server's is the second.
            ChatClient stranger = node.NewUser("NOBODY");
            Assert.False(stranger.Who());

            server.Poll();
            Assert.Empty(stranger.Poll());
        }

        /// <summary>
        /// After a rename, /who reports the NEW name.
        /// </summary>
        /// <remarks>
        /// The list comes from the room's own record of who is seated, so it cannot drift from what
        /// a Said is attributed to. This pins that they stay the same thing.
        /// </remarks>
        [Fact]
        public void TheListFollowsARename()
        {
            Node node = new Node();
            ChatServer server = node.OpenRoom("LOBBY", 4);

            ChatClient anna = node.NewUser("ANNA");
            anna.Join(ChatRooms.NameFor("LOBBY"));
            server.Poll();
            anna.Poll();

            Assert.True(anna.Rename("ANNIKA"));
            server.Poll();
            anna.Poll();

            anna.Who();
            server.Poll();

            ChatMessage answer = Only(anna.Poll(), ChatMessageKind.Who);
            Assert.Equal("ANNIKA", answer.Text);
        }
    }
}
