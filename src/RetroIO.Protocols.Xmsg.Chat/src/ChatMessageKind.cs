//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Chat
{
    /// <summary>
    /// The message types the chat service exchanges.
    /// </summary>
    /// <remarks>
    /// <para><b>This is OUR protocol, not ND's</b></para>
    /// No chat service exists on any SINTRAN image we have - the registry holds terminal access,
    /// file transfer, spooling and file access, and nothing else. So this vocabulary is invented
    /// here. What is NOT invented is everything underneath it: claiming a name, admitting callers
    /// against a free-connection count, learning a sender's address from an arrived message, and
    /// port-to-port delivery are all captured from a running machine.
    /// <para><b>Direction</b></para>
    /// A client only ever sends <see cref="Join"/>, <see cref="Say"/> and <see cref="Leave"/>;
    /// everything else travels from the server.
    /// </remarks>
    public enum ChatMessageKind : byte
    {
        /// <summary>
        /// Not a valid message. Guards against a zeroed buffer being read as a real one.
        /// </summary>
        None = 0,

        /// <summary>
        /// Client to server: asks to enter the room, carrying the nickname to be known by.
        /// </summary>
        /// <remarks>
        /// This is the only message addressed by NAME, so it is the only one that travels through
        /// XROUT. It costs the server one seat of its free-connection count.
        /// </remarks>
        Join = 1,

        /// <summary>
        /// Server to client: you are in, and here is the greeting.
        /// </summary>
        /// <remarks>
        /// Sent straight to the caller's own address, which the server learned from the arrived
        /// join. It is the reply that tells the client the server's address, so everything after
        /// this flows directly between the two ports.
        /// </remarks>
        Welcome = 2,

        /// <summary>
        /// Server to client: refused, with a reason.
        /// </summary>
        Reject = 3,

        /// <summary>
        /// Client to server: say something to the room.
        /// </summary>
        Say = 4,

        /// <summary>
        /// Server to clients: somebody said something.
        /// </summary>
        Said = 5,

        /// <summary>
        /// Client to server: leaving.
        /// </summary>
        Leave = 6,

        /// <summary>
        /// Server to clients: somebody entered the room.
        /// </summary>
        Joined = 7,

        /// <summary>
        /// Server to clients: somebody left the room.
        /// </summary>
        Left = 8,

        /// <summary>
        /// A member asking to be known by a different name from now on.
        /// </summary>
        /// <remarks>
        /// Sent by the member; the server decides. A rename can be refused for the same reason a
        /// join can - somebody else already answers to that name - so it is a request, not a
        /// statement.
        /// </remarks>
        Rename = 9,

        /// <summary>
        /// The room being told that somebody is now known by a different name.
        /// </summary>
        /// <remarks>
        /// Carries the NEW name in <c>Nickname</c> and the old one in <c>Text</c>. Both are needed:
        /// a client showing a transcript has the old name on screen and would otherwise have no way
        /// to connect the two.
        /// </remarks>
        Renamed = 10,

        /// <summary>
        /// Asking the room who is in it, and the room's answer.
        /// </summary>
        /// <remarks>
        /// <para><b>One kind, both directions</b></para>
        /// A client sends it with an empty <c>Text</c>; the room answers with the same kind and the
        /// members' names in <c>Text</c>, separated by single spaces. A second kind for the reply
        /// would buy nothing - the client knows it asked, and the room never asks.
        /// <para><b>Why the names go in the TEXT</b></para>
        /// The text is the only variable-length field in the format, and its length is two bytes
        /// big-endian, so a full room fits comfortably. The <c>Nickname</c> field carries the ASKER
        /// on the way out and is empty on the way back.
        /// </remarks>
        Who = 11,

        /// <summary>
        /// CHAT-MON to the server: report what you are doing.
        /// </summary>
        /// <remarks>
        /// <para><b>Kinds 32 and up are ADMIN, and they arrive on the COMMAND port</b></para>
        /// Not on <c>CHAT-LOBBY</c>. That port is a connection port and every arrival there spends
        /// one of the server's free connections, which is a seat a human then cannot have. An
        /// administrator asking for status must not cost the room a seat.
        /// <para><b>Sharing this enum is deliberate</b></para>
        /// A separate admin enum would mean a second decoder bound, and this one has already been
        /// left behind twice - see <see cref="ChatMessageKinds.Highest"/>. One vocabulary is one
        /// place to forget rather than two. Sharing the vocabulary is NOT sharing the authority: a
        /// server must still refuse an admin kind that arrives on the lobby port.
        /// <para><b>Why 32 and not 12</b></para>
        /// The admin block starts at a ROUND RESERVED BASE so both halves can grow without
        /// colliding. Admin began at 12, and the very next kind added was <see cref="Map"/> - a
        /// ROOM kind - which would have landed at 15, inside the admin range, and been misread by
        /// <see cref="ChatMessageKinds.IsAdmin"/>. Renumbering was free then because nothing was
        /// built; it would not be free once two machines are talking. Room kinds have 1..31, admin
        /// has 32 up, and TRUNK/UNTRUNK can join the admin block without pushing anything.
        /// </remarks>
        AdminStatus = 32,

        /// <summary>
        /// The server to CHAT-MON: the answer, as readable text.
        /// </summary>
        /// <remarks>
        /// Text rather than a struct of counters. What is worth reporting grows with every phase of
        /// federation - peers, trunk states, hop counts - and a text answer lets that grow without
        /// changing the wire and re-pinning the golden bytes each time.
        /// </remarks>
        AdminStatusReply = 33,

        /// <summary>
        /// CHAT-MON to the server: shut down cleanly.
        /// </summary>
        /// <remarks>
        /// <para><b>What it is for</b></para>
        /// A stop that does not need <c>STOP-TERMINAL</c> - NOT releasing the <c>CHAT-LOBBY</c>
        /// name. SINTRAN already clears the name when the task terminates, measured 2026-08-18,
        /// and the belief that it lingered is refuted.
        /// </remarks>
        AdminStop = 34,

        /// <summary>
        /// A member asking to see the network, and the server's answer.
        /// </summary>
        /// <remarks>
        /// <para><b>One kind, both directions</b></para>
        /// The client sends it with empty <c>Text</c>; the server answers with the machines and the
        /// trunks between them, as readable text. Same shape as <see cref="Who"/>, and for the same
        /// reason: the client knows it asked, and the server never asks.
        /// <para><b>A ROOM kind, not an admin one, deliberately</b></para>
        /// It arrives on <c>CHAT-LOBBY</c> from an ordinary member and costs a seat like any other
        /// room message - the asker already holds one. It shows the configuration and changes
        /// nothing. <c>TRUNK</c> and <c>UNTRUNK</c> stay in CHAT-MON on the command port, because
        /// seeing how the network is wired is not the same right as rewiring it.
        /// </remarks>
        Map = 12,

        /// <summary>
        /// A member asking which rooms exist, and the server's answer.
        /// </summary>
        /// <remarks>
        /// <para><b>One kind, both directions</b></para>
        /// The client sends it with empty <c>Text</c>; the server answers with the room names as
        /// readable text. Same shape as <see cref="Who"/> and <see cref="Map"/>, and a ROOM kind
        /// for the same reason - it shows what exists and changes nothing.
        /// <para><b>What the set of rooms IS has not been settled</b></para>
        /// A room is reached by joining a named port through XROUT - the prefix <c>CHAT-</c>
        /// followed by the room name - which is why <c>/join</c> needs that prefix. On that
        /// reading the set of rooms is a
        /// set of registered XROUT names - XROUT's data, not the server's - and there is no
        /// discovery call that returns it: <c>XMPFLMP</c> hands back only the CALLING task's own
        /// ports. So a server may only be able to list rooms IT holds. See open question C5 in
        /// <c>DOC/protocols/chat-wire.json</c>; decide the room model before implementing this.
        /// </remarks>
        Rooms = 13,

        /// <summary>
        /// Setting a room's topic, and the room being told it changed.
        /// </summary>
        /// <remarks>
        /// <para><b>One kind, both directions</b></para>
        /// A member sends it with the new topic in <c>Text</c>; the server stores it against the
        /// room and tells the room with the same kind. Same shape as <see cref="Who"/>.
        /// <para><b>A ROOM kind, not an admin one</b></para>
        /// It changes what a room is ABOUT, not how the server is configured, so an ordinary
        /// member may do it. If topics ever need protecting, that is a rule about who may send it -
        /// not a reason to move it to the command port.
        /// <para><b>The topic is also what /list shows</b></para>
        /// <see cref="Rooms"/> reports each room's topic beside its name, so the topic has to be
        /// held by the server rather than only echoed between clients.
        /// </remarks>
        Topic = 14,

        /// <summary>
        /// The answer to <see cref="Who"/> when the asker holds no seat - every room and the
        /// people in each.
        /// </summary>
        /// <remarks>
        /// <para><b>Server to client only</b></para>
        /// A client never sends this. It sends <see cref="Who"/> either way and the SERVER picks
        /// the answer, because the server is the one that knows whether the asker has a seat.
        /// <para><b>Why not just answer with Who</b></para>
        /// A client labels a <see cref="Who"/> answer with the room it believes it is in. Putting
        /// every room's people under that label would print something FALSE - the exact defect
        /// <see cref="Who"/> itself had until it learned to compare rooms. A separate kind costs
        /// one number and makes the label impossible to get wrong.
        /// <para><b>The text is ready to read</b></para>
        /// Rooms two spaces apart, people one, remote people qualified:
        /// <c>LOBBY: TESTER OLAV  GENERAL: ANNA@D102</c>. Nothing parses it. An EMPTY text is a
        /// real answer - nobody is anywhere - and the client says so in words.
        /// </remarks>
        AllWho = 15,

        /// <summary>
        /// One remembered message, replayed to somebody who has just joined.
        /// </summary>
        /// <remarks>
        /// <para><b>Server to client only, and one per message</b></para>
        /// Sent to the joiner alone, oldest first, after the welcome and BEFORE the room is told
        /// they arrived - so a transcript reads in the order it happened.
        /// <para><b>A distinct kind, deliberately not Said</b></para>
        /// Replaying <see cref="Said"/> would be less code and every client already renders it,
        /// but then a client CANNOT TELL history from live traffic - and that matters the moment
        /// it timestamps, beeps or counts unread. Choosing the cheap option would be choosing to
        /// make those features wrong later.
        /// <para><b>It does not cross a trunk</b></para>
        /// History is local to the machine that holds it. Somebody joining on D102 sees what was
        /// said ON D102. Relaying it would mean deciding whose copy is authoritative and merging
        /// rings across machines whose clocks differ - each ND runs its own.
        /// </remarks>
        History = 16,

        /// <summary>
        /// Configure a trunk to a peer machine.
        /// </summary>
        /// <remarks>
        /// An ADMIN kind: it rewires the network, which is not something a room member may do.
        /// The text carries the peer's system number. Answered with
        /// <see cref="AdminStatusReply"/>, like every other admin verb, so adding it cost no
        /// change to the wire format.
        /// </remarks>
        AdminStartTrunk = 35,

        /// <summary>
        /// Forget a peer machine.
        /// </summary>
        /// <remarks>
        /// The other half of <see cref="AdminStartTrunk"/>, and an admin kind for the same reason.
        /// </remarks>
        AdminStopTrunk = 36,

        /// <summary>
        /// Ask which peers are configured and what is believed about each.
        /// </summary>
        /// <remarks>
        /// Answers <c>102 up</c>, <c>103 down</c>, <c>200 unknown</c>. UNKNOWN and DOWN are kept
        /// apart on purpose: the first usually means a trunk that was never answered and is
        /// probably a configuration mistake, the second a machine that was there and went away.
        /// Collapsing them would hide the difference exactly when an operator needs it.
        /// </remarks>
        AdminListTrunks = 37,

        /// <summary>
        /// Set how many messages a room remembers, or the machine's default.
        /// </summary>
        /// <remarks>
        /// <para><b>One field or two</b></para>
        /// <c>INITIALIZE 20</c> sets the machine DEFAULT, which rooms pick up as they are created.
        /// <c>INITIALIZE LOBBY 20</c> sets one room. The server splits the text at the first
        /// space; the sender does not take it apart, so one place decides what a valid argument is.
        /// <para><b>Refused once a room has history</b></para>
        /// The ring is <c>block = id MOD size</c>, so changing size moves where every existing id
        /// lands - the messages do not move but the arithmetic that finds them does, and the
        /// replay would return other messages in the wrong order while looking healthy.
        /// </remarks>
        AdminInitialize = 38,

        /// <summary>
        /// CHAT-MON to server: what this machine calls itself.
        /// </summary>
        /// <remarks>
        /// <para><b>An empty name clears it</b></para>
        /// A machine with no name falls back to <c>D</c> plus its system number everywhere, which
        /// is exactly how every machine behaved before names existed - so there is a way back from
        /// a name typed wrongly that does not need a restart.
        /// <para><b>A name is only a label on a number</b></para>
        /// Which machine a message came from is still decided by the sender's magic, which the
        /// kernel supplies and no sender can choose. A machine may call itself anything and still
        /// cannot claim to BE another machine - that is the property the trunk protocol was built
        /// around and it is not weakened by naming.
        /// <para><b>How it reaches the other machines</b></para>
        /// On the trunk Hello, appended after the direction byte that was always there. The old
        /// Hello handler reads that first byte and nothing else, so a server that has not been
        /// upgraded ignores the name rather than breaking - the same property
        /// <see cref="ChatMessageKind.TrunkRelay"/> was given, and what lets three running
        /// machines be upgraded one at a time.
        /// <para><b>Not written to disk</b></para>
        /// It belongs in the machine's boot mode file beside START-TRUNK, which already drives
        /// CHAT-MON.
        /// </remarks>
        AdminSetName = 39,

        /// <summary>
        /// CHAT-MON to server, on the admin port: learn no NEW peers.
        /// </summary>
        /// <remarks>
        /// <para><b>What being open costs, and why closing it is a verb</b></para>
        /// A server adds any system that reaches its trunk port to its peer table. That is what
        /// lets a lab be set up without naming every machine, and it is the default. It also means
        /// the server believes whatever that system says about who is speaking on it, because a
        /// trunk line arrives as NAME@thatsystem. This kind is how an operator says the network is
        /// now complete.
        /// <para><b>It does not drop peers already known</b></para>
        /// Those were learned before the line was drawn, and cutting live trunks is not what "take
        /// no more" means. <c>STOP-TRUNK</c> removes a peer.
        /// <para><b>Deliberately not saved to disk</b></para>
        /// A server that came back from a reboot silently refusing its own trunks would be a worse
        /// failure than the one this prevents.
        /// <para><b>Status</b></para>
        /// PLANC calls it <c>kAdmLock</c>. Built and answered on the machine; see the registry
        /// entry in <c>chat-wire.json</c> for the current evidence.
        /// </remarks>
        AdminLockPeers = 40,

        /// <summary>
        /// CHAT-MON to server, on the admin port: learn any system again.
        /// </summary>
        /// <remarks>
        /// The other half of <see cref="AdminLockPeers"/>. Answered with the same admin status
        /// reply as every other admin verb, so CHAT-MON needs no new decoding.
        /// <para><b>Two kinds rather than one with an argument</b></para>
        /// Every other admin verb here is its own number, and a verb whose meaning depends on
        /// parsing its text is the one that gets typed wrongly.
        /// <para><b>Status</b></para>
        /// PLANC calls it <c>kAdmOpen</c>.
        /// </remarks>
        AdminOpenPeers = 41,

        /// <summary>
        /// CHAT-MON to server, on the admin port: free the named member's seat and tell nobody.
        /// </summary>
        /// <remarks>
        /// A TEST VERB, and the only way to reach <see cref="TrunkDirectBad"/> on purpose. No
        /// <see cref="Left"/> goes to the room and no member list goes to the peers; freeing the
        /// seat is the whole action.
        /// <para><b>Why a verb exists only for testing</b></para>
        /// TrunkDirectBad fires when a machine is asked to deliver a private message to somebody a
        /// PEER still believes is seated there. Every ordinary way of ending a session keeps the
        /// two machines in step, which is right for the product and leaves the refusal unreachable
        /// by hand:
        ///  - /quit sends Leave, which broadcasts Left and then tells the peers.
        ///  - /nick crosses the trunk, so the peer follows the new name.
        ///  - an RT-load empties the table, but the orphaned client rejoins by itself.
        ///  - a peer restart makes the other side purge what it knew.
        /// All four were measured against D100 and D102 on 2026-08-30 and every one self-healed,
        /// so every refusal came back as the LOCAL one and the trunk path never ran. The reason
        /// string is the only thing that tells the two apart.
        /// <para><b>Status</b></para>
        /// Answered with <see cref="AdminStatusReply"/> like every other admin verb, so it costs
        /// no new decoding. PLANC calls it <c>kAdmDrop</c>.
        /// </remarks>
        AdminDropMember = 42,

        /// <summary>
        /// CHAT-MON to server, on the admin port: list who is seated right now, and which room.
        /// </summary>
        /// <remarks>
        /// The other half of <see cref="AdminDropMember"/>: that acts on a seat, this shows them.
        /// Carries no argument, so it travels like <see cref="AdminListTrunks"/> rather than
        /// carrying text. Answered with <see cref="AdminStatusReply"/> like every other admin
        /// verb. PLANC calls it <c>kAdmMbrs</c>.
        /// </remarks>
        AdminListMembers = 43,

        /// <summary>
        /// Client to server: send this text to ONE person, who need not be in your room or in any
        /// room.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The name field carries the target as the user typed it - either a bare alias
        /// (<c>RONNY</c>) or one qualified by machine (<c>D102!RONNY</c>). The separator is
        /// <c>!</c> and not <c>@</c> deliberately: <c>@</c> is already the short command prefix, so
        /// <c>@RONNY@D100</c> would need a parser that counts at-signs. <c>machine!alias</c> reads
        /// left to right as route-then-person and has precedent in UUCP addressing of the same era.
        /// </para>
        /// <para>
        /// A bare alias that matches more than one person in the federation is REFUSED with
        /// <see cref="DirectBad"/> and the candidate list, never guessed at. Two machines can each
        /// hold a RONNY long before they ever trunk to each other, and there is no moment at which
        /// a global uniqueness rule could have been applied. A direct message delivered silently to
        /// the wrong person is a privacy failure, not an inconvenience.
        /// </para>
        /// </remarks>
        Direct = 17,

        /// <summary>
        /// Server to client: a direct message to show, addressed to you personally.
        /// </summary>
        /// <remarks>
        /// The name field carries the sender QUALIFIED - <c>D102!KARI</c> - so what is displayed is
        /// also what can be typed back. A client marks these so they cannot be mistaken for room
        /// traffic.
        /// </remarks>
        Directed = 18,

        /// <summary>
        /// Server to client: your direct message was delivered, and here is the qualified name it
        /// went to.
        /// </summary>
        /// <remarks>
        /// <para><b>Sent on EVERY delivery, not only ambiguous ones</b></para>
        /// <para>
        /// Refusing an ambiguous name protects the sender when the collision is visible. This
        /// protects them when it is not - a machine that just went Down, a second RONNY who logged
        /// in a moment ago, a trunk that came back with a different user list. Being told the
        /// qualified name that actually received it makes a wrong delivery visible immediately
        /// rather than days later, and it costs one line.
        /// </para>
        /// </remarks>
        DirectSent = 19,

        /// <summary>
        /// Server to client: the direct message was not delivered, and why.
        /// </summary>
        /// <remarks>
        /// The name field carries the target that was tried; the text carries the reason, and for
        /// an ambiguous alias the candidate list - <c>which RONNY? D100!RONNY, D102!RONNY</c>.
        /// Carrying the candidates is what makes the refusal usable rather than merely annoying.
        /// <c>not logged in</c> is a refusal too: there is deliberately NO store and forward,
        /// because a message that waits for somebody is a mailbox, and SINTRAN already has mail.
        /// </remarks>
        DirectBad = 20,

        /// <summary>
        /// Server to server: a greeting that brings a trunk up, and its answer.
        /// </summary>
        /// <remarks>
        /// <para><b>One kind both directions, with the direction in the first text byte</b></para>
        /// 0 asks, 1 answers. An answer that gets answered is two servers greeting each other for
        /// ever.
        /// <para><b>A trunk is a BELIEF, not a connection</b></para>
        /// XMSG named ports are message passing - there is no session to hold and nothing to drop.
        /// A trunk is the belief that a peer is reachable, refreshed by anything arriving from it,
        /// and neither side is the client. ONE SIDE IS ENOUGH: an unsolicited hello brings the
        /// trunk up and the rest follows.
        /// </remarks>
        TrunkHello = 48,

        /// <summary>
        /// Server to server: who is on your machine?
        /// </summary>
        /// <remarks>
        /// Empty text - the kind IS the question. Asked on EVERY hello, because a peer saying
        /// hello again has probably restarted, and the answer replaces its rows wholesale.
        /// </remarks>
        TrunkWho = 49,

        /// <summary>
        /// Server to server: the answer to <see cref="TrunkWho"/>.
        /// </summary>
        /// <remarks>
        /// <c>NAME/ROOM NAME/ROOM ...</c>, space between people, slash between a person and their
        /// room. A MACHINE REPORTS ONLY ITS OWN MEMBERS - relaying what it heard about a third
        /// machine would let a wrong entry echo round a ring with nothing to stop it. The receiver
        /// REPLACES everything it held for that peer; merging would leave ghosts of people who
        /// left while the machine was away.
        /// </remarks>
        TrunkMembers = 50,

        /// <summary>
        /// Server to server: one of my members spoke.
        /// </summary>
        /// <remarks>
        /// <para><b>Shape</b></para>
        /// Name is the speaker as their own machine knows them, unqualified. Text is
        /// <c>ROOM/what they said</c>, split at the FIRST slash - a message may contain slashes
        /// and a room name may not.
        /// <para><b>The receiver adds the machine</b></para>
        /// The <c>@Dnnn</c> comes from the magic the letter arrived with, so a speaker cannot forge
        /// the machine they are on.
        /// <para><b>Not relayed onward</b></para>
        /// A message received on a trunk is delivered locally and stops. That is complete for two
        /// machines; three needs a hop count and an origin, and forwarding blindly would loop the
        /// moment a third appeared.
        /// </remarks>
        TrunkSaid = 51,

        /// <summary>
        /// Server to server: a line being RELAYED onward, carrying where it started and how many
        /// hops it has left.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this is not just TrunkSaid with two more fields</b></para>
        /// <para>
        /// <see cref="TrunkSaid"/> has its bytes pinned by a golden test and the PLANC server
        /// reads the same layout. Adding fields to it would mean every machine had to be upgraded
        /// before ANY trunk worked again, and these machines are upgraded one at a time - so the
        /// federation would be down for the whole rollout. A separate kind leaves TrunkSaid
        /// untouched: an old server keeps handling its direct trunks exactly as before and simply
        /// ignores a kind it does not know.
        /// </para>
        /// <para><b>The two fields, and why both</b></para>
        /// <para>
        /// ORIGIN is the system the speaker is actually on. It has to travel explicitly, because
        /// the receiver's usual trick - qualify the speaker with the system the letter came from -
        /// names the RELAY on a forwarded message, not the person's machine.
        /// </para>
        /// <para>
        /// HOPS REMAINING is decremented at each relay and the message is dropped at zero. Without
        /// it a mesh reflects for ever; see constraint 3 in CHAT-FEDERATION-DESIGN.md. It mirrors
        /// what XMSG's own header does rather than inventing a scheme.
        /// </para>
        /// <para><b>Layout</b></para>
        /// <code>
        /// 34            kind = 52
        /// 00 67         origin system, big-endian (103)
        /// 03            hops remaining
        /// 04            speaker length - UNQUALIFIED, as in TrunkSaid
        /// 41 4E 4E 41   "ANNA"
        /// 00 09         text length, big-endian
        /// 4C 4F ...     "LOBBY/hei" - room, then the first slash, then the line
        /// </code>
        /// </remarks>
        TrunkRelay = 52,

        /// <summary>
        /// Server to server: a relayed line that also carries the ORIGIN'S OWN LINE NUMBER, so the
        /// same line arriving by two different paths can be recognised as one line.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the id has to come from the origin</b></para>
        /// <para>
        /// Dedup needs a name for the line that every machine agrees on. It cannot be assigned by a
        /// relay: a machine with two neighbours sends the same line to both, each would stamp its
        /// own number, and a node further out would see two different names for one line and
        /// deliver it twice - which is the exact fault this kind exists to stop. So the machine the
        /// line was TYPED ON stamps it, and the pair (origin, id) travels unchanged to the edge.
        /// </para>
        /// <para><b>Why not just add the id to <see cref="TrunkRelay"/></b></para>
        /// <para>
        /// Same reason <see cref="TrunkRelay"/> is not extra fields on <see cref="TrunkSaid"/>, and
        /// it is worth being blunt about it: these machines are upgraded ONE AT A TIME. If kind 52
        /// grew a longer header, a server that had not been upgraded would read the two extra bytes
        /// as a name length and a name, and hand its users GARBAGE. A kind it does not recognise it
        /// simply ignores. Silence during a rollout is recoverable; rubbish delivered to a user is
        /// not.
        /// </para>
        /// <para><b>Layout</b></para>
        /// <code>
        /// 35            kind = 53
        /// 00 67         origin system, big-endian (103)
        /// 03            hops remaining
        /// 04 D2         line number as stamped by the ORIGIN, big-endian
        /// 04            speaker length - UNQUALIFIED, as in TrunkSaid
        /// 41 4E 4E 41   "ANNA"
        /// 00 09         text length, big-endian
        /// 4C 4F ...     "LOBBY/hei"
        /// </code>
        /// <para><b>Wrapping</b></para>
        /// <para>
        /// The id is 16 bits and wraps. That is harmless as long as the receiver only remembers a
        /// short window of recent ids - a number cannot come round again inside a window far
        /// smaller than 65536.
        /// </para>
        /// </remarks>
        TrunkRelayId = 53,

        /// <summary>
        /// Server to server: a DIRECT message being carried across a trunk to somebody on another
        /// machine.
        /// </summary>
        /// <remarks>
        /// <para><b>It reuses TrunkRelayId's header and its text trick, on purpose</b></para>
        /// <para>
        /// A direct message to somebody two machines away has to be relayed and de-duplicated in
        /// exactly the same way a room line does. Inventing a second mechanism would mean two
        /// places to get hop counting wrong, so this carries the same five-byte header:
        /// origin system, hops remaining, line id.
        /// </para>
        /// <para>
        /// <see cref="TrunkSaid"/> already packs two things into the text as
        /// <c>ROOM/message</c>. This does the same with the TARGET in place of the room, so the
        /// name field is free to carry the SENDER - which is what the receiver has to show.
        /// </para>
        /// <para><b>Layout</b></para>
        /// <code>
        /// 36            kind = 54
        /// 00 66         origin system, big-endian (102)
        /// 03            hops remaining
        /// 04 D2         line id stamped by the ORIGIN
        /// 04            sender length
        /// 4B 41 52 49   "KARI" - the SENDER, unqualified
        /// 00 0F         text length, big-endian
        /// 52 4F ...     "RONNY/are you free" - TARGET, a slash, then the message
        /// </code>
        /// <para><b>The target is an alias, and it may be ambiguous</b></para>
        /// <para>
        /// The receiving server looks the alias up in its own table. Two machines may each hold a
        /// RONNY, so a bare alias that matches more than one person is REFUSED rather than guessed
        /// at - see CHAT-DIRECT-MESSAGES-AND-CLIENT-CONFIG.md. A qualified target arrives here as
        /// <c>machine!alias</c>.
        /// </para>
        /// </remarks>
        TrunkDirect = 54,

        /// <summary>
        /// Server to server: a direct message could NOT be delivered at the far end, carried back
        /// to the machine the sender is on.
        /// </summary>
        /// <remarks>
        /// <para><b>Without this, a direct message across a trunk vanishes in silence</b></para>
        /// <para>
        /// <c>takeRemDirect</c> in the PLANC server says so in its own comment: the sender was
        /// already told <see cref="DirectSent"/> by their own server, so somebody who logged out -
        /// or renamed - in the gap between the two ends agreeing loses the message with nobody
        /// told. That comment was written for a logout race. A rename that never propagates makes
        /// the two ends disagree permanently, which turns a narrow race into a standing hole.
        /// </para>
        /// <para><b>It reuses TrunkDirect's header, and DirectBad's tail</b></para>
        /// <para>
        /// The five-byte header is the same - origin system, hops remaining, line id. The ORIGIN
        /// is the machine that COULD NOT DELIVER, not the sender's machine: every other kind uses
        /// that field for the machine a line was typed on, and the duplicate check keys on the
        /// pair (origin, id), so a refusal we compose is ours. Nothing routes by it - the refusal
        /// reaches the sender's machine because the send call is told where to put it.
        /// </para>
        /// <para>
        /// The line id is the FAILED message's, passed straight through, which is what lets the
        /// far end say which message failed.
        /// </para>
        /// <para>
        /// The text is laid out exactly as <see cref="DirectBad"/>'s already is, so the receiving
        /// server hands it straight to the local path it already has and THE CLIENT NEEDS NO
        /// CHANGE. A person sees the same sentence in the same shape whether the failure happened
        /// on their own machine or two hops away.
        /// </para>
        /// <para><b>Layout</b></para>
        /// <code>
        /// 37            kind = 55
        /// 00 67         origin system, big-endian (103) - the machine that could not deliver
        /// 03            hops remaining
        /// 04 D2         line id of the message that failed
        /// 04            sender length
        /// 4B 41 52 49   "KARI" - the SENDER, the person to be told
        /// 00 12         text length, big-endian
        /// 52 4F ...     "RONNY/not logged in" - TARGET tried, a slash, then the reason
        /// </code>
        /// <para><b>A separate kind, for the reason every kind here is separate</b></para>
        /// <para>
        /// These machines are upgraded one at a time. A server that does not know 55 IGNORES it,
        /// which is exactly today's behaviour, so nothing regresses while the three are brought up
        /// in turn.
        /// </para>
        /// <para><b>What it does not fix</b></para>
        /// <para>
        /// The sender is found by NAME at the origin. If they rename in the window between sending
        /// and the refusal arriving, the refusal is dropped there for the same reason the message
        /// was dropped at the far end. Closing that needs an identity that survives a rename,
        /// which is a larger change and is deliberately not part of this one.
        /// </para>
        /// </remarks>
        TrunkDirectBad = 55,
    }

    /// <summary>
    /// Facts about <see cref="ChatMessageKind"/> that the decoder needs at runtime.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists at all</b></para>
    /// <c>ChatMessage.TryDecode</c> rejects a kind above the last one defined, and that bound has
    /// now been left behind TWICE by a new kind added above it. Each time the effect was the same
    /// and silent: the new kind decoded as a malformed message and was dropped, so one end sent it
    /// and the other never saw it, with nothing failing anywhere. It cost a build cycle on a real
    /// ND-100 the first time and a test run the second.
    /// <para><b>The fix is that the bound has a name and a test</b></para>
    /// <see cref="Highest"/> is what the decoder compares against, and
    /// <c>ChatWhoTests.TheDecoderBoundCoversEveryKind</c> checks it against the enum itself. Adding
    /// a twelfth kind without touching this constant now fails a test instead of losing messages.
    /// </remarks>
    public static class ChatMessageKinds
    {
        /// <summary>
        /// The largest value <see cref="ChatMessageKind"/> defines.
        /// </summary>
        /// <remarks>
        /// It is <see cref="ChatMessageKind.TrunkDirectBad"/> and no longer
        /// <c>AdminStop</c>. Ten kinds were added to the PLANC server between 2026-08-20 and
        /// 2026-08-23 - the trunk set, the admin trunk verbs, AllWho, History and
        /// AdminInitialize - and this constant was left behind again, which is the third time.
        /// Everything above the old bound decoded as malformed and was dropped in silence.
        /// </remarks>
        public const byte Highest = (byte)ChatMessageKind.TrunkDirectBad;

        /// <summary>
        /// The lowest value that is an ADMIN kind rather than a room kind.
        /// </summary>
        /// <remarks>
        /// Admin kinds arrive on the server's command port. A kind at or above this must be refused
        /// when it arrives on <c>CHAT-LOBBY</c>, and a kind below it must be refused on the command
        /// port - sharing one vocabulary is not sharing the authority to use any of it anywhere.
        /// </remarks>
        public const byte LowestAdmin = (byte)ChatMessageKind.AdminStatus;

        /// <summary>
        /// Says whether a kind is an administrative one.
        /// </summary>
        /// <param name="kind">
        /// The kind to test.
        /// </param>
        /// <returns>
        /// <c>true</c> when <paramref name="kind"/> belongs on the command port.
        /// </returns>
        public static bool IsAdmin(ChatMessageKind kind)
        {
            return (byte)kind >= LowestAdmin;
        }
    }
}
