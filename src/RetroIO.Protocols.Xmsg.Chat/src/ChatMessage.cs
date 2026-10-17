//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;

namespace RetroIO.Protocols.Xmsg.Chat
{
    /// <summary>
    /// One chat message: a kind, a nickname and a line of text.
    /// </summary>
    /// <remarks>
    /// <para><b>Wire layout</b></para>
    /// <code>
    /// byte  0      kind
    /// byte  1      nickname length, 0..255
    /// bytes 2..    nickname, ASCII
    /// then  2      text length, big-endian
    /// then  n      text, ASCII
    /// </code>
    /// Big-endian because everything else on this machine is: the ND-100 is a big-endian,
    /// word-addressed machine, and a chat program written in PLANC on the other side would read
    /// these as words. Lengths are explicit rather than terminator-scanned so a text containing any
    /// byte value survives the trip.
    /// <para><b>Why ASCII</b></para>
    /// A SINTRAN terminal is a 7-bit device and the TAD layer strips bit 7 from keystrokes, so a
    /// nickname or line that is not ASCII could never be typed at the other end anyway.
    /// </remarks>
    public readonly struct ChatMessage
    {
        /// <summary>
        /// The largest nickname the length byte can describe.
        /// </summary>
        public const int MaximumNicknameLength = 255;

        /// <summary>
        /// The largest text the two length bytes can describe.
        /// </summary>
        /// <remarks>
        /// Well beyond anything a terminal user types; the real limit in practice is the message
        /// buffer the sender reserved.
        /// </remarks>
        public const int MaximumTextLength = 65535;

        /// <summary>
        /// Initialises a message.
        /// </summary>
        /// <param name="kind">
        /// What the message is.
        /// </param>
        /// <param name="nickname">
        /// Who it concerns - the speaker, joiner or leaver. May be empty.
        /// </param>
        /// <param name="text">
        /// The line of text. May be empty.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when either string is longer than its length field can describe.
        /// </exception>
        public ChatMessage(ChatMessageKind kind, string nickname, string text)
        {
            if (nickname == null)
            {
                nickname = string.Empty;
            }

            if (text == null)
            {
                text = string.Empty;
            }

            if (nickname.Length > MaximumNicknameLength)
            {
                throw new ArgumentException(
                    "A nickname may be at most " + MaximumNicknameLength + " characters.",
                    nameof(nickname));
            }

            if (text.Length > MaximumTextLength)
            {
                throw new ArgumentException(
                    "Text may be at most " + MaximumTextLength + " characters.",
                    nameof(text));
            }

            Kind = kind;
            Nickname = nickname;
            Text = text;
            OriginSystem = 0;
            HopsRemaining = 0;
            LineId = 0;
        }

        /// <summary>
        /// Creates a relayed message, which carries where it started and how far it may still go.
        /// </summary>
        /// <param name="nickname">
        /// The speaker, as their OWN machine knows them - unqualified, exactly as in
        /// <see cref="ChatMessageKind.TrunkSaid"/>.
        /// </param>
        /// <param name="text">
        /// The room, a slash, then the line.
        /// </param>
        /// <param name="originSystem">
        /// The system the speaker is on. Not the machine that forwarded this.
        /// </param>
        /// <param name="hopsRemaining">
        /// How many more relays this may take. Decremented at each one, dropped at zero.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when either string is longer than its length field can describe.
        /// </exception>
        public ChatMessage(string nickname, string text, ushort originSystem, byte hopsRemaining)
            : this(ChatMessageKind.TrunkRelay, nickname, text)
        {
            OriginSystem = originSystem;
            HopsRemaining = hopsRemaining;
            LineId = 0;
        }

        /// <summary>
        /// Creates a relayed message that carries the origin's own line number, so a second copy
        /// arriving by another path can be recognised and dropped.
        /// </summary>
        /// <param name="nickname">
        /// The speaker, as their OWN machine knows them - unqualified.
        /// </param>
        /// <param name="text">
        /// The room, a slash, then the line.
        /// </param>
        /// <param name="originSystem">
        /// The system the speaker is on. Not the machine that forwarded this.
        /// </param>
        /// <param name="hopsRemaining">
        /// How many more relays this may take. Decremented at each one, dropped at zero.
        /// </param>
        /// <param name="lineId">
        /// The number the ORIGIN stamped on this line. A relay passes it on unchanged; renumbering
        /// it would make the same line look like two.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when either string is longer than its length field can describe.
        /// </exception>
        public ChatMessage(
            string nickname, string text, ushort originSystem, byte hopsRemaining, ushort lineId)
            : this(ChatMessageKind.TrunkRelayId, nickname, text, originSystem, hopsRemaining, lineId)
        {
        }

        /// <summary>
        /// Creates a relayed message of a given kind that carries the origin's line number.
        /// </summary>
        /// <param name="kind">
        /// <see cref="ChatMessageKind.TrunkRelayId"/> for a room line, or
        /// <see cref="ChatMessageKind.TrunkDirect"/> for a direct message.
        /// </param>
        /// <param name="nickname">
        /// The speaker or sender, as their own machine knows them - unqualified.
        /// </param>
        /// <param name="text">
        /// For a room line, the room, a slash, then the message. For a direct message, the TARGET,
        /// a slash, then the message - the same shape with the target in place of the room.
        /// </param>
        /// <param name="originSystem">
        /// The system the sender is on. Not the machine that forwarded this.
        /// </param>
        /// <param name="hopsRemaining">
        /// How many more relays this may take. Decremented at each one, dropped at zero.
        /// </param>
        /// <param name="lineId">
        /// The number the ORIGIN stamped. A relay passes it on unchanged.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when either string is longer than its length field can describe.
        /// </exception>
        public ChatMessage(
            ChatMessageKind kind, string nickname, string text,
            ushort originSystem, byte hopsRemaining, ushort lineId)
            : this(kind, nickname, text)
        {
            OriginSystem = originSystem;
            HopsRemaining = hopsRemaining;
            LineId = lineId;
        }

        /// <summary>
        /// A replayed history line, carrying the moment it was originally said.
        /// </summary>
        /// <param name="nickname">
        /// The speaker, as their own machine knew them when they said it.
        /// </param>
        /// <param name="text">
        /// What they said.
        /// </param>
        /// <param name="saidAt">
        /// When it was said, from the ND's own clock. Pass
        /// <see cref="NdCalendarTime.Unknown"/> for a line replayed out of a history block
        /// written before the time was recorded.
        /// </param>
        /// <remarks>
        /// The time is what separates history from live traffic in a way that matters. A replayed
        /// line used to be shown with the time it was REPLAYED, so a whole backlog shared one
        /// timestamp and could not say what happened when.
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// Thrown when either string is longer than its length field can describe.
        /// </exception>
        public ChatMessage(string nickname, string text, NdCalendarTime saidAt)
            : this(ChatMessageKind.History, nickname, text)
        {
            SaidAt = saidAt;
        }

        /// <summary>
        /// Gets what this message is.
        /// </summary>
        public ChatMessageKind Kind { get; }

        /// <summary>
        /// Gets when a <see cref="ChatMessageKind.History"/> line was originally said.
        /// </summary>
        /// <remarks>
        /// <see cref="NdCalendarTime.Unknown"/> on every other kind, and on a history line whose
        /// block predates the time being recorded. Live traffic needs no such field: it is
        /// arriving now, so the receiver's own clock is the right answer.
        /// </remarks>
        public NdCalendarTime SaidAt { get; }

        /// <summary>
        /// Gets the nickname this message concerns.
        /// </summary>
        public string Nickname { get; }

        /// <summary>
        /// Gets the line of text.
        /// </summary>
        public string Text { get; }

        /// <summary>
        /// Gets the system the speaker is on, for <see cref="ChatMessageKind.TrunkRelay"/>.
        /// </summary>
        /// <remarks>
        /// Zero on every other kind, which do not carry it. On a relayed message this is the
        /// ORIGIN and not the forwarder - the receiver cannot work it out from the letter, because
        /// the letter came from whoever relayed it last.
        /// </remarks>
        public ushort OriginSystem { get; }

        /// <summary>
        /// Gets how many further relays this message may take.
        /// </summary>
        /// <remarks>
        /// Zero on every other kind. A relay decrements it and drops the message at zero, which is
        /// what stops a mesh reflecting for ever.
        /// </remarks>
        public byte HopsRemaining { get; }

        /// <summary>
        /// Gets the line number stamped by the machine the line was typed on.
        /// </summary>
        /// <remarks>
        /// Zero on every kind except <see cref="ChatMessageKind.TrunkRelayId"/>. Together with
        /// <see cref="OriginSystem"/> it names the line, and that pair is what makes it possible to
        /// tell a second copy arriving by another path from a genuinely new line. It is stamped
        /// ONCE, by the origin, and travels unchanged - a relay must never renumber it.
        /// </remarks>
        public ushort LineId { get; }

        /// <summary>
        /// The origin system and hop count carried by <see cref="ChatMessageKind.TrunkRelay"/>.
        /// </summary>
        private const int RelayHeaderLength = 3;

        /// <summary>
        /// The same, plus the two bytes of <see cref="LineId"/>, carried by
        /// <see cref="ChatMessageKind.TrunkRelayId"/>.
        /// </summary>
        private const int RelayIdHeaderLength = 5;

        /// <summary>
        /// Whether this kind carries a relay header at all.
        /// </summary>
        /// <remarks>
        /// Asked in one place so encode, decode and the size can never disagree about it - which
        /// is the failure that would write three bytes and read none.
        /// </remarks>
        private bool HasRelayHeader
        {
            get
            {
                return Kind == ChatMessageKind.TrunkRelay
                    || Kind == ChatMessageKind.TrunkRelayId
                    || Kind == ChatMessageKind.TrunkDirect
                    || Kind == ChatMessageKind.TrunkDirectBad;
            }
        }

        /// <summary>
        /// Whether this kind carries <see cref="LineId"/> as well.
        /// </summary>
        private bool HasLineId
        {
            get
            {
                return Kind == ChatMessageKind.TrunkRelayId
                    || Kind == ChatMessageKind.TrunkDirect
                    || Kind == ChatMessageKind.TrunkDirectBad;
            }
        }

        /// <summary>
        /// How many header bytes sit between the kind and the speaker length for this kind.
        /// </summary>
        private int HeaderLength
        {
            get
            {
                if (HasLineId) { return RelayIdHeaderLength; }
                if (HasRelayHeader) { return RelayHeaderLength; }
                if (Kind == ChatMessageKind.History) { return NdCalendarTime.ByteCount; }
                return 0;
            }
        }

        /// <summary>
        /// Gets the number of bytes <see cref="Encode"/> will write.
        /// </summary>
        public int ByteCount
        {
            get
            {
                return 1 + HeaderLength
                    + 1 + Nickname.Length + 2 + Text.Length;
            }
        }

        /// <summary>
        /// Writes this message into a buffer.
        /// </summary>
        /// <param name="destination">
        /// The buffer to write into. Must be at least <see cref="ByteCount"/> bytes.
        /// </param>
        /// <returns>
        /// The number of bytes written.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="destination"/> is too small.
        /// </exception>
        public int Encode(Span<byte> destination)
        {
            int needed = ByteCount;
            if (destination.Length < needed)
            {
                throw new ArgumentException(
                    "The buffer needs " + needed + " bytes.", nameof(destination));
            }

            destination[0] = (byte)Kind;

            int at = 1;

            // The relay header sits between the kind and the name, so a reader that knows the
            // kind knows immediately whether to expect it.
            if (HasRelayHeader)
            {
                destination[at] = (byte)((OriginSystem >> 8) & 0xFF);
                destination[at + 1] = (byte)(OriginSystem & 0xFF);
                destination[at + 2] = HopsRemaining;
                at += RelayHeaderLength;

                // The id goes AFTER the hop count, so the first three bytes are laid out exactly
                // as kind 52 has them. Nothing depends on that today - a server reading kind 53 by
                // mistake would still get the name wrong - but it keeps the two kinds readable
                // side by side on a trace, which is where these get diagnosed.
                if (HasLineId)
                {
                    destination[at] = (byte)((LineId >> 8) & 0xFF);
                    destination[at + 1] = (byte)(LineId & 0xFF);
                    at += RelayIdHeaderLength - RelayHeaderLength;
                }
            }

            // A HISTORY LINE CARRIES WHEN IT WAS SAID, in the same place a relay header sits -
            // between the kind and the name. The two never occur together: history is local to
            // one machine and deliberately does not cross a trunk, so there is no kind that
            // would need both.
            if (Kind == ChatMessageKind.History)
            {
                SaidAt.WriteTo(destination.Slice(at));
                at += NdCalendarTime.ByteCount;
            }

            destination[at] = (byte)Nickname.Length;
            at += 1;
            at += Encoding.ASCII.GetBytes(Nickname, destination.Slice(at));

            destination[at] = (byte)((Text.Length >> 8) & 0xFF);
            destination[at + 1] = (byte)(Text.Length & 0xFF);
            at += 2;

            at += Encoding.ASCII.GetBytes(Text, destination.Slice(at));
            return at;
        }

        /// <summary>
        /// Reads a message from a buffer.
        /// </summary>
        /// <param name="source">
        /// The bytes to read.
        /// </param>
        /// <param name="message">
        /// The message read, or the default when the bytes are not a well-formed message.
        /// </param>
        /// <returns>
        /// True when a message was read.
        /// </returns>
        /// <remarks>
        /// Rejects rather than throws. A message arriving on a chat port came off a wire or out of
        /// another program, so malformed input is an expected condition, not a bug: a truncated or
        /// over-long buffer is dropped and the caller keeps serving everybody else.
        /// </remarks>
        public static bool TryDecode(ReadOnlySpan<byte> source, out ChatMessage message)
        {
            message = default;

            if (source.Length < 4)
            {
                // Kind, nickname length and the two text-length bytes are the irreducible minimum.
                return false;
            }

            // THE UPPER BOUND IS ChatMessageKinds.Highest, AND IT MUST NOT BE SPELLED OUT HERE.
            //
            // It used to name a kind directly, and it was left behind twice. It said Left while
            // Rename and Renamed existed above it: a rename decoded as a malformed message and was
            // dropped in silence, so the server never saw the request and the room never heard the
            // answer. It then said Renamed while Who was added above it, with the same result.
            // Neither failed loudly anywhere - the message simply stopped existing in transit.
            //
            // Highest lives next to the enum and is pinned by a test, so the next kind added above
            // it fails that test rather than quietly losing its own messages.
            byte kind = source[0];
            if (kind == (byte)ChatMessageKind.None || kind > ChatMessageKinds.Highest)
            {
                return false;
            }

            // THE RELAY HEADER, when the kind says there is one. Read before the name, because
            // that is where it sits - and checked for length first, since a truncated relay
            // header would otherwise be read as a nickname length and produce nonsense rather
            // than a refusal.
            int at = 1;
            ushort originSystem = 0;
            byte hopsRemaining = 0;

            ushort lineId = 0;
            // TrunkDirectBad carries the SAME five bytes. Leaving it out here was a real bug for
            // as long as it took to notice: the decoder bound had already been raised to 55, so
            // the frame decoded, and its header was then read as a nickname length and a name -
            // exactly the nonsense the comment above warns about, and it would have shown a
            // person garbage instead of telling them their message was dropped.
            bool carriesId = kind == (byte)ChatMessageKind.TrunkRelayId
                || kind == (byte)ChatMessageKind.TrunkDirect
                || kind == (byte)ChatMessageKind.TrunkDirectBad;

            if (kind == (byte)ChatMessageKind.TrunkRelay || carriesId)
            {
                int headerLength = carriesId ? RelayIdHeaderLength : RelayHeaderLength;
                if (source.Length < at + headerLength)
                {
                    return false;
                }

                originSystem = (ushort)((source[at] << 8) | source[at + 1]);
                hopsRemaining = source[at + 2];
                if (carriesId)
                {
                    lineId = (ushort)((source[at + 3] << 8) | source[at + 4]);
                }

                at += headerLength;
            }

            // The history time, read before the name for the same reason the relay header is -
            // and length-checked first, or a truncated one would be read as a nickname length
            // and produce nonsense instead of a refusal.
            NdCalendarTime saidAt = NdCalendarTime.Unknown;
            if (kind == (byte)ChatMessageKind.History)
            {
                if (source.Length < at + NdCalendarTime.ByteCount)
                {
                    return false;
                }

                saidAt = NdCalendarTime.ReadFrom(source.Slice(at));
                at += NdCalendarTime.ByteCount;
            }

            if (source.Length < at + 1)
            {
                return false;
            }

            int nicknameLength = source[at];
            at += 1;
            if (source.Length < at + nicknameLength + 2)
            {
                return false;
            }

            string nickname = nicknameLength == 0
                ? string.Empty
                : Encoding.ASCII.GetString(source.Slice(at, nicknameLength));
            at += nicknameLength;

            int textLength = (source[at] << 8) | source[at + 1];
            at += 2;
            if (source.Length < at + textLength)
            {
                return false;
            }

            string text = textLength == 0
                ? string.Empty
                : Encoding.ASCII.GetString(source.Slice(at, textLength));

            if (carriesId)
            {
                message = new ChatMessage(
                    (ChatMessageKind)kind, nickname, text, originSystem, hopsRemaining, lineId);
            }
            else if (kind == (byte)ChatMessageKind.TrunkRelay)
            {
                message = new ChatMessage(nickname, text, originSystem, hopsRemaining);
            }
            else if (kind == (byte)ChatMessageKind.History)
            {
                message = new ChatMessage(nickname, text, saidAt);
            }
            else
            {
                message = new ChatMessage((ChatMessageKind)kind, nickname, text);
            }

            return true;
        }

        /// <summary>
        /// Returns a readable form, for logs and test failures.
        /// </summary>
        /// <returns>
        /// A short description of the message.
        /// </returns>
        public override string ToString()
        {
            if (Nickname.Length == 0)
            {
                return Kind + " \"" + Text + "\"";
            }

            return Kind + " " + Nickname + " \"" + Text + "\"";
        }
    }
}
