using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Packet;
using RetroIO.Protocols.Xmsg.Fa;

namespace RetroIO.Protocols.Xmsg.Servers.Fa
{
    /// <summary>
    /// Pulls a file from a remote <c>*FA-SERVER</c>: turns the read ladder into datagrams, feeds the
    /// answers back into the sequencing state machine, and collects the content that arrives.
    /// </summary>
    /// <remarks>
    /// <para><b>The mirror of <see cref="FaWriteDriver"/></b></para>
    /// <para>
    /// Same shape and, where the protocol is the same, the same pieces: the connect letter, the
    /// request bodies, the counter rule, the fragment reassembly. It builds no frames of its own -
    /// <see cref="IXmsgServerTransport"/> owns Flags 1, the Counter and the channel, so there is
    /// exactly one place in the codebase that assembles a datagram.
    /// </para>
    /// <para><b>Three things a write never has to do</b></para>
    ///  - LEARN HOW LONG THE FILE IS. It arrives in the reply to
    ///    <see cref="FaOperation.OpenFile"/>, and until it does the ladder does not know how many
    ///    block requests it will send. See <see cref="FaClientReadSession.SetBlockCount"/>.
    ///  - ACKNOWLEDGE THE CONTENT. Each of the two data messages per block wants a short
    ///    acknowledgement of its own, so a block step costs three from us rather than one.
    ///  - TRIM THE PADDING. The last block arrives full, because there is no short block and no end
    ///    marker anywhere in this protocol. The length from the open reply is what says where the
    ///    file really stops.
    /// <para><b>Content is recognised by LENGTH, not by looking at it</b></para>
    /// <para>
    /// A reassembled data message is exactly <see cref="FaFileDataCodec.DataMessageLength"/> bytes
    /// - 8 of FA envelope and 1024 of raw file - and no reply is ever that size; the captured
    /// replies run 18 to 88 bytes. So the length settles it. The alternative, deciding by what the
    /// body looks like, is exactly the trap <see cref="FaFileDataCodec"/> warns about: there is no
    /// QFORM inside a data message, and file text decodes as plausible-looking tags if you try.
    /// </para>
    /// </remarks>
    public sealed class FaReadDriver
    {
        /// <summary>
        /// Where the file is coming from.
        /// </summary>
        private readonly FaReadSource _source;

        /// <summary>
        /// What to send next, and what each answer means.
        /// </summary>
        private readonly FaClientReadSession _session;

        /// <summary>
        /// The SINTRAN error number the server refused with, or zero.
        /// </summary>
        private ushort _sintranError;

        /// <summary>
        /// Set when a refusal has ended the transfer but the goodbye has not gone out yet.
        /// </summary>
        private bool _releaseOwed;

        /// <summary>
        /// The conversation state: the number in use, the exchange sequence and the message counter.
        /// </summary>
        /// <remarks>
        /// Built from the source's number to start with, then REPLACED when the server's connect
        /// confirmation arrives carrying the number it actually assigned.
        /// </remarks>
        private FaClientConversation _conversation;

        /// <summary>
        /// The port the server opened for this conversation, learned from the confirmation.
        /// </summary>
        private ushort _serverSessionPort;

        /// <summary>
        /// The conversation number in force: the source's until the confirmation replaces it.
        /// </summary>
        private ushort _serverConversation;

        /// <summary>
        /// Our own port, allocated from the node on the first frame we build.
        /// </summary>
        private ushort _ourPort;

        /// <summary>
        /// The Flags 1 of the last message the server sent us.
        /// </summary>
        private int _answerFlags1 = XmsgAnsweredFlags1.None;

        /// <summary>
        /// The file's true length, from the open reply. Zero until it arrives.
        /// </summary>
        private long _fileLength;

        /// <summary>
        /// The content collected so far, padding included. Trimmed by <see cref="Content"/>.
        /// </summary>
        /// <remarks>
        /// Sized once the block count is known and filled at the offset each message belongs at,
        /// rather than appended to. A growing list would quietly reorder the file if two messages
        /// ever arrived out of order; writing to a computed offset cannot.
        /// </remarks>
        private byte[] _collected = Array.Empty<byte>();

        /// <summary>
        /// How many content messages have landed in total, across every block.
        /// </summary>
        private int _messagesCollected;

        /// <summary>
        /// The block the outstanding <c>ReadFile</c> asked for, so its answer lands in the right
        /// place however many times the peer sends it.
        /// </summary>
        private int _blockBeingRead;

