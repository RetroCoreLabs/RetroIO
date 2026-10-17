//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Protocols.Xmsg.Fa;
using RetroIO.Protocols.Xmsg.Ndfs;
using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Qform;

namespace RetroIO.Protocols.Xmsg.Servers.Fa
{
    /// <summary>
    /// The COSMOS remote file-access server, <c>*FA-SERVER</c>, backed by one Windows folder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the JOIN between three pieces that already existed and had never been connected:
    /// <c>FaExchangeCodec</c> / <c>FaListFilesCodec</c> (the wire format),
    /// <see cref="FaServerConversation"/> (the reply envelope, replayed byte for byte against the
    /// captured conversations), and <see cref="FolderFileStore"/> (a Windows folder as a file store).
    /// It adds no new wire knowledge of its own.
    /// </para>
    /// <para><b>The rule that governs every path in here: ALWAYS ANSWER A REQUEST.</b></para>
    /// <para>
    /// On 2026-08-02 a node accepted the link and then said nothing to an FA request, and the calling
    /// SINTRAN machine's terminal hung - ESC did not abort it. So an operation we cannot do is
    /// answered with an <see cref="FaServerStatus"/> refusal, a body that will not parse is answered
    /// with <see cref="FaServerStatus.BadRequest"/>, and a request from an endpoint we have no
    /// session for opens one rather than being dropped. There is deliberately no path from "a request
    /// arrived" to "no frame went out".
    /// </para>
    /// <para><b>What is honest about this server, and what is not</b></para>
    ///  - The reply ENVELOPE is the one <see cref="FaServerConversation"/> reproduces from the
    ///    captures. VERIFIED there, unchanged here.
    ///  - The directory-listing reply body is built by <c>FaListFilesCodec</c>, whose shape came from
    ///    a captured listing. The 64-byte entries inside it, however, are SYNTHESISED from Windows
    ///    file facts (see <see cref="FaFolderEntry"/>) - a Windows folder cannot produce a real
    ///    SINTRAN object entry.
    ///  - The open, read and close layouts come from
    ///    <c>DOC\captures\FA-READ-WRITE-2026-08-04</c>, where a real machine reads a 17905-byte file
    ///    in nine steps. The request, the empty reply, the 1032-byte data messages and their
    ///    counter rule are all measured there.
    ///  - A real client has since READ AND WRITTEN a file through this server. CORRECTED
    ///    2026-08-06: this list used to say delivery was UNVERIFIED and that write, create and
    ///    delete were refused. A 12690-byte file has been read off the server and written back to
    ///    it byte-for-byte from a live D100, and write, delete, set-block-size and the
    ///    <c>SiiiSpecial</c> family are all dispatched below.
    ///  - Create is served but has NOT been driven live. <c>COPY-FILE</c> does not send it - it
    ///    opens with the quotes kept in the name - so no client we have exercises the path.
    ///  - Three operations remain uncaptured and are refused rather than guessed: <c>0x01</c>,
    ///    <c>0x04</c> and <c>0x0D</c>. A refusal is an ANSWER; the caller does not hang.
    /// </remarks>
    public sealed class FaServer : IXmsgServer
    {
        /// <summary>
        /// The registered name a client addresses in its XSLET letter.
        /// </summary>
        /// <remarks>
        /// VERIFIED from the capture write-up
        /// <c>DOC/XMSG-FA-SERVER-REQUEST-CAPTURED-2026-07-28.md</c>: <c>LIST-FILES d102(system).</c>
        /// writes a letter whose first string parameter is exactly <c>*FA-SERVER</c>.
        /// </remarks>
        public const string ServerName = "*FA-SERVER";

        /// <summary>
        /// The logical port this server registers on.
        /// </summary>
        /// <remarks>
        /// <b>OUR CHOICE, not a well-known number.</b> Ports are the kernel's port-table index for
        /// whatever port a server happened to open, so they move with load order - two boots gave
        /// <c>*XM-FIDO</c> ports 3 and 4. Only port 0 (XROUT) is known in advance. 4 is used here
        /// simply because it does not collide with the TAD server's 2.
        /// </remarks>
        public const int ServerLogicalPort = 4;

        /// <summary>
        /// Byte offset at which the file-access body starts inside a datagram: the 14-byte SINTRAN
        /// header followed by the 14-byte XMSG sub-header.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>APPLIED 2026-08-04.</b> <c>SintranHeader.Size + XmsgSubHeader.Size</c> is now 14 + 14
        /// = 28 and <see cref="SessionBody"/> uses it. <c>FaBodyOffsetTests</c> checks on a real
        /// captured datagram BOTH that 28 parses and that 32 does not.
        /// </para>
        /// <para>
        /// The SINTRAN header is 14 bytes, not 13. Word 6 is a ones-complement sum of words 0-5 with
        /// end-around carry, carved from the XMSG kernel and verified on 3595 of 3595 frames, so the
        /// byte at offset 13 that the current model calls the sub-header's <c>Counter</c> is that
        /// checksum's low half. That is why the sub-header at 14 opens with the marker
        /// <c>0x21 0x00</c>, and why its last word always equals Flags2 (1069 of 1069 frames).
        /// </para>
        /// <para>
        /// Both sides moved together, which is what made it safe: the sub-header is 14 bytes, so
        /// <c>BuildDatagram</c> now writes the reply body at 28 as well. Nothing is inserted ahead
        /// of the FA message type any more.
        /// </para>
        /// </remarks>
        private const int FileAccessBodyOffset = 28;

        /// <summary>
        /// The server's registered DIRECTORY port: logical port 4 in the high nine bits,
        /// <c>0x57</c> in the low seven.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the port the <c>*FA-SERVER</c> NAME resolves to. It is NOT the port replies are
        /// sent from - see <see cref="FaServerSession.SessionWirePort"/>. CORRECTED 2026-08-04:
        /// this used to be described as "the wire reply-from port" and every reply really did go
        /// out from it, which is not what a real server does.
        /// </para>
        /// <para>
        /// VERIFIED across the whole of
        /// <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-read.txt</c>. The connect letter is
        /// addressed to port <c>0x0000</c> - the XROUT directory, not to a server port at all:
        /// </para>
        /// <code>
        /// line 9   D100 100:0x0812 -> 102:0x0000   the *FA-SERVER letter
        /// line 13  D102 102:0x06B6 -> 100:0x0812   the ConnectionConfirm
        /// </code>
        /// <para>
        /// D102 answers from <c>0x06B6</c>, a port that appears NOWHERE in the letter, and every
        /// one of the several hundred later FA messages in that conversation runs between
        /// <c>102:0x06B6</c> and <c>100:0x0812</c> - the pair never changes. So the server answers
        /// from an endpoint of its own and hands it to the client by being the source of the
        /// confirmation.
        /// </para>
        /// <para>
        /// CORRECTED AGAIN 2026-08-10. That paragraph used to end "the server allocates a FRESH
        /// endpoint for the CONVERSATION", and we implemented it that way. It was an inference
        /// from a capture holding exactly one conversation, which cannot tell a per-conversation
        /// port from a per-server one. Captures with two conversations show a per-SERVER port, and
        /// the per-conversation reading broke a live run - see <see cref="_replyPort"/>.
        /// </para>
        /// <para>
        /// The low seven bits are arbitrary (a real kernel draws them from ZRAND). <c>0x57</c> keeps
        /// this clear of <c>0x0211</c>, the first port <c>XmsgServerHost.AllocateSessionPort</c>
        /// hands out.
        /// </para>
        /// </remarks>
        public const ushort FaServerWirePort = (ServerLogicalPort << 7) | 0x57;

        /// <summary>
        /// The most concurrent client conversations this server will hold.
        /// </summary>
        private const int MaxSessions = 10;

        /// <summary>
        /// The XROUT service byte that marks an XSLET letter (send a letter).
        /// </summary>
        private const byte XsletServiceByte = 0x41;

        /// <summary>
        /// The XMCSM control/service word of the connect letter and of our accept.
        /// </summary>
        private const uint XsletLetterControlService = (uint)XmcsmService.XsletLetter;

        /// <summary>
        /// The first connection number the confirmation stamps; later connections count up.
        /// </summary>
        /// <remarks>
        /// <b>INFERRED starting value.</b> Word 2 of the confirmation is the SERVER's own
        /// per-connection counter, not anything the client sent - see
        /// <c>FaServerConversation.BuildConnectionConfirm</c> for the five captured values. It
        /// counts up by one per connection and survives across conversations, so where it starts
        /// is a property of how long the server has been up. <c>0x0042</c> is where the
        /// 2026-08-04 capture session happens to begin.
        /// </remarks>
        private const ushort FirstConnectionNumber = 0x0042;

        /// <summary>
        /// The highest connection number we hand out before wrapping back to
        /// <see cref="FirstConnectionNumber"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Why there is a ceiling at all</b></para>
        /// <para>
        /// There did not used to be one, and the runner also persisted the counter across restarts
        /// in blocks of 64, so it climbed for ever. It reached <c>0x0E02</c>. A live D100 then
        /// ignored our connect confirmation completely - no error, no reject, it simply re-sent its
        /// letter until the terminal timed out. Resetting the counter into this range made a whole
        /// conversation run on the next attempt, 2026-08-09.
        /// </para>
        /// <para><b>What is measured, and what is NOT</b></para>
        /// <para>
        /// Measured: every connection number a real machine has ever sent us is small -
        /// <c>0004</c>, <c>0006</c>, <c>003F</c>, <c>0040</c>, <c>0042</c>, <c>0046</c> - and
        /// <c>0x0E02</c> demonstrably fails. NOT measured: where the real limit is. This ceiling is
        /// a judgement that keeps us in the same order of magnitude as the captures, nothing more.
        /// </para>
        /// <para>
        /// Do NOT read this as "the connection number was the first-connect stall". That theory was
        /// tested and DISPROVED on 2026-08-06 - a restart answering with a fresh <c>0x0082</c>
        /// stalled anyway. This bound stops an unbounded climb; it does not explain that stall.
        /// </para>
        /// </remarks>
        private const ushort LastConnectionNumber = 0x00FF;

        /// <summary>
        /// Byte offset, inside the connect letter's trailing extras, of the word the confirmation
        /// echoes.
        /// </summary>
        /// <remarks>
        /// The extras run <c>07E2 0000 word 6400 A200 FF00</c>, so the echoed word is the
        /// third of them. VERIFIED on the two connects in
        /// <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-read.txt</c>, whose extras differ
        /// (<c>0002</c> and <c>0004</c>) and whose confirmations differ the same way.
        /// </remarks>
        private const int LetterEchoWordOffset = 4;

        /// <summary>
        /// The default echoed word, used when the letter carried no extras to read it from.
        /// </summary>
        private const ushort DefaultEchoedRequestWord = 0x0002;

        /// <summary>
        /// The frame-flags byte of the connection confirmation, <c>0x82</c> in the capture.
        /// </summary>
        private const byte ConfirmFrameFlags = 0x82;

        /// <summary>
        /// The role byte of the connection confirmation, <c>0x84</c> in the capture.
        /// </summary>
        private const byte ConfirmRole = 0x84;

        /// <summary>
        /// The three-byte "nothing further to say" QFORM tail: the end-of-list selector alone.
        /// </summary>
        private static readonly byte[] OkFields =
        {
            (byte)QformTagByte.Selector, 0x00, 0xFF,
        };

        private readonly FolderFileStore _store;
        private readonly Dictionary<uint, FaServerSession> _sessions;
        private readonly List<FaServerSession> _sessionList;

