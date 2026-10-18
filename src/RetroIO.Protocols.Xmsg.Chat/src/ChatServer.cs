//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;

namespace RetroIO.Protocols.Xmsg.Chat
{
    /// <summary>
    /// A named chat service: claims a name with a number of seats, admits users who write to that
    /// name, and relays what they say to everybody in the room.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a connection port and not a plain name</b></para>
    /// A chat room has a size, and a connection port is the mechanism SINTRAN already has for that:
    /// XROUT keeps a free-connection count, forwards a join only while it is above zero, and
    /// decrements it each time. So "the room is full" is enforced by the system before the join
    /// ever reaches this class. That behaviour was captured from a running machine - the real file
    /// access server builds its 30 seats exactly this way.
    /// <para><b>Why the seat count is built one at a time</b></para>
    /// Servers on the real machine register with ZERO seats and then add them individually. This
    /// class does the same through <see cref="XroutDirectory"/>, so the count it presents to XROUT
    /// is built the way the observed servers build theirs rather than declared up front.
    /// <para><b>What travels through XROUT, and what does not</b></para>
    /// Only the join. XROUT forwards it and steps aside; the server learns the newcomer's address
    /// from the arrived message and answers it directly. Everything after that - every line said,
    /// every notice - goes port to port with no name lookup at all.
    /// </remarks>
    public sealed class ChatServer
    {
        private readonly XmsgKernel _kernel;
        private readonly XroutDirectory _directory;

        // THE RULES LIVE IN ChatRoom, not here. Who is in a room, which names are free and who
        // must be told what are identical for a port conversation and for somebody typing at a
        // SINTRAN terminal; only the plumbing differs. Written twice they would drift, and the
        // drift would be in the awkward cases.
        //
        // ONE PORT, MANY ROOMS - and it used to be one room per server, which meant one room per
        // registered NAME. That model is retired on both sides. CHATSV.PLNC now opens the single
        // name *CHAT and carries the room in the Join's text field, because port-per-room made the
        // set of rooms XROUT's private data with no way to read it, fragmented the seats, and
        // turned a federated room into a name collision.
        //
        // A room exists only while somebody is in it. There is no create and no delete, which is
        // also what the PLANC does - it has no room objects at all, just a room NAME stored beside
        // each seat.
        private readonly Dictionary<string, ChatRoom> _rooms;

        // Which room each member is in, keyed by the same handle the rooms use. This is the C#
        // equivalent of the PLANC's mbrRoom(seat) - the room name stored beside the seat.
        private readonly Dictionary<long, string> _roomOf;

        // The magic number each member is reachable at, keyed by the same handle the room uses.
        // The room deliberately knows nothing about addresses.
        private readonly Dictionary<long, XmsgMagicNumber> _addresses;

        // What this node believes about each peer MACHINE, as opposed to each member. The rules
        // live in ChatTrunks for the same reason the room rules live in ChatRoom: CHATSV.PLNC keeps
        // the identical table, and two implementations of one protocol only interoperate if they
        // agree about STATE as well as about bytes.
        private readonly ChatTrunks _trunks;

        // Where a peer machine is reachable, keyed by its system number. A trunk has no session to
        // hold, so this is just the return address of the last thing that arrived from it - which
        // is also why a peer that moves ports simply starts working again on its next greeting.
        private readonly Dictionary<int, XmsgMagicNumber> _peerAddresses;

        private readonly byte[] _scratch;

        private XmsgPortNumber _port;
        private XmsgMagicNumber _magic;
        private string _name;
        private string _greeting;
        private int _seats;