        /// <summary>
        /// The serial the connect letter carries; the reply echoes it.
        /// </summary>
        private byte _letterSerial = 0x1B;

        /// <summary>
        /// Where the assigned connection number sits in a connection confirmation.
        /// </summary>
        /// <remarks>
        /// The confirmation is <c>{type, echoedRequestWord, connectionNumber, trailingWord}</c>, so
        /// the number is the THIRD word. It is not <see cref="FaExchangeCodec.ConversationOffset"/>,
        /// which addresses the second - correct for a request, wrong here.
        /// </remarks>
        private const int ConfirmConnectionNumberOffset = 4;

        /// <summary>
        /// XROUT's well-known port. A letter asks a server, so it is addressed here.
        /// </summary>
        private const ushort XroutRequestPort = 0x0000;

        /// <summary>
        /// Starts a pull.
        /// </summary>
        /// <param name="source">
        /// Where the file is coming from.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="source"/> is null.
        /// </exception>
        /// <remarks>
        /// The session starts with a ONE-block ladder, which is a placeholder and not a guess about
        /// the file: the real count replaces it the moment the open reply arrives, before any block
        /// request is built. One rather than zero so the ladder is always well formed.
        /// </remarks>
        public FaReadDriver(FaReadSource source)
            : this(source, false)
        {
        }

        /// <summary>
        /// Starts a pull, or a diagnostic probe that opens nothing.
        /// </summary>
        /// <param name="source">
        /// Where the file is coming from.
        /// </param>
        /// <param name="probeWithoutOpen">
        /// <c>true</c> to run <see cref="FaReadLadder.ProbeWithoutOpen"/> instead of a transfer:
        /// reserve a file entry, set the block size, and stop. <c>false</c> for an ordinary pull.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="source"/> is null.
        /// </exception>
        /// <remarks>
        /// <para>
        /// <b>The probe transfers nothing.</b> It exists to answer what the follow-on refusal
        /// <c>A2 4104</c> means - see <see cref="FaReadLadder.ProbeWithoutOpen"/> and
        /// <c>DOC/CARVE-FA-READ-REFUSAL-2026-08-18.md</c>. Point it at a file that EXISTS: the
        /// question is what the server says about a block size on an entry nothing has opened, and
        /// a missing file would put the ordinary refusal in the way of the answer.
        /// </para>
        /// <para>
        /// A separate constructor rather than a flag with a default, so no existing caller can
        /// acquire probe behaviour by accident.
        /// </para>
        /// </remarks>
        public FaReadDriver(FaReadSource source, bool probeWithoutOpen)
        {
            if (source == null) { throw new ArgumentNullException(nameof(source)); }

            _source = source;

            // The session starts with a ONE-block ladder, which is a placeholder and not a guess
            // about the file: the real count replaces it the moment the open reply arrives, before
            // any block request is built. One rather than zero so the ladder is always well formed.
            _session = probeWithoutOpen
                ? FaClientReadSession.CreateProbeWithoutOpen()
                : new FaClientReadSession(1);

            // Opening values only. The letter is the one message that goes out before the server
            // has told us anything; the confirmation then replaces both.
            _serverConversation = source.Conversation;
            _serverSessionPort = source.ServerPort;
            _conversation = new FaClientConversation(_serverConversation);
        }

        /// <summary>
        /// Gets the file's length as the server reported it, or zero before the open reply.
        /// </summary>
        public long FileLength
        {
            get { return _fileLength; }
        }

        /// <summary>
        /// Gets how many blocks this pull expects.
        /// </summary>
        public int BlockCount
        {
            get { return _session.BlockCount; }
        }

        /// <summary>
        /// Gets the session port this conversation answers on, or zero before the first frame.
        /// </summary>
        /// <remarks>
        /// It is what separates our own traffic from another conversation's on the same node -
        /// see <see cref="OnFrame"/>.
        /// </remarks>
        public ushort OurPort
        {
            get { return _ourPort; }
        }

