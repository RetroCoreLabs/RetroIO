//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs - https://github.com/HackerCorpLabs
//

namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// The first word of an FA message body, identifying what kind of message it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every FA message body opens with this word at offset 0, before anything else. It is what
    /// tells a reader whether the rest of the body is a QFORM structure, a bare acknowledgement,
    /// or a control message with no payload at all.
    /// </para>
    /// <para>
    /// The values were read off the wire between two live ND-100s; they are not documented in any
    /// manual we hold. Both directions use the same set - the message type says what the message
    /// is, not who sent it, so a Close carries <c>0x07C0</c> whichever side is closing.
    /// </para>
    /// <para>
    /// These were four separate <c>const ushort</c> fields split across
    /// <see cref="FaExchangeCodec"/> and <see cref="FaServerConversation"/>, which made it
    /// impossible to see the set as a set or to spot a value that belonged to neither.
    /// </para>
    /// </remarks>
    public enum FaMessageType : ushort
    {
        /// <summary>
        /// A request carrying a QFORM body.
        /// </summary>
        /// <remarks>
        /// The operation itself is a separate field further into the body - see
        /// <see cref="FaOperation"/>. This word only says "a QFORM structure follows".
        /// </remarks>
        Request = 0x07F0,

        /// <summary>
        /// The short eight-byte acknowledgement.
        /// </summary>
        /// <remarks>
        /// Carries no QFORM body at all: the eight bytes are the whole message.
        /// </remarks>
        ShortAck = 0x07A2,

        /// <summary>
        /// The client has finished with the conversation and expects a <see cref="Close"/> back.
        /// </summary>
        /// <remarks>
        /// <para>
        /// VERIFIED in <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-list-files.txt</c>, at the
        /// very end of a complete LIST-FILES:
        /// </para>
        /// <code>
        /// f1=01F5  100->102  07F0 ReleaseFileEntry
        /// f1=01F7  100->102  0782 conv=0008  0002 8000 ....   -- ten bytes, no QFORM body
        /// f1=01F7  102->100  07C0 Close
        /// </code>
        /// <para>
        /// So the answer is a Close, NOT a refusal. Answering it as an unreadable request made
        /// D100 abandon the session and immediately reconnect, over and over.
        /// </para>
        /// </remarks>
        SessionFinished = 0x0782,

        /// <summary>
        /// The same "I am finished" message with the low bit clear, seen live from D100.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>OBSERVED, NOT CAPTURED.</b> D100 sent <c>0781 0042 0002 8000 0014</c> - the same ten
        /// bytes, the same layout, at the same point in the conversation as the captured
        /// <see cref="SessionFinished"/>. It appears in NONE of the four 2026-08-04 captures.
        /// </para>
        /// <para>
        /// Treating it the same way is INFERRED from that structural match, not proven. What the
        /// low bit distinguishes is UNKNOWN - a plausible reading is "ended early" against the
        /// captured "ran to the end of the directory", since our folder holds two files and the
        /// captured listing held more than a hundred. Do not state that as fact without a capture
        /// that shows both.
        /// </para>
        /// </remarks>
        SessionFinishedAlternate = 0x0781,

        /// <summary>
        /// The server's confirmation that the conversation is open.
        /// </summary>
        /// <remarks>
        /// Sent once, in reply to the opening letter, before any operation is accepted.
        /// </remarks>
        ConnectionConfirm = 0x07D2,

        /// <summary>
        /// The conversation is being closed.
        /// </summary>
        /// <remarks>
        /// Sent by whichever side closes first; the other side answers with the same type.
        /// </remarks>
        Close = 0x07C0,
    }
}
