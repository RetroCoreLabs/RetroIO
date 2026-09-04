using System;

using RetroIO.Protocols.Xmsg.Qform;

namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// Drives the client side of a <c>*FA-SERVER</c> conversation: it holds the conversation number,
    /// the exchange sequence and the session token, and builds each request body in turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every captured file-server operation has the same shape - open, one or more operation
    /// exchanges, close - and differs only in the operation codes and payloads. This class owns the
    /// part that is identical across operations, so a client for a new operation only has to supply
    /// its own operation code and payload.
    /// </para>
    /// <para>
    /// <b>Payloads are opaque here, on purpose.</b> The opening exchange carries a directory and user
    /// spec whose internal layout is only partly understood, and synthesising one from a guess would
    /// produce a request the server rejects. Callers pass the QFORM field bytes they want sent -
    /// taken from a capture until the layout is decoded - and this class supplies the envelope
    /// around them.
    /// </para>
    /// <para>
    /// <b>Structural resemblance to RR-LIB.</b> The captured conversation looks like the
    /// request-response model in <c>RetroIO.Protocols.Xmsg.Api.Rr</c>: an opening letter carrying user data that names
    /// the server, a confirmation, request/response pairs, then a disconnect. That resemblance is
    /// NOT verified - no document has been found stating that <c>*FA-SERVER</c> is an RR-LIB server,
    /// and the mapping is inferred from shape alone. It is recorded because it is a useful lead, not
    /// because it is established.
    /// </para>
    /// </remarks>
    public sealed class FaClientConversation
    {
        /// <summary>
        /// The conversation number this client uses on every request.
        /// </summary>
        private readonly ushort _conversation;

        /// <summary>
        /// How many exchanges have been built so far. Drives the SEQUENCE number.
        /// </summary>
        private int _exchangesBuilt;

        /// <summary>
        /// How many messages this side has sent. Drives the SESSION HEADER counter.
        /// </summary>
        /// <remarks>
        /// <para><b>Not the same as the exchange count, and that was a defect</b></para>
        /// <para>
        /// The session-header byte was built as <c>0x80 + exchangesBuilt</c>, which is right only
        /// while every exchange is a single message. It is not: a file write sends a request AND
        /// the block, and the block leaves as a FRAGMENT PAIR - subtype <c>0x0A</c> then
        /// <c>0x0C</c> - so it costs two more.
        /// </para>
        /// <para>
        /// Measured across the whole captured write
        /// (<c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-write.txt</c>), the counter moves by
        /// ONE after a request with nothing following it and by THREE after a request whose block
        /// follows:
        /// </para>
        /// <code>
        /// 80 81 82 83 86 89 8C 8F 92 95 98 9B 9E 9F A0
        ///     +1 +1 +1 +3 +3 +3 +3 +3 +3 +3 +3 +3 +1 +1
        /// </code>
        /// <para>
        /// 15 requests plus 9 blocks at two fragments each is 33 messages, and the last counter is
        /// <c>0x80 + 32</c>. The arithmetic closes exactly, which is why this is stated as measured
        /// rather than as a reading.
        /// </para>
        /// <para>
        /// <b>Bit 7 is a FLAG, not part of the count.</b> The counting rule - seven-bit counter,
        /// bit 7 set on everything except the first message of a content pair - is the same on both
        /// sides and lives in <see cref="FaMessageCounter"/>. This used to be an <c>int</c> that
        /// <see cref="BuildRequest"/> turned into <c>0x80 + n</c> unconditionally, which is right
        /// for a request and wrong for the first half of a content pair.
        /// </para>
        /// </remarks>
        private readonly FaMessageCounter _messageCounter = new FaMessageCounter();

        /// <summary>
        /// Starts a conversation.
        /// </summary>
        /// <param name="conversation">
        /// The conversation number to use. The asker picks this; across the captures it ran 003F,
        /// 0044, 0046, 0048 and never repeated, so it behaves as a counter rather than an opcode.
        /// </param>
        public FaClientConversation(ushort conversation)
        {
            _conversation = conversation;
        }

        /// <summary>
        /// Gets the conversation number in use.
        /// </summary>
        public ushort Conversation
        {
            get { return _conversation; }
        }

        /// <summary>
        /// Gets how many exchanges have been built.
        /// </summary>
        public int ExchangesBuilt
        {
            get { return _exchangesBuilt; }
        }

        /// <summary>
        /// Builds the next request body, advancing the exchange sequence.
        /// </summary>
        /// <param name="operation">
        /// The operation code for this exchange - see the <c>Operation*</c> constants on
        /// <see cref="FaExchangeCodec"/>.
        /// </param>
        /// <param name="qformFields">
        /// The QFORM fields that follow the operation and sequence pair, including the closing
        /// <c>F2 00FF</c> end-of-list selector. Pass an empty span for an exchange that carries
        /// nothing but the pair and the terminator, and it will be added.
        /// </param>
        /// <returns>
        /// The complete message body, ready to place in an XMSG message.
        /// </returns>
        public byte[] BuildRequest(FaOperation operation, ReadOnlySpan<byte> qformFields)
        {
            // 07F0 | conversation | session header (4) | 92 operation | 92 sequence | fields
            int contentLength = FaExchangeCodec.QformOffset + 3 + 3 + qformFields.Length;
            int length = contentLength;

            // WORD ALIGNMENT. The ND-100 is a 16-bit word machine and a request body must be an
            // EVEN number of bytes. An odd one is not rejected - it is silently DROPPED, which is
            // far harder to diagnose than an error.
            //
            // MEASURED 2026-08-10 against a real client writing to a real server
            // (DOC/captures/ND-TO-ND-WRITE-2026-08-10/). Every message D102 sent was even:
            //
            //     step 8000  112     step 8300   26     step 8600   34
            //     step 8100   44     step 0400  594     step 8700   18
            //     step 8200   24     step 8500  594     step 8800   18
            //
            // and it appends the extra byte ONLY when one is needed - steps 8000, 8100 and 8600
            // come out even on their own and get none. Our SetBlockSize was 23 bytes against the
            // real 24, and that is exactly the message D100 stopped answering: it accepted every
            // earlier step, then went silent with no reply and no XENSE. Our two earlier steps
            // survived only because they happened to be even already (112 and 42).
            //
            // The pad VALUE is not a field. Real machines put stale buffer contents there - the
            // same three captured steps carry 0x0A, 0xB0 and 0x06 - so nothing reads it, and it
            // sits after the QFORM end-of-list selector (F2 00FF) where a parser has already
            // stopped. We write a defined zero rather than copy the junk.
            if ((length & 1) != 0)
            {
                length++;
            }

            byte[] body = new byte[length];

            // Session header: 0x80 + messages sent, a zero byte, then the token. The counter is
            // MESSAGES, not exchanges - see _messagesSent. The token is 0001 on the very first
            // exchange and the steady-state asker value on every one after it.
            // A request is never half of a content pair, so bit 7 is always set on it.
            FaExchangeCodec.WriteEnvelope(
                body,
                FaMessageType.Request,
                _conversation,
                _messageCounter.Next(true),
                _exchangesBuilt == 0 ? FaExchangeCodec.SessionTokenFirst : FaExchangeCodec.SessionTokenAsker);

            // The writer is sized to the CONTENT, not to the buffer: any alignment pad added above
            // sits past the end of the QFORM list and must stay untouched, or EnsureComplete would
            // demand that we fill it with a field that does not exist.
            QformWriter writer = new QformWriter(
                new Span<byte>(body, FaExchangeCodec.QformOffset, contentLength - FaExchangeCodec.QformOffset));
            writer.WriteInteger((ushort)operation);
            writer.WriteInteger((ushort)(_exchangesBuilt + 1));
            writer.WriteRaw(qformFields);
            writer.EnsureComplete("Client request body");

            _exchangesBuilt++;
            return body;
        }

        /// <summary>
        /// Builds the pair of messages that carry ONE block of file content.
        /// </summary>
        /// <param name="content">
        /// The block's bytes. Anything shorter than <see cref="FaWriteLadder.ContentBytesPerBlock"/>
        /// is padded, which is what the last block of a file needs.
        /// </param>
        /// <returns>
        /// Two message bodies, in the order they go out.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="content"/> is null.
        /// </exception>
        /// <remarks>
        /// <para><b>Two messages, and the second is marked as the last</b></para>
        /// <para>
        /// One WriteFile exchange is followed by exactly two content messages of
        /// <see cref="FaWriteLadder.ContentBytesPerMessage"/> bytes each. The first carries our own
        /// session token with bit 7 of the counter CLEAR; the second carries
        /// <see cref="FaFileDataCodec.LastDataMessageToken"/> with bit 7 SET. Measured across the
        /// captured write - see <see cref="FaMessageCounter"/>.
        /// </para>
        /// <para><b>The block is padded, never shortened</b></para>
        /// <para>
        /// There is no short block and no end marker in this protocol; the real length is declared
        /// separately by SetEndOfFile, which carries the last byte INDEX. Shortening the last block
        /// would be inventing a signal the protocol does not have.
        /// </para>
        /// <para>
        /// Both messages are longer than one datagram carries, so each leaves as a fragment pair -
        /// subtype <c>0x0A</c> then <c>0x0C</c>. That is the transport's business, not this
        /// class's; the counter here already accounts for two MESSAGES, not four fragments.
        /// </para>
        /// </remarks>
        public byte[][] BuildContentMessages(byte[] content)
        {
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            byte[][] messages = new byte[FaWriteLadder.MessagesPerBlock][];

            for (int i = 0; i < FaWriteLadder.MessagesPerBlock; i++)
            {
                bool last = i == FaWriteLadder.MessagesPerBlock - 1;

                // Where this message's slice of the block starts, and how much of it is real file
                // content rather than padding.
                //
                // BOTH are clamped to the content, and the offset matters as much as the length: a
                // last block shorter than one message leaves the SECOND message starting past the
                // end of the array, and a span of length zero at an out-of-range offset still
                // throws. That is not a rare case - it is every file whose tail is under 1024
                // bytes.
                int from = i * FaWriteLadder.ContentBytesPerMessage;
                if (from > content.Length) { from = content.Length; }

                int available = content.Length - from;
                if (available > FaWriteLadder.ContentBytesPerMessage)
                {
                    available = FaWriteLadder.ContentBytesPerMessage;
                }

                messages[i] = FaFileDataCodec.BuildDataMessage(
                    _conversation,
                    _messageCounter.Next(last),
                    last ? FaFileDataCodec.LastDataMessageToken : FaExchangeCodec.SessionTokenAsker,
                    new ReadOnlySpan<byte>(content, from, available));
            }

            return messages;
        }

        /// <summary>
        /// How many short acknowledgements this side has sent.
        /// </summary>
        /// <remarks>
        /// A SEPARATE counter from <c>_messageCounter</c>, and that is measured rather than
        /// assumed. In <c>capture-write.txt</c> the asker's stream interleaves them:
        /// <code>
        /// 07F0 0044 8000 0001   request        message counter 00
        /// 07A2 0044 0100 8485   short ack      ack counter     01
        /// 07F0 0044 8100 D761   request        message counter 01
        /// 07A2 0044 0200 8485   short ack      ack counter     02
        /// </code>
        /// If a short acknowledgement advanced the message counter, the second request could not
        /// carry <c>81</c>. So it does not, and the acknowledgements count 1, 2, 3 on their own.
        /// </remarks>
        private byte _acksSent;

        /// <summary>
        /// Takes the counter byte for the next short acknowledgement.
        /// </summary>
        /// <returns>
        /// The counter, starting at 1.
        /// </returns>
        /// <remarks>
        /// Starts at ONE, not zero - the first acknowledgement in every capture is <c>0100</c>.
        /// The value sits in the HIGH byte of the session-header word, which is what
        /// <c>FaExchangeCodec.WriteEnvelope</c> does with it.
        /// </remarks>
        public byte NextAckCounter()
        {
            _acksSent++;
            return _acksSent;
        }

        /// <summary>
        /// Gets how many short acknowledgements this side has sent.
        /// </summary>
        public int AcksSent
        {
            get { return _acksSent; }
        }

        /// <summary>
        /// Gets how many messages this side has sent, requests and content alike.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>NoteMessagesSent</c> is GONE (2026-08-09). It existed because the conversation could
        /// not build a content message, so a caller had to send one and then tell the counter it
        /// had - which is a rule that is obeyed correctly right up until somebody forgets. Now
        /// <see cref="BuildContentMessages"/> builds them, so the counter cannot be told the wrong
        /// thing.
        /// </para>
        /// </remarks>
        public int MessagesSent
        {
            get { return _messageCounter.Count; }
        }

        /// <summary>
        /// Checks that a reply belongs to this conversation's exchange.
        /// </summary>
        /// <param name="body">
        /// The reply body.
        /// </param>
        /// <param name="expectedOperation">
        /// The operation code the request carried.
        /// </param>
        /// <param name="expectedSequence">
        /// The exchange sequence the request carried.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the reply echoes both fields.
        /// </returns>
        /// <remarks>
        /// The reply echoes the operation code and the exchange sequence, which is the only way to
        /// match a reply to its request - the conversation number is not echoed, because the
        /// responder answers with its own.
        /// </remarks>
        public static bool IsReplyTo(ReadOnlySpan<byte> body, FaOperation expectedOperation, ushort expectedSequence)
        {
            if (!FaExchangeCodec.IsReply(body)) { return false; }

            FaOperation operation;
            ushort sequence;
            if (!FaExchangeCodec.TryReadOperation(body, out operation, out sequence)) { return false; }

            return operation == expectedOperation && sequence == expectedSequence;
        }

    }
}