        /// <summary>
        /// Ends the transfer as failed, for a reason the caller worked out rather than the peer.
        /// </summary>
        /// <param name="reason">
        /// What went wrong, in words that will be shown to a person.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="reason"/> is null.
        /// </exception>
        /// <remarks>
        /// <para><b>Why the driver needs this at all</b></para>
        /// <para>
        /// Some ways a transfer dies are not visible from inside the ladder. The commonest is
        /// silence: the caller sends its connect letter four times, nothing ever answers, and it
        /// stops. That decision is made ABOVE the driver, so without this the driver has no idea the
        /// transfer is over and goes on reporting itself unfinished.
        /// </para>
        /// <para><b>The write driver has had this since 2026-08-18; the read driver did not</b></para>
        /// <para>
        /// Which is why the pull could not be given a connect-letter retry at all - there was no way
        /// to tell the driver it had given up. A pull whose first letter went unanswered therefore
        /// sat silent for the whole 240-second transfer timeout and reported only "did NOT finish".
        /// See <c>FaPullRun.RetryConnectLetterIfSilent</c>.
        /// </para>
        /// </remarks>
        public void Abandon(string reason)
        {
            if (reason == null) { throw new ArgumentNullException(nameof(reason)); }

            _session.OnRejected(reason);
        }

        /// <summary>
        /// Gets what the driver wants to do next.
        /// </summary>
        public FaClientAction NextAction()
        {
            return _session.NextAction();
        }

        /// <summary>
        /// Gets the operation the current ladder step will send.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the ladder is already finished.
        /// </exception>
        /// <remarks>
        /// Exposed so a caller can log or match a reply against what was asked. It comes from the
        /// session, so there is ONE reader of the ladder rather than two that can disagree.
        /// </remarks>
        public FaOperation CurrentOperation
        {
            get { return _session.CurrentOperation; }
        }

        /// <summary>
        /// Gets the exchange sequence the current ladder step will send.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the ladder is already finished.
        /// </exception>
        public ushort CurrentSequence
        {
            get { return _session.CurrentSequence; }
        }

        /// <summary>
        /// Gets why the pull failed, or an empty string while it has not.
        /// </summary>
        public string Failure
        {
            get { return _session.Failure; }
        }

        /// <summary>
        /// Gets the SINTRAN error number the server refused with, or zero when it did not.
        /// </summary>
        /// <remarks>
        /// Kept as a number beside the words, because one caller has to decide on it rather than
        /// print it: the sync daemon treats 62, "File already exists", as the answer to a question
        /// it could not otherwise ask, and reading that out of a sentence would break the first time
        /// somebody improved the sentence.
        /// </remarks>
        public ushort SintranError
        {
            get { return _sintranError; }
        }

        /// <summary>
        /// Gets whether a goodbye is still owed to the server.
        /// </summary>
        /// <remarks>
        /// A caller that stops the moment the transfer reports failure would leave the conversation
        /// open, so the server never closes its session port and the connection seat XROUT spent on
        /// our connect letter is never given back. A refusal ends the TRANSFER; only the Release
        /// ends the CONVERSATION. See <see cref="BuildReleaseBody"/>.
        /// </remarks>
        public bool ReleasePending
        {
            get { return _releaseOwed; }
        }

        /// <summary>
        /// Gets whether the pull has finished successfully.
        /// </summary>
        public bool Done
        {
            get { return _session.NextAction() == FaClientAction.Done; }
        }

        /// <summary>
        /// Gets the file, with the last block's padding removed.
        /// </summary>
        /// <returns>
        /// Exactly <see cref="FileLength"/> bytes.
        /// </returns>
        /// <remarks>
        /// <para>
        /// The trim is the whole reason the open reply's length is worth having. Blocks arrive full
        /// - the captured 20400-byte file came back as 20 messages of 1024, which is 20480 - and
        /// nothing in the content says which of the last 80 bytes are the file.
        /// </para>
        /// <para>
        /// Callable before the pull finishes, and it returns what has arrived so far padded out to
        /// the file's length. That is deliberate: a caller that wants to know whether the file is
        /// COMPLETE asks <see cref="Done"/>, and a caller that saves an unfinished read is doing
        /// something it should be able to see the result of.
        /// </para>
        /// </remarks>
        public byte[] Content()
        {
            long length = _fileLength;
            if (length > _collected.Length)
            {
                length = _collected.Length;
            }

            if (length < 0)
            {
                length = 0;
            }

            byte[] trimmed = new byte[length];
            for (int i = 0; i < length; i++)
            {
                trimmed[i] = _collected[i];
            }

            return trimmed;
        }