        /// <summary>
        /// Initialises a chat server on a kernel and a name table.
        /// </summary>
        /// <param name="kernel">
        /// The kernel the server's port belongs to.
        /// </param>
        /// <param name="directory">
        /// The XROUT stand-in to register the name with.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when either argument is null.
        /// </exception>
        public ChatServer(XmsgKernel kernel, XroutDirectory directory)
        {
            if (kernel == null)
            {
                throw new ArgumentNullException(nameof(kernel));
            }

            if (directory == null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            _kernel = kernel;
            _directory = directory;

            // Ordinal-ignore-case, because the PLANC compares room names byte by byte after the
            // client has upper-cased what was typed. Matching that here means LOBBY and lobby are
            // one room on both sides rather than two on one side and one on the other.
            _rooms = new Dictionary<string, ChatRoom>(StringComparer.OrdinalIgnoreCase);
            _roomOf = new Dictionary<long, string>();
            _addresses = new Dictionary<long, XmsgMagicNumber>();

            // The kernel already knows which machine it is, so the trunk table takes its own number
            // from there rather than being told separately. Two sources for one fact is how a node
            // ends up enrolling ITSELF, which is a defect we have already had once on the ND.
            _trunks = new ChatTrunks(kernel.SystemNumber);
            _peerAddresses = new Dictionary<int, XmsgMagicNumber>();

            _scratch = new byte[ChatMessageBufferSize];
            _name = string.Empty;
            _greeting = string.Empty;
        }

        /// <summary>
        /// The receive buffer size. Generous for a line of chat, and the reason a nickname and text
        /// are length-prefixed rather than unbounded.
        /// </summary>
        private const int ChatMessageBufferSize = 1024;

        /// <summary>
        /// Gets the registered name, or an empty string before <see cref="Open"/>.
        /// </summary>
        public string Name
        {
            get { return _name; }
        }

        /// <summary>
        /// The room a Join with no room named goes to.
        /// </summary>
        /// <remarks>
        /// The same fallback <c>CHATSV.PLNC</c> applies in <c>copyRoomIn</c>: a Join whose text
        /// field is empty lands in LOBBY rather than being refused. A client that knows nothing
        /// about rooms therefore still works, which is what keeps the room a feature rather than a
        /// new thing every client must learn.
        /// </remarks>
        public const string DefaultRoom = "LOBBY";

        /// <summary>
        /// The longest room name the server will accept.
        /// </summary>
        /// <remarks>
        /// SIXTEEN, for the same reason <see cref="ChatRoom.MaxNicknameLength"/> is sixteen: the
        /// PLANC stores the room beside each seat in <c>mbrRoom(1:16, 1:16)</c>, and PLANC checks
        /// no array bound. A longer name there writes through the neighbouring seats.
        /// </remarks>
        public const int MaxRoomNameLength = 16;

        /// <summary>
        /// Gets the number of users on the server, counting every room.
        /// </summary>
        /// <remarks>
        /// Across ALL rooms on purpose. The seats are a property of the one port, not of a room -
        /// XROUT's free-connection count is what turns people away, and it knows nothing about
        /// rooms. So the number that can be compared against the seat count is this one.
        /// </remarks>
        public int MemberCount
        {
            get
            {
                int total = 0;
                foreach (KeyValuePair<string, ChatRoom> entry in _rooms)
                {
                    total += entry.Value.Count;
                }

                return total;
            }
        }

        /// <summary>
        /// Gets the names of the rooms that currently have somebody in them.
        /// </summary>
        /// <returns>
        /// A fresh array; the caller may keep it.
        /// </returns>
        /// <remarks>
        /// An empty room is not a room. Nothing registers or reserves a room name, so a room comes
        /// into existence when the first person joins it and stops existing when the last one
        /// leaves - which is exactly what the PLANC does, having no room objects at all.
        /// </remarks>
        public string[] Rooms()
        {
            string[] names = new string[_rooms.Count];
            int at = 0;
            foreach (KeyValuePair<string, ChatRoom> entry in _rooms)
            {
                names[at] = entry.Key;
                at++;
            }

            return names;
        }

        /// <summary>
        /// Gets the nicknames on the server, every room together, in no particular order.
        /// </summary>
        /// <returns>
        /// A fresh array; the caller may keep it.
        /// </returns>
        /// <remarks>
        /// For a per-room list - which is what a member asking <c>/who</c> gets - use
        /// <see cref="MembersOf"/>. This one exists for an operator looking at the whole server.
        /// </remarks>
        public string[] Members()
        {
            List<string> all = new List<string>();
            foreach (KeyValuePair<string, ChatRoom> entry in _rooms)
            {
                string[] names = entry.Value.CopyNicknames();
                for (int i = 0; i < names.Length; i++)
                {
                    all.Add(names[i]);
                }
            }

            return all.ToArray();
        }

        /// <summary>
        /// Gets the nicknames in one room, in the order they joined.
        /// </summary>
        /// <param name="room">
        /// The room's name.
        /// </param>
        /// <returns>
        /// A fresh array, empty when nobody is in that room.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="room"/> is null.
        /// </exception>
        public string[] MembersOf(string room)
        {
            if (room == null)
            {
                throw new ArgumentNullException(nameof(room));
            }

            ChatRoom? found;
            if (!_rooms.TryGetValue(room, out found))
            {
                return new string[0];
            }

            return found.CopyNicknames();
        }

        /// <summary>
        /// Opens the room: takes a port, claims the name, and adds the seats.
        /// </summary>
        /// <param name="name">
        /// The name users will join, conventionally starting with an asterisk.
        /// </param>
        /// <param name="seats">
        /// How many users may be in the room at once.
        /// </param>
        /// <param name="greeting">
        /// The line sent back to each user who is admitted.
        /// </param>
        /// <returns>
        /// <see cref="XroutError.XRSOK"/>, or the reason the name could not be claimed.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="name"/> is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="seats"/> is not positive.
        /// </exception>
        public XroutError Open(string name, int seats, string greeting)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (seats <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(seats), "A room needs at least one seat.");
            }

            XmsgStatus opened = _kernel.OpenPort(out _port);
            if (opened.IsError)
            {
                return XroutError.XRNSP;
            }

            XmsgStatus converted = _kernel.ConvertPortToMagic(_port, out _magic);
            if (converted.IsError)
            {
                return XroutError.XRUNM;
            }

            // Claim the name with NO seats, then add them one at a time - the sequence the real
            // servers use. Registering with the total up front would work here, but it would not be
            // what the machine does, and this class is meant to be readable next to a real capture.
            XroutError claimed = _directory.RegisterConnectionPort(name, _kernel, _magic, 0, true);
            if (claimed != XroutError.XRSOK)
            {
                _kernel.ClosePort(_port);
                return claimed;
            }