        /// <summary>
        /// The one port every conversation is answered from, or zero before the first connect
        /// letter has been answered.
        /// </summary>
        /// <remarks>
        /// <para><b>One port for the server's whole life, NOT one per conversation</b></para>
        /// <para>
        /// This used to be a port per conversation, allocated in
        /// <see cref="GetOrCreateSession"/> and released again when the conversation closed. That
        /// is not what a real server does, and it cost a live run: D100 ran one conversation
        /// against us perfectly, closed it, opened a second, and then ignored our confirmation
        /// eleven times before giving up with "NO ANSWER FROM REMOTE SYSTEM; FILE-ACCESS
        /// CONNECTION ABORTED" and tearing the link down. The only thing that had changed between
        /// the confirmation it accepted and the ones it ignored was the port we answered from.
        /// </para>
        /// <para>
        /// MEASURED, across three captures on three days, with two different client ports and four
        /// different connection numbers - D100 answers every one of them from <c>0x05B9</c>:
        /// </para>
        /// <code>
        /// nd-to-nd-write.pcapng        client 0x03F9  server 0x05B9   two conversations, one port
        /// readback-10-blocks.pcapng    client 0x03A6  server 0x05B9
        /// readback-proves-content      client 0x03A6  server 0x05B9
        /// </code>
        /// <para>
        /// The second line of the write capture is the case that settles it: the client releases
        /// one conversation, opens another, and the server confirms the new one from the SAME
        /// port. A different server has a different port - D102 is <c>0x06B6</c> throughout
        /// <c>capture-read.txt</c> - so the port belongs to the server, not to the conversation.
        /// </para>
        /// <para><b>What is NOT established</b></para>
        /// <para>
        /// That the moved port is the CAUSE of the refusal rather than something that travels with
        /// it. No capture shows a real client rejecting a confirmation from a moved port, because
        /// no real server has ever moved one. This reproduces what real servers do; it does not
        /// prove what real clients check.
        /// </para>
        /// <para>
        /// Whether the port survives an XMSG restart on the real machine is also unknown, and does
        /// not matter here: ours is allocated once per server and that is already stable across
        /// every conversation, which is the property the captures show.
        /// </para>
        /// </remarks>
        private ushort _replyPort;

        /// <summary>
        /// The connection number the next confirmation will stamp.
        /// </summary>
        private ushort _nextConnectionNumber = FirstConnectionNumber;

        /// <summary>
        /// Gets or sets the connection number the NEXT connect letter will be answered with.
        /// </summary>
        /// <remarks>
        /// <para><b>Its scope is the SERVER'S LIFETIME, not a conversation</b></para>
        /// Of the three counters in this protocol, two are per-conversation and reset with it - the
        /// session-header byte (base 0, see <c>FaServerConversation.NextMessageCounter</c>) and the
        /// short-acknowledgement counter (base 1, see
        /// <see cref="FaServerSession.NextShortAckCounter"/>). Measured in
        /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-read.txt</c>, where the ack counter runs
        /// <c>01</c>..<c>0F</c> under conversation <c>0002</c> and restarts at <c>01</c> the moment
        /// conversation <c>0004</c> begins.
        /// <para>
        /// This one does NOT restart. It counts up per CONNECTION and carries across conversations,
        /// which is why the captures show <c>0042</c>, <c>0043</c>, <c>0044</c>, <c>0045</c> over
        /// separate sessions on different days.
        /// </para>
        /// <para><b>Why it is settable</b></para>
        /// A real server keeps this for as long as it is up. Ours is a process restarted constantly
        /// during development, and each restart handed out <c>0x0042</c> again - a number the peer
        /// had already seen. The host therefore seeds it from a state file, the same way the runner
        /// already persists the datagram sequence.
        /// <para><b>This was NOT the first-connect stall - that theory was DISPROVED</b></para>
        /// The stall (a connect letter answered, then silence, then a retry minutes later that
        /// works) was what prompted this. Tested 2026-08-06 with the state file in place: a restart
        /// answered the first connect with a fresh <c>0x0082</c> and stalled exactly the same way.
        /// The cause is something else that resets with the process. Persisting this is still right
        /// on its own terms - it is what the captured numbering does - but do not credit it with
        /// curing the stall.
        /// </remarks>
        public ushort NextConnectionNumber
        {
            get { return _nextConnectionNumber; }
            set { _nextConnectionNumber = ClampToRange(value); }
        }

        /// <summary>
        /// Takes the next connection number and steps the counter, wrapping at
        /// <see cref="LastConnectionNumber"/>.
        /// </summary>
        /// <returns>
        /// The number to put in the confirmation.
        /// </returns>
        private ushort NextConnectionNumberInRange()
        {
            ushort taken = ClampToRange(_nextConnectionNumber);

            _nextConnectionNumber = taken >= LastConnectionNumber
                ? FirstConnectionNumber
                : (ushort)(taken + 1);

            return taken;
        }

        /// <summary>
        /// Brings a connection number back inside the range real machines use.
        /// </summary>
        /// <param name="value">
        /// The number to check.
        /// </param>
        /// <returns>
        /// <paramref name="value"/> when it is already in range, otherwise
        /// <see cref="FirstConnectionNumber"/>.
        /// </returns>
        /// <remarks>
        /// Applied on the SETTER as well as on each step, because the number that arrives from the
        /// runner's persisted state is exactly the one that had climbed to <c>0x0E02</c>. Clamping
        /// only on the way out would have let a stale state file put us straight back there.
        /// </remarks>
        private static ushort ClampToRange(ushort value)
        {
            if (value < FirstConnectionNumber || value > LastConnectionNumber)
            {
                return FirstConnectionNumber;
            }

            return value;
        }

        /// <summary>
        /// The owning user index written into every synthesised directory entry.
        /// </summary>
        private readonly byte _userIndex;

        /// <summary>
        /// The directory name reported for the served folder.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this value</b></para>
        /// A SINTRAN listing heads its files with the directory they came from, as in
        /// <c>D100.(PACK-ONE:SYSTEM)SINTRAN:DATA;1</c>. We serve a Windows folder and have no pack,
        /// so the name is a CHOICE. <c>PACK-ONE</c> is what the real machines in this setup call
        /// theirs, which keeps our listings looking like theirs.
        /// <para><b>Not yet configurable</b></para>
        /// It belongs in the topology file beside the served root once there is a reason to vary it.
        /// </remarks>
        public const string DirectoryName = "PACK-ONE";

        /// <summary>
        /// The user name reported for the served folder.
        /// </summary>
        /// <remarks>
        /// The other half of the listing header, as in
        /// <c>D100.(PACK-ONE:SYSTEM)SINTRAN:DATA;1</c>. A CHOICE for the same reason as
        /// <see cref="DirectoryName"/>: there is no user directory behind a Windows folder.
        /// </remarks>
        public const string UserName = "SYSTEM";

        /// <summary>
        /// Drops the leading operation and sequence pair from a codec-built body.
        /// </summary>
        /// <param name="full">
        /// The whole body as the codec writes it.
        /// </param>
        /// <returns>
        /// The body from the third tagged field onwards.
        /// </returns>
        /// <remarks>
        /// <c>FaListFilesCodec</c> builds the WHOLE body including the operation and sequence pair,
        /// and <c>FaServerConversation</c> writes those two itself, so the first six bytes would
        /// otherwise be written twice.
        /// </remarks>
        private static byte[] StripOperationAndSequence(byte[] full)
        {
            byte[] fields = new byte[full.Length - 6];
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i] = full[i + 6];
            }