        /// <summary>
        /// Builds the next thing to send.
        /// </summary>
        /// <param name="transport">
        /// The node's transport, which fills in Flags 1, the Counter and the channel.
        /// </param>
        /// <returns>
        /// The frames to transmit in order. Empty when the driver is waiting or has finished.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="transport"/> is null.
        /// </exception>
        public IReadOnlyList<XmsgFrame> BuildNext(IXmsgServerTransport transport)
        {
            if (transport == null) { throw new ArgumentNullException(nameof(transport)); }

            if (_ourPort == 0)
            {
                _ourPort = transport.AllocateSessionPort();
            }

            List<XmsgFrame> frames = new List<XmsgFrame>(2);

            // Outside the ladder on purpose: once the session has failed, NextAction answers Failed
            // and would send nothing, so the goodbye would never go and the seat would be stranded.
            if (_releaseOwed)
            {
                _releaseOwed = false;
                AddBodyMessage(frames, transport, BuildReleaseBody(),
                    (byte)XmsgFrameFlags.ControlBare, 0x84);
                return frames;
            }

            switch (_session.NextAction())
            {
                case FaClientAction.SendConnectLetter:
                    frames.Add(BuildConnectLetter(transport));
                    _session.OnConnectLetterSent();
                    break;

                case FaClientAction.SendRequest:
                    AddBodyMessage(frames, transport, BuildRequestBody());
                    _session.OnRequestSent();
                    break;

                case FaClientAction.SendShortAck:
                    // ORIGINATED, never echoed. An FA short acknowledgement is an acknowledgement
                    // at the FA level but it travels as an ordinary Data message, so it spends one
                    // of OUR Flags 1 numbers like any other. Only the DATAGRAM acknowledgement,
                    // subtype 0x03, echoes.
                    //
                    // The write side got this backwards and it cost days: our acknowledgements
                    // landed BEHIND the peer's expectation, where a datagram is dropped in silence,
                    // so D100 never saw them, resent everything, and eventually dropped the link.
                    AddBodyMessage(frames, transport, BuildShortAckBody());
                    _session.OnShortAckSent();
                    break;

                case FaClientAction.SendRelease:
                    // frameFlags 0x82 / role 0x84, as every real ND FA message carries.
                    AddBodyMessage(frames, transport, BuildReleaseBody(),
                        (byte)XmsgFrameFlags.ControlBare, 0x84);
                    _session.OnReleaseSent();
                    break;

                default:
                    // Wait, Done and Failed all mean "nothing to send".
                    break;
            }

            return frames;
        }

