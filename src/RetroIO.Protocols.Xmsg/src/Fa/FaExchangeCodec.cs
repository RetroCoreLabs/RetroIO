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
    /// The envelope shared by every <c>*FA-SERVER</c> operation: the message type, the conversation
    /// number, the session header, and the operation-code / sequence pair that opens the QFORM body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dumping FILE-STATISTICS and DELETE-FILE side by side, in wire order and in full, showed the
    /// two operations are the SAME conversation shape. Their opening 112-byte request is
    /// byte-identical apart from the conversation number:
    /// </para>
    /// <code>
    /// FILE-STAT    07F0 0046  8000 0001  92 0002 92 0001 ...
    /// DELETE-FILE  07F0 0048  8000 0001  92 0002 92 0001 ...
    ///                   ^^^^ conversation number only
    /// </code>
    /// <para>
    /// So the operation is not named anywhere in the opening exchange. That is consistent with
    /// the decentralised dispatch seen in the disassembly, and it is why the 16-bit word after the
    /// message type was concluded to be a conversation counter rather than an opcode: across the
    /// captures it runs 003F, 0044, 0046, 0048 and never repeats.
    /// </para>
    /// <para><b>Body layout</b></para>
    /// <code>
    /// off 0  message type      07F0 request, 07A2 short, 0782, 07C0 / 07D2 closing
    /// off 2  conversation      chosen by the asker; the responder answers with 0002
    /// off 4  session header    80+n, 00, then 0001 on the first exchange and D761 after
    /// off 8  92 operation    the operation code for THIS exchange
    /// off 11 92 sequence     the exchange number, 1, 2, 3 ...
    /// off 14 ... QFORM fields, ending F2 00FF
    /// </code>
    /// <para>
    /// The operation code and sequence are echoed by the reply, which is what lets a reply be matched
    /// to its request.
    /// </para>
    /// </remarks>
    public static class FaExchangeCodec
    {
        /// <summary>
        /// Conversation number the responder uses on every reply.
        /// </summary>
        /// <remarks>
        /// The asker picks its own conversation number, but node 100 answered with <c>0x0002</c> in
        /// every captured operation.
        /// </remarks>
        public const ushort ResponderConversation = 0x0002;

        /// <summary>
        /// The 16-bit word that closes the connection confirmation, at offset 6. MEANING UNKNOWN.
        /// </summary>
        /// <remarks>
        /// <para><b>It is a constant, and it is NOT a system number</b></para>
        /// <para>
        /// This was modelled as "the system number byte, <c>0x64</c> = 100 in every capture", and
        /// built from the CLIENT's node number. Both halves of that are wrong, measured 2026-08-09
        /// across every capture that holds a real connection confirmation:
        /// </para>
        /// <code>
        /// server D100 (0x64), client D102 (0x66)  ->  07D2 0002 0055 6400
        /// server D100 (0x64), client D102 (0x66)  ->  07D2 0002 004D 6400
        /// server D100 (0x64), client D102 (0x66)  ->  07D2 0002 0046 6400
        /// server D102 (0x66), client D103 (0x67)  ->  07D2 0008 003F 6400
        /// </code>
        /// <para>
        /// The last line settles it. The server is <c>0x66</c> and the client is <c>0x67</c>, and
        /// the word is still <c>0x6400</c> - so it is neither side's node number. The first three
        /// lines only ever agreed with "the server's number" because that server WAS node 100. Every
        /// connect letter carries the same <c>6400</c> in its trailing extras, including a letter
        /// sent from node 103 to node 19999, where neither endpoint is 100.
        /// </para>
        /// <para>
        /// It cannot be a system number for a second, independent reason: an ND system number is
        /// SIXTEEN bits, so a single byte could not hold one whatever its value.
        /// </para>
        /// <para>
        /// So it is reproduced as the constant every real machine emits, and its meaning is left
        /// UNKNOWN rather than guessed at. Do not derive it from a node number again.
        /// </para>
        /// </remarks>
        public const ushort ConfirmTrailingWord = 0x6400;

        /// <summary>
        /// Session token carried on the first exchange of a conversation.
        /// </summary>
        public const ushort SessionTokenFirst = 0x0001;

        /// <summary>
        /// Session token carried on every exchange after the first, asker side.
        /// </summary>
        public const ushort SessionTokenAsker = 0xD761;

        /// <summary>
        /// Session token carried on every exchange after the first, responder side.
        /// </summary>
        /// <remarks>
        /// <para>
        /// NOT A CONSTANT - corrected 2026-07-30. This value held on the FILE-STATISTICS and
        /// DELETE-FILE recordings, so it was fixed here as a constant. The CREATE-FILE recording taken
        /// later the same day uses <c>0x909E</c> on every server reply instead.
        /// </para>
        /// <para>
        /// Its meaning is UNKNOWN. It is per-conversation, not per-message: it is identical on every
        /// reply within one conversation and differs between conversations. Treat this constant as
        /// "the value seen in the two 2026-07-29 recordings" and read the real one off the first
        /// reply, rather than asserting it.
        /// </para>
        /// </remarks>
        public const ushort SessionTokenResponder = 0x9081;

        /// <summary>
        /// The responder session token seen in the CREATE-FILE recording of 2026-07-30.
        /// </summary>
        /// <remarks>
        /// Recorded only so the two known values sit side by side and neither looks like the rule. Two
        /// samples are not a pattern; do not infer one from them.
        /// </remarks>
        public const ushort SessionTokenResponderCreateFile = 0x909E;

        /// <summary>
        /// Field selector carrying the access mode on an open request.
        /// </summary>
        /// <remarks>
        /// Absent means read, <c>0x0001</c> means write. Established by running the same
        /// <c>OPEN-FILE</c> twice and changing only the access letter, so the attribution is clean.
        /// The other access letters have not been recorded, so whether the value is a bit set or an
        /// enumeration is UNKNOWN - do not assume <c>0x0002</c> is next.
        /// </remarks>
        public const ushort SelectorAccessMode = 0x0003;

        /// <summary>
        /// Offset of the message type within a body.
        /// </summary>
        public const int MessageTypeOffset = 0;

        /// <summary>
        /// Offset of the conversation number within a body.
        /// </summary>
        public const int ConversationOffset = 2;

        /// <summary>
        /// Offset of the four-byte session header within a body.
        /// </summary>
        public const int SessionHeaderOffset = 4;

        /// <summary>
        /// Offset at which the QFORM fields begin.
        /// </summary>
        public const int QformOffset = 8;

        /// <summary>
        /// Smallest body that can carry the envelope and one field pair.
        /// </summary>
        public const int MinimumBodyLength = 14;

        /// <summary>
        /// Writes the envelope of a file-server message body - the counterpart of
        /// <see cref="TryReadEnvelope"/>.
        /// </summary>
        /// <param name="body">
        /// The message body, starting at the message type. Must be at least
        /// <see cref="QformOffset"/> bytes long.
        /// </param>
        /// <param name="messageType">
        /// The message type written at <see cref="MessageTypeOffset"/>.
        /// </param>
        /// <param name="conversation">
        /// The conversation number written at <see cref="ConversationOffset"/>. Which number that
        /// is depends on who is writing, and getting it wrong has hung a live terminal - see
        /// <see cref="FaServerConversation.ResponderConversation"/>. This method does not choose it.
        /// </param>
        /// <param name="sessionCounter">
        /// The session header's first byte, <c>0x80 + n</c> on the asker side and the reply counter
        /// on the responder side. The byte after it is always zero and is written for you.
        /// </param>
        /// <param name="sessionToken">
        /// The 16-bit session token that closes the envelope.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="body"/> is shorter than <see cref="QformOffset"/> bytes.
        /// </exception>
        /// <remarks>
        /// <para><b>Why this exists</b></para>
        /// <para>
        /// There was a reader for this envelope but no writer, so four builders wrote the same five
        /// fields out by hand: the client request, the server reply, the server short
        /// acknowledgement and the file-data message. They agreed on shape, but three separate live
        /// defects (2026-08-04, -06 and -08) were each ONE of those fields being wrong at ONE of the
        /// sites while the others were right - which is exactly the failure a single writer removes.
        /// </para>
        /// <para>
        /// Only the envelope is written here. What follows it differs completely - a QFORM body for
        /// a request or reply, raw file content for a data message, nothing at all for a short
        /// acknowledgement - so that part stays with each caller.
        /// </para>
        /// <para>
        /// The connection confirmation and the close do NOT use this. They look similar but carry a
        /// 16-bit word at offset 4 where this carries a byte, a zero and a word, so they are a
        /// different layout that merely resembles this one.
        /// </para>
        /// </remarks>
        public static void WriteEnvelope(
            Span<byte> body,
            FaMessageType messageType,
            ushort conversation,
            byte sessionCounter,
            ushort sessionToken)
        {
            if (body.Length < QformOffset)
            {
                throw new ArgumentException(
                    "A file-server envelope needs at least " + QformOffset + " bytes.", nameof(body));
            }

            NdEndian.PutBe16(body, MessageTypeOffset, (ushort)messageType);
            NdEndian.PutBe16(body, ConversationOffset, conversation);

            // The session header is a counter byte, a zero, then the token. The zero is not padding
            // that happens to be there - every capture carries it, on both sides.
            body[SessionHeaderOffset] = sessionCounter;
            body[SessionHeaderOffset + 1] = 0x00;
            NdEndian.PutBe16(body, SessionHeaderOffset + 2, sessionToken);
        }

        /// <summary>
        /// Reads the envelope of a file-server message body.
        /// </summary>
        /// <param name="body">
        /// The message body, starting at the message type.
        /// </param>
        /// <param name="messageType">
        /// The message type.
        /// </param>
        /// <param name="conversation">
        /// The conversation number.
        /// </param>
        /// <param name="sequenceByte">
        /// The session header's first byte, <c>0x80 + n</c>, where <c>n</c> counts exchanges from
        /// zero.
        /// </param>
        /// <param name="sessionToken">
        /// The 16-bit session token.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the body was long enough to carry an envelope.
        /// </returns>
        public static bool TryReadEnvelope(
            ReadOnlySpan<byte> body,
            out FaMessageType messageType,
            out ushort conversation,
            out byte sequenceByte,
            out ushort sessionToken)
        {
            messageType = default;
            conversation = 0;
            sequenceByte = 0;
            sessionToken = 0;

            if (body.Length < QformOffset) { return false; }

            messageType = (FaMessageType)NdEndian.GetBe16(body, MessageTypeOffset);
            conversation = NdEndian.GetBe16(body, ConversationOffset);
            sequenceByte = body[SessionHeaderOffset];
            sessionToken = NdEndian.GetBe16(body, SessionHeaderOffset + 2);
            return true;
        }

        /// <summary>
        /// Reads the operation code and exchange sequence that open the QFORM body.
        /// </summary>
        /// <param name="body">
        /// The message body, starting at the message type.
        /// </param>
        /// <param name="operation">
        /// The operation code for this exchange.
        /// </param>
        /// <param name="sequence">
        /// The exchange number, counting from one.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when both fields were present and correctly tagged.
        /// </returns>
        /// <remarks>
        /// Both fields are class-1 two-byte integers, tag <c>0x92</c>. A body whose first two fields
        /// are not tagged that way is not one of these exchanges, and the method reports false rather
        /// than returning bytes read from the wrong place.
        /// </remarks>
        public static bool TryReadOperation(ReadOnlySpan<byte> body, out FaOperation operation, out ushort sequence)
        {
            operation = default;
            sequence = 0;

            if (body.Length < MinimumBodyLength) { return false; }
            if (body[QformOffset] != (byte)QformTagByte.Integer || body[QformOffset + 3] != (byte)QformTagByte.Integer) { return false; }

            // Cast rather than validate: a code outside FaOperation is a real thing that can arrive
            // on the wire, and swallowing it here would hide it. Callers that care should compare
            // against the named values.
            operation = (FaOperation)NdEndian.GetBe16(body, QformOffset + 1);
            sequence = NdEndian.GetBe16(body, QformOffset + 4);
            return true;
        }

        /// <summary>
        /// Determines whether a body is a reply rather than a request.
        /// </summary>
        /// <param name="body">
        /// The message body.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the conversation number is the responder's.
        /// </returns>
        public static bool IsReply(ReadOnlySpan<byte> body)
        {
            if (body.Length < QformOffset) { return false; }
            return NdEndian.GetBe16(body, ConversationOffset) == ResponderConversation;
        }

    }
}
