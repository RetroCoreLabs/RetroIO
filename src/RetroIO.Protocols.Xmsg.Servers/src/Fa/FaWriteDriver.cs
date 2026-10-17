//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Packet;
using RetroIO.Protocols.Xmsg.Fa;

namespace RetroIO.Protocols.Xmsg.Servers.Fa
{
    /// <summary>
    /// Pushes a local file to a remote <c>*FA-SERVER</c>: turns the write ladder into datagrams and
    /// feeds the answers back into the sequencing state machine.
    /// </summary>
    /// <remarks>
    /// <para><b>What this adds, and what it deliberately does not</b></para>
    /// <para>
    /// Every piece it uses already existed and is tested against captures - the connect letter, the
    /// fifteen-step ladder, the request bodies, the content messages, the counter rule. This class
    /// only decides WHICH of them to send next and hands the bytes to the node. It builds no
    /// frames of its own: <see cref="IXmsgServerTransport"/> owns Flags 1, the Counter and the
    /// channel, so there is exactly one place in the codebase that assembles a datagram.
    /// </para>
    /// <para><b>The direction this reverses</b></para>
    /// <para>
    /// Everything before this ANSWERED a file server. This ORIGINATES against one, which is the
    /// half of the protocol we had only ever watched. Where the two differ is the Flags 1 rule: a
    /// request of ours starts a new exchange and takes a fresh number, while our short
    /// acknowledgement of the server's reply ECHOES that reply's number, because on a real link
    /// Flags 1 is one value per exchange shared by both directions.
    /// </para>
    /// <para><b>One measurable unknown remains</b></para>
    /// <para>
    /// The captured WriteFile sequence numbers carry <c>0x8000</c> on every other request -
    /// <c>0004, 8005, 0006, 8007, ...</c> - and nothing establishes what sets it. It is NOT
    /// invented here: <see cref="FaWriteLadder.SequenceForStep"/> emits the plain count, and if
    /// D102 objects, the bit is the first thing to look at. Recorded rather than guessed.
    /// </para>
    /// </remarks>
    public sealed class FaWriteDriver
    {
        /// <summary>
        /// Where the file is going.
        /// </summary>
        private readonly FaWriteTarget _target;

        /// <summary>
        /// The bytes being pushed.
        /// </summary>
        private readonly byte[] _content;

        /// <summary>
        /// What to send next, and what each answer means.
        /// </summary>
        private readonly FaClientWriteSession _session;

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
        /// Built from the target's number to start with, then REPLACED when the server's connect
        /// confirmation arrives carrying the number it actually assigned. Not readonly for that
        /// reason: the value we open with is a placeholder, not the answer.
        /// </remarks>
        private FaClientConversation _conversation;

        /// <summary>
        /// The port the server opened for this conversation, learned from the confirmation.
        /// </summary>
        /// <remarks>
        /// Zero until the confirmation arrives. The well-known <c>*FA-SERVER</c> port accepts the
        /// LETTER; everything after it belongs to the session port the server answers from.
        /// </remarks>
        private ushort _serverSessionPort;

        /// <summary>
        /// The conversation number in force: the target's until the confirmation replaces it with
        /// the one the server assigned.
        /// </summary>
        /// <remarks>
        /// One field rather than "the target's, unless...", so every message that stamps a
        /// conversation number reads the SAME place. The split reader is how the old code sent
        /// requests under a number the server had never agreed to.
        /// </remarks>
        private ushort _serverConversation;

        /// <summary>
        /// How many blocks the file needs.
        /// </summary>
        private readonly int _blockCount;

        /// <summary>
        /// Our own port, allocated from the node on the first frame we build.
        /// </summary>
        private ushort _ourPort;

        /// <summary>
        /// The Flags 1 of the last message the server sent us, so our acknowledgement can echo it.
        /// </summary>
        private int _answerFlags1 = XmsgAnsweredFlags1.None;

        /// <summary>
        /// Which content block goes out next.
        /// </summary>
        private int _nextBlock;

        /// <summary>
        /// The serial the connect letter carries; the reply echoes it.
        /// </summary>
        private byte _letterSerial = 0x1B;

