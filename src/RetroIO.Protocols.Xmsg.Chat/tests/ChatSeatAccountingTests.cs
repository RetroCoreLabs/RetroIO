//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Chat;
using Xunit;

namespace RetroIO.Protocols.Xmsg.Chat.Tests
{
    /// <summary>
    /// Every seat XROUT hands out is either sat in or given back.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this file exists separately</b></para>
    /// <para>
    /// A seat is spent by XROUT when it FORWARDS a letter, before the room has seen a single byte
    /// of what is inside it. So the accounting is not a property of joining - it is a property of
    /// every letter that arrives, whatever it turns out to say. The other chat tests all drive the
    /// server through <see cref="ChatClient"/>, which only ever sends a Join as a letter, so none
    /// of them can see a letter carrying anything else.
    /// </para>
    /// <para><b>The failure this guards against is silent and late</b></para>
    /// <para>
    /// A leaked seat costs nothing at the time: no error, no log line, nobody refused. The room
    /// simply becomes one seat smaller forever. It surfaces much later as XROUT turning joins away
    /// from a room that visibly has space, which is a fault nobody would connect to a message sent
    /// hours earlier - so it is worth pinning rather than reasoning about.
    /// </para>
    /// </remarks>
    public sealed class ChatSeatAccountingTests
    {
        private const int Seats = 4;

        /// <summary>
        /// A letter carrying something other than a Join still hands its seat back.
        /// </summary>
        /// <remarks>
        /// The hand-written PLANC client sends its Join as a letter; nothing stops it, a bug, or a
        /// probe from putting a different kind in one. The room ignores the message - correctly -
        /// but ignoring it must not also mean keeping the seat.
        /// </remarks>
        [Fact]
        public void ALetterThatIsNotAJoinGivesItsSeatBack()
        {
            Fixture fixture = new Fixture(Seats);

            // A Say from somebody who never joined: the room has nothing to do with it.
            fixture.SendLetter(new ChatMessage(ChatMessageKind.Say, "NOBODY", "hello?"));
            fixture.Server.Poll();

            Assert.Equal(0, fixture.Server.MemberCount);
            Assert.Equal(Seats, fixture.FreeSeats);
        }

        /// <summary>
        /// A letter the room cannot even decode still hands its seat back.
        /// </summary>
        /// <remarks>
        /// This is the worst case for the accounting, because the body never reaches a handler at
        /// all: <see cref="ChatServer.Poll"/> drops an undecodable message on purpose so that one
        /// confused client cannot stop the room. Dropping the message must not drop the seat.
        /// </remarks>
        [Fact]
        public void AnUndecodableLetterGivesItsSeatBack()
        {
            Fixture fixture = new Fixture(Seats);

            // Not a chat message in any version: a kind byte that has never been assigned.
            fixture.SendRaw(new byte[] { 0xEE, 0x01, 0x41 });
            fixture.Server.Poll();

            Assert.Equal(0, fixture.Server.MemberCount);
            Assert.Equal(Seats, fixture.FreeSeats);
        }

        /// <summary>
        /// Repeating a stray letter does not grind the room down to nothing.
        /// </summary>
        /// <remarks>
        /// The point of the loop is that the leak, if it came back, would be CUMULATIVE. A single
        /// stray letter costing one seat is easy to overlook; the same letter arriving in a retry
        /// loop closes the room to everybody, and that is the shape the fault actually took.
        /// </remarks>
        [Fact]
        public void ManyStrayLettersDoNotCloseTheRoom()
        {
            Fixture fixture = new Fixture(Seats);

            for (int i = 0; i < Seats * 5; i++)
            {
                fixture.SendLetter(new ChatMessage(ChatMessageKind.Leave, "NOBODY", string.Empty));
                fixture.Server.Poll();
            }

            Assert.Equal(Seats, fixture.FreeSeats);

            // And the room still works afterwards, which is the thing that actually matters.
            ChatClient ronny = fixture.NewUser("RONNY");
            Assert.Equal(XroutError.XRSOK, ronny.Join(ChatRooms.NameFor(Fixture.Room)));
            fixture.Server.Poll();

            Assert.Equal(1, fixture.Server.MemberCount);
        }

        /// <summary>
        /// A join that is refused hands its seat back, and one that is accepted keeps it.
        /// </summary>
        /// <remarks>
        /// The other half of the same rule, pinned here so both halves sit together: a seat is kept
        /// only by somebody who is actually in the room.
        /// </remarks>
        [Fact]
        public void ARefusedJoinGivesItsSeatBackAndAnAcceptedOneKeepsIt()
        {
            Fixture fixture = new Fixture(Seats);

            ChatClient ronny = fixture.NewUser("RONNY");
            ronny.Join(ChatRooms.NameFor(Fixture.Room));
            fixture.Server.Poll();

            Assert.Equal(1, fixture.Server.MemberCount);
            Assert.Equal(Seats - 1, fixture.FreeSeats);

            // Same nickname: refused, and the seat comes back.
            ChatClient impostor = fixture.NewUser("RONNY");
            impostor.Join(ChatRooms.NameFor(Fixture.Room));
            fixture.Server.Poll();

            Assert.Equal(1, fixture.Server.MemberCount);
            Assert.Equal(Seats - 1, fixture.FreeSeats);
        }

        /// <summary>
        /// One node, one room, and a way to post a letter into it by hand.
        /// </summary>
        private sealed class Fixture
        {
            internal const string Room = "LOBBY";

            private readonly XroutDirectory _directory;
            private readonly XmsgKernel _kernel;
            private readonly XmsgMagicNumber _strangerMagic;

            internal Fixture(int seats)
            {
                _directory = new XroutDirectory();
                _kernel = new XmsgKernel(100, 0x1111, null);

                Server = new ChatServer(_kernel, _directory);
                Assert.Equal(
                    XroutError.XRSOK,
                    Server.Open(ChatRooms.NameFor(Room), seats, "welcome"));

                // A port that never joins anything - it exists only to be a return address.
                XmsgPortNumber port;
                Assert.False(_kernel.OpenPort(out port).IsError);
                Assert.False(_kernel.ConvertPortToMagic(port, out _strangerMagic).IsError);
            }

            internal ChatServer Server { get; }

            internal int FreeSeats
            {
                get { return _directory.FreeConnections(ChatRooms.NameFor(Room)); }
            }

            internal ChatClient NewUser(string nickname)
            {
                return new ChatClient(_kernel, _directory, nickname);
            }

            internal void SendLetter(ChatMessage message)
            {
                byte[] bytes = new byte[message.ByteCount];
                message.Encode(bytes);
                SendRaw(bytes);
            }

            internal void SendRaw(byte[] body)
            {
                // Straight through XROUT, exactly as a Join arrives - which is the point: XROUT
                // spends the seat here, without looking at the body at all.
                Assert.Equal(
                    XroutError.XRSOK,
                    _directory.SendLetter(ChatRooms.NameFor(Room), _strangerMagic, body));
            }
        }
    }
}
