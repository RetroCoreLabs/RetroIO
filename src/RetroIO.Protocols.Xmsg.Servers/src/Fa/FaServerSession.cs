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

namespace RetroIO.Protocols.Xmsg.Servers.Fa
{
    /// <summary>
    /// One client's conversation with <see cref="FaServer"/>: who it is, which port we answer from,
    /// the reply builder, and the snapshot of the folder a directory walk is stepping through.
    /// </summary>
    internal sealed class FaServerSession
    {
        /// <summary>
        /// The snapshot the current directory walk is stepping through.
        /// </summary>
        private IReadOnlyList<FaFileInfo> _walkEntries;

        /// <summary>
        /// The reply builder, created on the first message that names a conversation number.
        /// </summary>
        private FaServerConversation? _conversation;

        /// <summary>
        /// The session-header counter of the request the last reply answered, and the reply itself,
        /// so a REPEAT of that request is answered with the very same bytes.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a repeat must be REPLAYED, not answered afresh - MEASURED 2026-08-11</b></para>
        /// <para>
        /// A real D100 asked for directory entry 0 NINE times, every request byte for byte
        /// identical (<c>07F0 0082 8100 D761 ...</c>) at the same datagram Flags 1 <c>0x0008</c> -
        /// one datagram being retransmitted, not nine requests. We answered each one from the
        /// conversation's own counter, so the replies went out as <c>8200, 8300, 8400, 8500,
        /// 8600</c> and not one of them carried the <c>8100</c> the client was waiting on. It never
        /// advanced to entry 1 and the listing died.
        /// </para>
        /// <para>
        /// The real server's listing shows the answer plainly: request <c>07F0 0008 8100 D761</c> is
        /// answered by <c>07F0 0002 8100 90BB</c> - same word 3. That does NOT make the counter an
        /// echo (see <c>FaServerConversation.BuildReply</c>: on a READ the reply is 8A where the
        /// request was 86, because the content messages take the numbers between). It makes a
        /// retransmission a retransmission: the answer already sent is the answer, and sending a
        /// differently numbered one is a new message the peer never asked for.
        /// </para>
        /// <para>
        /// This is the fifth defect from treating a repeat as a new event. See the memory
        /// <c>fa-retransmitted-confirm-rewinds-session</c> and "A REPEAT is not a new event" in the
        /// xmsg-decode skill.
        /// </para>
        /// </remarks>
        private ushort _lastAnsweredFlags1;

        /// <summary>
        /// Whether <see cref="_lastAnsweredFlags1"/> and <see cref="_lastReply"/> hold anything.
        /// </summary>
        private bool _haveLastReply;

        /// <summary>
        /// The exact bytes of the last reply built, kept for replay.
        /// </summary>
        private byte[]? _lastReply;

        /// <summary>
        /// Remembers the reply just built, so a repeat of the same request can be answered with it.
        /// </summary>
        /// <param name="requestFlags1">
        /// The DATAGRAM sequence the request arrived on.
        /// </param>
        /// <param name="reply">
        /// The reply body that answered it.
        /// </param>
        /// <remarks>
        /// Keyed on the datagram's Flags 1, not on anything inside the file-access message. Flags 1
        /// is the sender's own per-peer sequence and it identifies the DATAGRAM: a retransmission
        /// carries the number it first went out with, and a genuinely new request always carries a
        /// new one. Keying on the file-access session counter instead was tried first and is wrong -
        /// our own test client reuses that byte across different requests, so a new request was
        /// answered with the previous reply.
        /// </remarks>
        public void RememberReply(ushort requestFlags1, byte[] reply)
        {
            _lastAnsweredFlags1 = requestFlags1;
            _lastReply = reply;
            _haveLastReply = true;
        }

        /// <summary>
        /// Returns the stored reply when this request is a repeat of the one it answered.
        /// </summary>
        /// <param name="requestFlags1">
        /// The datagram sequence of the request that has just arrived.
        /// </param>
        /// <returns>
        /// The bytes to send again, or <c>null</c> when this is a new request.
        /// </returns>
        public byte[]? ReplayFor(ushort requestFlags1)
        {
            if (!_haveLastReply || _lastAnsweredFlags1 != requestFlags1)
            {
                return null;
            }

            return _lastReply;
        }