        /// <summary>
        /// Feeds a datagram from the server into the state machine.
        /// </summary>
        /// <param name="incoming">
        /// The frame the node received, already reassembled if it arrived as a fragment pair.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="incoming"/> is null.
        /// </exception>
        /// <remarks>
        /// Anything this conversation does not understand is IGNORED rather than treated as an
        /// error - a link carries acknowledgements and other servers' traffic, and failing the pull
        /// on them would make it fail for reasons that have nothing to do with it.
        /// </remarks>
        public void OnFrame(XmsgFrame incoming)
        {
            if (incoming == null) { throw new ArgumentNullException(nameof(incoming)); }

            byte[] body = incoming.GetBodyBytes();
            if (body.Length < FaExchangeCodec.QformOffset)
            {
                return;
            }

            // ONLY FRAMES ADDRESSED TO OUR OWN SESSION PORT BELONG TO THIS CONVERSATION.
            //
            // Every frame the node receives is offered here, and the node is a file SERVER as well
            // as a client. When a real D100 lists our directory it opens its own conversation with
            // our *FA-SERVER port and short-acknowledges every reply we send it - and those
            // acknowledgements are file-access messages of exactly the shape this method reads.
            //
            // MEASURED 2026-08-10, on the first live run of the pull. D100 was made to address us
            // with LIST-FILES so the Ethernet link could learn its id - which is the ONLY way to
            // start an Ethernet transfer - and that listing's acknowledgements arrived here before
            // our own first request had left. The pull died instantly with "A short
            // acknowledgement arrived before any request was sent", having never sent anything.
            //
            // The port is what separates the two: D100 addresses the listing to our well-known
            // *FA-SERVER wire port, and our own conversation to the session port we allocated and
            // put in the connect letter. The conversation NUMBER cannot do this job - we do not
            // learn the server's until its confirmation arrives, which is itself a frame that has
            // to get through this test.
            //
            // No offline test could have found this: a simulated server sends only our own
            // conversation's traffic. It needed a second conversation running at the same time,
            // which is the normal state of a real node.
            // BEFORE WE HAVE A PORT, NOTHING CAN BE OURS. The port is allocated when the first
            // frame is built, so a zero here means we have not sent so much as the connect letter
            // and no reply to us can exist yet.
            //
            // This half is not a refinement of the port test - it IS the case that bites. The
            // first fix here read "_ourPort != 0 && destination != ours", which let everything
            // through during exactly the window that matters: the far machine has to address us
            // before an Ethernet transfer can start at all, so its traffic ALWAYS arrives before
            // our first frame leaves. Measured twice on 2026-08-10, the second time after the
            // first fix, with the same failure text.
            if (_ourPort == 0)
            {
                return;
            }

            if (incoming.SubHeader != null && incoming.SubHeader.DestinationPort != _ourPort)
            {
                return;
            }

            // Remember what to echo when we acknowledge at the DATAGRAM level.
            _answerFlags1 = incoming.Header.Flags1;

            FaMessageType type = (FaMessageType)NdEndian.GetBe16(body, 0);

            if (type == FaMessageType.ConnectionConfirm)
            {
                // A RETRANSMITTED CONFIRM MUST NOT RESTART THE CONVERSATION. Everything below
                // replaces _conversation, which resets the message counter and the session token.
                // Doing that mid-session silently rewinds us, and on the write side it presented as
                // "stalls at OpenFile" and was chased for days as a ShortAck problem.
                if (_session.IsConnected)
                {
                    return;
                }

                // THE CONFIRM IS WHERE THE CONVERSATION'S REAL ADDRESS ARRIVES. Its SOURCE port is
                // the session the server opened for us - requests go there, not to *FA-SERVER's
                // well-known port, which takes letters and not traffic.
                if (incoming.SubHeader != null)
                {
                    _serverSessionPort = incoming.SubHeader.SourcePort;
                }

                // MIND THE OFFSET: the number we want is the THIRD word. Offset 2 holds the word WE
                // chose and the server echoed, which is a different thing.
                _serverConversation = NdEndian.GetBe16(body, ConfirmConnectionNumberOffset);
                _conversation = new FaClientConversation(_serverConversation);

                _session.OnConnectionConfirmed();
                return;
            }

            if (type == FaMessageType.ShortAck)
            {
                _session.OnShortAckReceived();
                return;
            }

            if (type == FaMessageType.Close)
            {
                _session.OnRejected("the server closed the conversation.");
                return;
            }

            if (type != FaMessageType.Request)
            {
                return;
            }

            // A CONTENT MESSAGE AND A REPLY ARE BOTH 0x07F0. Length is what separates them: a data
            // message is exactly 1032 bytes and no captured reply comes close. Checked BEFORE the
            // conversation-word test below, because a data message carries the same word and would
            // otherwise be handed to the QFORM reader as if it were a reply.
            if (body.Length == FaFileDataCodec.DataMessageLength)
            {
                CollectContent(body);
                return;
            }

            // A REPLY is not its own message type - what separates it from a request is the
            // CONVERSATION WORD: the server stamps the word it echoed in its confirmation on
            // everything it sends.
            //
            // FaExchangeCodec.IsReply is deliberately NOT used. It compares against the constant
            // 0x0002, which is only the USUAL value of that word - the same assumption that once
            // hung a live terminal. A client knows the real word, because it chose it.
            if (NdEndian.GetBe16(body, FaExchangeCodec.ConversationOffset) != _source.LetterEchoWord)
            {
                return;
            }

            FaOperation operation;
            ushort sequence;
            if (!FaExchangeCodec.TryReadOperation(body, out operation, out sequence))
            {
                return;
            }

            // A REFUSAL ENDS THE LADDER HERE, and until 2026-08-18 it did not end it at all.
            //
            // The server says no by putting QFORM selector 1 in the reply, carrying a SINTRAN
            // file-system error number; a success omits the selector entirely. This driver never
            // read it. MEASURED against D100: a pull of a file that does not exist was refused on
            // the FIRST step -
            //
            //     OpenFile reply  ... F2 0001  A2 002E ...      0x2E = 46 = NO SUCH FILE NAME
            //
            // and this code took that for an ordinary reply, climbed SetBlockSize, SiiiSpecial and
            // ReadFile against a file that was never opened, collected a refusal on each of those
            // too, and then waited for a data block that was never coming. The transfer only ended
            // because a wall-clock timeout was added above it - which was the right net and the
            // wrong explanation.
            //
            // TESTED BOTH WAYS. A successful 53-block read carries no selector 1 anywhere, so this
            // cannot fail a healthy transfer; FaRefusalCodecTests pins both endings against bytes
            // captured from the machine. See DOC\CARVE-FA-READ-REFUSAL-2026-08-18.md.
            //
            // Checked BEFORE LearnFileLength: a refused open has no length to learn, and reading one
            // out of a refusal would size the read from whatever the error fields happen to hold.
            ushort refusal;
            if (FaRefusalCodec.TryReadStatus(body, out refusal))
            {
                _sintranError = refusal;

                // STILL SAY GOODBYE - the same fix as the write driver, for the same measured
                // reason. A refusal ends the TRANSFER but not the CONVERSATION, and the seat
                // belongs to the conversation. See DOC\CARVE-FA-SEAT-LEAK-2026-08-18.md.
                _releaseOwed = true;

            _session.OnRejected(
                    operation + " was refused: SINTRAN error " + refusal
                    + FaSintranError.Describe(refusal));
                return;
            }

            // The OPEN reply is the one that carries the file's length, and it must be read BEFORE
            // the reply is handed to the session - the session may complete the step on it, and the
            // block count has to be settled while the ladder is still in its prologue.
            if (operation == FaOperation.OpenFile)
            {
                LearnFileLength(body);
            }

            _session.OnReplyReceived(operation, sequence);
        }