        /// <summary>
        /// Where the assigned connection number sits in a connection confirmation.
        /// </summary>
        /// <remarks>
        /// The confirmation is <c>{type, echoedRequestWord, connectionNumber, trailingWord}</c>,
        /// so the number is the THIRD word. It is not
        /// <see cref="FaExchangeCodec.ConversationOffset"/>, which addresses the second - correct
        /// for a request, wrong here.
        /// </remarks>
        private const int ConfirmConnectionNumberOffset = 4;

        /// <summary>
        /// Starts a push.
        /// </summary>
        /// <param name="target">
        /// Where the file is going.
        /// </param>
        /// <param name="content">
        /// The whole file. It is split into blocks of
        /// <see cref="FaWriteLadder.ContentBytesPerBlock"/>, the last one padded.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="target"/> or <paramref name="content"/> is null.
        /// </exception>
        public FaWriteDriver(FaWriteTarget target, byte[] content)
        {
            if (target == null) { throw new ArgumentNullException(nameof(target)); }
            if (content == null) { throw new ArgumentNullException(nameof(content)); }

            _target = target;
            _content = content;

            // The block count follows from the file length and the block size - both measured, so
            // it is derived rather than copied from the capture. A zero-length file still needs
            // one block: the ladder has no way to say "no content".
            _blockCount = (content.Length + FaWriteLadder.ContentBytesPerBlock - 1)
                / FaWriteLadder.ContentBytesPerBlock;
            if (_blockCount == 0)
            {
                _blockCount = 1;
            }

            _session = new FaClientWriteSession(_blockCount);

            // Opening values only. The letter is the one message that goes out before the server
            // has told us anything, so it uses these; the confirmation then replaces both.
            _serverConversation = target.Conversation;
            _serverSessionPort = target.ServerPort;
            _conversation = new FaClientConversation(_serverConversation);
        }

        /// <summary>
        /// Gets how many blocks this push will send.
        /// </summary>
        public int BlockCount
        {
            get { return _blockCount; }
        }