            _name = name;
            _greeting = greeting ?? string.Empty;

            for (int i = 0; i < seats; i++)
            {
                XroutError added = _directory.AdjustFreeConnections(_name, 1);
                if (added != XroutError.XRSOK)
                {
                    return added;
                }
            }

            _seats = seats;
            return XroutError.XRSOK;
        }

        /// <summary>
        /// Handles everything waiting on the server's port.
        /// </summary>
        /// <returns>
        /// The number of messages handled.
        /// </returns>
        /// <remarks>
        /// Call this from whatever loop the host program already has. It never blocks: the kernel
        /// has no scheduler to suspend on, so an empty port simply returns zero.
        /// </remarks>
        public int Poll()
        {
            int handled = 0;

            while (true)
            {
                XmsgReceiveResult arrived = _kernel.Receive(_port, XmsgWaitOptions.None);
                if (!arrived.Received)
                {
                    return handled;
                }

                XmsgMagicNumber sender = _kernel.GetMessageStatus(arrived.Message).Sender;

                // A ROUTED message is one XROUT forwarded, and forwarding it cost this port one of
                // its free seats - spent before the room saw a single byte of the body. So the seat
                // is owed back by the ARRIVAL, not by any particular kind of message, and that is
                // why the accounting lives here rather than in the handlers.
                bool spentASeat = arrived.MessageType == XmsgMessageType.XMROU;
                long id = Handle(sender);

                // "Is this endpoint seated ANYWHERE", not "in one particular room" - the seat
                // belongs to the port, which serves every room, so the room it landed in does not
                // come into it.
                bool wasMember = _roomOf.ContainsKey(id);

                int read;
                _kernel.Read(arrived.Message, _scratch, 0, out read);
                _kernel.ReleaseBuffer(arrived.Message);

                ChatMessage message;
                if (ChatMessage.TryDecode(new ReadOnlySpan<byte>(_scratch, 0, read), out message))
                {
                    Handle(sender, message);
                }

                // A message we cannot decode is dropped on purpose - one confused client must not
                // stop the room. It still counts as handled so a caller draining the port
                // terminates.
                //
                // Note that the seat below is settled for it too. An undecodable letter is exactly
                // the case that used to leak: the body never reaches a handler, so no handler could
                // ever have given the seat back.
                if (spentASeat && !(!wasMember && _roomOf.ContainsKey(id)))
                {
                    // The letter did not seat anybody new, so the seat it spent is free again. The
                    // condition is deliberately "became a member", not "was accepted": a member who
                    // sends a second letter spends a second seat, and only one of them is theirs.
                    ReleaseSeat();
                }

                handled++;
            }
        }

        /// <summary>
        /// Sends a line to EVERY room as the server itself, with no nickname attached.
        /// </summary>
        /// <param name="text">
        /// The line to send.
        /// </param>
        /// <remarks>
        /// Every room deliberately. This is the operator speaking to the machine's users - "going
        /// down in five minutes" - which is the one thing that is not a room's business to keep to
        /// itself. To speak to one room, use <see cref="AnnounceTo"/>.
        /// </remarks>
        public void Announce(string text)
        {
            string[] rooms = Rooms();
            for (int i = 0; i < rooms.Length; i++)
            {
                BroadcastTo(rooms[i], new ChatMessage(ChatMessageKind.Said, string.Empty, text), NoSkip);
            }
        }

        /// <summary>
        /// Sends a line to one room as the server itself, with no nickname attached.
        /// </summary>
        /// <param name="room">
        /// The room to speak to. An unknown room is not an error and reaches nobody.
        /// </param>
        /// <param name="text">
        /// The line to send.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="room"/> is null.
        /// </exception>
        public void AnnounceTo(string room, string text)
        {
            if (room == null)
            {
                throw new ArgumentNullException(nameof(room));
            }

            BroadcastTo(room, new ChatMessage(ChatMessageKind.Said, string.Empty, text), NoSkip);
        }

        /// <summary>
        /// Closes the room: tells everybody, releases the name and the port.
        /// </summary>
        public void Close()
        {
            // Every room, because the port is what is closing and it carries all of them.
            foreach (KeyValuePair<string, ChatRoom> entry in _rooms)
            {
                ChatRoom room = entry.Value;
                long[] leaving = room.CopyMemberIds();

                for (int i = 0; i < leaving.Length; i++)
                {
                    string who;
                    room.TryGetNickname(leaving[i], out who);

                    XmsgMagicNumber address;
                    if (_addresses.TryGetValue(leaving[i], out address))
                    {
                        SendTo(address, new ChatMessage(ChatMessageKind.Left, who, "room closed"));
                    }
                }

                for (int i = 0; i < leaving.Length; i++)
                {
                    string ignored;
                    room.TryLeave(leaving[i], out ignored);
                }
            }

            _rooms.Clear();
            _roomOf.Clear();
            _addresses.Clear();

            if (_name.Length > 0)
            {
                _directory.ClearName(_name);
                _name = string.Empty;
            }

            _kernel.ClosePort(_port);
        }