        /// <summary>
        /// Takes the file's length out of the open reply and sizes the read from it.
        /// </summary>
        /// <param name="body">
        /// The reply body.
        /// </param>
        /// <remarks>
        /// <para>
        /// Read once. A retransmitted open reply would otherwise re-enter this and resize a read
        /// already in progress; the length would be the same value, but relying on that is relying
        /// on the peer rather than on our own state.
        /// </para>
        /// <para>
        /// A reply with NO length is not an error here - the captured WRITE open reply carries
        /// none. It leaves the pull with its one-block placeholder, which is visible in the log and
        /// far easier to diagnose than a silent failure.
        /// </para>
        /// </remarks>
        private void LearnFileLength(byte[] body)
        {
            if (_fileLength != 0)
            {
                return;
            }

            ushort serial;
            ushort fileNumber;
            long byteLength;
            if (!FaOpenFileCodec.TryReadReply(
                    new ReadOnlySpan<byte>(body, FaExchangeCodec.QformOffset,
                        body.Length - FaExchangeCodec.QformOffset),
                    out serial, out fileNumber, out byteLength))
            {
                return;
            }

            if (byteLength <= 0)
            {
                return;
            }

            _fileLength = byteLength;

            int blocks = FaReadLadder.BlockCountForLength(byteLength);
            _session.SetBlockCount(blocks);
            _collected = new byte[(long)blocks * FaWriteLadder.ContentBytesPerBlock];
        }

        /// <summary>
        /// Copies one data message's 1024 bytes into the file being assembled.
        /// </summary>
        /// <param name="body">
        /// The whole data message: the eight-byte FA envelope, then raw file content.
        /// </param>
        /// <remarks>
        /// <para><b>The offset comes from the LADDER, not from a count of arrivals</b></para>
        /// <para>
        /// Each message lands at the position of the block the outstanding request asked for,
        /// remembered as <c>_blockBeingRead</c> when that request was built. That makes the write
        /// idempotent: a block the peer sends TWICE lands on itself instead of shifting everything
        /// after it along. It is remembered rather than read from the session because the ladder
        /// has already stepped on by the time the content arrives.
        /// </para>
        /// <para><b>It used to be a running count, and a real machine broke it</b></para>
        /// <para>
        /// The position was <c>_messagesCollected * BlockLength</c>. That is appending with extra
        /// steps, and it only holds while every message arrives exactly once. Real ND machines
        /// retransmit - measured live 2026-08-11, D100 re-sending content while we pulled a
        /// 20400-byte file - so a repeat was written to the NEXT slot, the buffer overran, and the
        /// pull died on the guard below saying the server had sent more than it declared. The
        /// server had done nothing of the kind; we had counted the same block twice.
        /// </para>
        /// <para>
        /// The guard stays, because a message genuinely past the end of the declared file is a real
        /// disagreement and worth failing on rather than papering over.
        /// </para>
        /// </remarks>
        private void CollectContent(byte[] body)
        {
            // Which HALF of the block this is. One block is two messages of 1024 bytes, and the
            // sender marks them: bit 7 of the session-header counter is set on every message EXCEPT
            // the first of a content pair. Reading the half off the message rather than counting
            // arrivals is what keeps this idempotent - a repeated pair rewrites itself.
            bool isSecondHalf =
                (body[FaExchangeCodec.SessionHeaderOffset] & FaFileDataCodec.LastDataMessageFlag) != 0;

            long at = ((long)_blockBeingRead * FaWriteLadder.ContentBytesPerBlock)
                + (isSecondHalf ? FaFileDataCodec.BlockLength : 0);

            if (at + FaFileDataCodec.BlockLength > _collected.Length)
            {
                _session.OnRejected(
                    "the server sent more content than the " + _fileLength
                        + " bytes its open reply declared.");
                return;
            }

            for (int i = 0; i < FaFileDataCodec.BlockLength; i++)
            {
                _collected[at + i] = body[FaExchangeCodec.QformOffset + i];
            }

            // Counted for reporting only - the file's shape no longer depends on it.
            _messagesCollected++;
            _session.OnContentReceived();
        }