        /// <summary>
        /// Initialises the conversation.
        /// </summary>
        /// <param name="remoteNode">
        /// The node the client sits on.
        /// </param>
        /// <param name="clientSystem">
        /// The client's system number.
        /// </param>
        /// <param name="clientPort">
        /// The client's port.
        /// </param>
        /// <param name="sessionWirePort">
        /// The port allocated to this conversation.
        /// </param>
        public FaServerSession(ushort remoteNode, ushort clientSystem, ushort clientPort, ushort sessionWirePort)
        {
            RemoteNode = remoteNode;
            ClientSystem = clientSystem;
            ClientPort = clientPort;
            SessionWirePort = sessionWirePort;
            _walkEntries = Array.Empty<FaFileInfo>();
        }

        /// <summary>
        /// Gets or sets the connection number this session was already confirmed with, or zero
        /// when it has not been confirmed yet.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A real ND client RETRANSMITS its connect letter until it sees the confirmation, and a
        /// repeat must be answered with the SAME connection number rather than a fresh one.
        /// </para>
        /// <para>
        /// Measured against D100 on 2026-08-10: it repeated one letter and we answered ELEVEN
        /// times, allocating 0x0043 through 0x004D. That flood is what tore the link down - and
        /// the file push riding the same link died with it, its SetBlockSize buried unanswered in
        /// the middle of the storm. Zero is not a legal connection number here (the range starts
        /// at 0x0042), so it is a safe "not yet confirmed" marker.
        /// </para>
        /// </remarks>
        public ushort ConfirmedConnectionNumber { get; set; }

        /// <summary>
        /// Gets or sets the confirmation datagram already sent for this session, replayed verbatim
        /// when the client repeats its letter.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A RETRANSMISSION IS THE SAME DATAGRAM SENT AGAIN, Flags 1 included - it must not be
        /// rebuilt, because rebuilding takes a fresh number and the peer sees a brand-new message.
        /// </para>
        /// <para>
        /// Measured 2026-08-10 across two captures of D100 talking to us. D100 resent 12 and 15
        /// distinct messages (69 and 58 extra copies) and NEVER once sent the same body under a
        /// different Flags 1. We did the opposite: our confirmation went out under eleven
        /// different numbers in one run (0x00D5..0x00E1) and nine in the other. D100 acknowledged
        /// our datagrams contiguously up to the point that burst began, and then stopped.
        /// </para>
        /// </remarks>
        public XmsgFrame? ConfirmationSent { get; set; }

        /// <summary>
        /// Gets the node the client sits on.
        /// </summary>
        public ushort RemoteNode { get; }

        /// <summary>
        /// Gets the client's system number.
        /// </summary>
        public ushort ClientSystem { get; }

        /// <summary>
        /// Gets the client's port.
        /// </summary>
        public ushort ClientPort { get; }

        /// <summary>
        /// Gets the port allocated to this conversation.
        /// </summary>
        public ushort SessionWirePort { get; }

        /// <summary>
        /// Gets or sets the counter for the NEXT short acknowledgement this server sends, which the
        /// wire carries in the HIGH byte of the word at body offset 4.
        /// </summary>
        /// <remarks>
        /// VERIFIED in <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-list-files.txt</c>: the
        /// real server's acknowledgements run <c>0100922A</c>, <c>0200922A</c>, <c>0300922A</c> -
        /// one per REQUEST answered, starting at 1. The client's run in parallel with its own
        /// constant (<c>0100 8485</c>, <c>0200 8485</c>, ...).
        /// </remarks>
        public byte NextShortAckCounter { get; set; } = 1;

        /// <summary>
        /// Gets the snapshot the current directory walk is stepping through, empty when no walk has
        /// begun.
        /// </summary>
        public IReadOnlyList<FaFileInfo> WalkEntries
        {
            get { return _walkEntries; }
        }

        /// <summary>
        /// Gets the reply builder for this conversation.
        /// </summary>
        /// <remarks>
        /// Before the first message that carries a conversation number, a builder for conversation 0
        /// is used. That number only matters for the connection confirmation and the close, both of
        /// which echo it - ordinary replies carry the responder's own number instead.
        /// </remarks>
        public FaServerConversation Conversation
        {
            get
            {
                if (_conversation == null)
                {
                    _conversation = new FaServerConversation(0);
                }

                return _conversation;
            }
        }