        /// <summary>
        /// Gets the session port this conversation answers on, or zero before the first frame.
        /// </summary>
        /// <remarks>
        /// It is what separates our own traffic from another conversation's on the same node -
        /// see <see cref="OnFrame"/>. Exposed so a caller can log it, and so a test can address a
        /// frame at us the way a real server does rather than leaving the field zero.
        /// </remarks>
        public ushort OurPort
        {
            get { return _ourPort; }
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
        /// Gets why the push failed, or an empty string while it has not.
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
        /// A caller that stops the moment the transfer reports failure would cut the conversation
        /// off before its Release reached the wire. The server would then never conclude the
        /// session, never close its session port, and the <c>5PKOC</c> bit XROUT set on that port
        /// would never fire - so XROUT is never kicked and the seat is gone for the life of the
        /// server. A refusal ends the TRANSFER; only the Release ends the CONVERSATION, and the
        /// seat belongs to the conversation. See <see cref="BuildReleaseBody"/>.
        /// </remarks>
        public bool ReleasePending
        {
            get { return _releaseOwed; }
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
        /// Some ways a transfer dies are not visible from inside the ladder. The commonest is
        /// silence: the caller sends its connect letter four times, nothing ever answers, and it
        /// stops. That decision is made ABOVE the driver, so the driver has no idea the transfer is
        /// over and goes on reporting itself unfinished.
        /// <para><b>What that cost, measured 2026-08-18</b></para>
        /// A push to a user that does not exist printed "GIVING UP" after 25 seconds and the process
        /// then sat there until the wall-clock timeout fired at 45 - because nothing had marked the
        /// transfer finished. The run had known the answer for twenty seconds and could not say so.
        /// </remarks>
        public void Abandon(string reason)
        {
            if (reason == null) { throw new ArgumentNullException(nameof(reason)); }

            _session.OnRejected(reason);
        }

        /// <summary>
        /// Gets whether the push has finished successfully.
        /// </summary>
        /// <summary>
        /// Whether the peer is holding the target file open on our behalf.
        /// </summary>
        /// <value>
        /// <c>true</c> between the answered OpenFile and the answered CloseFile.
        /// </value>
        public bool FileOpenOnPeer
        {
            get { return _session.FileOpenOnPeer; }
        }

        /// <summary>
        /// Gives up on the content but keeps going far enough to close the file on the peer.
        /// </summary>
        /// <returns>
        /// <c>true</c> when there was an open file and the driver now has epilogue frames to
        /// send; <c>false</c> when there was nothing to put down.
        /// </returns>
        /// <remarks>
        /// A caller that abandons a push MUST call this and then keep pumping until
        /// <see cref="Done"/>. Walking away instead leaves the file open on the machine for good:
        /// it cannot be rewritten and cannot be deleted - SINTRAN answers FILE ALREADY OPEN - and
        /// it does not appear in LIST-OPEN-FILES either, because the file server's RT program owns
        /// it rather than any terminal. Clearing that needed a file-server restart on D100.
        /// </remarks>
        public bool AbandonButCloseFile()
        {
            return _session.AbandonAfterOpenFile();
        }

        /// <summary>
        /// Gets whether the whole write has finished and the conversation is closed.
        /// </summary>
        /// <value>
        /// <c>true</c> once the close has been sent.
        /// </value>
        public bool Done
        {
            get { return _session.NextAction() == FaClientAction.Done; }
        }

        /// <summary>
        /// Builds the next thing to send.
        /// </summary>
        /// <param name="transport">
        /// The node's transport, which fills in Flags 1, the Counter and the channel.
        /// </param>
        /// <returns>
        /// The frames to transmit in order. Empty when the driver is waiting for the peer or has
        /// finished.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="transport"/> is null.
        /// </exception>
        /// <remarks>
        /// A list rather than a single frame because two of the steps are naturally plural: a
        /// content block is two messages, and a message too long for one datagram leaves as a
        /// fragment pair.
        /// </remarks>
        public IReadOnlyList<XmsgFrame> BuildNext(IXmsgServerTransport transport)
        {
            if (transport == null) { throw new ArgumentNullException(nameof(transport)); }

            if (_ourPort == 0)
            {
                _ourPort = transport.AllocateSessionPort();
            }

            List<XmsgFrame> frames = new List<XmsgFrame>(4);

            // The owed Release goes out FIRST and outside the ladder, because the ladder is over:
            // NextAction answers Failed and would send nothing at all. See where _releaseOwed is
            // set - the transfer has failed, the conversation has not, and the seat belongs to the
            // conversation.
            if (_releaseOwed)
            {
                _releaseOwed = false;
                AddBodyMessage(frames, transport, BuildReleaseBody(), originated: true,
                    frameFlags: (byte)XmsgFrameFlags.ControlBare, role: 0x84);
                return frames;
            }

            switch (_session.NextAction())
            {
                case FaClientAction.SendConnectLetter:
                    frames.Add(BuildConnectLetter(transport));
                    _session.OnConnectLetterSent();
                    break;

                case FaClientAction.SendRequest:
                    AddBodyMessage(frames, transport, BuildRequestBody(), originated: true);
                    _session.OnRequestSent();
                    break;

                case FaClientAction.SendData:
                    AddContent(frames, transport);
                    _session.OnDataSent();
                    break;

                case FaClientAction.SendShortAck:
                    // Our acknowledgement ANSWERS the reply, so it echoes that reply's Flags 1.
                    // ORIGINATED, not echoed. An FA short acknowledgement is an acknowledgement at
                    // the FA level but it travels as an ordinary Data message (subtype 0x0E), so
                    // it spends one of OUR Flags 1 numbers like any other Data frame. Only the
                    // DATAGRAM acknowledgement (subtype 0x03) echoes.
                    //
                    // MEASURED against D100 on 2026-08-10. D100's own originations run a
                    // contiguous 008A..009B and its FA short acknowledgements sit INSIDE that
                    // series (008D, 008F, 0091, 0093, 0095, 0097, 009A) - its own counter, never
                    // ours. We echoed instead, which put our acknowledgements at 0090 and 0094
                    // while our own sequence was already at 009A+. A datagram BEHIND the peer's
                    // expectation is silently dropped, so D100 never saw them, resent its reply,
                    // and eventually dropped the link.
                    //
                    // Our own FILE SERVER already originates these correctly - in the same capture
                    // its acknowledgements sit in our series - so the two halves disagreed, which
                    // is what made this look like a peer problem.
                    //
                    // The earlier ND-to-ND capture could NOT have shown this: the write ladder
                    // alternates one-for-one, so both counters stay level and an echo is
                    // indistinguishable from an origination. Same trap as the Flags 1 ratchet.
                    AddBodyMessage(frames, transport, BuildShortAckBody(), originated: true);
                    _session.OnShortAckSent();
                    break;

                case FaClientAction.SendRelease:
                    // The flags travel with the body: a real ND sends every FA message with
                    // frameFlags 0x82 / role 0x84.
                    AddBodyMessage(frames, transport, BuildReleaseBody(), originated: true,
                        frameFlags: (byte)XmsgFrameFlags.ControlBare, role: 0x84);
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
        /// The frame the node received.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="incoming"/> is null.
        /// </exception>
        /// <remarks>
        /// Anything that is not a message this conversation understands is IGNORED rather than
        /// treated as an error - a link carries acknowledgements and other servers' traffic, and
        /// failing the push on them would make it fail for reasons that have nothing to do with it.
        /// </remarks>
        public void OnFrame(XmsgFrame incoming)
        {
            if (incoming == null) { throw new ArgumentNullException(nameof(incoming)); }

            byte[] body = incoming.GetBodyBytes();
            if (body.Length < FaExchangeCodec.QformOffset)
            {
                return;
            }

            // ONLY FRAMES ADDRESSED TO OUR OWN SESSION PORT BELONG TO THIS CONVERSATION, and
            // before we have a port nothing can be ours at all.
            //
            // Our node is a file SERVER as well as a client, so another machine's conversation
            // with our *FA-SERVER produces file-access messages of exactly the shape read below -
            // its short acknowledgements in particular. One of those arriving before our first
            // request has left fails the push with "A short acknowledgement arrived before any
            // request was sent", having never sent anything.
            //
            // MEASURED on the READ path on 2026-08-10, twice, and fixed there first. This driver
            // has the identical exposure and has simply never been run beside another live
            // conversation - the pushes that succeeded had the link to themselves. Fixed here at
            // the same time rather than left as a defect waiting for the sync daemon, which will
            // by its nature have both directions in flight.
            if (_ourPort == 0)
            {
                return;
            }

            if (incoming.SubHeader != null && incoming.SubHeader.DestinationPort != _ourPort)
            {
                return;
            }

            // Remember what to echo when we acknowledge.
            _answerFlags1 = incoming.Header.Flags1;

            FaMessageType type = (FaMessageType)NdEndian.GetBe16(body, 0);

            if (type == FaMessageType.ConnectionConfirm)
            {
                // A RETRANSMITTED CONFIRM MUST NOT RESTART THE CONVERSATION.
                //
                // Everything below REPLACES _conversation, which resets the message counter to its
                // first value and the session token to the first-exchange one. Doing that in the
                // middle of a live session silently rewinds us.
                //
                // MEASURED 2026-08-10 against D100: it sent its confirmation TWICE (both at
                // Flags 1 0x005F - the same datagram, retransmitted), we rebuilt on the second,
                // and our OpenFile then went out stamped 8000 - the counter ReserveFileEntry had
                // already used - with the first-exchange token 0001 instead of the steady-state
                // one. A real client's stream is 8000, 8100, 8200. D100 refused the repeat and
                // resent its reply until the push died, which is the "stalls at OpenFile" symptom
                // that was chased for days as a ShortAck problem. The ShortAck was always correct.
                //
                // The session knows whether the confirmation has already arrived, so ask it rather
                // than keeping a second flag that could drift out of step with it.
                if (_session.IsConnected)
                {
                    return;
                }

                // THE CONFIRM IS WHERE THE CONVERSATION'S REAL ADDRESS ARRIVES. Until it comes we
                // have only the well-known port we sent the letter through and a conversation
                // number of our own choosing; both are placeholders, and using them for the
                // requests that follow drew XEIMA ("invalid magic", -19) from D100 on 2026-08-09.
                //
                // The confirm carries both answers:
                //  - its SOURCE port is the session the server opened for us. Requests go there,
                //    NOT to *FA-SERVER's well-known port - that port takes letters, not traffic.
                //  - its body word at ConversationOffset is the conversation number the SERVER
                //    assigned. Measured: we sent the letter and D100 answered
                //    07D2 0002 003F 6400 from port 0x05B9, then refused our request because we
                //    addressed port 0x0257 and stamped our own 0x0044.
                // A short frame (an Ack, a reachability answer) has no sub-header at all, so the
                // port is only taken when there IS one. The conversation number lives in the body
                // and is safe either way.
                if (incoming.SubHeader != null)
                {
                    _serverSessionPort = incoming.SubHeader.SourcePort;
                }

                // MIND THE OFFSET. A confirmation is laid out
                // {ConnectionConfirm, echoedRequestWord, connectionNumber, trailingWord} - see
                // FaServerConversation.BuildConnectionConfirm - so the number we want is at 4.
                // Offset 2 holds the word WE chose and the server echoed, which is a different
                // thing and would put us straight back to stamping our own value.
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

            // A REPLY is not its own message type - it is 0x07F0, exactly like a request. What
            // separates them is the CONVERSATION WORD: the server stamps the word it echoed in its
            // confirmation on everything it sends, and we stamp our own on everything we send.
            //
            // FaExchangeCodec.IsReply is deliberately NOT used here. It compares against the
            // constant 0x0002, which is only the usual value of that word - the same assumption
            // that once hung a live terminal. A client knows the real word, because it chose it.
            if (type == FaMessageType.Request
                && NdEndian.GetBe16(body, FaExchangeCodec.ConversationOffset) == _target.LetterEchoWord)
            {
                FaOperation operation;
                ushort sequence;
                if (FaExchangeCodec.TryReadOperation(body, out operation, out sequence))
                {
                    // A REFUSAL HERE USED TO BE REPORTED AS A SUCCESSFUL PUSH, and that is the
                    // worst outcome this driver had.
                    //
                    // The server says no with QFORM selector 1 carrying a SINTRAN error number; a
                    // success omits it. Nothing read it, so every refusal was taken for an ordinary
                    // reply and the ladder ran to the end. MEASURED 2026-08-18: a push to a user
                    // that does not exist was refused on the OPEN -
                    //
                    //     OpenFile reply  ... F2 0001  A2 0027 ...
                    //
                    // 0x27 is 39, which is 047 octal, "No such user name in main directory" in
                    // SINTRAN's own table - exactly right for the "(NOUSR)" it was given. The push
                    // then printed
                    //
                    //     [push] finished: 10 bytes written to "(NOUSR)A:T"
                    //
                    // and exited 0. Nothing was written. A transfer that reports bytes it did not
                    // write is worse than one that hangs: the hang is noticed.
                    //
                    // The read driver had the same blind spot and is fixed the same way. See
                    // DOC\CARVE-FA-READ-REFUSAL-2026-08-18.md.
                    ushort refusal;
                    if (FaRefusalCodec.TryReadStatus(body, out refusal))
                    {
                        _sintranError = refusal;

                        // STILL SAY GOODBYE. A refusal ends the TRANSFER but not the CONVERSATION,
                        // and the conversation is what holds the server's connection seat. The
                        // session goes straight to Failed and never reaches SendRelease, so without
                        // this a refused transfer strands a seat exactly as every transfer used to.
                        //
                        // MEASURED 2026-08-18: three refused creates took *FA-SERVER from 30 free
                        // to 27, while every SUCCESSFUL transfer in the same runs returned its own.
                        _releaseOwed = true;

                    _session.OnRejected(
                            operation + " was refused: SINTRAN error " + refusal
                            + FaSintranError.Describe(refusal));
                        return;
                    }

                    _session.OnReplyReceived(operation, sequence);
                }

                return;
            }

            if (type == FaMessageType.Close)
            {
                _session.OnRejected("the server closed the conversation.");
            }
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
        /// <para><b>The body goes on the wire VERBATIM - do not send it through BuildDatagram</b></para>
        /// <para>
        /// <see cref="IXmsgServerTransport.BuildDatagram"/> composes an XROUT header of its own
        /// out of the control/service word and puts the payload after it. That is right for a
        /// letter whose parameters are all the caller has, and wrong here, because
        /// <see cref="FaConnectLetter.BuildBody"/> already builds the XROUT header - serial,
        /// service, declared length. Going through that path emitted the header TWICE:
        /// </para>
        /// <code>
        /// real D100 letter   1B 41 0014  FF0A "*FA-SERVER" ...
        /// what we sent       00 41 0023  1B 41 0014  FF0A "*FA-SERVER" ...
        ///                    ^^^^^^^^^^  the extra header, and a length covering the wrong span
        /// </code>
        /// <para>
        /// The receiver reads the first four bytes as the header, so it looked for a parameter
        /// tag at the <c>1B</c> and found nothing it knew. Measured against D100 on 2026-08-09.
        /// </para>
        /// <para><b>The sub-header's last word is the body length</b></para>
        /// <para>
        /// Every captured letter agrees: <c>0x0022</c> = 34 bytes for a letter naming
        /// <c>"D102"</c>, <c>0x0024</c> = 36 for one naming <c>"D19999"</c>. So it is derived
        /// here, never chosen.
        /// </para>
        /// </remarks>
        private XmsgFrame BuildConnectLetter(IXmsgServerTransport transport)
        {
            byte[] body = FaConnectLetter.BuildBody(
                _letterSerial,
                FaServer.ServerName,
                _target.ServerSystemName,
                _target.LetterEchoWord);

            return transport.BuildBodyDatagram(
                _target.ServerNode,
                _target.ServerSystem,
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
        /// XROUT's well-known port. A letter asks a server, so it is addressed here, never to a
        /// session port.
        /// </summary>
        private const ushort XroutRequestPort = 0x0000;

        /// <summary>
        /// Builds the request body for the step the ladder is on.
        /// </summary>
        /// <returns>
        /// The whole message body.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the ladder reaches an operation this driver has no fields for.
        /// </exception>
        /// <remarks>
        /// The ladder decides the ORDER; this decides the fields. They are separate because the
        /// order is measured off a capture and the fields are not.
        /// </remarks>
        private byte[] BuildRequestBody()
        {
            FaOperation operation = _session.CurrentOperation;

            switch (operation)
            {
                case FaOperation.ReserveFileEntry:
                    return _conversation.BuildRequest(
                        operation,
                        FaWriteRequests.ReserveFileEntry(
                            _target.BackgroundProgram,
                            _target.LocalUser,
                            _target.User,
                            _target.PasswordWord));

                case FaOperation.OpenFile:
                    return _conversation.BuildRequest(
                        operation, FaWriteRequests.OpenFile(_target.FileSpec, _target.Access));

                case FaOperation.SetBlockSize:
                    return _conversation.BuildRequest(
                        operation,
                        FaWriteRequests.SetBlockSize((ushort)FaWriteLadder.ContentBytesPerBlock));

                case FaOperation.WriteFile:
                    return _conversation.BuildRequest(
                        operation, FaWriteRequests.WriteFile((uint)_nextBlock));

                case FaOperation.SiiiSpecial:
                    // The TRUE length, not the padded one. It carries the last byte INDEX, which
                    // is why the length is passed rather than the index - the builder subtracts.
                    return _conversation.BuildRequest(
                        operation, FaWriteRequests.SetEndOfFile(_content.Length));

                case FaOperation.CloseFile:
                    return _conversation.BuildRequest(operation, FaWriteRequests.CloseFile());

                case FaOperation.ReleaseFileEntry:
                    return _conversation.BuildRequest(operation, FaWriteRequests.ReleaseFileEntry());

                default:
                    throw new InvalidOperationException(
                        "The write ladder asked for " + operation
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
        /// <para><b>This message is what gives the file server's connection seat back</b></para>
        /// <para>
        /// Not directly - it cannot be, because nothing a client sends can touch XROUT's counter.
        /// The chain, carved end to end on 2026-08-18 and written up in
        /// <c>DOC\COSMOS-RE\CARVE-ANSWER-FA-SEAT-RETURN-2026-08-18.md</c>:
        /// </para>
        /// <list type="number">
        /// <item>our connect letter goes to the name <c>*FA-SERVER</c>;</item>
        /// <item>XROUT decrements that port's free-connection count and forwards the letter - the
        /// seat is spent here, before the server has seen a byte of the body;</item>
        /// <item>XROUT marks the port with <c>5PKOC</c>, bit 0 of the port status word, which
        /// <c>XMSG-POFTABS-L03.SYMB</c> documents in ND's own words as
        /// "KICK XROUT ON CLOSE (SET BY XROUT)";</item>
        /// <item>the server serves the session on that port;</item>
        /// <item><b>this Release</b> makes the server conclude the session: it answers Close
        /// (<c>0x07C0</c>) and CLOSES the port;</item>
        /// <item>the kernel's port-close routine <c>YCLOS</c> (131306) tests <c>5PKOC</c> at
        /// <c>ram:b30e</c> and, when set, calls <c>131460 = YKROU</c> - it kicks XROUT;</item>
        /// <item>XROUT restores the count.</item>
        /// </list>
        /// <para>
        /// So the seat is released by ending the conversation properly, and by nothing else. There
        /// is no seat-shaped message to send: the file server itself only ever calls
        /// <c>XMPINFC</c>/<c>XSNSP</c> on its initialisation path, never per session.
        /// </para>
        /// <para><b>Three things were wrong here at once, and only all three together work</b></para>
        /// <para>
        /// The TYPE: we sent <c>0x07C0</c>, which is the SERVER's Close. A client that sends it
        /// answers its own question - the server never hears "I am finished", never closes the port,
        /// <c>5PKOC</c> never fires, and the seat is stranded. The client's message is Release
        /// (<c>0x0782</c>). The OPERANDS: sender's conversation first, then the peer's, which is
        /// what our own server writes in <c>FaServerConversation</c> and what a real ND accepts;
        /// this code had them swapped, because <c>LetterEchoWord</c> is the number the SERVER stamps
        /// on its messages, not ours. The FLAGS: every real FA message carries frame flags
        /// <c>0x82</c> and role <c>0x84</c>; ours carried <c>0x96</c>/<c>0x00</c>.
        /// </para>
        /// <para>
        /// The swap is why the earlier attempts were destructive rather than merely useless. As a
        /// Close the wrong operands are survivable. As a Release the server ACTS on them and frees
        /// the session named in the first word - named one it does not hold, it took the whole file
        /// server down with it, twice. See <c>DOC\CARVE-FA-SEAT-LEAK-2026-08-18.md</c> for how each
        /// side's conversation number was measured.
        /// </para>
        /// </remarks>
        private byte[] BuildReleaseBody()
        {
            byte[] body = new byte[10];
            NdEndian.PutBe16(body, 0, (ushort)FaMessageType.SessionFinished);
            NdEndian.PutBe16(body, 2, _serverConversation);
            NdEndian.PutBe16(body, 4, _target.LetterEchoWord);
            NdEndian.PutBe16(body, 6, 0x8000);
            NdEndian.PutBe16(body, 8, 0x0000);
            return body;
        }

        /// <summary>
        /// Adds the two messages that carry the next block of content.
        /// </summary>
        /// <param name="frames">
        /// The list being filled.
        /// </param>
        /// <param name="transport">
        /// The node's transport.
        /// </param>
        private void AddContent(List<XmsgFrame> frames, IXmsgServerTransport transport)
        {
            int from = _nextBlock * FaWriteLadder.ContentBytesPerBlock;
            int length = _content.Length - from;
            if (length < 0) { length = 0; }
            if (length > FaWriteLadder.ContentBytesPerBlock)
            {
                length = FaWriteLadder.ContentBytesPerBlock;
            }

            byte[] block = new byte[length];
            for (int i = 0; i < length; i++)
            {
                block[i] = _content[from + i];
            }

            byte[][] messages = _conversation.BuildContentMessages(block);
            for (int i = 0; i < messages.Length; i++)
            {
                AddBodyMessage(frames, transport, messages[i], originated: true);
            }

            _nextBlock++;
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
        /// <param name="originated">
        /// <see langword="true"/> when this message starts an exchange of its own, so it takes a
        /// fresh Flags 1; <see langword="false"/> when it answers the server and must echo.
        /// </param>
        /// <remarks>
        /// Everything goes through the FRAGMENTED builder, short bodies included. That is not
        /// laziness: a body short enough to travel whole is sent by exactly the same rule, with
        /// its own length as XMCSM, and that rule is verified over every file-access data frame in
        /// the captures. Choosing an XMCSM here instead would mean inventing one, which is what
        /// the test client had to do.
        /// </remarks>
        private void AddBodyMessage(
            List<XmsgFrame> frames, IXmsgServerTransport transport, byte[] body, bool originated)
        {
            AddBodyMessage(frames, transport, body, originated,
                (byte)XmsgFrameFlags.DataA, 0x00);
        }

        private void AddBodyMessage(
            List<XmsgFrame> frames, IXmsgServerTransport transport, byte[] body, bool originated,
            byte frameFlags, byte role)
        {
            IReadOnlyList<XmsgFrame> built = transport.BuildFragmentedBodyDatagram(
                _target.ServerNode,
                _target.ServerSystem,
                _serverSessionPort,
                _ourPort,
                frameFlags: frameFlags,
                role: role,
                body: body,
                answeredFlags1: originated ? XmsgAnsweredFlags1.None : _answerFlags1);

            for (int i = 0; i < built.Count; i++)
            {
                frames.Add(built[i]);
            }
        }
    }
}