            return fields;
        }

        /// <summary>
        /// Initialises the server over a folder.
        /// </summary>
        /// <param name="store">
        /// The Windows folder presented as a file store.
        /// </param>
        /// <param name="userIndex">
        /// The user index stamped into synthesised directory entries. There is no real user directory
        /// behind a Windows folder, so this is whatever user we are pretending to serve as.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="store"/> is null.
        /// </exception>
        public FaServer(FolderFileStore store, byte userIndex = 0)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _userIndex = userIndex;
            _sessions = new Dictionary<uint, FaServerSession>();
            _sessionList = new List<FaServerSession>();
        }

        /// <summary>
        /// Occurs for every step worth following in a live log: a letter arriving, a request, the
        /// reply that went back.
        /// </summary>
        /// <remarks>
        /// Lines are prefixed <c>[fa]</c> so they read alongside the runner's own <c>[runner]</c> /
        /// <c>[tad]</c> lines in <c>xmsg-d19999.log</c>.
        /// </remarks>
        public event XmsgLogHandler? Log;

        /// <summary>
        /// Gets the folder being served.
        /// </summary>
        public string RootFolder
        {
            get { return _store.RootFolder; }
        }

        /// <summary>
        /// Gets the registered server name (<c>*FA-SERVER</c>).
        /// </summary>
        public string Name
        {
            get { return ServerName; }
        }

        /// <summary>
        /// Gets the logical port this server registers on.
        /// </summary>
        public int LogicalPort
        {
            get { return ServerLogicalPort; }
        }

        /// <summary>
        /// Gets the wire reply-from port.
        /// </summary>
        public ushort WirePort
        {
            get { return FaServerWirePort; }
        }

        /// <summary>
        /// Gets the number of open client conversations.
        /// </summary>
        public int SessionCount
        {
            get { return _sessions.Count; }
        }

        /// <summary>
        /// Gets the most concurrent conversations this server accepts.
        /// </summary>
        public int SessionCapacity
        {
            get { return MaxSessions; }
        }

        /// <summary>
        /// Gets a value indicating whether output advances on the remote's ACK. It does not: this
        /// server answers request by request and never streams.
        /// </summary>
        public bool AdvancesOutputOnAck
        {
            get { return false; }
        }

        /// <summary>
        /// Handles a datagram routed to this server: the XSLET connect letter, or a file-access
        /// request on one of our ports.
        /// </summary>
        /// <param name="incoming">
        /// The received datagram.
        /// </param>
        /// <param name="transport">
        /// The node transport used to build reply frames.
        /// </param>
        /// <returns>
        /// The frames to transmit, in order. Empty ONLY when the datagram was not a request (a bare
        /// acknowledgement), or when it carries no endpoint to answer to.
        /// </returns>
        public IReadOnlyList<XmsgFrame> Handle(XmsgFrame incoming, IXmsgServerTransport transport)
        {
            if (incoming == null || incoming.Header == null || incoming.SubHeader == null)
            {
                // There is no return address in a frame with no sub-header, so there is nothing that
                // COULD be sent. This is the one case where silence is not a choice.
                Log?.Invoke("[fa] a datagram with no sub-header arrived; it carries no return address, so nothing can be answered");
                return Array.Empty<XmsgFrame>();
            }

            if (transport == null)
            {
                throw new ArgumentNullException(nameof(transport));
            }

            if (incoming.SubHeader.DestinationPort == 0x0000
                && (incoming.XmcsmWithBodyWord & 0xFF) == XsletServiceByte)
            {
                return OnConnect(incoming, transport);
            }

            // Any other datagram that reached us is session traffic. Create the session if we have
            // none - a request must never fall through unanswered just because we lost the accept.
            FaServerSession session = GetOrCreateSession(incoming, transport);
            return OnSessionMessage(session, incoming, transport);
        }

        /// <summary>
        /// Returns true when the port is this server's registered directory port or a port one of
        /// its conversations holds.
        /// </summary>
        /// <remarks>
        /// Both matter: the directory port is where a name lookup lands, and the reply port is
        /// where the conversations actually run once the confirmation has handed it over. There is
        /// only ever ONE reply port - see <see cref="_replyPort"/> - so this is a comparison
        /// rather than a lookup, and it stays true after a conversation closes, which is exactly
        /// what lets the next one arrive.
        /// </remarks>
        /// <param name="port">
        /// The destination wire port of an incoming datagram.
        /// </param>
        /// <returns>
        /// True when this server owns the port.
        /// </returns>
        public bool OwnsPort(ushort port)
        {
            return port == FaServerWirePort || (_replyPort != 0 && port == _replyPort);
        }

        /// <summary>
        /// Drains queued asynchronous output. This server has none - every reply is produced by
        /// <see cref="Handle"/> while the request is being handled.
        /// </summary>
        /// <param name="transport">
        /// The node transport (unused).
        /// </param>
        /// <returns>
        /// An empty list.
        /// </returns>
        public IReadOnlyList<XmsgFrame> DrainPending(IXmsgServerTransport transport)
        {
            return Array.Empty<XmsgFrame>();
        }

        /// <summary>
        /// Notes that the remote acknowledged one of our frames. Nothing here is windowed, so there
        /// is nothing to release.
        /// </summary>
        /// <param name="remoteNode">
        /// The node that acknowledged.
        /// </param>
        /// <param name="ackedFlags1">
        /// The Flags 1 the acknowledgement echoes.
        /// </param>
        public void NotifyAck(ushort remoteNode, ushort ackedFlags1)
        {
        }

        /// <summary>
        /// Opens a conversation for a connect letter and returns the file-access connection
        /// confirmation.
        /// </summary>
        /// <param name="request">
        /// The connect letter.
        /// </param>
        /// <param name="transport">
        /// The node transport.
        /// </param>
        /// <returns>
        /// The confirmation frame, or an empty list when we are at capacity.
        /// </returns>
        /// <remarks>
        /// <para>
        /// <b>CORRECTED 2026-08-04.</b> This used to answer with the TAD-shaped generic XSLET
        /// accept (body <c>0041 0008 01020000 0202000A</c>), citing
        /// <c>DOC/XMSG-XSLET-ACCEPT-VS-XRNRO-2026-07-28.md</c>'s finding that the accept form is
        /// GENERIC rather than per-server. A live capture DISPROVES that for file access.
        /// </para>
        /// <para>
        /// In <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-read.txt</c> D100 sends the
        /// <c>*FA-SERVER</c> letter (the one containing <c>2A46412D534552564552</c>) and D102
        /// answers with a secure ACK (subtype 03) and then a DATA frame:
        /// </para>
        /// <code>
        /// 2113 000E 0064 0066 01F9 0008 DC13   SINTRAN header (dest 100, src 102, Flags2 0x0008)
        /// 2100 82 84 0064 0812 0066 06B6 0008  XMSG sub-header, XMCSM 0x0008
        /// 07D2 0002 0042 6400                  the BODY, at absolute 28
        /// </code>
        /// <para>
        /// <c>0x07D2</c> is <c>FaMessageType.ConnectionConfirm</c> and <c>0x0002</c> is
        /// <c>FaExchangeCodec.ResponderConversation</c> - both VERIFIED. XMCSM <c>0x0008</c> is the
        /// body's byte length, which is also what Flags 2 carries. The trailing <c>0042 6400</c> is
        /// derived and INFERRED in the two places named in
        /// <c>FaServerConversation.BuildConnectionConfirm</c>: word 1 is ECHOED from the letter's
        /// trailing extras, word 2 is our own connection counter (see
        /// <see cref="FirstConnectionNumber"/>), and word 3 is the constant
        /// <see cref="FaExchangeCodec.ConfirmTrailingWord"/> - NOT a system number, whatever the
        /// value <c>0x6400</c> once suggested.
        /// </para>
        /// <para>
        /// The letter also carries eight raw bytes past the declared XROUT length
        /// (<c>07 E2 00 00 00 02 64 00</c> in this capture) and a four-byte appended write. Their
        /// meaning is UNKNOWN, so they are logged and otherwise left alone rather than answered.
        /// </para>
        /// </remarks>
        private IReadOnlyList<XmsgFrame> OnConnect(XmsgFrame request, IXmsgServerTransport transport)
        {
            ushort clientSystem = request.SubHeader!.SourceSystem;
            ushort clientPort = request.SubHeader.SourcePort;

            if (_sessions.Count >= MaxSessions
                && !_sessions.ContainsKey(SessionKey(clientSystem, clientPort)))
            {
                Log?.Invoke($"[fa] connect from system {clientSystem} port 0x{clientPort:X4} REFUSED: all {MaxSessions} conversation slots are in use");
                return Array.Empty<XmsgFrame>();
            }

            FaServerSession session = GetOrCreateSession(request, transport);

            // The confirmation body: 07D2 <echoed letter word> <our connection number> <system> 00.
            ushort echoed = ReadLetterEchoWord(request);

            // A RETRANSMITTED LETTER GETS THE SAME NUMBER BACK. A real ND client repeats its
            // connect letter until it sees a confirmation, and allocating a fresh connection for
            // each repeat is not merely wasteful - it is fatal.
            //
            // MEASURED against D100 on 2026-08-10: it repeated ONE letter and we answered ELEVEN
            // times with 0x0043..0x004D. D100 stopped acknowledging our datagrams partway through
            // that burst and then dropped the LINK with ND frame kind 0x6F. The file push running
            // over the same link died with it - its SetBlockSize went out in the middle of the
            // flood and was never answered - so this responder defect was masquerading as a
            // client-side stall.
            //
            // Keyed on the session, which is already keyed on (client system, client port), so a
            // genuinely NEW conversation from the same client on a new port still gets its own
            // number.
            ushort connectionNumber = session.ConfirmedConnectionNumber;
            if (connectionNumber == 0)
            {
                connectionNumber = NextConnectionNumberInRange();
                session.ConfirmedConnectionNumber = connectionNumber;
            }

            byte[] confirm = session.Conversation.BuildConnectionConfirm(
                echoed, connectionNumber, FaExchangeCodec.ConfirmTrailingWord);

            Log?.Invoke(
                $"[fa] connect letter from node {request.Header!.SourceNode} (system {clientSystem} port 0x{clientPort:X4}); "
                + $"serving {_store.RootFolder}; answering from session port 0x{session.SessionWirePort:X4} "
                + $"with an FA ConnectionConfirm {Convert.ToHexString(confirm)}");

            // From the SESSION port, not the registered directory port - see FaServerWirePort.
            // The client learns the endpoint to continue on from this frame's source port, exactly
            // as D102 hands over 0x06B6 in the capture.
            // ANSWERING the letter, so the confirmation echoes its Flags 1 - the capture's
            // L 9 F1=0x01F9 letter and L13 F1=0x01F9 confirmation.
            List<XmsgFrame> outgoing = new List<XmsgFrame>(1);

            // A REPEAT REPLAYS THE SAME DATAGRAM. Building a second one would take a fresh Flags 1
            // and the client would see a brand-new message rather than the answer it is waiting
            // for. See FaServerSession.ConfirmationSent for the measurement: D100 resent dozens of
            // messages and never once changed their Flags 1, while our confirmation went out under
            // eleven different numbers in a single run.
            if (session.ConfirmationSent != null)
            {
                outgoing.Add(session.ConfirmationSent);
                return outgoing;
            }

            XmsgFrame built = transport.BuildBodyDatagram(
                session.RemoteNode, session.ClientSystem, session.ClientPort,
                session.SessionWirePort, (ushort)confirm.Length,
                ConfirmFrameFlags, ConfirmRole, confirm,
                // Our own outgoing count, not the letter's number - see ChooseFlags1. These are
                // equal on a conversation that starts in step, which is why echoing looked right.
                XmsgAnsweredFlags1.None);

            session.ConfirmationSent = built;
            outgoing.Add(built);
            return outgoing;
        }

        /// <summary>
        /// Reads the word a connect letter carries past its declared XROUT length, which the
        /// confirmation echoes.
        /// </summary>
        /// <param name="request">
        /// The connect letter.
        /// </param>
        /// <returns>
        /// The echoed word, or <see cref="DefaultEchoedRequestWord"/> when the letter has no
        /// extras.
        /// </returns>
        /// <remarks>
        /// The letter's BODY is read rather than its <c>TrailingBytes</c>, because those two mean
        /// different things depending on where the frame came from: a PARSED letter keeps only the
        /// bytes past the declared length there, while a frame built in memory keeps the whole
        /// body. The body plus the declared length is unambiguous either way. Body layout, from
        /// the captured letter:
        /// <code>
        /// 1B41 0012   FF0A 2A46412D534552564552 FE04 44313032   07E2 0000 0002 6400 A200 FF00
        /// serial/svc  ---- 18 bytes of parameter blocks -----   ---------- the extras ---------
        /// </code>
        /// </remarks>
        private static ushort ReadLetterEchoWord(XmsgFrame request)
        {
            byte[] body = request.GetBodyBytes();
            if (body.Length < XroutMessage.HeaderSize)
            {
                return DefaultEchoedRequestWord;
            }

            int declared = (body[2] << 8) | body[3];
            int extrasStart = XroutMessage.HeaderSize + declared;
            int wordAt = extrasStart + LetterEchoWordOffset;
            if (wordAt + 1 >= body.Length)
            {
                return DefaultEchoedRequestWord;
            }

            return (ushort)((body[wordAt] << 8) | body[wordAt + 1]);
        }

        // ConfirmSystemByte lived here and returned the CLIENT's system number for body offset 6.
        // It is gone: the word there is a CONSTANT, not anyone's node number, and a system number
        // could not fit in a byte in the first place. See FaExchangeCodec.ConfirmTrailingWord for
        // the captures that settle it. Its own comment already admitted it was "a reading, not a
        // carved fact" - the reading was wrong.

        /// <summary>
        /// Handles one file-access message on an open conversation.
        /// </summary>
        /// <param name="session">
        /// The conversation.
        /// </param>
        /// <param name="incoming">
        /// The received datagram.
        /// </param>
        /// <param name="transport">
        /// The node transport.
        /// </param>
        /// <returns>
        /// The reply frames. A REQUEST always produces exactly one; an acknowledgement produces none,
        /// because answering an acknowledgement with an acknowledgement never terminates.
        /// </returns>
        private IReadOnlyList<XmsgFrame> OnSessionMessage(
            FaServerSession session, XmsgFrame incoming, IXmsgServerTransport transport)
        {
            ReadOnlySpan<byte> body = SessionBody(incoming);

            FaMessageType messageType;
            ushort conversation;
            byte sequenceByte;
            ushort sessionToken;
            if (!FaExchangeCodec.TryReadEnvelope(body, out messageType, out conversation, out sequenceByte, out sessionToken))
            {
                // Too short to be an FA message at all. Answer anyway - see the class remarks.
                Log?.Invoke($"[fa] {body.Length} byte(s) from system {session.ClientSystem} is too short to be an FA message; answering BadRequest");

                // Too short to read a session-header byte, so the reply carries 0x80 - the value the
                // real server's first reply in a conversation uses.
                return AckThenReply(
                    session, incoming, transport,
                    session.Conversation.BuildReply(default, 0, ErrorFields(FaServerStatus.BadRequest)));
            }

            session.EnsureConversation(conversation);

            // A DATA MESSAGE of a write in progress, before anything tries to parse it as QFORM.
            // After its eight-byte prefix it is raw file content to the end, so walking it as
            // tagged fields decodes file text as tags - it is recognised by a write being armed
            // and by the message being exactly one block long, never by parsing it.
            if (session.WriteOffset != FaServerSession.NoWriteInProgress
                && messageType == FaMessageType.Request
                && body.Length >= FaFileDataCodec.DataMessageLength)
            {
                return OnWriteBlock(session, incoming, transport, body, sequenceByte);
            }

            if (messageType == FaMessageType.ShortAck)
            {
                // An acknowledgement is not a request. Answering it with another acknowledgement is
                // an endless ping-pong, which is why this is the one message we say nothing to.
                Log?.Invoke($"[fa] short acknowledgement from system {session.ClientSystem}, conversation 0x{conversation:X4}");
                return Array.Empty<XmsgFrame>();
            }

            // The whole 0x078x family means "I am finished" - see
            // FaMessageTypeExtensions.IsSessionFinished. This used to test two exact values, and
            // D100 ended a COMPLETED listing with a third, 0x0788, which we answered with a
            // BadRequest and which cost us a spurious reconnect.
            if (messageType.IsSessionFinished())
            {
                // "I am finished" - the real server answers this with a Close, and so do we. See
                // FaMessageType.SessionFinished for the captured teardown. Answering it as an
                // unreadable request (which is what happened before, because it carries no
                // operation/sequence pair) made D100 abandon the session and reconnect in a loop.
                Log?.Invoke(
                    $"[fa] system {session.ClientSystem} has finished with conversation 0x{conversation:X4} "
                    + $"(0x{(ushort)messageType:X4}); answering with a close");
                XmsgFrame finishedReply = BuildRawReply(session, incoming, transport, session.Conversation.BuildClose());
                CloseSession(session);
                return One(finishedReply);
            }

            if (messageType == FaMessageType.Close)
            {
                Log?.Invoke($"[fa] close from system {session.ClientSystem}, conversation 0x{conversation:X4}; answering with our close");
                XmsgFrame closeReply = BuildRawReply(session, incoming, transport, session.Conversation.BuildClose());
                CloseSession(session);
                return One(closeReply);
            }

            if (messageType == FaMessageType.ConnectionConfirm)
            {
                // The confirmation is a message the SERVER sends. Seeing one here means the peer is
                // acting as the server, which is not a request to us.
                Log?.Invoke($"[fa] a connection confirmation arrived from system {session.ClientSystem}; that is a server-side message, so nothing is answered");
                return Array.Empty<XmsgFrame>();
            }

            FaOperation operation;
            ushort sequence;
            if (!FaExchangeCodec.TryReadOperation(body, out operation, out sequence))
            {
                Log?.Invoke($"[fa] message type 0x{(ushort)messageType:X4} from system {session.ClientSystem} carries no operation/sequence pair; answering BadRequest");
                return AckThenReply(
                    session, incoming, transport,
                    session.Conversation.BuildReply(default, 0, ErrorFields(FaServerStatus.BadRequest)));
            }

            // A REPEAT OF THE REQUEST WE JUST ANSWERED GETS THE SAME BYTES BACK.
            //
            // Not a fresh reply built from the conversation's counter. A real D100 asked for
            // directory entry 0 nine times - the same datagram retransmitted, every request byte
            // for byte identical - and each fresh answer carried a counter it had never asked for
            // (8200, 8300, 8400 ... where it was waiting on 8100), so it never advanced and the
            // listing died. Measured 2026-08-11. See FaServerSession.RememberReply.
            //
            // The dispatch is skipped as well as the numbering: walking the directory again would
            // move the cursor for a question that has already been answered.
            ushort requestFlags1 = incoming.Header != null ? incoming.Header.Flags1 : (ushort)0;
            byte[]? replay = session.ReplayFor(requestFlags1);
            if (replay != null)
            {
                Log?.Invoke(
                    $"[fa] system {session.ClientSystem} re-sent the request at datagram "
                    + $"Flags 1 0x{requestFlags1:X4}; replaying the same reply rather than building a new one");
                return AckThenReply(session, incoming, transport, replay);
            }

            byte[]? fields = Dispatch(session, operation, sequence, body);

            if (fields == null)
            {
                // A WriteFile that was ARMED rather than answered. Its reply belongs after the
                // content, so all that goes out now is the acknowledgement of the request itself.
                // See ArmWrite: a write is a read backwards.
                return One(BuildShortAck(session, incoming, transport));
            }

            byte[] reply = session.Conversation.BuildReply(operation, sequence, fields);
            session.RememberReply(requestFlags1, reply);

            return AckThenReply(session, incoming, transport, reply);
        }

        /// <summary>
        /// Returns the two frames a request is answered with: the short acknowledgement at the
        /// REQUEST's Flags 1, then the reply as a new exchange one higher.
        /// </summary>
        /// <param name="session">
        /// The conversation.
        /// </param>
        /// <param name="incoming">
        /// The request being answered.
        /// </param>
        /// <param name="transport">
        /// The node transport.
        /// </param>
        /// <param name="replyBody">
        /// The FA reply body. The reply FRAME is assembled inside this method, AFTER the
        /// acknowledgement, because the two must take their Flags 1 in that order - building the
        /// reply first would hand it the request's number and the acknowledgement the one after.
        /// </param>
        /// <returns>
        /// The acknowledgement followed by the reply.
        /// </returns>
        /// <remarks>
        /// <para>
        /// THE MISSING HALF OF EVERY EXCHANGE. Until 2026-08-04 this server answered a request with
        /// the reply alone, stamped with the request's Flags 1. The real server does BOTH, and the
        /// pattern repeats unbroken through
        /// <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-list-files.txt</c>:
        /// </para>
        /// <code>
        /// f1=018D  100->102  07F0 SiiiSpecial request  sess=8100D761
        /// f1=018D  102->100  07A2 ShortAck             sess=0200922A
        /// f1=018E  102->100  07F0 the reply            sess=810090BB
        /// f1=018E  100->102  07A2 ShortAck             sess=02008485
        /// f1=018F  100->102  the next request
        /// </code>
        /// <para>
        /// D100 rejected our directory reply with a subtype-0x07 network error carrying XENSE
        /// (-34 = <c>0xFFDE</c>), a SEQUENCING reject - which is exactly what a missing exchange
        /// looks like from the other end.
        /// </para>
        /// </remarks>
        private IReadOnlyList<XmsgFrame> AckThenReply(
            FaServerSession session, XmsgFrame incoming, IXmsgServerTransport transport, byte[] replyBody)
        {
            XmsgFrame ack = BuildShortAck(session, incoming, transport);

            // Only now, with the link's Flags 1 advanced past the acknowledgement, does the reply
            // take its own number - one higher, as a new exchange.
            XmsgFrame reply = BuildRawReply(session, incoming, transport, replyBody);

            List<XmsgFrame> frames = new List<XmsgFrame>(2);
            frames.Add(ack);
            frames.Add(reply);

            // A ReadFile parks the file content while its reply is built, because the reply carries
            // none. Now that the reply has taken its Flags 1, the content follows as messages of its
            // own. Every other operation leaves nothing parked and this does nothing.
            DeliverPending(session, incoming, transport, frames);

            return frames;
        }

        /// <summary>
        /// Carries out one operation and returns the QFORM fields that follow the echoed operation
        /// and sequence in the reply.
        /// </summary>
        /// <param name="session">
        /// The conversation.
        /// </param>
        /// <param name="operation">
        /// The operation asked for.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence, echoed by the reply and used as the listing serial (they are the
        /// same field on the wire).
        /// </param>
        /// <param name="body">
        /// The whole request body, starting at the message type.
        /// </param>
        /// <returns>
        /// The reply's QFORM fields, ending with the end-of-list selector.
        /// </returns>
        private byte[]? Dispatch(FaServerSession session, FaOperation operation, ushort sequence, ReadOnlySpan<byte> body)
        {
            switch (operation)
            {
                case FaOperation.ReserveFileEntry:
                case FaOperation.ReleaseFileEntry:
                    // The two operations that open and close every captured conversation. They name
                    // a user and directory we do not model - one Windows folder is the whole world
                    // here - so they are accepted and nothing is looked up.
                    Log?.Invoke($"[fa] request {operation} seq {sequence} from system {session.ClientSystem}: accepted");
                    return OkFields;

                case FaOperation.OpenFile:
                    return OpenFile(session, sequence, body);

                case FaOperation.CloseFile:
                    return CloseFile(session, sequence);

                case FaOperation.ReadFile:
                    return ReadFile(session, sequence, body);

                case FaOperation.WriteFile:
                    return ArmWrite(session, sequence, body);

                case FaOperation.SetBlockSize:
                    return SetBlockSize(session, sequence, body);

                case FaOperation.CreateFile:
                    return CreateFile(session, sequence, body);

                case FaOperation.DeleteFile:
                    return DeleteFile(session, sequence, body);

                case FaOperation.SiiiSpecial:
                    return DirectoryEnquiry(session, sequence, body);

                default:
                    // What is left is 0x01, 0x04 and 0x0D - the only FA operations whose request
                    // and reply layouts have never been captured. (This comment used to list write,
                    // create and delete too; all three are dispatched above.) Refusing is the
                    // honest answer, and it is an ANSWER - the caller does not hang.
                    Log?.Invoke($"[fa] request {operation} seq {sequence} from system {session.ClientSystem}: NOT SUPPORTED (its wire layout has never been captured); answering a refusal");
                    return ErrorFields(FaServerStatus.NotSupported);
            }
        }

        /// <summary>
        /// Answers a <see cref="FaSpecialFunction.FileInformation"/> request with the open file's
        /// directory entry.
        /// </summary>
        /// <param name="session">
        /// The conversation.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence, echoed by the reply.
        /// </param>
        /// <returns>
        /// The reply's QFORM fields.
        /// </returns>
        /// <remarks>
        /// <para><b>It asks about the OPEN file and names nothing</b></para>
        /// The request is just the sub-function, so the answer has to come from what the
        /// conversation already holds - which is why <see cref="FaServerSession.OpenName"/> exists.
        /// <para><b>The record is the listing's record</b></para>
        /// Same builder, so the byte length, block count, dates and access rights are whatever a
        /// listing would have reported for the same file. Building a second, differently shaped
        /// record here would be exactly the duplication that has bitten this server before.
        /// <para><b>Found live 2026-08-06</b></para>
        /// While this was refused, <c>COPY-FILE</c> opened the file, got a <c>BadRequest</c> here,
        /// then closed and released without ever issuing a <c>ReadFile</c> - so no file could be
        /// copied off us even though the read path itself worked.
        /// </remarks>
        private byte[] FileInformation(FaServerSession session, ushort sequence)
        {
            if (session.OpenHandle == 0 || session.OpenName.Length == 0)
            {
                Log?.Invoke($"[fa] FileInformation seq {sequence} from system {session.ClientSystem} with no file open; answering a refusal");
                return ErrorFields(FaServerStatus.NoSuchFile);
            }

            // Find the entry the same way a listing would, so the two can never disagree.
            IReadOnlyList<FaFileInfo> all = _store.ListFiles();
            string wantedName;
            string wantedType;
            if (!SplitSpecification(session.OpenName, out wantedName, out wantedType))
            {
                Log?.Invoke($"[fa] FileInformation seq {sequence}: '{session.OpenName}' is not a file specification we can parse; answering a refusal");
                return ErrorFields(FaServerStatus.NoSuchFile);
            }

            for (int i = 0; i < all.Count; i++)
            {
                if (!Matches(all[i].Name, wantedName, wantedType))
                {
                    continue;
                }

                byte[] record = FaFolderEntry.BuildRecord(all[i], _userIndex, i);

                Log?.Invoke(
                    $"[fa] FileInformation seq {sequence} for system {session.ClientSystem}: "
                    + $"{all[i].Name}, {all[i].ByteLength} byte(s)");

                return StripOperationAndSequence(
                    FaListFilesCodec.BuildFileInformationReply(sequence, record));
            }

            Log?.Invoke($"[fa] FileInformation seq {sequence}: '{session.OpenName}' is open but no longer in the folder; answering a refusal");
            return ErrorFields(FaServerStatus.NoSuchFile);
        }

        /// <summary>
        /// Creates the named file in the served folder.
        /// </summary>
        /// <param name="session">
        /// The conversation.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence, echoed by the reply.
        /// </param>
        /// <param name="body">
        /// The whole request body, starting at the message type.
        /// </param>
        /// <returns>
        /// The reply's QFORM fields.
        /// </returns>
        /// <remarks>
        /// <para><b>Both halves are captured</b></para>
        /// <c>claude-create-file-102-to-100-2026-07-30.pcapng</c> carries the request AND its reply.
        /// The reply is the plain accepted form with no value returned - not even the file number,
        /// which <c>OpenFile</c> does report. See <c>FaListFilesCodec.TryReadCreateFile</c>.
        /// <para><b>The page count is read and then deliberately ignored</b></para>
        /// SINTRAN reserves a contiguous run of pages up front; a Windows folder has no equivalent
        /// and needs none, so the file is created empty and grows as it is written. The count is
        /// logged rather than dropped silently, because a client that asked for a size and got a
        /// zero-length file should be visible in the log if it ever objects.
        /// <para><b>This is NOT how COPY-FILE creates a file</b></para>
        /// Measured live on 2026-08-06: <c>COPY-FILE</c> to a new remote name sends no
        /// <c>CreateFile</c> at all - it sends <c>OpenFile</c> with the quotes KEPT in the name, and
        /// <c>FaFileName.TryParse</c> strips them. Both routes now work; they are different clients,
        /// not alternatives we chose between.
        /// </remarks>
        private byte[] CreateFile(FaServerSession session, ushort sequence, ReadOnlySpan<byte> body)
        {
            string named;
            uint pageCount;
            if (!FaListFilesCodec.TryReadCreateFile(
                    body.Slice(FaExchangeCodec.QformOffset), out named, out pageCount))
            {
                Log?.Invoke($"[fa] CreateFile seq {sequence} from system {session.ClientSystem} carries no name and page count; answering BadRequest");
                return ErrorFields(FaServerStatus.BadRequest);
            }

            ushort fileNumber;
            FaStoreStatus status = _store.Create(named, out fileNumber);
            if (status != FaStoreStatus.Ok)
            {
                Log?.Invoke($"[fa] CreateFile '{named}' for system {session.ClientSystem}: {status}");
                return ErrorFields(StatusFor(status));
            }

            Log?.Invoke(
                $"[fa] CreateFile '{named}' for system {session.ClientSystem}: file number {fileNumber}, "
                + $"{pageCount} page(s) asked for (a folder reserves nothing, so the file starts empty)");

            return OkFields;
        }

        /// <summary>
        /// Deletes the named file from the served folder.
        /// </summary>
        /// <param name="session">
        /// The conversation.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence, echoed by the reply.
        /// </param>
        /// <param name="body">
        /// The whole request body, starting at the message type.
        /// </param>
        /// <returns>
        /// The reply's QFORM fields.
        /// </returns>
        /// <remarks>
        /// <para><b>The request is captured; the reply shape is NOT</b></para>
        /// Two real <c>DELETE-FILE</c> requests from D100 on 2026-08-06 pin the request - see
        /// <c>FaListFilesCodec.TryReadDeleteFileName</c>. We refused both, so no captured reply
        /// exists. The plain accepted form is used here because every other accepted operation uses
        /// it - <c>ReserveFileEntry</c>, <c>SetBlockSize</c>, <c>CloseFile</c> - and the captured
        /// <c>SetBlockSize</c> reply confirms that form carries nothing but the echo. If a live
        /// client rejects it, THAT is the thing to change.
        /// <para><b>It deletes for real</b></para>
        /// The served folder is a real Windows folder, so this removes a real file. It is refused
        /// when no file matches rather than reported as success, so a client is never told it
        /// deleted something that is still there.
        /// </remarks>
        private byte[] DeleteFile(FaServerSession session, ushort sequence, ReadOnlySpan<byte> body)
        {
            string named;
            if (!FaListFilesCodec.TryReadDeleteFileName(
                    body.Slice(FaExchangeCodec.QformOffset), out named))
            {
                Log?.Invoke($"[fa] DeleteFile seq {sequence} from system {session.ClientSystem} names no file; answering BadRequest");
                return ErrorFields(FaServerStatus.BadRequest);
            }

            FaStoreStatus status = _store.Delete(named);
            if (status != FaStoreStatus.Ok)
            {
                Log?.Invoke($"[fa] DeleteFile '{named}' for system {session.ClientSystem}: {status}");
                return ErrorFields(StatusFor(status));
            }

            Log?.Invoke($"[fa] DeleteFile '{named}' for system {session.ClientSystem}: deleted");
            return OkFields;
        }

        /// <summary>
        /// Records the transfer block size the client is setting.
        /// </summary>
        /// <param name="session">
        /// The conversation.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence.
        /// </param>
        /// <param name="body">
        /// The whole request body, starting at the message type.
        /// </param>
        /// <returns>
        /// The reply's QFORM fields.
        /// </returns>
        /// <remarks>
        /// <para><b>Why this had to exist before a read could work</b></para>
        /// Found on a live D100 on 2026-08-06: a <c>COPY-FILE</c> reading from us sends
        /// <c>SetBlockSize</c> between <c>OpenFile</c> and the first <c>ReadFile</c>. We refused it
        /// as uncaptured, and the client gave up with <c>NO ANSWER FROM REMOTE SYSTEM;
        /// FILE-ACCESS CONNECTION ABORTED</c>. It was the only thing standing between us and a real
        /// client reading a file.
        /// <para><b>The reply carries no value back</b></para>
        /// Just the echoed operation and sequence, measured in
        /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-read.txt</c> line 54:
        /// <c>07F0 0002 8200 90BB 92 0007 92 0003 F2 00FF</c>. So the plain accepted form is right
        /// and there is nothing to add to it.
        /// <para><b>A size we cannot read is refused, not assumed</b></para>
        /// Silently keeping the default would leave the client and us disagreeing about what a
        /// position means, and every later read would land at the wrong offset.
        /// </remarks>
        private byte[] SetBlockSize(FaServerSession session, ushort sequence, ReadOnlySpan<byte> body)
        {
            ushort blockSize;
            if (!FaFileDataCodec.TryReadBlockSize(body.Slice(FaExchangeCodec.QformOffset), out blockSize))
            {
                Log?.Invoke($"[fa] request SetBlockSize seq {sequence} from system {session.ClientSystem} carries no block size; answering BadRequest");
                return ErrorFields(FaServerStatus.BadRequest);
            }

            session.BlockSize = blockSize;
            Log?.Invoke($"[fa] request SetBlockSize seq {sequence} from system {session.ClientSystem}: block size is now {blockSize} bytes");
            return OkFields;
        }

        /// <summary>
        /// Opens the named file and reports its file number and size.
        /// </summary>
        /// <param name="session">
        /// The conversation.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence, echoed by the reply.
        /// </param>
        /// <param name="body">
        /// The request body.
        /// </param>
        /// <returns>
        /// The reply fields.
        /// </returns>
        /// <remarks>
        /// <para><b>The open file belongs to the conversation</b></para>
        /// The reply reports a file number, and then nothing on the wire ever quotes it again - no
        /// read, write or close in any capture carries one. So the handle is kept on the session and
        /// the number we report is for the client's display, not for it to hand back.
        /// <para><b>One file at a time</b></para>
        /// Every capture opens one file, works on it and closes it. A second open on the same
        /// conversation closes the first, which is the only behaviour consistent with a handle that
        /// is never named.
        /// </remarks>
        private byte[] OpenFile(FaServerSession session, ushort sequence, ReadOnlySpan<byte> body)
        {
            ushort serial;
            string fileName;
            bool forWrite;
            if (!FaOpenFileCodec.TryReadRequest(
                    body.Slice(FaExchangeCodec.QformOffset), out serial, out fileName, out forWrite))
            {
                Log?.Invoke($"[fa] OpenFile seq {sequence} from system {session.ClientSystem} carries no file name; answering BadRequest");
                return ErrorFields(FaServerStatus.BadRequest);
            }

            if (session.OpenHandle != 0)
            {
                _store.Close(session.OpenHandle);
                session.OpenHandle = 0;
            }

            ushort handle;
            FaStoreStatus status = _store.Open(fileName, forWrite, out handle);
            if (status != FaStoreStatus.Ok)
            {
                Log?.Invoke($"[fa] OpenFile '{fileName}' for system {session.ClientSystem}: {status}");
                return ErrorFields(StatusFor(status));
            }

            session.OpenHandle = handle;
            session.OpenForWrite = forWrite;
            session.OpenName = fileName;

            long length = LengthOf(fileName);

            Log?.Invoke(
                $"[fa] OpenFile '{fileName}' for system {session.ClientSystem}: "
                + $"file number {handle}, {length} byte(s), {(forWrite ? "write" : "read")}");

            return StripOperationAndSequence(FaOpenFileCodec.BuildReply(sequence, handle, length));
        }

        /// <summary>
        /// Closes the file the conversation has open.
        /// </summary>
        /// <param name="session">
        /// The conversation.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence, echoed by the reply.
        /// </param>
        /// <returns>
        /// The reply fields.
        /// </returns>
        /// <remarks>
        /// A close carries no file number and no flush parameter. Closing when nothing is open is
        /// answered normally rather than refused: after a FAILED open the client sends no close at
        /// all, so a close we did not expect is more likely our own bookkeeping being wrong than the
        /// client being wrong.
        /// </remarks>
        private byte[] CloseFile(FaServerSession session, ushort sequence)
        {
            if (session.OpenHandle != 0)
            {
                _store.Close(session.OpenHandle);
                Log?.Invoke($"[fa] CloseFile for system {session.ClientSystem}: file number {session.OpenHandle}");
                session.OpenHandle = 0;
                session.OpenForWrite = false;
            }
            else
            {
                Log?.Invoke($"[fa] CloseFile for system {session.ClientSystem} with nothing open; answering normally");
            }

            return StripOperationAndSequence(FaOpenFileCodec.BuildCloseReply(sequence));
        }

        /// <summary>
        /// Reads one block-pair from the open file and parks the bytes for delivery.
        /// </summary>
        /// <param name="session">
        /// The conversation.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence, echoed by the reply.
        /// </param>
        /// <param name="body">
        /// The request body.
        /// </param>
        /// <returns>
        /// The reply fields, which carry no file content.
        /// </returns>
        /// <remarks>
        /// <para><b>The reply announces nothing</b></para>
        /// In <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-read.txt</c> the reply to a read is
        /// <c>92 0008 92 seq F2 00FF</c> - the echo and the end of list. The content arrives as two
        /// separate messages afterwards, so the bytes are parked on the session here and sent by
        /// <see cref="DeliverPending"/> once the reply has gone out.
        /// <para><b>The position counts blocks, not bytes</b></para>
        /// The capture's nine reads run <c>00000000</c> to <c>00000008</c> over a 17905-byte file,
        /// which is nine units of <c>FaFileDataCodec.ReadLength</c>. It is multiplied out here.
        /// <para><b>Reading past the end is not an error</b></para>
        /// The last read of the captured file returns a full 2048 bytes with only 1521 left, so a
        /// short tail is zero padded rather than refused or short counted. The client stops on the
        /// size it learned at open, and inventing an end signal the protocol does not have would be
        /// worse than padding.
        /// </remarks>
        private byte[] ReadFile(FaServerSession session, ushort sequence, ReadOnlySpan<byte> body)
        {
            ushort serial;
            uint position;
            ushort wantedBytes;
            if (!FaFileDataCodec.TryReadRequest(
                    body.Slice(FaExchangeCodec.QformOffset), out serial, out position, out wantedBytes))
            {
                Log?.Invoke($"[fa] ReadFile seq {sequence} from system {session.ClientSystem} carries no position; answering BadRequest");
                return ErrorFields(FaServerStatus.BadRequest);
            }

            if (session.OpenHandle == 0)
            {
                Log?.Invoke($"[fa] ReadFile seq {sequence} from system {session.ClientSystem} with no file open; answering a refusal");
                return ErrorFields(FaServerStatus.NoSuchFile);
            }

            // A whole read every time, zero padded at the tail - see the remarks.
            byte[] delivery = new byte[FaFileDataCodec.ReadLength];

            // THE POSITION COUNTS BLOCKS, AND THE BLOCK IS THE ONE THE CLIENT SET - not the amount
            // we deliver. The two are only the same when the client sent SetBlockSize 2048, which is
            // what COPY-FILE does, and that is why multiplying by ReadLength worked at all.
            //
            // Measured in DOC\FA-READ-WRITE-WIRE-PROTOCOL-2026-08-04.md section 6: the conversation
            // that set 2048 stepped its positions 0, 1, 2, ...; the one that set nothing stepped
            // them 0, 4, 8, ... for the SAME 2048 bytes per read. Same bytes, different step, so the
            // step is in units of the block size and the delivery size is independent of it.
            long offset = (long)position * session.BlockSize;

            int bytesRead;
            FaStoreStatus status = _store.Read(session.OpenHandle, offset, delivery, out bytesRead);
            if (status != FaStoreStatus.Ok)
            {
                Log?.Invoke($"[fa] ReadFile at {offset} for system {session.ClientSystem}: {status}");
                return ErrorFields(StatusFor(status));
            }

            session.PendingDelivery = delivery;

            Log?.Invoke(
                $"[fa] ReadFile seq {sequence} for system {session.ClientSystem}: position {position} "
                + $"= byte {offset}, {bytesRead} real byte(s) of {FaFileDataCodec.ReadLength} delivered");

            // The delivered count is reported only to a request that named a wanted one.
            ushort delivered = wantedBytes > 0 ? (ushort)FaFileDataCodec.ReadLength : (ushort)0;
            return StripOperationAndSequence(FaFileDataCodec.BuildReply(sequence, delivered));
        }

        /// <summary>
        /// Builds the short acknowledgement that answers one inbound message.
        /// </summary>
        /// <param name="session">
        /// The conversation.
        /// </param>
        /// <param name="incoming">
        /// The message being acknowledged, whose frame-flags and role bytes are echoed.
        /// </param>
        /// <param name="transport">
        /// The node transport.
        /// </param>
        /// <returns>
        /// The acknowledgement frame.
        /// </returns>
        /// <remarks>
        /// Flags 1 is OUR OWN outgoing count, not an echo - see <c>ChooseFlags1</c>. In a healthy
        /// conversation this equals the request's number anyway, because the two sides alternate;
        /// it only differs when we and the peer have drifted, which is exactly when echoing is
        /// wrong. XMCSM is this frame's own length (8), like every other frame.
        /// </remarks>
        private XmsgFrame BuildShortAck(
            FaServerSession session, XmsgFrame incoming, IXmsgServerTransport transport)
        {
            byte counter = session.NextShortAckCounter;
            session.NextShortAckCounter = (byte)(counter + 1);

            byte[] ackBody = session.Conversation.BuildShortAck(counter, true);

            XmsgFrame ack = transport.BuildBodyDatagram(
                session.RemoteNode, session.ClientSystem, session.ClientPort,
                session.SessionWirePort, (ushort)ackBody.Length,
                incoming.SubHeader!.FrameFlags, incoming.SubHeader.Role, ackBody,
                XmsgAnsweredFlags1.None);

            Log?.Invoke(
                $"[fa] short acknowledgement {counter} to system {session.ClientSystem} at Flags1 "
                + $"0x{incoming.Header!.Flags1:X4}");

            return ack;
        }

        /// <summary>
        /// Handles one arriving data message of a write in progress.
        /// </summary>
        /// <param name="session">
        /// The conversation.
        /// </param>
        /// <param name="incoming">
        /// The data message's frame.
        /// </param>
        /// <param name="transport">
        /// The node transport.
        /// </param>
        /// <param name="body">
        /// The message body: the eight-byte prefix, then raw file content.
        /// </param>
        /// <param name="sequenceByte">
        /// The session-header byte the reply carries when the write completes.
        /// </param>
        /// <returns>
        /// The acknowledgement alone while more content is expected, or the acknowledgement
        /// followed by the write's reply once the last block has landed.
        /// </returns>
        /// <remarks>
        /// Each data message is acknowledged on its own before the next is sent - stop and wait,
        /// recorded for both this and the older transfer service. The REPLY comes only at the end,
        /// because on a write the reply is the completion of the operation.
        /// </remarks>
        private IReadOnlyList<XmsgFrame> OnWriteBlock(
            FaServerSession session,
            XmsgFrame incoming,
            IXmsgServerTransport transport,
            ReadOnlySpan<byte> body,
            byte sequenceByte)
        {
            bool complete = StoreWriteBlock(session, body);

            XmsgFrame ack = BuildShortAck(session, incoming, transport);

            if (!complete)
            {
                return One(ack);
            }

            ushort delivered = session.WriteReportsCount
                ? (ushort)session.WriteBytesReceived
                : (ushort)0;
            ushort sequence = session.WriteSequence;

            session.WriteOffset = FaServerSession.NoWriteInProgress;
            session.WriteBytesReceived = 0;
            session.WriteReportsCount = false;

            Log?.Invoke(
                $"[fa] write complete for system {session.ClientSystem}: seq {sequence}, "
                + $"{delivered} byte(s) reported");

            byte[] fields = StripOperationAndSequence(
                FaFileDataCodec.BuildReply(FaOperation.WriteFile, sequence, delivered));

            List<XmsgFrame> frames = new List<XmsgFrame>(2);
            frames.Add(ack);
            frames.Add(BuildRawReply(
                session, incoming, transport,
                session.Conversation.BuildReply(FaOperation.WriteFile, sequence, fields)));
            return frames;
        }

        /// <summary>
        /// Arms a <c>WriteFile</c>: records where the content is to land, and answers NOTHING yet.
        /// </summary>
        /// <param name="session">
        /// The conversation.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence, echoed by the reply once the content has arrived.
        /// </param>
        /// <param name="body">
        /// The request body.
        /// </param>
        /// <returns>
        /// The reply fields when the request is refused, or <see langword="null"/> when the write
        /// has been armed and the reply must wait for the data.
        /// </returns>
        /// <remarks>
        /// <para><b>A write is a read backwards</b></para>
        /// Measured in wire order across both captures:
        ///  - READ: request, then the reply, THEN the data messages from the server.
        ///  - WRITE: request, then the data messages from the client, THEN the reply.
        /// The reply is the COMPLETION of the operation in both cases, so this returns null and
        /// <see cref="OnSessionMessage"/> holds the reply back until the last block lands.
        /// <para><b>The request itself is the read request with one field changed</b></para>
        /// Operation <c>0x0009</c> instead of <c>0x0008</c>, and nothing else - so
        /// <c>FaFileDataCodec.TryReadRequest</c> parses it unchanged.
        /// </remarks>
        private byte[]? ArmWrite(FaServerSession session, ushort sequence, ReadOnlySpan<byte> body)
        {
            ushort serial;
            uint position;
            ushort wantedBytes;
            if (!FaFileDataCodec.TryReadRequest(
                    body.Slice(FaExchangeCodec.QformOffset), out serial, out position, out wantedBytes))
            {
                Log?.Invoke($"[fa] WriteFile seq {sequence} from system {session.ClientSystem} carries no position; answering BadRequest");
                return ErrorFields(FaServerStatus.BadRequest);
            }

            if (session.OpenHandle == 0)
            {
                Log?.Invoke($"[fa] WriteFile seq {sequence} from system {session.ClientSystem} with no file open; answering a refusal");
                return ErrorFields(FaServerStatus.NoSuchFile);
            }

            if (!session.OpenForWrite)
            {
                Log?.Invoke($"[fa] WriteFile seq {sequence} from system {session.ClientSystem} on a file opened for READ; answering a refusal");
                return ErrorFields(FaServerStatus.StoreError);
            }

            session.WriteOffset = (long)position * FaFileDataCodec.ReadLength;
            session.WriteSequence = sequence;
            session.WriteBytesReceived = 0;
            session.WriteReportsCount = wantedBytes > 0;

            Log?.Invoke(
                $"[fa] WriteFile seq {sequence} from system {session.ClientSystem} armed at position "
                + $"{position} = byte {session.WriteOffset}; awaiting {FaFileDataCodec.ReadLength} byte(s)");

            // No reply yet - it belongs after the content.
            return null;
        }

        /// <summary>
        /// Stores one arriving block of a write in progress.
        /// </summary>
        /// <param name="session">
        /// The conversation.
        /// </param>
        /// <param name="body">
        /// The whole data message: the eight-byte file-access prefix, then raw file bytes.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the write is now complete and its reply should go out.
        /// </returns>
        /// <remarks>
        /// <para><b>A data message carries no QFORM</b></para>
        /// After the eight-byte prefix it is raw content to the end - see
        /// <c>FaFileDataCodec</c>. Walking it as tagged fields decodes file text as tags, so it is
        /// recognised by its LENGTH and by a write being in progress, never by parsing it.
        /// <para><b>The end of the delivery is marked, and also counted</b></para>
        /// The last message of a pair carries <c>FaFileDataCodec.LastDataMessageFlag</c> in its
        /// counter. That flag is used, but the byte count is what actually decides completion: a
        /// flag we misread would otherwise leave a write hanging forever with no way out.
        /// </remarks>
        private bool StoreWriteBlock(FaServerSession session, ReadOnlySpan<byte> body)
        {
            ReadOnlySpan<byte> content = body.Slice(
                FaExchangeCodec.QformOffset, FaFileDataCodec.BlockLength);

            FaStoreStatus status = _store.Write(session.OpenHandle, session.WriteOffset, content);
            if (status != FaStoreStatus.Ok)
            {
                Log?.Invoke($"[fa] writing {FaFileDataCodec.BlockLength} byte(s) at {session.WriteOffset} for system {session.ClientSystem}: {status}");
                session.WriteOffset = FaServerSession.NoWriteInProgress;
                return true;
            }

            session.WriteOffset += FaFileDataCodec.BlockLength;
            session.WriteBytesReceived += FaFileDataCodec.BlockLength;

            byte counter = body[FaExchangeCodec.SessionHeaderOffset];
            bool marked = (counter & FaFileDataCodec.LastDataMessageFlag) != 0;
            bool full = session.WriteBytesReceived >= FaFileDataCodec.ReadLength;

            Log?.Invoke(
                $"[fa] write block from system {session.ClientSystem}: counter 0x{counter:X2}, "
                + $"{session.WriteBytesReceived} of {FaFileDataCodec.ReadLength} byte(s)"
                + (marked ? ", marked last" : string.Empty));

            return marked || full;
        }

        /// <summary>
        /// Sets the served file's true byte length, which a writer states once its blocks are in.
        /// </summary>
        /// <param name="session">
        /// The conversation.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence, echoed by the reply.
        /// </param>
        /// <param name="body">
        /// The request body.
        /// </param>
        /// <returns>
        /// The reply fields.
        /// </returns>
        /// <remarks>
        /// A write ships whole 2048-byte blocks, so the last one is padded and the file on disk
        /// would otherwise be rounded up to a block boundary. The captured write states
        /// <c>0x45F0</c> = 17904 bytes for a file that arrived as nine 2048-byte blocks. See
        /// <see cref="FaSpecialFunction.SetEndOfFile"/>.
        /// </remarks>
        private byte[] SetEndOfFile(FaServerSession session, ushort sequence, ReadOnlySpan<byte> body)
        {
            uint byteLength;
            if (!FaListFilesCodec.TryReadEndOfFile(
                    body.Slice(FaExchangeCodec.QformOffset), out byteLength))
            {
                Log?.Invoke($"[fa] SetEndOfFile seq {sequence} from system {session.ClientSystem} carries no length; answering BadRequest");
                return ErrorFields(FaServerStatus.BadRequest);
            }

            if (session.OpenHandle == 0)
            {
                Log?.Invoke($"[fa] SetEndOfFile seq {sequence} from system {session.ClientSystem} with no file open; answering a refusal");
                return ErrorFields(FaServerStatus.NoSuchFile);
            }

            // THE VALUE IS THE LAST BYTE'S INDEX, NOT A COUNT, so the file is one byte longer than
            // it says. Three independent sources agree:
            //
            //  - NDIX's cps client adds one when it reads this field off a connect reply and takes
            //    one off when it sends it, and tags that line /* kludge factor !! */
            //    (E:\Dev\Ronny\NDIX-C\baseline\bin\cps\xmsg.c:588, :824).
            //  - capture-read.txt: the OPEN reply for the same file carries A4 000045F1 = 17905
            //    where SetEndOfFile carries 45F0 = 17904.
            //  - Live on D100 2026-08-06: it copied a 12690-byte file to us - its own
            //    FILE-STATISTICS says 12690 - and sent SetEndOfFile 12689. Taking that literally
            //    stored 12689 bytes, byte-identical to the source but with the final newline gone.
            //
            // A silent one-byte truncation on every file written to us, which no test could catch
            // because both sides of our own code would have agreed on the wrong number.
            long trueLength = (long)byteLength + 1;

            FaStoreStatus status = _store.SetLength(session.OpenHandle, trueLength);
            if (status != FaStoreStatus.Ok)
            {
                Log?.Invoke($"[fa] SetEndOfFile to {trueLength} for system {session.ClientSystem}: {status}");
                return ErrorFields(StatusFor(status));
            }

            Log?.Invoke(
                $"[fa] SetEndOfFile for system {session.ClientSystem}: last byte index {byteLength}, "
                + $"so {trueLength} byte(s)");
            return OkFields;
        }

        /// <summary>
        /// Turns a conversation's parked file content into the data messages that carry it.
        /// </summary>
        /// <param name="session">
        /// The conversation.
        /// </param>
        /// <param name="incoming">
        /// The request being answered, whose frame-flags and role bytes are echoed.
        /// </param>
        /// <param name="transport">
        /// The node transport.
        /// </param>
        /// <param name="outgoing">
        /// The frame list to append to.
        /// </param>
        /// <remarks>
        /// <para><b>Two messages, and each one is a fragment pair</b></para>
        /// A read moves two blocks of 1024 bytes, each in its own 1032-byte message. 1032 does not
        /// fit in one frame, so every message leaves as a
        /// <see cref="SintranPacketSubtype.MessageFirstFragment"/> and a
        /// <see cref="SintranPacketSubtype.MessageContinuation"/> - four frames for one read. The
        /// splitting itself belongs to the transport; this method only says what to send.
        /// <para><b>The last message of the pair is marked</b></para>
        /// Its counter carries <c>FaFileDataCodec.LastDataMessageFlag</c> and its session token is
        /// replaced by <c>FaFileDataCodec.LastDataMessageToken</c>. Both are measured in both
        /// directions across three captured conversations; WHY the token is replaced rather than
        /// repeated is UNKNOWN.
        /// </remarks>
        private void DeliverPending(
            FaServerSession session,
            XmsgFrame incoming,
            IXmsgServerTransport transport,
            List<XmsgFrame> outgoing)
        {
            byte[]? delivery = session.PendingDelivery;
            if (delivery == null)
            {
                return;
            }

            session.PendingDelivery = null;

            for (int block = 0; block < FaFileDataCodec.BlocksPerRead; block++)
            {
                bool isLast = block == FaFileDataCodec.BlocksPerRead - 1;

                // The SAME counter the replies draw from - bit 7 set on the last message of the
                // pair and clear on the first. See FaServerConversation.NextMessageCounter.
                byte counter = session.Conversation.NextMessageCounter(isLast);

                // The first message of a pair carries our own responder token, the last carries
                // 0x0001 in its place.
                ushort token = isLast
                    ? FaFileDataCodec.LastDataMessageToken
                    : FaExchangeCodec.SessionTokenResponder;

                ReadOnlySpan<byte> content = new ReadOnlySpan<byte>(
                    delivery, block * FaFileDataCodec.BlockLength, FaFileDataCodec.BlockLength);

                // The RESPONDER's own conversation number, not the client's - the same rule an
                // ordinary reply follows.
                //
                // This used to pass the CONSTANT FaExchangeCodec.ResponderConversation, justified by
                // "the captured data messages carry 07F0 0002". That capture is the one session
                // whose letter happened to carry 0x0002, so the constant was right by luck - the
                // same trap that hung the terminal on 2026-08-08 and was fixed for replies, short
                // acknowledgements and the close. This one site kept the constant, so a file read on
                // any other conversation would have sent data the client never asked for.
                // See FaServerConversation.ResponderConversation for the four captures.
                byte[] message = FaFileDataCodec.BuildDataMessage(
                    session.Conversation.ResponderConversation, counter, token, content);

                IReadOnlyList<XmsgFrame> fragments = transport.BuildFragmentedBodyDatagram(
                    session.RemoteNode, session.ClientSystem, session.ClientPort,
                    session.SessionWirePort,
                    incoming.SubHeader!.FrameFlags, incoming.SubHeader.Role, message,
                    XmsgAnsweredFlags1.None);

                for (int i = 0; i < fragments.Count; i++)
                {
                    outgoing.Add(fragments[i]);
                }

                Log?.Invoke(
                    $"[fa] data message {block + 1} of {FaFileDataCodec.BlocksPerRead} to system "
                    + $"{session.ClientSystem}: counter 0x{counter:X2}, token "
                    + $"0x{token:X4}, {message.Length} byte(s) in {fragments.Count} frame(s)");
            }
        }

        /// <summary>
        /// Reports the length of a served file, or zero when it cannot be measured.
        /// </summary>
        /// <param name="specification">
        /// The file specification as the client typed it.
        /// </param>
        /// <returns>
        /// The length in bytes.
        /// </returns>
        /// <remarks>
        /// <para><b>This used to ask the wrong question</b></para>
        /// It searched for the file's name INSIDE the specification and returned the first hit. A
        /// file called <c>A</c> is a substring of nearly every specification, so an open of
        /// <c>BETA:DATA</c> in a folder that also held <c>A:SYMB</c> reported A's length - and
        /// because the store had already resolved the open correctly, the client would have been
        /// reading BETA while believing it was A bytes long.
        /// <para>
        /// That matters more than it looks: the read protocol has no end marker at all, so the
        /// length reported HERE is the only thing telling a client when to stop. See
        /// <see cref="ReadFile"/>.
        /// </para>
        /// </remarks>
        private long LengthOf(string specification)
        {
            string wantedName;
            string wantedType;
            if (!SplitSpecification(specification, out wantedName, out wantedType))
            {
                return 0;
            }

            IReadOnlyList<FaFileInfo> files = _store.ListFiles();
            for (int i = 0; i < files.Count; i++)
            {
                if (Matches(files[i].Name, wantedName, wantedType))
                {
                    return files[i].ByteLength;
                }
            }

            return 0;
        }

        /// <summary>
        /// Maps a store outcome onto the SINTRAN error number to report.
        /// </summary>
        /// <param name="status">
        /// What the store said.
        /// </param>
        /// <returns>
        /// The status to put in the refusal.
        /// </returns>
        private static FaServerStatus StatusFor(FaStoreStatus status)
        {
            if (status == FaStoreStatus.NoSuchFile)
            {
                return FaServerStatus.NoSuchFile;
            }

            return FaServerStatus.StoreError;
        }

        /// <summary>
        /// Answers one step of a directory walk with the entry the cursor asks for.
        /// </summary>
        /// <param name="session">
        /// The conversation.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence, which is also the serial the reply echoes.
        /// </param>
        /// <param name="body">
        /// The whole request body, starting at the message type.
        /// </param>
        /// <returns>
        /// The reply's QFORM fields.
        /// </returns>
        /// <remarks>
        /// <para>
        /// A listing is one request/reply round trip PER ENTRY, driven by a cursor: <c>0xFFFF</c>
        /// asks for the first entry and thereafter the cursor is the index wanted, running 1, 2, 3
        /// and up (see <c>FaListFilesCodec</c>). Mapping <c>0xFFFF</c> to index 0 and cursor n to
        /// index n is INFERRED from that description - the captured walk does not by itself prove
        /// whether the numbering is 0-based or 1-based after the first entry.
        /// </para>
        /// <para>
        /// The folder is listed ONCE, when the walk starts, and the snapshot is walked. Re-listing
        /// per request would let a file created mid-walk shift every later index.
        /// </para>
        /// </remarks>
        private byte[] DirectoryEnquiry(FaServerSession session, ushort sequence, ReadOnlySpan<byte> body)
        {
            // THE SUB-FUNCTION IS READ FIRST, on its own. Not every SiiiSpecial is a listing:
            // SetEndOfFile carries a 32-bit length and NO cursor, so the listing parse below would
            // refuse it before this branch was ever reached.
            FaSpecialFunction function;
            if (!FaListFilesCodec.TryReadFunction(body.Slice(FaExchangeCodec.QformOffset), out function))
            {
                Log?.Invoke($"[fa] request SiiiSpecial seq {sequence} from system {session.ClientSystem} carries no sub-function; answering BadRequest");
                return ErrorFields(FaServerStatus.BadRequest);
            }

            // NOT a listing at all - it is how a writer states the file's true length once its
            // blocks are in. See FaSpecialFunction.SetEndOfFile.
            if (function == FaSpecialFunction.SetEndOfFile)
            {
                return SetEndOfFile(session, sequence, body);
            }

            // Also not a listing: a reader asking about the file it already has open, between
            // SetBlockSize and its first ReadFile. See FaSpecialFunction.FileInformation.
            if (function == FaSpecialFunction.FileInformation)
            {
                return FileInformation(session, sequence);
            }

            ushort serial;
            ushort cursor;
            FaSpecialFunction listingFunction;
            if (!FaListFilesCodec.TryReadRequest(body.Slice(FaExchangeCodec.QformOffset), out serial, out cursor, out listingFunction))
            {
                Log?.Invoke($"[fa] request SiiiSpecial seq {sequence} from system {session.ClientSystem} is not a directory enquiry we can read; answering BadRequest");
                return ErrorFields(FaServerStatus.BadRequest);
            }

            // The client says WHICH of three things it wants; the walk is not positional. Answering
            // every request with the next file entry is what made D100 abandon a listing after the
            // second file no matter what the folder held - the two requests that wanted the
            // directory and the user got file records instead. See FaSpecialFunction.

            if (function == FaSpecialFunction.DirectoryEntry)
            {
                Log?.Invoke($"[fa] system {session.ClientSystem} asked for the DIRECTORY entry: pack {DirectoryName}");
                return StripOperationAndSequence(
                    FaListFilesCodec.BuildDirectoryReply(sequence, FaDirectoryEntry.BuildRecord(DirectoryName)));
            }

            if (function == FaSpecialFunction.UserEntry)
            {
                Log?.Invoke($"[fa] system {session.ClientSystem} asked for the USER entry: {UserName}");
                return StripOperationAndSequence(FaListFilesCodec.BuildUserReply(sequence, UserName));
            }

            IReadOnlyList<FaFileInfo> entries;
            if (cursor == FaListFilesCodec.FirstEntryCursor)
            {
                entries = SnapshotFor(body);
                session.BeginWalk(entries);
                Log?.Invoke($"[fa] directory walk started by system {session.ClientSystem}: {entries.Count} file(s) in {_store.RootFolder}");
            }
            else
            {
                entries = session.WalkEntries;
                if (entries.Count == 0)
                {
                    // A walk that never started: take a snapshot now rather than refuse, so a client
                    // that resumes mid-walk after our restart still gets files.
                    entries = SnapshotFor(body);
                    session.BeginWalk(entries);
                    Log?.Invoke($"[fa] system {session.ClientSystem} asked for entry {cursor} with no walk in progress; taking a fresh snapshot of {entries.Count} file(s)");
                }
            }

            // The cursor is NOT an index - see FaServerSession.WalkPosition. 0xFFFF restarts the
            // walk (BeginWalk above has already zeroed the position); anything else means "next",
            // and the position we hold is the answer.
            int index = session.WalkPosition;
            if (index < 0 || index >= entries.Count)
            {
                Log?.Invoke($"[fa] directory walk for system {session.ClientSystem} reached the end at entry {index} of {entries.Count}; answering EndOfDirectory");
                return ErrorFields(FaServerStatus.EndOfDirectory);
            }

            session.WalkPosition = index + 1;

            FaFileInfo file = entries[index];
            byte[] record = FaFolderEntry.BuildRecord(file, _userIndex, index);

            // FaListFilesCodec builds the WHOLE reply body including the leading operation and
            // sequence pair; FaServerConversation writes those two itself. So the first six bytes are
            // dropped here rather than a second copy of the body layout being written out.
            // The second A2 value is the entry's ORDINAL in the walk - see FaListFilesCodec.
            // BuildReply. Leaving it at its default made every entry claim to be entry zero.
            byte[] fields = StripOperationAndSequence(
                FaListFilesCodec.BuildReply(sequence, record, 0, (ushort)index));

            Log?.Invoke(
                $"[fa] directory entry {index} for system {session.ClientSystem}: "
                + $"{file.Name} file {file.FileNumber}, {file.ByteLength} byte(s)");
            return fields;
        }

        /// <summary>
        /// Takes the snapshot a directory walk will step through: the whole folder, or just the one
        /// file the request named.
        /// </summary>
        /// <param name="body">
        /// The whole request body, starting at the message type.
        /// </param>
        /// <returns>
        /// The files to walk, in order.
        /// </returns>
        /// <remarks>
        /// <para><b>What this fixes</b></para>
        /// The request's 62-byte spec block was ignored entirely, so a <c>FILE-STATISTICS</c> of one
        /// file walked the whole folder and reported every file in it. The block names the file - see
        /// <c>FaListFilesCodec.TryReadRequestedFileName</c> for the bytes it was read out of.
        /// <para><b>A named file that is not here still starts a walk</b></para>
        /// It starts an EMPTY one, so the next request is answered with
        /// <see cref="FaServerStatus.EndOfDirectory"/> rather than with the wrong file. Refusing
        /// outright would be the other reasonable choice; an empty directory is chosen because it
        /// keeps every listing on one path, and because we cannot tell a missing file from a
        /// specification we failed to match.
        /// <para><b>The specification is PARSED, not searched for</b></para>
        /// A filespec carries an optional user, a name and an optional type -
        /// <c>(SYSTEM)SINTRAN:DATA</c> in the capture - so it is put through
        /// <see cref="FaFileName.TryParse"/> and the parts compared field by field. The type is
        /// compared only when the client gave one, because <c>BETA</c> should match <c>BETA:DATA</c>
        /// while <c>BETA:SYMB</c> should not. The user is ignored: a served folder is one user's
        /// whole world and we have nothing to check it against.
        /// <para>
        /// Asking instead whether the file's name APPEARS IN the specification - which is what this
        /// did at first, and what <see cref="LengthOf"/> still does for an open - is wrong for any
        /// short name. A file called <c>A</c> is a substring of nearly every specification a client
        /// could send, so it would be returned for all of them.
        /// </para>
        /// </remarks>
        private IReadOnlyList<FaFileInfo> SnapshotFor(ReadOnlySpan<byte> body)
        {
            IReadOnlyList<FaFileInfo> all = _store.ListFiles();

            string named;
            if (!FaListFilesCodec.TryReadRequestedFileName(
                    body.Slice(FaExchangeCodec.QformOffset), out named)
                || named.Length == 0)
            {
                return all;
            }

            string wantedName;
            string wantedType;
            if (!SplitSpecification(named, out wantedName, out wantedType))
            {
                // A specification we cannot read is NOT treated as "give me everything" - that would
                // turn a request for one file into a listing of the folder, which is the very thing
                // reading the block was meant to stop.
                Log?.Invoke($"[fa] the request names '{named}', which is not a file specification we can parse; no file matches");
                return Array.Empty<FaFileInfo>();
            }

            List<FaFileInfo> matching = new List<FaFileInfo>(1);
            for (int i = 0; i < all.Count; i++)
            {
                if (Matches(all[i].Name, wantedName, wantedType))
                {
                    matching.Add(all[i]);
                }
            }

            Log?.Invoke($"[fa] the request names '{named}': {matching.Count} of {all.Count} file(s) match");
            return matching;
        }

        /// <summary>
        /// Splits a file specification into its name and type, accepting either separator.
        /// </summary>
        /// <param name="specification">
        /// The specification as the client sent it.
        /// </param>
        /// <param name="name">
        /// The bare file name.
        /// </param>
        /// <param name="type">
        /// The type, or an empty string when the specification gave none.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the specification could be split.
        /// </returns>
        /// <remarks>
        /// <para><b>Why both separators</b></para>
        /// <see cref="FaFileName.TryParse"/> is the SINTRAN parser and splits the type on a COLON,
        /// which is right - <c>SINTRAN:DATA</c> is what a real client sends. But this server hands
        /// out the names of files in a WINDOWS folder, and a client that echoes one back says
        /// <c>PATCH.SYMB</c>. Parsed strictly that is a name of <c>PATCH.SYMB</c> with no type, and
        /// it matches nothing.
        /// <para>
        /// So the dot is accepted as a second separator HERE, in the server that serves a folder,
        /// rather than by loosening the SINTRAN parser for everyone. Only the LAST dot is treated as
        /// a separator, so a name that contains one keeps it.
        /// </para>
        /// </remarks>
        private static bool SplitSpecification(string specification, out string name, out string type)
        {
            name = string.Empty;
            type = string.Empty;

            FaFileName? parsed;
            if (!FaFileName.TryParse(specification, out parsed) || parsed == null)
            {
                return false;
            }

            name = parsed.Name;
            type = parsed.Type ?? string.Empty;

            if (type.Length == 0)
            {
                int dot = name.LastIndexOf('.');
                if (dot > 0 && dot < name.Length - 1)
                {
                    type = name.Substring(dot + 1);
                    name = name.Substring(0, dot);
                }
            }

            return name.Length > 0;
        }

        /// <summary>
        /// Decides whether a served file answers to the name and type a client asked for.
        /// </summary>
        /// <param name="candidate">
        /// The file as the store reports it.
        /// </param>
        /// <param name="wantedName">
        /// The bare name asked for.
        /// </param>
        /// <param name="wantedType">
        /// The type asked for, or an empty string when the client named none.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the file is the one asked for.
        /// </returns>
        /// <remarks>
        /// Case is ignored on both parts: SINTRAN names are upper case and a Windows folder's are
        /// whatever the user typed. Naming no type accepts any type, so <c>BETA</c> finds
        /// <c>BETA:DATA</c> while <c>BETA:SYMB</c> does not.
        /// </remarks>
        private static bool Matches(FaFileName candidate, string wantedName, string wantedType)
        {
            if (!string.Equals(candidate.Name, wantedName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (wantedType.Length == 0)
            {
                return true;
            }

            return string.Equals(candidate.Type, wantedType, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Wraps an already-built FA message body in a datagram back to the client.
        /// </summary>
        /// <param name="session">
        /// The conversation.
        /// </param>
        /// <param name="incoming">
        /// The message being answered.
        /// </param>
        /// <param name="transport">
        /// The node transport.
        /// </param>
        /// <param name="replyBody">
        /// The FA message body.
        /// </param>
        /// <returns>
        /// The reply datagram.
        /// </returns>
        /// <remarks>
        /// <para>
        /// The frame-flags byte and the role byte are ECHOED from the request: what those carry on
        /// an FA session frame is UNKNOWN, and echoing what the client chose invents nothing.
        /// </para>
        /// <para>
        /// XMCSM and Flags 1 are NOT echoed - both are properties of THIS frame. CORRECTED
        /// 2026-08-04, see the two blocks below.
        /// </para>
        /// </remarks>
        private XmsgFrame BuildRawReply(
            FaServerSession session, XmsgFrame incoming, IXmsgServerTransport transport, byte[] replyBody)
        {
            // AN FA BODY IS WORD ALIGNED. Pad an odd-length body with one zero byte, and declare the
            // PADDED length - the pad is inside the message, not after it.
            //
            // MEASURED 2026-08-04 over all four captures in DOC\captures\FA-READ-WRITE-2026-08-04:
            // 480 Data frames, every single body length EVEN, none odd. The real server's reserve
            // reply is 18 bytes and its directory entry 98; ours were 17 and 97, one short in each
            // case, with the missing byte visible in the decoder as a trailing "00" past the
            // F2 00FF end-of-list selector.
            //
            // This is the natural shape for a 16-bit machine, and the one place to do it is here -
            // every FA reply body leaves through this method.
            if ((replyBody.Length & 1) != 0)
            {
                byte[] padded = new byte[replyBody.Length + 1];
                for (int i = 0; i < replyBody.Length; i++)
                {
                    padded[i] = replyBody[i];
                }

                replyBody = padded;
            }

            // The FA body goes on the wire at absolute 28 with NOTHING in front of it - no XROUT
            // serial / service / length header.
            //
            // XMCSM IS THIS FRAME'S OWN BODY LENGTH, not the request's. VERIFIED 2026-08-04 across
            // every Data frame in the four FA-READ-WRITE captures and in our own HDLC log: an 8-byte
            // ShortAck carries 0x0008, a 98-byte reply carries 0x0062, a 34-byte connect letter
            // carries 0x0022. Echoing the request's value made a 97-byte reply declare 100 and a
            // 17-byte reply declare 112.
            //
            // FLAGS 1: a reply ORIGINATES a new exchange, it does not answer one. VERIFIED in
            // capture-list-files.txt, three times over:
            //     f1=N    100->102  the request
            //     f1=N    102->100  the ShortAck          <- THIS is the answer at the request's f1
            //     f1=N+1  102->100  the reply             <- a NEW exchange
            //     f1=N+1  100->102  100's own ShortAck
            // Passing XmsgAnsweredFlags1.None allocates the link's next number, which is N+1 because
            // the ShortAck sent just before this carried N and advanced the link.
            XmsgFrame reply = transport.BuildBodyDatagram(
                session.RemoteNode, session.ClientSystem, session.ClientPort,
                session.SessionWirePort, (ushort)replyBody.Length,
                incoming.SubHeader!.FrameFlags, incoming.SubHeader.Role, replyBody,
                XmsgAnsweredFlags1.None);

            Log?.Invoke($"[fa] reply to system {session.ClientSystem}: {replyBody.Length} byte(s), FA body {Convert.ToHexString(replyBody, 0, replyBody.Length < 16 ? replyBody.Length : 16)}...");
            return reply;
        }

        /// <summary>
        /// Extracts the FA message body from a datagram: everything after the SINTRAN header and the
        /// XMSG sub-header.
        /// </summary>
        /// <param name="frame">
        /// The datagram.
        /// </param>
        /// <returns>
        /// The body bytes, empty when the frame is too short to hold any.
        /// </returns>
        /// <remarks>
        /// The frame's own <c>TrailingBytes</c> cannot be used: a decoded non-letter data frame has
        /// its trailer parsed into a TAD chain instead, which leaves <c>TrailingBytes</c> empty. The
        /// serialised bytes are the one representation that is always complete and byte-exact.
        /// </remarks>
        /// <seealso cref="FileAccessBodyOffset"/>
        private static ReadOnlySpan<byte> SessionBody(XmsgFrame frame)
        {
            byte[] all = frame.ToArray();

            // 14 + 14 = 28. FileAccessBodyOffset records the same number independently.
            int offset = SintranHeader.Size + XmsgSubHeader.Size;
            if (all.Length <= offset)
            {
                return ReadOnlySpan<byte>.Empty;
            }

            return new ReadOnlySpan<byte>(all, offset, all.Length - offset);
        }

        /// <summary>
        /// Builds the QFORM fields of a refusal: the status under selector 1, then end of list.
        /// </summary>
        /// <param name="status">
        /// The refusal to report.
        /// </param>
        /// <returns>
        /// The nine-byte field run.
        /// </returns>
        /// <remarks>
        /// <para><b>VERIFIED 2026-08-05, and the tag was wrong</b></para>
        /// A refusal is <c>F2 0001</c> then <c>A2 code</c>, measured in <c>capture-open-error.txt</c>
        /// where an open of a missing file was answered <c>F2 0001 A2 002E</c>, and in
        /// <c>capture-list-files.txt</c> where the end of a walk carries <c>F2 0001 A2 00C5</c>. We
        /// were writing the code under <see cref="QformTagByte.Integer"/> where a real server writes
        /// <see cref="QformTagByte.TypedInteger"/>.
        /// <para><b>Selector 1 means "this failed"</b></para>
        /// A successful reply never carries selector 1 at all - it starts at selector 2. So the
        /// presence of the selector IS the error signal, and the value is a SINTRAN error number.
        /// </remarks>
        private static byte[] ErrorFields(FaServerStatus status)
        {
            byte[] fields = new byte[9];
            fields[0] = (byte)QformTagByte.Selector;
            fields[1] = 0x00;
            fields[2] = 0x01;
            fields[3] = (byte)QformTagByte.TypedInteger;
            fields[4] = (byte)((ushort)status >> 8);
            fields[5] = (byte)status;
            fields[6] = (byte)QformTagByte.Selector;
            fields[7] = (byte)(QformReader.EndOfListSelector >> 8);
            fields[8] = (byte)QformReader.EndOfListSelector;
            return fields;
        }

        /// <summary>
        /// Wraps a single frame in a list.
        /// </summary>
        /// <param name="frame">
        /// The frame.
        /// </param>
        /// <returns>
        /// A one-element list.
        /// </returns>
        private static IReadOnlyList<XmsgFrame> One(XmsgFrame frame)
        {
            List<XmsgFrame> list = new List<XmsgFrame>(1);
            list.Add(frame);
            return list;
        }

        /// <summary>
        /// Finds the conversation for a client endpoint, opening one when there is none.
        /// </summary>
        /// <param name="incoming">
        /// The received datagram.
        /// </param>
        /// <param name="transport">
        /// The node transport, used to allocate a session port.
        /// </param>
        /// <returns>
        /// The conversation.
        /// </returns>
        private FaServerSession GetOrCreateSession(XmsgFrame incoming, IXmsgServerTransport transport)
        {
            ushort clientSystem = incoming.SubHeader!.SourceSystem;
            ushort clientPort = incoming.SubHeader.SourcePort;
            uint key = SessionKey(clientSystem, clientPort);

            FaServerSession? existing;
            if (_sessions.TryGetValue(key, out existing))
            {
                return existing!;
            }

            // ONE port for the server, taken the first time it is needed and kept for good. A port
            // per conversation is what we used to do and it is what a real machine refuses - see
            // the remarks on _replyPort for the captures and the live failure.
            if (_replyPort == 0)
            {
                _replyPort = transport.AllocateSessionPort();
            }

            FaServerSession session = new FaServerSession(
                incoming.Header!.SourceNode, clientSystem, clientPort, _replyPort);

            _sessions[key] = session;
            _sessionList.Add(session);
            return session;
        }

        /// <summary>
        /// Forgets a conversation. The reply port is NOT released - it belongs to the server, not
        /// to the conversation, and the next client has to be able to reach it.
        /// </summary>
        /// <param name="session">
        /// The conversation to close.
        /// </param>
        private void CloseSession(FaServerSession session)
        {
            _sessions.Remove(SessionKey(session.ClientSystem, session.ClientPort));
            for (int i = 0; i < _sessionList.Count; i++)
            {
                if (_sessionList[i] == session)
                {
                    _sessionList.RemoveAt(i);
                    break;
                }
            }
        }

        /// <summary>
        /// Builds the dictionary key for a client endpoint.
        /// </summary>
        /// <param name="clientSystem">
        /// The client's system number.
        /// </param>
        /// <param name="clientPort">
        /// The client's port.
        /// </param>
        /// <returns>
        /// The packed key.
        /// </returns>
        private static uint SessionKey(ushort clientSystem, ushort clientPort)
        {
            return ((uint)clientSystem << 16) | clientPort;
        }
    }
}