        /// <summary>
        /// Creates the reply builder for the conversation number the client is using, the first time
        /// one is seen.
        /// </summary>
        /// <param name="conversation">
        /// The conversation number from the client's message.
        /// </param>
        /// <remarks>
        /// The builder counts the replies it has produced (the <c>0x80 + n</c> session-header byte),
        /// so it must NOT be replaced mid-conversation - that would restart the count and send a
        /// header byte the client has already seen.
        /// </remarks>
        public void EnsureConversation(ushort conversation)
        {
            if (_conversation == null)
            {
                _conversation = new FaServerConversation(conversation);
                return;
            }

            // The builder already exists - which is the NORMAL case, because answering the connect
            // letter creates one before any message has carried a conversation number. It learns
            // the number rather than being replaced: replacing it would restart the reply count and
            // send a session-header byte the client has already seen.
            //
            // FOUND LIVE 2026-08-05. Without this every Close we sent carried conversation 0x0000
            // where the capture carries the client's own, and D100 answered it with XEIMA (-19,
            // invalid magic) after an otherwise perfect listing.
            _conversation.LearnClientConversation(conversation);
        }

        /// <summary>
        /// Starts a directory walk over a snapshot of the served folder.
        /// </summary>
        /// <param name="entries">
        /// The files, in the order they will be handed out.
        /// </param>
        public void BeginWalk(IReadOnlyList<FaFileInfo> entries)
        {
            _walkEntries = entries ?? (IReadOnlyList<FaFileInfo>)Array.Empty<FaFileInfo>();
            WalkPosition = 0;
            DirectoryEntrySent = false;
        }

        /// <summary>
        /// Gets or sets how far the directory walk has got: the index of the entry the NEXT request
        /// should be answered with.
        /// </summary>
        /// <remarks>
        /// <para>
        /// THE SERVER HOLDS THE POSITION, not the client. VERIFIED 2026-08-04 in
        /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-list-files.txt</c>: across a walk of
        /// more than a hundred files, the cursor field in the request carries <c>A2 FFFF</c>
        /// exactly ONCE (the first request) and <c>A2 0000</c> on all 102 others - yet the real
        /// server hands back a different file every time.
        /// </para>
        /// <para>
        /// So the cursor only says WHICH OF TWO THINGS to do: <c>0xFFFF</c> start the walk again,
        /// anything else (in practice <c>0x0000</c>) give me the next one. It is NOT an index. We
        /// used to treat it as one, which made every request after the first ask for index 0 and
        /// get the same file back forever.
        /// </para>
        /// </remarks>
        public int WalkPosition { get; set; }

        /// <summary>
        /// Gets or sets whether the PACK directory entry has already been handed out in this walk.
        /// </summary>
        /// <remarks>
        /// <para><b>Where it belongs in the walk</b></para>
        /// The real server returns the 42-byte directory entry as the SECOND reply - after the first
        /// file and before the second - and it consumes no walk position, so the file entries count
        /// <c>0, 1, 2</c> unbroken across it. Measured in
        /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-list-files.txt</c>, where exactly one
        /// such record appears in a walk of more than a hundred files.
        /// <para><b>Why a flag and not a position test</b></para>
        /// The walk position alone cannot say whether the directory entry has been sent, because it
        /// deliberately does not advance for it. Reset by <see cref="BeginWalk"/> so a restarted walk
        /// sends it again.
        /// </remarks>
        public bool DirectoryEntrySent { get; set; }

        /// <summary>
        /// Gets or sets the store handle of the file this conversation has open, or zero.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the session holds it</b></para>
        /// The open reply reports a file number, and nothing on the wire ever quotes it again - no
        /// read, write or close in any capture carries one. The open file therefore belongs to the
        /// CONVERSATION, so the server must remember it rather than expect the client to name it.
        /// </remarks>
        public ushort OpenHandle { get; set; }

        /// <summary>
        /// Gets or sets whether the open file was opened for writing.
        /// </summary>
        public bool OpenForWrite { get; set; }

        /// <summary>
        /// Gets or sets the specification the open file was opened by, or an empty string.
        /// </summary>
        /// <remarks>
        /// Needed because <see cref="FaSpecialFunction.FileInformation"/> asks about the OPEN file
        /// without naming it - the store handle alone cannot be turned back into a directory entry.
        /// </remarks>
        public string OpenName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the file bytes a <c>ReadFile</c> has promised but not yet sent, or
        /// <c>null</c> when nothing is outstanding.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the reply and the content are separate</b></para>
        /// A read is answered by a reply that carries NO data - measured in
        /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-read.txt</c>, where the reply is
        /// <c>92 0008 92 seq F2 00FF</c> and nothing else. The content follows as its own messages
        /// afterwards. So the bytes are parked here while the reply is built, and picked up once it
        /// has gone out.
        /// <para><b>Always a whole read</b></para>
        /// This holds <c>FaFileDataCodec.ReadLength</c> bytes even at the end of a file, zero
        /// padded. The protocol has no short block and no end marker; the client stops on the size
        /// it learned when it opened the file.
        /// </remarks>
        public byte[]? PendingDelivery { get; set; }