        private void Handle(XmsgMagicNumber sender, ChatMessage message)
        {
            switch (message.Kind)
            {
                case ChatMessageKind.Join:
                    HandleJoin(sender, message);
                    break;

                case ChatMessageKind.Say:
                    HandleSay(sender, message);
                    break;

                case ChatMessageKind.Rename:
                    HandleRename(sender, message);
                    break;

                case ChatMessageKind.Leave:
                    HandleLeave(sender);
                    break;

                case ChatMessageKind.Who:
                    HandleWho(sender);
                    break;

                case ChatMessageKind.TrunkHello:
                    HandleTrunkHello(sender, message);
                    break;

                case ChatMessageKind.TrunkSaid:
                    HandleTrunkSaid(sender, message);
                    break;

                default:
                    // Server-to-client kinds have no meaning arriving here. Ignore rather than
                    // answer, so a confused or hostile client learns nothing from probing.
                    break;
            }
        }

        /// <summary>
        /// Admits a newcomer to the room their Join names.
        /// </summary>
        /// <param name="sender">
        /// The address the Join arrived from, which is also where the welcome goes.
        /// </param>
        /// <param name="message">
        /// The Join. Its nickname is who they want to be; its TEXT is which room.
        /// </param>
        /// <remarks>
        /// <para><b>The room rides in the text field, and that cost nothing to add</b></para>
        /// A Join already had an empty text field, so carrying the room in it changed no byte
        /// layout on either side. That is the whole reason one port could replace port-per-room
        /// without a wire change.
        /// <para><b>A NICKNAME IS UNIQUE ACROSS THE SERVER, not within a room</b></para>
        /// This is not a preference - it is what <c>CHATSV.PLNC</c> does, and the two must agree or
        /// a name means different people on the two sides of a trunk. Its <c>findByName</c> walks
        /// the whole seat table, because the PLANC has ONE flat table of sixteen seats with the
        /// room stored beside each one; there is no per-room structure to search instead. Checking
        /// per room here would admit a second OLAV in GENERAL that the ND would refuse.
        /// </remarks>
        private void HandleJoin(XmsgMagicNumber sender, ChatMessage message)
        {
            long id = Handle(sender);

            string room = NormaliseRoom(message.Text);
            if (room.Length > MaxRoomNameLength)
            {
                SendTo(sender, new ChatMessage(
                    ChatMessageKind.Reject, message.Nickname, "a room name is at most 16 characters"));
                return;
            }

            // Server-wide, before the room is asked - see the remarks.
            if (NicknameTaken(message.Nickname))
            {
                SendTo(sender, new ChatMessage(
                    ChatMessageKind.Reject, message.Nickname, "that nickname is taken"));
                return;
            }

            ChatRoom? joining;
            if (!_rooms.TryGetValue(room, out joining))
            {
                // The first person to name a room is what brings it into being. Created only after
                // the checks above, so a refused join does not leave an empty room behind.
                joining = new ChatRoom();
                _rooms[room] = joining;
            }

            string refusal;
            if (!joining.TryJoin(id, message.Nickname, out refusal))
            {
                // NO ReleaseSeat here. The seat came with the ARRIVAL, not with the join, and Poll
                // settles it there for every letter - including the ones that never reach a handler
                // at all, which is the case this used to miss.
                DropRoomIfEmpty(room);
                SendTo(sender, new ChatMessage(ChatMessageKind.Reject, message.Nickname, refusal));
                return;
            }

            _addresses[id] = sender;
            _roomOf[id] = room;

            // The welcome goes straight to the address the join arrived from. This is the reply
            // that reveals the server's own address, and it is why nothing after it needs XROUT.
            SendTo(sender, new ChatMessage(ChatMessageKind.Welcome, message.Nickname, _greeting));

            // Their room hears it; they do not - they had the welcome instead.
            BroadcastTo(room, new ChatMessage(ChatMessageKind.Joined, message.Nickname, string.Empty), id);
        }

        /// <summary>
        /// Answers a member asking who else is in the room.
        /// </summary>
        /// <param name="sender">
        /// The member's address.
        /// </param>
        /// <remarks>
        /// <para><b>Members only, and silent to anybody else</b></para>
        /// A stranger gets nothing back, for the same reason <see cref="HandleSay"/> stays silent:
        /// answering would confirm to something that never joined that this port is a chat room, and
        /// would hand it the membership list as well.
        /// <para><b>The answer is one line, not one message per member</b></para>
        /// The names go in the text separated by single spaces. A message each would multiply the
        /// scarcest thing XMSG has - the ten data transmit blocks measured in
        /// <c>DOC/BRINGUP-ORDER-AND-TRAPS-2026-08-18.md</c> - by the size of the room.
        /// </remarks>
        private void HandleWho(XmsgMagicNumber sender)
        {
            long id = Handle(sender);

            // THEIR ROOM, not the server. /who answers "who is here with me", and with one port
            // serving every room the two are no longer the same question.
            ChatRoom? room;
            if (!TryRoomOf(id, out room) || room == null)
            {
                return;
            }

            string asker;
            if (!room.TryGetNickname(id, out asker))
            {
                return;
            }

            string[] names = room.CopyNicknames();

            // Built by hand rather than with string.Join so the separator rule is visible: single
            // space between names, nothing before the first and nothing after the last. The PLANC
            // client splits on exactly that.
            StringBuilder list = new StringBuilder();
            for (int i = 0; i < names.Length; i++)
            {
                if (list.Length > 0) { list.Append(' '); }
                list.Append(names[i]);
            }

            SendTo(sender, new ChatMessage(ChatMessageKind.Who, string.Empty, list.ToString()));
        }