        /// <summary>
        /// Builds the XSLET letter that opens the conversation.
        /// </summary>
        /// <param name="transport">
        /// The node's transport.
        /// </param>
        /// <returns>
        /// The letter frame, addressed to XROUT's port 0.
        /// </returns>
        /// <remarks>
        /// The body goes on the wire VERBATIM. <see cref="IXmsgServerTransport.BuildDatagram"/>
        /// composes an XROUT header of its own, and <see cref="FaConnectLetter.BuildBody"/> has
        /// already built one - going through that path emits the header TWICE, which is what D100
        /// rejected on 2026-08-09.
        /// </remarks>
        private XmsgFrame BuildConnectLetter(IXmsgServerTransport transport)
        {
            byte[] body = FaConnectLetter.BuildBody(
                _letterSerial,
                FaServer.ServerName,
                _source.ServerSystemName,
                _source.LetterEchoWord);

            return transport.BuildBodyDatagram(
                _source.ServerNode,
                _source.ServerSystem,
                clientPort: XroutRequestPort,
                sourcePort: _ourPort,
                xmcsm: (ushort)body.Length,
                frameFlags: (byte)XmsgFrameFlags.Setup,
                role: (byte)(XmsgSendOptions.WaitForTransfer | XmsgSendOptions.WakeOnStatus
                    | XmsgSendOptions.HighPriority | XmsgSendOptions.RoutedLetter),
                body: body,
                answeredFlags1: XmsgAnsweredFlags1.None);
        }

        /// <summary>
        /// Builds the request body for the step the ladder is on.
        /// </summary>
        /// <returns>
        /// The whole message body.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the ladder reaches an operation this driver has no request fields for.
        /// </exception>
        private byte[] BuildRequestBody()
        {
            FaOperation operation = _session.CurrentOperation;

            switch (operation)
            {
                case FaOperation.ReserveFileEntry:
                    // Identical to a write's. The reserve says WHO IS ASKING, not what for.
                    return _conversation.BuildRequest(
                        operation,
                        FaWriteRequests.ReserveFileEntry(
                            _source.BackgroundProgram,
                            _source.LocalUser,
                            _source.User,
                            _source.PasswordWord));

                case FaOperation.OpenFile:
                    // NOT the write builder: a read's open carries no access selector and no
                    // quotes. See FaReadRequests.OpenFile.
                    return _conversation.BuildRequest(
                        operation, FaReadRequests.OpenFile(_source.FileSpec));

                case FaOperation.SetBlockSize:
                    return _conversation.BuildRequest(
                        operation,
                        FaWriteRequests.SetBlockSize((ushort)FaWriteLadder.ContentBytesPerBlock));

                case FaOperation.SiiiSpecial:
                    // A read asks for the file information; a write declares the end of file. Same
                    // operation, different sub-function, and sending the write's here would tell
                    // the server to truncate a file we are only reading.
                    return _conversation.BuildRequest(operation, FaReadRequests.FileInformation());

                case FaOperation.ReadFile:
                    // Remember WHICH block this asks for. The content that answers it arrives after
                    // the ladder has already stepped on, so by then the session can no longer say -
                    // and the answer has to land at the position of the request, not wherever a
                    // count of arrivals happens to point. See CollectContent.
                    _blockBeingRead = _session.CurrentBlock;
                    return _conversation.BuildRequest(
                        operation, FaReadRequests.ReadFile((uint)_blockBeingRead));

                case FaOperation.CloseFile:
                    return _conversation.BuildRequest(operation, FaWriteRequests.CloseFile());

                case FaOperation.ReleaseFileEntry:
                    return _conversation.BuildRequest(operation, FaWriteRequests.ReleaseFileEntry());

                default:
                    throw new InvalidOperationException(
                        "The read ladder asked for " + operation
                            + ", which this driver has no request fields for.");
            }
        }