        // The per-session data-message counter that used to live here is GONE (2026-08-06). It
        // stepped by two between deliveries where the capture steps by three, and its own remarks
        // said the gap was unexplained and that the fix, if a client ever objected, was one counter
        // shared by the replies and the data messages. A client objected: D100 took the first
        // ReadFile of a COPY-FILE and then stopped. The counter now lives on the conversation -
        // FaServerConversation.NextMessageCounter - because that is the thing both kinds of message
        // are sent from.

        /// <summary>
        /// Gets or sets the byte offset a <c>WriteFile</c> in progress is filling, or -1 when no
        /// write is outstanding.
        /// </summary>
        /// <remarks>
        /// <para><b>A write runs the opposite way round from a read</b></para>
        /// Measured in wire order across both captures: a READ is request, reply, then our data
        /// messages. A WRITE is request, the client's data messages, THEN the reply. So the request
        /// only ARMS the write - it records where the content is to land and answers nothing - and
        /// the reply goes out once the last block has arrived.
        /// <para><b>Why an offset and not a flag</b></para>
        /// The blocks arrive one after another and each has to land in the right place. Holding the
        /// byte offset means the second block of a pair needs nothing but the offset advanced by
        /// its own length.
        /// </remarks>
        public long WriteOffset { get; set; } = NoWriteInProgress;

        /// <summary>
        /// The value <see cref="WriteOffset"/> holds when no write is outstanding.
        /// </summary>
        /// <remarks>
        /// Negative because zero is a perfectly good offset - it is where every write starts.
        /// </remarks>
        public const long NoWriteInProgress = -1;

        /// <summary>
        /// Gets or sets the exchange sequence of the <c>WriteFile</c> in progress, which its reply
        /// echoes once the content has arrived.
        /// </summary>
        /// <remarks>
        /// The reply is built long after the request was parsed, so the sequence has to be carried
        /// across the data messages that sit between them.
        /// </remarks>
        public ushort WriteSequence { get; set; }

        /// <summary>
        /// Gets or sets how many bytes of the current write have arrived.
        /// </summary>
        /// <remarks>
        /// The reply reports a delivered count when the request asked for one, and the write is
        /// complete when this reaches a whole read length. See
        /// <c>FaFileDataCodec.ReadLength</c>.
        /// </remarks>
        public int WriteBytesReceived { get; set; }

        /// <summary>
        /// Gets or sets whether the write request asked for a byte count to be reported back.
        /// </summary>
        public bool WriteReportsCount { get; set; }

        /// <summary>
        /// Gets or sets the transfer block size in bytes, which a read or write position counts in.
        /// </summary>
        /// <remarks>
        /// <para><b>512 is ND's documented default - CONFIRMED 2026-08-06</b></para>
        /// It was INFERRED from the wire first: a conversation that never sends <c>SetBlockSize</c>
        /// still moves 2048 bytes per read, but its positions step by FOUR where a conversation that
        /// set 2048 steps by ONE, and 2048 / 4 = 512. See
        /// <c>DOC\FA-READ-WRITE-WIRE-PROTOCOL-2026-08-04.md</c> section 6.
        /// <para>
        /// ND's own manual then said the same thing outright, in the entry for the monitor call this
        /// operation is named after -
        /// <c>Reference-Manuals\ND-860228-2-EN SINTRAN III Monitor Calls.md</c> line 19416,
        /// <c>76B SetBlockSize SETBS</c>: "The standard block size is 512 bytes. This block size
        /// is set when the file is opened... The block size is reset when the file is closed...
        /// Factors of 2048 bytes are the most efficient block sizes."
        /// </para>
        /// <para>
        /// So 2048 is a performance sweet spot rather than a rule, 512 is the real default, and the
        /// arithmetic above was reading the protocol correctly. Two further constraints from the
        /// same entry that we do not yet enforce: the block size must be an EVEN number of bytes,
        /// and under access code D (direct transfer) it must be a multiple of the page size with the
        /// byte count a multiple of the block size - which is the one case where a short final block
        /// is NOT legal.
        /// </para>
        /// <para><b>What it is used for</b></para>
        /// A <c>ReadFile</c> or <c>WriteFile</c> position is an index in units of THIS, so the byte
        /// offset is the position multiplied by it.
        /// </remarks>
        public ushort BlockSize { get; set; } = DefaultBlockSize;

        /// <summary>
        /// The block size assumed until a client sets one.
        /// </summary>
        public const ushort DefaultBlockSize = 512;
    }
}
