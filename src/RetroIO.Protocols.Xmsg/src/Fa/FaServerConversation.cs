//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Qform;

namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// Drives the server side of a <c>*FA-SERVER</c> conversation: it answers each request with a
    /// reply carrying the echoed operation and sequence, and builds the short acknowledgement,
    /// connection confirmation and close messages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server's envelope differs from the client's in two ways, both taken from the captures
    /// rather than assumed symmetric:
    /// </para>
    ///  - It answers with the word it ECHOED in its own connection confirmation, not the client's
    ///    conversation number. That word comes from the connect letter's extras. It was modelled as
    ///    the constant <c>0x0002</c> because that is its usual value - see
    ///    <see cref="ResponderConversation"/> for the four captures that disprove the constant and
    ///    the live stall it caused.
    ///  - Its session token is <c>0x9081</c> on <b>every</b> exchange including the first. The client
    ///    is different: it sends <c>0x0001</c> on the first exchange and <c>0xD761</c> thereafter.
    ///    Assuming the two sides behaved alike here would produce a wrong first reply.
    /// </remarks>
    public sealed class FaServerConversation
    {
        /// <summary>
        /// Short-acknowledgement constant sent by the responder.
        /// </summary>
        /// <remarks>
        /// The asker sends <c>0x8485</c> in the same position. Meaning UNKNOWN for both; they are
        /// reproduced because they are what the capture contains.
        /// </remarks>
        public const ushort ResponderShortAckConstant = 0x922A;

        /// <summary>
        /// Short-acknowledgement constant sent by the asker.
        /// </summary>
        public const ushort AskerShortAckConstant = 0x8485;

        /// <summary>
        /// The conversation number the client is using.
        /// </summary>
        private ushort _clientConversation;

        /// <summary>
        /// The word this side puts in every message it sends: the one it echoed in its connection
        /// confirmation.
        /// </summary>
        private ushort _responderConversation = FaExchangeCodec.ResponderConversation;

        /// <summary>
        /// How many replies have been built so far.
        /// </summary>
        private int _repliesBuilt;

        /// <summary>
        /// The session-header counter, shared by every message this side sends.
        /// </summary>
        /// <remarks>
        /// The counting rule lives in <see cref="FaMessageCounter"/> because the CLIENT side obeys
        /// exactly the same one, and it has already been got wrong twice.
        /// </remarks>
        private readonly FaMessageCounter _messageCounter = new FaMessageCounter();

        /// <summary>
        /// Takes the next session-header byte for a message this side is about to send.
        /// </summary>
        /// <param name="highBit">
        /// <see langword="true"/> to set bit 7, which every message carries EXCEPT the first data
        /// message of a two-message delivery.
        /// </param>
        /// <returns>
        /// The session-header byte.
        /// </returns>
        /// <remarks>
        /// <para><b>ONE counter for replies AND data messages - CORRECTED 2026-08-06</b></para>
        /// Measured over the first 26 messages the server sends in
        /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-read.txt</c>:
        /// <code>
        /// 80 81 82 83 84 05 86 | 87 08 89 | 8A 0B 8C | 8D 0E 8F | 90 11 92 ...
        /// low bits: 00 01 02 03 04 05 06   07 08 09   0A 0B 0C   0D 0E 0F   10 11 12
        /// </code>
        /// One counter, +1 per message sent, bit 7 set on all of them except the first data message
        /// of each pair.
        /// <para><b>What it replaced, and why that looked right</b></para>
        /// Replies used to ECHO the request's byte, and data messages counted separately from zero.
        /// On a LISTING those two models are indistinguishable - no data messages are sent, so an
        /// own-counter stays in step with the echo, and the captured listing shows request 80 / reply
        /// 80, request 81 / reply 81 either way. The read capture separates them: its request at
        /// line 144 carries <c>86</c> and the reply carries <c>8A</c>, so the reply is NOT an echo.
        /// <para><b>What the old model cost</b></para>
        /// Live on D100 on 2026-08-06 a <c>COPY-FILE</c> took the first <c>ReadFile</c>, received
        /// both data messages, and then stopped instead of asking for position 1. Our numbering was
        /// reply <c>84</c>, data <c>01</c>, data <c>82</c> where the capture wants <c>8A 0B 8C</c>.
        /// <c>FaServerSession.DataMessageCounter</c> had predicted exactly this: "if a client ever
        /// objects, the fix is to give the conversation one counter that both the replies and the
        /// data messages draw from."
        /// </remarks>
        public byte NextMessageCounter(bool highBit)
        {
            return _messageCounter.Next(highBit);
        }

        /// <summary>
        /// Starts answering a conversation.
        /// </summary>
        /// <param name="clientConversation">
        /// The conversation number the client chose. It is echoed in the connection confirmation and
        /// the close, but NOT in ordinary replies.
        /// </param>
        public FaServerConversation(ushort clientConversation)
        {
            _clientConversation = clientConversation;
        }

        /// <summary>
        /// Gets how many replies have been built.
        /// </summary>
        public int RepliesBuilt
        {
            get { return _repliesBuilt; }
        }

        /// <summary>
        /// Gets the word this side stamps on everything it sends - the one echoed in the connection
        /// confirmation.
        /// </summary>
        /// <remarks>
        /// <para><b>This was a constant, and the constant caused a live hang</b></para>
        /// <para>
        /// It was modelled as <c>FaExchangeCodec.ResponderConversation</c> (<c>0x0002</c>), which is
        /// simply its most common value. Measured 2026-08-08 across four real ND-to-ND
        /// conversations, the word a server puts in its replies is ALWAYS the one it echoed in its
        /// own connection confirmation:
        /// </para>
        /// <code>
        /// confirm 07D2 0006 0004 6400   ->   replies 07F0 0006 ...
        /// confirm 07D2 0008 003F 6400   ->   replies 07F0 0008 ...
        /// confirm 07D2 000C 0040 6400   ->   replies 07F0 000C ...
        /// confirm 07D2 0002 0004 6400   ->   replies 07F0 0002 ...
        /// </code>
        /// <para>
        /// The echoed word itself comes from the connect letter's extras, so the client chooses it
        /// and it is <b>not</b> always <c>0x0002</c>.
        /// </para>
        /// <para>
        /// What the constant did live: the client accepted the connection, sent its first request,
        /// took our acknowledgement and our reply - and then stopped dead, with no error of any
        /// kind, because the reply carried a conversation word it had never issued. The terminal
        /// hung until SINTRAN timed it out. It is recorded across five runs on 2026-08-08, and the
        /// ONE run that completed is the one where the letter happened to carry <c>0x0002</c> and
        /// the constant was accidentally right. See
        /// <c>DOC/captures/ND-TO-ND-2026-08-08/RIG.md</c>.
        /// </para>
        /// </remarks>
        public ushort ResponderConversation
        {
            get { return _responderConversation; }
        }

        /// <summary>
        /// Gets the conversation number the CLIENT is using, learned from its first request.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="ResponderConversation"/>: this is the number the client stamps
        /// on what it sends, and it appears in the close at word 2. The client takes it from the
        /// connection number our confirmation gave it.
        /// </remarks>
        public ushort ClientConversation
        {
            get { return _clientConversation; }
        }

        /// <summary>
        /// Records the client's conversation number the first time one is seen.
        /// </summary>
        /// <param name="clientConversation">
        /// The number from the client's message. Zero is ignored - it is the "not known yet" value.
        /// </param>
        /// <remarks>
        /// <para><b>Why this exists, found live 2026-08-05</b></para>
        /// A server conversation gets built before the client's number is known. The connect LETTER
        /// carries no file-access conversation number, so the confirmation that answers it is built
        /// by a conversation constructed with zero - and the number only appears on the first real
        /// request afterwards.
        /// <para>
        /// The builder must NOT be replaced at that point: it counts the replies it has produced
        /// (the <c>0x80 + n</c> session-header byte), and a fresh one would restart that count and
        /// send a header byte the client has already seen. So it LEARNS the number instead.
        /// </para>
        /// <para><b>What went wrong without it</b></para>
        /// <see cref="BuildClose"/> echoes this number, and every close we sent carried
        /// <c>0x0000</c> where the captured teardown carries the client's own
        /// (<c>07C0 0002 0044 0000</c>). D100 answered the close with a subtype-07 network error,
        /// <c>Flags2 = 0xFFED</c> = -19 = <c>XEIMA</c>, an invalid-magic reject - after a listing
        /// that had otherwise completed perfectly.
        /// </remarks>
        public void LearnClientConversation(ushort clientConversation)
        {
            if (_clientConversation == 0 && clientConversation != 0)
            {
                _clientConversation = clientConversation;
            }
        }

        /// <summary>
        /// Builds the reply to a request, echoing its operation and sequence.
        /// </summary>
        /// <param name="operation">
        /// The operation code from the request.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence from the request.
        /// </param>
        /// <param name="qformFields">
        /// The QFORM fields following the operation and sequence pair, including the closing
        /// end-of-list selector.
        /// </param>
        /// <returns>
        /// The complete reply body.
        /// </returns>
        /// <remarks>
        /// <para>
        /// The session-header byte comes from this conversation's own counter, shared with the data
        /// messages - see <see cref="NextMessageCounter"/>.
        /// </para>
        /// <para><b>It was modelled as an ECHO of the request's byte, and that was wrong</b></para>
        /// The listing capture <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-list-files.txt</c>
        /// shows every reply carrying the byte its request carried:
        /// <code>
        /// request 80000001  -> reply 800090BB
        /// request 8100D761  -> reply 810090BB
        /// request 8200D761  -> reply 820090BB     ... unbroken up to B5
        /// </code>
        /// which reads as an echo but is equally well explained by a counter, because a listing sends
        /// nothing but replies so the two never diverge. <c>capture-read.txt</c> separates them: the
        /// read request at line 144 carries <c>86</c> and its reply carries <c>8A</c>. Data messages
        /// take the numbers in between.
        /// <para>
        /// The older model before the echo was <c>0x80 + _repliesBuilt</c>, an own-counter that
        /// counted only replies. That was closer than the echo, and failed for the opposite reason -
        /// it ignored the data messages rather than the count.
        /// </para>
        /// </remarks>
        public byte[] BuildReply(
            FaOperation operation, ushort sequence, ReadOnlySpan<byte> qformFields)
        {
            int length = FaExchangeCodec.QformOffset + 3 + 3 + qformFields.Length;
            byte[] body = new byte[length];

            // The conversation is the word we echoed in the confirmation, NOT the 0x0002 constant
            // this used to write. Getting it wrong makes the client abandon the conversation
            // silently right here. See ResponderConversation.
            //
            // Every reply carries bit 7 set - see NextMessageCounter for the sequence it belongs to.
            // The token is always the responder one: unlike the client, there is no first-exchange
            // special case.
            FaExchangeCodec.WriteEnvelope(
                body,
                FaMessageType.Request,
                _responderConversation,
                NextMessageCounter(true),
                FaExchangeCodec.SessionTokenResponder);

            QformWriter writer = new QformWriter(new Span<byte>(body, FaExchangeCodec.QformOffset, body.Length - FaExchangeCodec.QformOffset));
            writer.WriteInteger((ushort)operation);
            writer.WriteInteger(sequence);
            int at = FaExchangeCodec.QformOffset + writer.Position;

            qformFields.CopyTo(new Span<byte>(body, at, qformFields.Length));

            _repliesBuilt++;
            return body;
        }

        /// <summary>
        /// Builds the eight-byte short acknowledgement.
        /// </summary>
        /// <param name="counter">
        /// The exchange counter, which the capture carries in the HIGH byte of the word: the first
        /// acknowledgement is <c>0x0100</c>, the second <c>0x0200</c>, and so on.
        /// </param>
        /// <param name="fromResponder">
        /// <see langword="true"/> for the server's form, <see langword="false"/> for the client's.
        /// The two differ only in the trailing constant.
        /// </param>
        /// <returns>
        /// The eight-byte body.
        /// </returns>
        public byte[] BuildShortAck(byte counter, bool fromResponder)
        {
            // Same envelope as a request or a reply - a short acknowledgement is simply the envelope
            // with nothing after it. The two sides differ only in which conversation word and which
            // trailing constant they carry.
            byte[] body = new byte[FaExchangeCodec.QformOffset];
            FaExchangeCodec.WriteEnvelope(
                body,
                FaMessageType.ShortAck,
                fromResponder ? _responderConversation : _clientConversation,
                counter,
                fromResponder ? ResponderShortAckConstant : AskerShortAckConstant);
            return body;
        }

        /// <summary>
        /// Builds the server's connection confirmation, echoing the client's request word and
        /// stamping the server's own connection number.
        /// </summary>
        /// <param name="echoedRequestWord">
        /// The word the connect letter carried past its declared XROUT length, at extras offset
        /// 4-5. VERIFIED echoed - see the remarks.
        /// </param>
        /// <param name="connectionNumber">
        /// The server's own connection number for this conversation.
        /// </param>
        /// <param name="trailingWord">
        /// The 16-bit word at offset 6. Pass
        /// <see cref="FaExchangeCodec.ConfirmTrailingWord"/> - it is a constant every real machine
        /// emits, and its meaning is UNKNOWN. See that constant and word 3 in the remarks below.
        /// </param>
        /// <returns>
        /// The eight-byte body.
        /// </returns>
        /// <remarks>
        /// <para>
        /// CORRECTED 2026-08-04 from the four captures in
        /// <c>DOC/captures/FA-READ-WRITE-2026-08-04/</c>. Every confirmation D102 sent:
        /// </para>
        /// <code>
        /// capture-read       L 13  07D2 0002 0042 6400   letter extras ... 0002 6400
        /// capture-read       L423  07D2 0004 0043 6400   letter extras ... 0004 6400
        /// capture-list-files L 14  07D2 0002 0008 6400
        /// capture-write      L  5  07D2 0002 0044 6400
        /// capture-open-error L  5  07D2 0002 0045 6400
        /// </code>
        /// <para>
        /// Word 1 was modelled as the constant <c>FaExchangeCodec.ResponderConversation</c>
        /// (<c>0x0002</c>). The two connects inside capture-read disprove that: the second carries
        /// <c>0x0004</c>, and its letter's trailing extras carry <c>07E2 0000 0004 6400</c> against
        /// the first letter's <c>07E2 0000 0002 6400</c>. So word 1 is ECHOED from the letter. It
        /// looked constant only because <c>0x0002</c> is the usual value.
        /// </para>
        /// <para>
        /// Word 2 is the server's own per-connection number: <c>0x0042</c> then <c>0x0043</c> for
        /// the two connects in one capture, then <c>0x0044</c> and <c>0x0045</c> in the two
        /// captures taken after it, and <c>0x0008</c> in an earlier session. It counts UP by one
        /// per connection and survives across conversations. It cannot be derived from the letter -
        /// nothing in the letter carries it - so the caller supplies it. It is NOT the client's
        /// conversation number, which is what this class used to put here; the client's own number
        /// does not appear until its FIRST REQUEST, which arrives after the confirmation.
        /// </para>
        /// <para>
        /// Word 3 was modelled as a system-number BYTE built from the client's node, and that is
        /// wrong twice over - it is a constant, and a system number would not fit in a byte anyway.
        /// Corrected 2026-08-09; see <see cref="FaExchangeCodec.ConfirmTrailingWord"/> for the four
        /// captures that settle it.
        /// </para>
        /// </remarks>
        public byte[] BuildConnectionConfirm(ushort echoedRequestWord, ushort connectionNumber, ushort trailingWord)
        {
            // Remember it: everything this side sends afterwards carries the SAME word - replies,
            // short acknowledgements and the close. See ResponderConversation.
            _responderConversation = echoedRequestWord;

            byte[] body = new byte[8];
            NdEndian.PutBe16(body, 0, (ushort)FaMessageType.ConnectionConfirm);
            NdEndian.PutBe16(body, 2, echoedRequestWord);
            NdEndian.PutBe16(body, 4, connectionNumber);

            // A 16-bit word, not a byte plus a pad - see the parameter's remarks.
            NdEndian.PutBe16(body, 6, trailingWord);
            return body;
        }

        /// <summary>
        /// Builds the close message.
        /// </summary>
        /// <returns>
        /// The eight-byte body.
        /// </returns>
        /// <remarks>
        /// Both sides send this, byte for byte identical in shape - the capture shows node 100 and
        /// node 102 each emitting <c>07C0 responder-word conversation 0000</c>.
        /// <para><b>A network error AFTER the close is NORMAL. Do not chase it.</b></para>
        /// <para>
        /// D100 answers our close with a subtype-07 network error carrying
        /// <c>Flags2 = 0xFFED</c>, and that looks exactly like the invalid-magic reject this class
        /// used to cause. It is not. VERIFIED 2026-08-11 against
        /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-list-files.txt</c>: a REAL D102 sends
        /// its close at 02:20:29.594 and a REAL D100 answers with
        /// <c>2113 0007 0066 0064 01F7 FFED DC36</c> seventeen milliseconds later. Two real
        /// machines, same exchange, same <c>0xFFED</c>.
        /// </para>
        /// <para>
        /// Our own teardown was compared field by field against that capture on 2026-08-11 and
        /// agrees throughout - the FA body is byte-identical (<c>07C0 0002 0042 0000</c> answering
        /// <c>0782 0042 0002 8000</c>), the sub-header mirrors the client's ports the same way, the
        /// close carries the same Flags 1 as the release it answers, and it follows a datagram
        /// acknowledgement in the same order. The only thing left that differs is timing: the real
        /// server waits about 147 ms where we answer in 8.
        /// </para>
        /// <para>
        /// So a <c>0xFFED</c> after a close is evidence of NOTHING. It was evidence once, when the
        /// close carried <c>0x0000</c> as the client's conversation, and that is fixed - which is
        /// exactly why it is worth writing down that the symptom outlived the defect.
        /// </para>
        /// <para>
        /// Word 1 is the echoed responder word, not the <c>0x0002</c> constant this used to write.
        /// The real close in <c>DOC/captures/ND-TO-ND-2026-08-08/nd-to-nd-scenarios.pcapng</c> is
        /// <c>07C0 000C 0040 0000</c>, answering a conversation whose confirmation echoed
        /// <c>000C</c> and whose connection number was <c>0x0040</c>. Our close was once compared
        /// against a real one and declared correct - but that comparison used a session whose
        /// letter carried <c>0x0002</c>, so the constant matched by luck. See
        /// <see cref="ResponderConversation"/>.
        /// </para>
        /// </remarks>
        public byte[] BuildClose()
        {
            byte[] body = new byte[8];
            NdEndian.PutBe16(body, 0, (ushort)FaMessageType.Close);
            NdEndian.PutBe16(body, 2, _responderConversation);
            NdEndian.PutBe16(body, 4, _clientConversation);
            NdEndian.PutBe16(body, 6, 0x0000);
            return body;
        }

    }
}