        /// <summary>
        /// Builds the short acknowledgement that closes an exchange.
        /// </summary>
        /// <returns>
        /// The eight-byte body.
        /// </returns>
        private byte[] BuildShortAckBody()
        {
            byte[] body = new byte[FaExchangeCodec.QformOffset];
            FaExchangeCodec.WriteEnvelope(
                body,
                FaMessageType.ShortAck,
                _serverConversation,
                _conversation.NextAckCounter(),
                FaServerConversation.AskerShortAckConstant);
            return body;
        }

        /// <summary>
        /// Builds the client's Release, the ten-byte message that ends the conversation.
        /// </summary>
        /// <returns>
        /// The ten-byte body.
        /// </returns>
        /// <remarks>
        /// The pull side of the identical message the write driver sends, and it matters for the
        /// identical reason: this Release is what makes the server conclude the session and CLOSE
        /// its session port, and closing that port is what returns the connection seat. XROUT spent
        /// the seat forwarding our connect letter and marked the port with <c>5PKOC</c>
        /// ("KICK XROUT ON CLOSE (SET BY XROUT)", <c>XMSG-POFTABS-L03.SYMB</c>); the kernel's
        /// <c>YCLOS</c> sees that bit on close and kicks XROUT, which restores the count. Sending
        /// the server's own Close (<c>0x07C0</c>) instead leaves the server waiting, the port open
        /// and the seat gone. Full chain and the operand order:
        /// <c>DOC\COSMOS-RE\CARVE-ANSWER-FA-SEAT-RETURN-2026-08-18.md</c> and
        /// <c>DOC\CARVE-FA-SEAT-LEAK-2026-08-18.md</c>.
        /// </remarks>
        private byte[] BuildReleaseBody()
        {
            // Sender's conversation FIRST, then the peer's - LetterEchoWord is the number the
            // SERVER stamps on its messages, not ours. This code had them swapped.
            byte[] body = new byte[10];
            NdEndian.PutBe16(body, 0, (ushort)FaMessageType.SessionFinished);
            NdEndian.PutBe16(body, 2, _serverConversation);
            NdEndian.PutBe16(body, 4, _source.LetterEchoWord);
            NdEndian.PutBe16(body, 6, 0x8000);
            NdEndian.PutBe16(body, 8, 0x0000);
            return body;
        }

        /// <summary>
        /// Adds one file-access message, fragmenting it when it is too long for a datagram.
        /// </summary>
        /// <param name="frames">
        /// The list being filled.
        /// </param>
        /// <param name="transport">
        /// The node's transport.
        /// </param>
        /// <param name="body">
        /// The message body.
        /// </param>
        /// <remarks>
        /// Everything a reader sends ORIGINATES - requests, acknowledgements and the close alike -
        /// so there is no <c>originated</c> parameter here as there is on the write side. Nothing a
        /// reader sends echoes at the FA level.
        /// <para>
        /// Everything goes through the FRAGMENTED builder, short bodies included. A body short
        /// enough to travel whole is sent by exactly the same rule, with its own length as XMCSM,
        /// and that rule is verified over every file-access data frame in the captures.
        /// </para>
        /// </remarks>
        private void AddBodyMessage(
            List<XmsgFrame> frames, IXmsgServerTransport transport, byte[] body)
        {
            AddBodyMessage(frames, transport, body, (byte)XmsgFrameFlags.DataA, 0x00);
        }

        private void AddBodyMessage(
            List<XmsgFrame> frames, IXmsgServerTransport transport, byte[] body,
            byte frameFlags, byte role)
        {
            IReadOnlyList<XmsgFrame> built = transport.BuildFragmentedBodyDatagram(
                _source.ServerNode,
                _source.ServerSystem,
                _serverSessionPort,
                _ourPort,
                frameFlags: frameFlags,
                role: role,
                body: body,
                answeredFlags1: XmsgAnsweredFlags1.None);

            for (int i = 0; i < built.Count; i++)
            {
                frames.Add(built[i]);
            }
        }
    }
}