        private void HandleSay(XmsgMagicNumber sender, ChatMessage message)
        {
            long id = Handle(sender);

            ChatRoom? room;
            if (!TryRoomOf(id, out room) || room == null)
            {
                // Not in any room. Silence is the right answer: replying would confirm the port is
                // a chat server to something that never joined.
                return;
            }

            string speaker;
            if (!room.TryGetNickname(id, out speaker))
            {
                return;
            }

            // THEIR ROOM ONLY. This is the whole point of the room table: with one port every
            // member of every room is reachable from here, so an unfiltered send would put
            // GENERAL's conversation on LOBBY's screens.
            //
            // The speaker hears it too - that is their confirmation the line actually got out,
            // rather than an echo of what they typed.
            BroadcastTo(_roomOf[id], new ChatMessage(ChatMessageKind.Said, speaker, message.Text), NoSkip);

            // And out to the machines that are up. Only what a LOCAL member said is forwarded - a
            // line that arrived on a trunk is delivered here and stops, which is what keeps two
            // machines from bouncing one sentence back and forth for ever. Three machines would
            // need a hop count and an origin, and this protocol does not have them yet.
            ForwardOverTrunks(_roomOf[id], speaker, message.Text);
        }

        /// <summary>
        /// Sends a line a local member said to every peer machine that is up.
        /// </summary>
        /// <param name="room">
        /// The room it was said in. It travels in the text, ahead of the first slash.
        /// </param>
        /// <param name="speaker">
        /// The local member's nickname, unqualified. The receiving machine adds the suffix itself.
        /// </param>
        /// <param name="text">
        /// What was said.
        /// </param>
        /// <remarks>
        /// <para><b>The name goes across bare, on purpose</b></para>
        /// <para>
        /// The receiver builds the machine suffix from the address the letter arrived from, never
        /// from anything in the letter. Qualifying it here would be information the receiver has to
        /// distrust anyway, and would let one peer put words in a third machine's mouth.
        /// </para>
        /// <para><b>Only peers that are up</b></para>
        /// <para>
        /// A peer that has gone quiet is skipped rather than written to. Writing to it would not
        /// fail loudly - the letter would simply be queued or dropped somewhere - so the state is
        /// the only thing standing between a dead machine and a growing pile of undelivered chat.
        /// </para>
        /// </remarks>
        private void ForwardOverTrunks(string room, string speaker, string text)
        {
            int[] peers = new int[ChatTrunks.MaxPeers];
            int count = _trunks.ListPeers(peers);

            for (int i = 0; i < count; i++)
            {
                if (_trunks.StateOf(peers[i]) != ChatTrunkState.Up) { continue; }

                XmsgMagicNumber where;
                if (!_peerAddresses.TryGetValue(peers[i], out where)) { continue; }

                SendTo(where, new ChatMessage(ChatMessageKind.TrunkSaid, speaker, room + "/" + text));
            }
        }

        /// <summary>
        /// Answers a member asking to be known by a different name.
        /// </summary>
        /// <param name="sender">
        /// The member's address.
        /// </param>
        /// <param name="message">
        /// The request, carrying the wanted name.
        /// </param>
        /// <remarks>
        /// <para>
        /// A rename can fail for exactly the reasons a join can, so it is answered the same way -
        /// with a <see cref="ChatMessageKind.Reject"/> naming the reason - rather than ignored.
        /// </para>
        /// <para>
        /// NO SEAT IS INVOLVED here, and no handler in this class touches one any more: seats are
        /// settled in <see cref="Poll"/>, against the arrival that actually spent one. Releasing a
        /// seat here would hand back one the member is still sitting in, the room would then admit
        /// one more person than it has room for, and the fault would show up much later as XROUT
        /// refusing joins for no visible reason.
        /// </para>
        /// </remarks>
        private void HandleRename(XmsgMagicNumber sender, ChatMessage message)
        {
            long id = Handle(sender);

            // A STRANGER GETS SILENCE, exactly as in HandleSay, and the check has to happen HERE
            // rather than being left to the room: TryRename reports "you are not in the room" as a
            // refusal like any other, so falling through would answer a Reject to something that
            // never joined - confirming the port is a chat server to anybody who probes it.
            //
            // This is also what CHATSV.PLNC does, which is the reason it is written out. That
            // program tests membership once, before it looks at the message kind at all, so every
            // kind gets the same silence. The two implementations share no code, so a rule that
            // lives only in the C# is a rule the PLANC will drift away from.
            ChatRoom? room;
            if (!TryRoomOf(id, out room) || room == null)
            {
                return;
            }

            string existing;
            if (!room.TryGetNickname(id, out existing))
            {
                return;
            }

            // Server-wide, exactly as on the join, and for the same reason: the PLANC's findByName
            // walks every seat. Asking only this room would let somebody in LOBBY take a name that
            // is already in use in GENERAL - allowed here, refused on the ND.
            //
            // Asking for the name you already have is NOT a collision. Checking that first keeps
            // this from refusing a no-op, which the room reports separately as "not news".
            if (!string.Equals(message.Nickname, existing, StringComparison.OrdinalIgnoreCase)
                && NicknameTaken(message.Nickname))
            {
                SendTo(sender, new ChatMessage(
                    ChatMessageKind.Reject, message.Nickname, "that nickname is taken"));
                return;
            }

            string previous;
            string refusal;
            if (!room.TryRename(id, message.Nickname, out previous, out refusal))
            {
                if (refusal.Length == 0)
                {
                    // Asking for the name you already have: nothing changed, and it is not an
                    // error either, so there is nothing to say.
                    return;
                }

                // NO ReleaseSeat here - see the remarks.
                SendTo(sender, new ChatMessage(ChatMessageKind.Reject, message.Nickname, refusal));
                return;
            }

            // Their room hears it, INCLUDING the member who asked - that is their confirmation, and
            // it means one message kind covers both jobs.
            BroadcastTo(_roomOf[id], new ChatMessage(ChatMessageKind.Renamed, message.Nickname, previous), NoSkip);
        }

