using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Chat;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Chat.Tests
{
    /// <summary>
    /// Two C# chat servers on two machines, trunked, carrying each other's conversation.
    /// </summary>
    /// <remarks>
    /// <para><b>These pin behaviour that was first proved on real hardware</b></para>
    /// <para>
    /// D100 and D102 federated on 2026-08-22 running CHATSV.PLNC: a line typed on one appeared on
    /// the other qualified with its machine, and only D100 was ever told to trunk. Everything here
    /// checks the C# server does the same thing, because the two must interoperate.
    /// </para>
    /// <para><b>Real kernels, real datagrams, no transport</b></para>
    /// <para>
    /// The two kernels are wired to each other with a sink that calls the other's Deliver, which is
    /// exactly what a node layer does when a datagram arrives. Nothing here fakes the chat server
    /// or the message format - only the wire between the machines is a method call.
    /// </para>
    /// </remarks>
    public sealed class ChatFederationTests
    {
        /// <summary>
        /// One machine: its own system number, kernel, name table and chat server.
        /// </summary>
        private sealed class Machine
        {
            internal Machine(ushort systemNumber, ushort portSeed)
            {
                SystemNumber = systemNumber;
                Directory = new XroutDirectory();
                Kernel = new XmsgKernel(systemNumber, portSeed, null);
                Server = new ChatServer(Kernel, Directory);

                Assert.Equal(
                    XroutError.XRSOK,
                    Server.Open(ChatRooms.NameFor("LOBBY"), 8, "welcome"));
            }

            internal ushort SystemNumber { get; }

            internal XroutDirectory Directory { get; }

            internal XmsgKernel Kernel { get; }

            internal ChatServer Server { get; }

            internal ChatClient NewUser(string nickname)
            {
                return new ChatClient(Kernel, Directory, nickname);
            }
        }

        /// <summary>
        /// The wire between two machines: hands a datagram straight to the other kernel.
        /// </summary>
        /// <remarks>
        /// <para><b>Delivery is queued, not immediate</b></para>
        /// <para>
        /// The far kernel queues the message on the destination port, exactly as an arriving frame
        /// would. Nothing is seen until that side's server polls, which is what keeps the test
        /// honest about the order things happen in.
        /// </para>
        /// </remarks>
        private sealed class Wire : IXmsgDatagramSink
        {
            private readonly Dictionary<ushort, XmsgKernel> _machines = new Dictionary<ushort, XmsgKernel>();

            internal void Add(ushort systemNumber, XmsgKernel kernel)
            {
                _machines[systemNumber] = kernel;
            }

            public XmsgStatus Send(
                XmsgMagicNumber destination,
                XmsgMagicNumber sender,
                ReadOnlySpan<byte> userData,
                XmsgSendFlags flags)
            {
                XmsgKernel? far;
                if (!_machines.TryGetValue(destination.SystemNumber, out far))
                {
                    return XmsgStatus.Failure(XmsgError.XENOS);
                }

                return far.Deliver(destination, sender, userData, flags);
            }
        }

        /// <summary>
        /// Builds two machines wired to each other, and lets each accept the other's messages.
        /// </summary>
        /// <param name="left">
        /// Receives the first machine, system 100.
        /// </param>
        /// <param name="right">
        /// Receives the second machine, system 102.
        /// </param>
        /// <remarks>
        /// <para><b>The friend grant is not decoration</b></para>
        /// <para>
        /// A kernel refuses a message from a system it has not been told is a friend, which is the
        /// same rule the real machines enforce with DEFINE-FRIEND-SYSTEM. Leaving it out here would
        /// make every one of these tests fail for a reason that has nothing to do with chat.
        /// </para>
        /// </remarks>
        private static void TwoMachines(out Machine left, out Machine right)
        {
            left = new Machine(100, 0x1111);
            right = new Machine(102, 0x2222);

            Wire wire = new Wire();
            wire.Add(left.SystemNumber, left.Kernel);
            wire.Add(right.SystemNumber, right.Kernel);

            left.Kernel.AttachSink(wire);
            right.Kernel.AttachSink(wire);

            left.Kernel.DefineFriendSystem(right.SystemNumber, true);
            right.Kernel.DefineFriendSystem(left.SystemNumber, true);
        }

        /// <summary>
        /// Runs both servers until neither has anything left to read.
        /// </summary>
        /// <param name="left">
        /// The first machine.
        /// </param>
        /// <param name="right">
        /// The second machine.
        /// </param>
        /// <remarks>
        /// An exchange takes several passes - a greeting crosses, is answered, and the answer
        /// crosses back - so one poll each is not enough. A fixed number of rounds is used rather
        /// than looping until quiet, so a message that keeps bouncing shows up as a failing
        /// assertion instead of a test that never ends.
        /// </remarks>
        private static void Settle(Machine left, Machine right)
        {
            for (int round = 0; round < 8; round++)
            {
                left.Server.Poll();
                right.Server.Poll();
            }
        }

        private static ChatMessage? FirstOf(IReadOnlyList<ChatMessage> messages, ChatMessageKind kind)
        {
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Kind == kind) { return messages[i]; }
            }

            return null;
        }

        /// <summary>
        /// One side is enough: greeting a peer brings the trunk up at both ends.
        /// </summary>
        /// <remarks>
        /// <para><b>Proved live before it was written here</b></para>
        /// <para>
        /// D102 was never told to trunk. D100 greeted it, D102's server marked D100 up and answered,
        /// and D100 marked 102 up. That the untold side ends up believing in the trunk is the
        /// property being checked - not just that the greeting arrived.
        /// </para>
        /// </remarks>
        [Fact]
        public void GreetingAPeerBringsTheTrunkUpAtBothEnds()
        {
            Machine left, right;
            TwoMachines(out left, out right);

            Assert.True(left.Server.StartTrunk(right.SystemNumber, right.Server.Magic));

            Settle(left, right);

            Assert.Equal(ChatTrunkState.Up, left.Server.Trunks.StateOf(right.SystemNumber));
            Assert.Equal(ChatTrunkState.Up, right.Server.Trunks.StateOf(left.SystemNumber));
        }

        /// <summary>
        /// An answered greeting is not answered again.
        /// </summary>
        /// <remarks>
        /// <para><b>The direction byte is the whole reason this terminates</b></para>
        /// <para>
        /// Two servers that replied to every greeting would greet each other for ever, and on a
        /// real link that is not a slow test - it is two machines saturating a line with hellos.
        /// Settling for eight rounds and finding both sides quiet is what proves it stops.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheGreetingExchangeStopsAfterOneAnswer()
        {
            Machine left, right;
            TwoMachines(out left, out right);

            left.Server.StartTrunk(right.SystemNumber, right.Server.Magic);
            Settle(left, right);

            // Nothing is left queued anywhere. A hello still bouncing would show up here as a port
            // that still has something to read.
            Assert.Equal(0, left.Server.Poll());
            Assert.Equal(0, right.Server.Poll());
        }

        /// <summary>
        /// A line said on one machine appears on the other, qualified with the speaker's machine.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the product</b></para>
        /// <para>
        /// Two people on two machines in one room. The suffix is what makes the room usable - two
        /// people called SYSTEM on different machines are common, and without it they are one name
        /// saying contradictory things.
        /// </para>
        /// </remarks>
        [Fact]
        public void ALineSaidOnOneMachineArrivesOnTheOtherQualified()
        {
            Machine left, right;
            TwoMachines(out left, out right);

            left.Server.StartTrunk(right.SystemNumber, right.Server.Magic);
            Settle(left, right);

            ChatClient here = left.NewUser("ANNA");
            ChatClient there = right.NewUser("OLAV");

            Assert.Equal(XroutError.XRSOK, here.Join(ChatRooms.NameFor("LOBBY"), "LOBBY"));
            Assert.Equal(XroutError.XRSOK, there.Join(ChatRooms.NameFor("LOBBY"), "LOBBY"));
            Settle(left, right);

            here.Poll();
            there.Poll();

            here.Say("god morgen");
            Settle(left, right);

            IReadOnlyList<ChatMessage> heard = there.Poll();
            ChatMessage? said = FirstOf(heard, ChatMessageKind.Said);

            Assert.NotNull(said);
            Assert.Equal("ANNA@D100", said!.Value.Nickname);
            Assert.Equal("god morgen", said!.Value.Text);
        }

        /// <summary>
        /// A forwarded line is delivered and stops - it is never sent back out.
        /// </summary>
        /// <remarks>
        /// <para><b>Two machines is complete; three needs a hop count</b></para>
        /// <para>
        /// Relaying blindly would loop the moment a third machine appeared, and worse, a pair would
        /// echo one sentence between them for ever. The rule is deliberate and it is checked here
        /// rather than left to be discovered on a live link.
        /// </para>
        /// </remarks>
        [Fact]
        public void AForwardedLineIsNotForwardedOnward()
        {
            Machine left, right;
            TwoMachines(out left, out right);

            left.Server.StartTrunk(right.SystemNumber, right.Server.Magic);
            Settle(left, right);

            ChatClient here = left.NewUser("ANNA");
            ChatClient there = right.NewUser("OLAV");

            here.Join(ChatRooms.NameFor("LOBBY"), "LOBBY");
            there.Join(ChatRooms.NameFor("LOBBY"), "LOBBY");
            Settle(left, right);

            here.Poll();
            there.Poll();

            here.Say("ein gong");
            Settle(left, right);

            // The far side heard it exactly once, and nothing is still in flight.
            IReadOnlyList<ChatMessage> heard = there.Poll();

            int saids = 0;
            for (int i = 0; i < heard.Count; i++)
            {
                if (heard[i].Kind == ChatMessageKind.Said) { saids++; }
            }

            Assert.Equal(1, saids);
            Assert.Equal(0, left.Server.Poll());
            Assert.Equal(0, right.Server.Poll());
        }

        /// <summary>
        /// A line for a room nobody here is in is dropped rather than creating the room.
        /// </summary>
        /// <remarks>
        /// A peer that could conjure rooms into this node's table could fill it with rooms that
        /// have no members, and they would then be listed to everybody asking what rooms exist.
        /// </remarks>
        [Fact]
        public void ALineForAnUnknownRoomDoesNotCreateIt()
        {
            Machine left, right;
            TwoMachines(out left, out right);

            left.Server.StartTrunk(right.SystemNumber, right.Server.Magic);
            Settle(left, right);

            ChatClient here = left.NewUser("ANNA");
            here.Join(ChatRooms.NameFor("KANTINA"), "KANTINA");
            Settle(left, right);
            here.Poll();

            here.Say("er det nokon her");
            Settle(left, right);

            // 102 has no KANTINA, so it has nothing to deliver to and does not invent one.
            string[] roomsThere = right.Server.Rooms();
            for (int i = 0; i < roomsThere.Length; i++)
            {
                Assert.NotEqual("KANTINA", roomsThere[i]);
            }
        }

        /// <summary>
        /// A peer that goes quiet is declared down, and stops being written to.
        /// </summary>
        /// <remarks>
        /// <para><b>Sixty ticks, from an idle clock</b></para>
        /// <para>
        /// Measured on D100: stopping D102's server made the trunk say down in about fifty seconds.
        /// Once down, a line said here must not be sent to it - a dead machine would otherwise
        /// collect an ever-growing pile of chat that nobody will ever read.
        /// </para>
        /// </remarks>
        [Fact]
        public void APeerThatGoesQuietStopsBeingWrittenTo()
        {
            Machine left, right;
            TwoMachines(out left, out right);

            left.Server.StartTrunk(right.SystemNumber, right.Server.Magic);
            Settle(left, right);

            Assert.Equal(ChatTrunkState.Up, left.Server.Trunks.StateOf(right.SystemNumber));

            ChatClient here = left.NewUser("ANNA");
            ChatClient there = right.NewUser("OLAV");
            here.Join(ChatRooms.NameFor("LOBBY"), "LOBBY");
            there.Join(ChatRooms.NameFor("LOBBY"), "LOBBY");
            Settle(left, right);
            here.Poll();
            there.Poll();

            // Age it out WITHOUT letting the far side answer the greetings, which is what a stopped
            // server looks like from here.
            ChatTrunkTick tick = new ChatTrunkTick();
            for (int second = 0; second <= ChatTrunks.DeadAfterSeconds; second++)
            {
                left.Server.TickTrunks(tick);
            }

            Assert.Equal(ChatTrunkState.Down, left.Server.Trunks.StateOf(right.SystemNumber));

            // Drop whatever the greetings left on 102's port, so what follows is only about the say.
            right.Server.Poll();
            there.Poll();

            here.Say("er du der");
            Settle(left, right);

            Assert.Null(FirstOf(there.Poll(), ChatMessageKind.Said));
        }
    }
}