        private void HandleLeave(XmsgMagicNumber sender)
        {
            long id = Handle(sender);

            ChatRoom? room;
            if (!TryRoomOf(id, out room) || room == null)
            {
                return;
            }

            string where = _roomOf[id];

            string nickname;
            if (!room.TryLeave(id, out nickname))
            {
                return;
            }

            _addresses.Remove(id);
            _roomOf.Remove(id);

            // Give the seat back. Forget this and the room fills up permanently: XROUT stops
            // forwarding joins long before anybody notices the members left.
            ReleaseSeat();

            // Told BEFORE the room is dropped, and aimed at the room they were in - the leaver is
            // already out of it, so nothing needs skipping. Same order as the PLANC's kLeave path.
            BroadcastTo(where, new ChatMessage(ChatMessageKind.Left, nickname, string.Empty), NoSkip);

            DropRoomIfEmpty(where);
        }

        /// <summary>
        /// Sends a message to everybody in one room, optionally leaving one member out.
        /// </summary>
        /// <param name="room">
        /// The room whose members should hear it. An unknown room sends to nobody.
        /// </param>
        /// <param name="message">
        /// What to send.
        /// </param>
        /// <param name="skipId">
        /// One member to leave out, or <see cref="NoSkip"/> to leave nobody out.
        /// </param>
        /// <remarks>
        /// <para><b>The audience and the exclusion are two parameters on purpose</b></para>
        /// They were one in <c>CHATSV.PLNC</c>, and that was a real defect measured on D100 on
        /// 2026-08-20: one slot number named both the room to send to AND the member to leave out,
        /// the routine treated slot 0 as "no room" and returned, and every Say passed 0 meaning
        /// "leave nobody out". Two people in one room heard nothing the other said for as long as
        /// that stood, while <c>/who</c> and the Joined notice - the two paths that do not pass a
        /// zero - kept working and made it look like a delivery problem.
        /// <para>
        /// This side never had the bug, because <see cref="NoSkip"/> is negative and a real handle
        /// never is, so the two meanings could not collide. That is the lesson worth keeping: the
        /// sentinel has to be outside the value space, not a value inside it that happens to be
        /// unused today.
        /// </para>
        /// </remarks>
        private void BroadcastTo(string room, ChatMessage message, long skipId)
        {
            ChatRoom? found;
            if (!_rooms.TryGetValue(room, out found))
            {
                return;
            }

            long[] ids = found.CopyMemberIds();
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] == skipId)
                {
                    continue;
                }

                XmsgMagicNumber address;
                if (_addresses.TryGetValue(ids[i], out address))
                {
                    SendTo(address, message);
                }
            }
        }

        /// <summary>
        /// Finds the room a member is in.
        /// </summary>
        /// <param name="id">
        /// The member's handle.
        /// </param>
        /// <param name="room">
        /// Their room, when this returns <c>true</c>.
        /// </param>
        /// <returns>
        /// <c>true</c> when they are in a room on this server.
        /// </returns>
        private bool TryRoomOf(long id, out ChatRoom? room)
        {
            room = null;

            string? where;
            if (!_roomOf.TryGetValue(id, out where))
            {
                return false;
            }

            return _rooms.TryGetValue(where, out room);
        }

        /// <summary>
        /// Reports whether a nickname is in use anywhere on the server.
        /// </summary>
        /// <param name="nickname">
        /// The name to look for. A null or empty name is never "taken" - that is a different
        /// refusal, and the room is what words it.
        /// </param>
        /// <returns>
        /// <c>true</c> when somebody in some room already answers to it.
        /// </returns>
        /// <remarks>
        /// Server-wide because the PLANC is server-wide - see the remarks on <c>HandleJoin</c>.
        /// A linear walk, like its <c>findByName</c>: the seat count is sixteen, so there is
        /// nothing here worth indexing.
        /// </remarks>
        private bool NicknameTaken(string nickname)
        {
            if (string.IsNullOrEmpty(nickname))
            {
                return false;
            }

            foreach (KeyValuePair<string, ChatRoom> entry in _rooms)
            {
                string[] names = entry.Value.CopyNicknames();
                for (int i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], nickname, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Forgets a room once the last member has gone.
        /// </summary>
        /// <param name="room">
        /// The room to check.
        /// </param>
        /// <remarks>
        /// A room is not a registered thing - it exists while somebody is in it and not otherwise.
        /// Keeping empty rooms would make <see cref="Rooms"/> grow for ever and would show a
        /// chooser rooms nobody is in.
        /// </remarks>
        private void DropRoomIfEmpty(string room)
        {
            ChatRoom? found;
            if (_rooms.TryGetValue(room, out found) && found.Count == 0)
            {
                _rooms.Remove(room);
            }
        }

        /// <summary>
        /// Settles what room name a Join asked for.
        /// </summary>
        /// <param name="text">
        /// The Join's text field, which is where the room travels.
        /// </param>
        /// <returns>
        /// The room name, upper-cased, or <see cref="DefaultRoom"/> when none was named.
        /// </returns>
        private static string NormaliseRoom(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return DefaultRoom;
            }

            string trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                return DefaultRoom;
            }

            return trimmed.ToUpperInvariant();
        }

        /// <summary>
        /// The address this server is reachable at, or none before <see cref="Open"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>A trunk needs an address, and there is no name lookup across machines yet</b></para>
        /// <para>
        /// A client finds this server by name through XROUT. A peer server cannot: it would have to
        /// look up a name on a machine it is not on. So the first greeting is sent to an address
        /// the operator supplies, and after that every peer's own messages keep it current.
        /// </para>
        /// </remarks>
        public XmsgMagicNumber Magic
        {
            get { return _magic; }
        }

        /// <summary>
        /// What this node believes about each peer machine.
        /// </summary>
        /// <remarks>
        /// Exposed so an operator command can list the trunks and so a test can read the state
        /// without a clock. The rules are enforced in <see cref="ChatTrunks"/>, not here.
        /// </remarks>
        public ChatTrunks Trunks
        {
            get { return _trunks; }
        }

        /// <summary>
        /// Starts trunking to a peer machine, and greets it once immediately.
        /// </summary>
        /// <param name="system">
        /// The peer's system number.
        /// </param>
        /// <param name="where">
        /// The address to greet it at. A trunk has no name lookup of its own yet, so the caller
        /// supplies the peer's chat port the first time; after that the peer's own messages keep
        /// the address current.
        /// </param>
        /// <returns>
        /// <c>true</c> when the peer is now in the table.
        /// </returns>
        /// <remarks>
        /// <para><b>One side is enough</b></para>
        /// <para>
        /// Only one of the two machines ever needs this. The greeting brings the trunk up at the
        /// far end, whose answer brings it up here - proved live, where D102 was never told to
        /// trunk and joined anyway.
        /// </para>
        /// </remarks>
        public bool StartTrunk(int system, XmsgMagicNumber where)
        {
            if (!_trunks.Add(system)) { return false; }

            _peerAddresses[system] = where;

            // Direction 0 - this is an ASK, and the peer is expected to answer it.
            SendTo(where, new ChatMessage(ChatMessageKind.TrunkHello, string.Empty, "0"));
            return true;
        }

        /// <summary>
        /// Advances the trunks by one second, greeting and forgetting as the table requires.
        /// </summary>
        /// <param name="result">
        /// Scratch space for what the second produced. Reused across calls rather than allocated,
        /// because this runs once a second for as long as the server does.
        /// </param>
        /// <remarks>
        /// <para><b>Drive this from an idle clock, never from the message loop</b></para>
        /// <para>
        /// The ND server calls its equivalent from the one-second idle sleep. A tick per message
        /// would make a busy room time its peers out faster, which is exactly backwards.
        /// </para>
        /// </remarks>
        public void TickTrunks(ChatTrunkTick result)
        {
            if (result == null) { throw new ArgumentNullException(nameof(result)); }

            _trunks.Tick(result);

            for (int i = 0; i < result.GreetCount; i++)
            {
                int peer = result.GreetAt(i);

                XmsgMagicNumber where;
                if (_peerAddresses.TryGetValue(peer, out where))
                {
                    SendTo(where, new ChatMessage(ChatMessageKind.TrunkHello, string.Empty, "0"));
                }
            }
        }

        /// <summary>
        /// Answers a peer server's greeting, and brings the trunk up.
        /// </summary>
        /// <param name="sender">
        /// The address the greeting arrived from, which is where an answer goes.
        /// </param>
        /// <param name="message">
        /// The greeting. Its first text byte is the direction: 0 asks, 1 answers.
        /// </param>
        /// <remarks>
        /// <para><b>An answer is not answered</b></para>
        /// <para>
        /// The direction byte exists for exactly this: two servers that both replied to every
        /// greeting would greet each other for ever. An ASK is answered; an ANSWER only marks the
        /// peer up.
        /// </para>
        /// <para><b>Hearing from a peer is what brings a trunk up, not being told about it</b></para>
        /// <para>
        /// A machine that was never configured still adds the peer here, which is what makes ONE
        /// SIDE ENOUGH - proved on the real machines, where D102 was never told to trunk and the
        /// link came up anyway.
        /// </para>
        /// </remarks>
        private void HandleTrunkHello(XmsgMagicNumber sender, ChatMessage message)
        {
            int peer = SystemOf(sender);
            if (peer == 0 || peer == _trunks.MySystem) { return; }

            _trunks.Add(peer);
            _trunks.MarkHeard(peer);
            _peerAddresses[peer] = sender;

            // Direction lives in the first text byte. An empty text is an ASK, because a sender
            // that could not be bothered to say is not claiming to be an answer.
            bool isAnswer = message.Text.Length > 0 && message.Text[0] == '1';
            if (isAnswer) { return; }

            SendTo(sender, new ChatMessage(ChatMessageKind.TrunkHello, string.Empty, "1"));
        }

        /// <summary>
        /// Delivers a line one of a peer's members said, to the room here.
        /// </summary>
        /// <param name="sender">
        /// The peer the line arrived from. This is where the machine name comes from.
        /// </param>
        /// <param name="message">
        /// The forwarded line: the speaker in the name, and the room and text separated by the
        /// first slash.
        /// </param>
        /// <remarks>
        /// <para><b>The receiver qualifies the speaker, so it cannot be forged</b></para>
        /// <para>
        /// The machine suffix is built from the address the letter ARRIVED from, never from
        /// anything the sender wrote. A peer cannot claim to be somebody on a third machine.
        /// </para>
        /// <para><b>Split at the FIRST slash</b></para>
        /// <para>
        /// A message may contain slashes and a room name may not, so the first one is the
        /// separator. Splitting at the last would put half a sentence in the room name.
        /// </para>
        /// <para><b>Not relayed onward, deliberately</b></para>
        /// <para>
        /// A line arriving on a trunk is delivered locally and stops. That is complete for two
        /// machines; three needs a hop count and an origin, and forwarding blindly would loop the
        /// moment a third appeared.
        /// </para>
        /// </remarks>
        private void HandleTrunkSaid(XmsgMagicNumber sender, ChatMessage message)
        {
            int peer = SystemOf(sender);
            if (peer == 0 || peer == _trunks.MySystem) { return; }

            // Anything arriving proves the peer is alive, not just a greeting.
            _trunks.Add(peer);
            _trunks.MarkHeard(peer);
            _peerAddresses[peer] = sender;

            int slash = message.Text.IndexOf('/');
            if (slash <= 0) { return; }

            string room = message.Text.Substring(0, slash).ToUpperInvariant();
            string said = message.Text.Substring(slash + 1);

            // A room nobody here is in does not exist here - and it must NOT be conjured up by a
            // peer, or a machine could fill this node's table with rooms that have no members.
            if (!_rooms.ContainsKey(room)) { return; }

            string speaker = message.Nickname + "@D" + peer.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // NoSkip: everybody local hears it. The speaker is on the other machine, so there is
            // nobody here to leave out.
            BroadcastTo(room, new ChatMessage(ChatMessageKind.Said, speaker, said), NoSkip);
        }

        /// <summary>
        /// The system number an address belongs to.
        /// </summary>
        /// <param name="magic">
        /// The address a message arrived from.
        /// </param>
        /// <returns>
        /// The peer's system number, or zero when the address carries none.
        /// </returns>
        /// <remarks>
        /// This is what makes a forwarded speaker impossible to forge: the machine comes from the
        /// magic the letter arrived with, which the sender does not control.
        /// </remarks>
        private static int SystemOf(XmsgMagicNumber magic)
        {
            return magic.SystemNumber;
        }

        private void SendTo(XmsgMagicNumber destination, ChatMessage message)
        {
            // Shared with ChatClient.SendToServer - see ChatWire, which is where this sequence
            // lives now.
            ChatWire.Send(_kernel, destination, _port, message);
        }

        private void ReleaseSeat()
        {
            if (_name.Length > 0)
            {
                _directory.AdjustFreeConnections(_name, 1);
            }
        }

        /// <summary>
        /// The room's handle for a client, derived from the address it writes from.
        /// </summary>
        /// <param name="magic">
        /// The client's magic number.
        /// </param>
        /// <returns>
        /// A stable handle for that client.
        /// </returns>
        /// <remarks>
        /// The room tells members apart by a number and does not care what it means. Here it is
        /// the magic number, which identifies a (system, port) endpoint - the same thing that
        /// decided membership before the rules were pulled out.
        /// </remarks>
        private static long Handle(XmsgMagicNumber magic)
        {
            return magic.Value;
        }

        /// <summary>
        /// Passed to <c>Broadcast</c> when everybody should hear it, including the member who
        /// caused it.
        /// </summary>
        /// <remarks>
        /// A real handle is never negative, so this cannot collide with one.
        /// </remarks>
        private const long NoSkip = -1;

    }
}
