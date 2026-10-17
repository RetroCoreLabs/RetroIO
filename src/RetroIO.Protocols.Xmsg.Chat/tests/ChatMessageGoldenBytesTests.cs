//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Chat;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Chat.Tests
{
    /// <summary>
    /// The exact bytes of a chat message, pinned.
    /// </summary>
    /// <remarks>
    /// <para><b>This file IS the specification, and that is not a figure of speech</b></para>
    /// <para>
    /// Every other wire format in this repository is pinned against a capture of a real ND. This
    /// one cannot be: we invented it, and no Norsk Data machine has ever sent a chat message. So
    /// there is nothing to check against except a decision written down - and this is where it is
    /// written down.
    /// </para>
    /// <para><b>Why a round trip is not enough</b></para>
    /// <para>
    /// <c>EveryKindSurvivesARoundTrip</c> encodes and decodes with the same code, so it passes
    /// even if the layout changes - a one-byte text length, or a little-endian one, would round
    /// trip perfectly. What it cannot see is the OTHER implementation: <c>SINTRAN-CHAT\CHATSV.PLNC</c>
    /// and <c>CHAT.PLNC</c> lay these bytes out by hand, in PLANC, against no shared code at all.
    /// A change here that still round trips would leave the two silently disagreeing, and the
    /// symptom would be a garbled nickname on somebody's terminal.
    /// </para>
    /// <para><b>If one of these fails</b></para>
    /// <para>
    /// Do not update the expected bytes to make it pass. Either the change was a mistake, or it is
    /// deliberate and the PLANC files have to change in the same commit.
    /// </para>
    /// </remarks>
    public sealed class ChatMessageGoldenBytesTests
    {
        /// <summary>
        /// The all-rooms answer: no name, and the whole picture in the text.
        /// </summary>
        /// <remarks>
        /// <para><b>Pinned against the PLANC that emits it</b></para>
        /// <c>buildAllWho</c> in <c>CHATSV.PLNC</c> writes the kind, then a ZERO name length -
        /// the SERVER is answering, not a member - then the two-byte text length and the text.
        /// The empty name field is load-bearing: a client that printed the name would print
        /// nothing, and one that expected a name there would read the length bytes as characters.
        /// <para><b>The text is ready to read and nothing parses it</b></para>
        /// Rooms two spaces apart, people one apart. A format nobody parses cannot be parsed
        /// wrongly, which is the whole reason it is laid out for a person rather than a decoder.
        /// </remarks>
        [Fact]
        public void AnAllWhoMessageHasExactlyTheseBytes()
        {
            ChatMessage all = new ChatMessage(ChatMessageKind.AllWho, string.Empty, "LOBBY: ANNA");

            byte[] buffer = new byte[64];
            int written = all.Encode(buffer);

            //  0F        kind = AllWho (15)
            //  00        name length - EMPTY, the server is answering
            //  00 0B     text length, HIGH byte first
            //  4C 4F 42 42 59 3A 20 41 4E 4E 41   "LOBBY: ANNA"
            Assert.Equal("0F00000B4C4F424259 3A20414E4E41".Replace(" ", string.Empty),
                Convert.ToHexString(buffer, 0, written));
        }

        /// <summary>
        /// A replayed line: the same shape as a spoken one, under a different kind.
        /// </summary>
        /// <remarks>
        /// <para><b>NOT the same bytes as Said any more</b></para>
        /// It used to be Said with a different first byte. It no longer is: a history line now
        /// carries WHEN IT WAS SAID, in the seven bytes between the kind and the name - the same
        /// place a relay header sits on the trunk kinds.
        /// <para><b>Why the time had to go on the wire at all</b></para>
        /// A replayed line was shown with the time it was REPLAYED, so an entire backlog shared
        /// one timestamp and could not say what happened when. Measured on D103, 2026-08-28:
        /// every line of a rejoin backlog read <c>13:24</c> while the same line read <c>03:33</c>
        /// in the other machine's live room.
        /// <para><b>The ND's own calendar, not an epoch and not text</b></para>
        /// Second, minute, hour, day, month, then the FULL year big-endian - the words
        /// <c>MN113</c> hands back, in the order it hands them back. Each ND runs its own clock,
        /// so a time only means anything beside the machine that produced it, which is the same
        /// reason history does not cross a trunk.
        /// <para><b>Pinned against histReplay in CHATSV.PLNC</b></para>
        /// </remarks>
        [Fact]
        public void AHistoryMessageCarriesWhenItWasSaid()
        {
            ChatMessage past = new ChatMessage(
                "ANNA", "hei", new NdCalendarTime(1998, 8, 27, 13, 24, 5));

            byte[] buffer = new byte[64];
            int written = past.Encode(buffer);

            //  10        kind = History (16)
            //  05        second
            //  18        minute = 24
            //  0D        hour   = 13
            //  1B        day    = 27
            //  08        month
            //  07 CE     year   = 1998, HIGH byte first
            //  04        nickname length
            //  41 4E 4E 41   "ANNA"
            //  00 03     text length, HIGH byte first
            //  68 65 69  "hei"
            Assert.Equal(
                "1005180D1B0807CE04414E4E41000368 6569".Replace(" ", string.Empty),
                Convert.ToHexString(buffer, 0, written));
        }

        /// <summary>
        /// A history line whose block predates the time being recorded says so, rather than lying.
        /// </summary>
        /// <remarks>
        /// The history already on the three machines was written before there was anywhere to put
        /// a time. Those blocks still replay - that is the whole point of the marker in
        /// <c>histSave</c> - and they arrive with an all-zero time, which
        /// <see cref="NdCalendarTime.IsKnown"/> reports as unknown so the client can leave the
        /// column blank instead of inventing a moment.
        /// </remarks>
        [Fact]
        public void AHistoryLineWithNoRecordedTimeDecodesAsUnknown()
        {
            ChatMessage past = new ChatMessage("ANNA", "hei", NdCalendarTime.Unknown);

            byte[] buffer = new byte[64];
            int written = past.Encode(buffer);

            Assert.Equal(
                "1000000000000000" + "04414E4E41" + "0003" + "686569",
                Convert.ToHexString(buffer, 0, written));

            Assert.True(ChatMessage.TryDecode(buffer.AsSpan(0, written), out ChatMessage back));
            Assert.False(back.SaidAt.IsKnown);
            Assert.Equal(string.Empty, back.SaidAt.ToString());
        }

        /// <summary>
        /// The time survives a round trip through the wire, field for field.
        /// </summary>
        /// <remarks>
        /// Encode and decode agreeing with each other is not enough on its own - both could be
        /// wrong the same way - which is why the byte-for-byte test above exists alongside this
        /// one. This guards the ORDER of the fields, where a swapped day and month would still
        /// round-trip if both halves swapped them.
        /// </remarks>
        [Fact]
        public void TheHistoryTimeSurvivesARoundTrip()
        {
            NdCalendarTime when = new NdCalendarTime(1998, 8, 27, 13, 24, 5);
            ChatMessage past = new ChatMessage("ANNA", "hei", when);

            byte[] buffer = new byte[64];
            int written = past.Encode(buffer);

            Assert.True(ChatMessage.TryDecode(buffer.AsSpan(0, written), out ChatMessage back));

            Assert.Equal(ChatMessageKind.History, back.Kind);
            Assert.Equal("ANNA", back.Nickname);
            Assert.Equal("hei", back.Text);
            Assert.Equal(when, back.SaidAt);
            Assert.Equal(1998, back.SaidAt.Year);
            Assert.Equal(8, back.SaidAt.Month);
            Assert.Equal(27, back.SaidAt.Day);
            Assert.Equal("13:24", back.SaidAt.ToString());
        }

        /// <summary>
        /// A history message truncated inside its time is refused, not read as a name.
        /// </summary>
        /// <remarks>
        /// The failure this guards is the one the relay header already had to answer for: a
        /// truncated header read on as though it were a nickname length builds a plausible
        /// message out of rubbish, which is far worse than a refusal.
        /// </remarks>
        [Fact]
        public void AHistoryMessageCutInsideItsTimeIsRefused()
        {
            ChatMessage past = new ChatMessage(
                "ANNA", "hei", new NdCalendarTime(1998, 8, 27, 13, 24, 5));

            byte[] buffer = new byte[64];
            int written = past.Encode(buffer);

            for (int cut = 1; cut <= NdCalendarTime.ByteCount; cut++)
            {
                Assert.False(
                    ChatMessage.TryDecode(buffer.AsSpan(0, cut), out _),
                    "a history message cut off after " + cut + " byte(s) is inside its time and"
                        + " must be refused, never read on as a nickname length.");
            }

            Assert.True(ChatMessage.TryDecode(buffer.AsSpan(0, written), out _));
        }

        /// <summary>
        /// A forwarded line between servers: the speaker unqualified, the room in front of the text.
        /// </summary>
        /// <remarks>
        /// <para><b>The room travels in the text, split at the FIRST slash</b></para>
        /// A message may contain slashes and a room name may not, so the first one is the
        /// separator. Splitting at the last would put half a sentence in the room name.
        /// <para><b>The speaker is NOT qualified here</b></para>
        /// The name is who their own machine knows them as. The receiver adds the machine, taken
        /// from the magic the letter arrived with - so a speaker cannot forge the machine they
        /// are on by putting it in the name.
        /// </remarks>
        [Fact]
        public void ATrunkSaidMessageHasExactlyTheseBytes()
        {
            ChatMessage forwarded = new ChatMessage(ChatMessageKind.TrunkSaid, "ANNA", "LOBBY/hei");

            byte[] buffer = new byte[64];
            int written = forwarded.Encode(buffer);

            //  33        kind = TrunkSaid (51 decimal)
            //  04        speaker length, UNQUALIFIED
            //  41 4E 4E 41   "ANNA"
            //  00 09     text length, HIGH byte first
            //  4C 4F 42 42 59 2F 68 65 69   "LOBBY/hei"
            Assert.Equal("3304414E4E410009 4C4F4242592F686569".Replace(" ", string.Empty),
                Convert.ToHexString(buffer, 0, written));
        }

        /// <summary>
        /// A RELAYED line: the same shape as TrunkSaid with an origin and a hop count in front.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the origin travels and cannot be inferred</b></para>
        /// The receiver's usual trick is to qualify the speaker with the system the letter arrived
        /// from. On a relayed message that names the RELAY, not the speaker's own machine, so the
        /// origin has to be carried explicitly.
        /// <para><b>Why TrunkSaid was not simply extended</b></para>
        /// Its bytes are pinned by the test above and the PLANC server reads the same layout.
        /// Adding fields there would mean every machine had to be upgraded before any trunk
        /// worked, and these machines are upgraded one at a time.
        /// </remarks>
        [Fact]
        public void ATrunkRelayMessageHasExactlyTheseBytes()
        {
            ChatMessage relayed = new ChatMessage("ANNA", "LOBBY/hei", 103, 3);

            byte[] buffer = new byte[64];
            int written = relayed.Encode(buffer);

            //  34        kind = TrunkRelay (52 decimal)
            //  00 67     origin system = 103, HIGH byte first
            //  03        hops remaining
            //  04        speaker length, UNQUALIFIED
            //  41 4E 4E 41   "ANNA"
            //  00 09     text length, HIGH byte first
            //  4C 4F 42 42 59 2F 68 65 69   "LOBBY/hei"
            Assert.Equal("34006703 04414E4E41 0009 4C4F4242592F686569".Replace(" ", string.Empty),
                Convert.ToHexString(buffer, 0, written));
        }

        /// <summary>
        /// The other direction: those exact bytes decode back to the same message.
        /// </summary>
        /// <remarks>
        /// A golden for EACH direction is the rule for a new kind - CHAT-FEDERATION-DESIGN.md
        /// constraint 5. The /nick defect was precisely an unpinned direction: the client wrote
        /// one field and the server read another, and both ends silently did nothing.
        /// </remarks>
        [Fact]
        public void ThoseExactBytesDecodeBackToARelayedMessage()
        {
            byte[] wire = Convert.FromHexString(
                "34006703" + "04414E4E41" + "0009" + "4C4F4242592F686569");

            ChatMessage decoded;
            bool ok = ChatMessage.TryDecode(wire, out decoded);

            Assert.True(ok);
            Assert.Equal(ChatMessageKind.TrunkRelay, decoded.Kind);
            Assert.Equal((ushort)103, decoded.OriginSystem);
            Assert.Equal((byte)3, decoded.HopsRemaining);
            Assert.Equal("ANNA", decoded.Nickname);
            Assert.Equal("LOBBY/hei", decoded.Text);
        }

        /// <summary>
        /// A relay carrying the origin's line number has exactly these bytes.
        /// </summary>
        /// <remarks>
        /// <para><b>Both directions are pinned, and that is the rule for a new kind</b></para>
        /// CHAT-FEDERATION-DESIGN.md constraint 5. The /nick defect was an unpinned direction: the
        /// client wrote one field, the server read another, and both ends silently did nothing.
        /// <para><b>Why the id sits after the hop count</b></para>
        /// It keeps the first three bytes identical in layout to kind 52, which makes the two
        /// readable side by side on a trace - which is where these get diagnosed.
        /// </remarks>
        [Fact]
        public void ATrunkRelayIdMessageHasExactlyTheseBytes()
        {
            ChatMessage relayed = new ChatMessage("ANNA", "LOBBY/hei", 103, 3, 1234);

            byte[] buffer = new byte[64];
            int written = relayed.Encode(buffer);

            //  35        kind = TrunkRelayId (53 decimal)
            //  00 67     origin system = 103, HIGH byte first
            //  03        hops remaining
            //  04 D2     line id = 1234, HIGH byte first
            //  04        speaker length, UNQUALIFIED
            //  41 4E 4E 41   "ANNA"
            //  00 09     text length, HIGH byte first
            //  4C 4F 42 42 59 2F 68 65 69   "LOBBY/hei"
            Assert.Equal(
                "3500670304D2 04414E4E41 0009 4C4F4242592F686569".Replace(" ", string.Empty),
                Convert.ToHexString(buffer, 0, written));
        }

        /// <summary>
        /// Those exact bytes decode back, id and all.
        /// </summary>
        [Fact]
        public void ThoseExactBytesDecodeBackToARelayedMessageWithAnId()
        {
            byte[] wire = Convert.FromHexString(
                "3500670304D2" + "04414E4E41" + "0009" + "4C4F4242592F686569");

            ChatMessage decoded;
            bool ok = ChatMessage.TryDecode(wire, out decoded);

            Assert.True(ok);
            Assert.Equal(ChatMessageKind.TrunkRelayId, decoded.Kind);
            Assert.Equal((ushort)103, decoded.OriginSystem);
            Assert.Equal((byte)3, decoded.HopsRemaining);
            Assert.Equal((ushort)1234, decoded.LineId);
            Assert.Equal("ANNA", decoded.Nickname);
            Assert.Equal("LOBBY/hei", decoded.Text);
        }

        /// <summary>
        /// Kind 52 is untouched by the arrival of kind 53.
        /// </summary>
        /// <remarks>
        /// The same guarantee TrunkSaid was given when TrunkRelay arrived, for the same reason: a
        /// server that has not been upgraded still speaks 52, and its bytes must not have moved
        /// under it.
        /// </remarks>
        [Fact]
        public void TrunkRelayIsUnchangedByTheArrivalOfTrunkRelayId()
        {
            ChatMessage relayed = new ChatMessage("ANNA", "LOBBY/hei", 103, 3);

            byte[] buffer = new byte[64];
            int written = relayed.Encode(buffer);

            Assert.Equal("34006703" + "04414E4E41" + "0009" + "4C4F4242592F686569",
                Convert.ToHexString(buffer, 0, written));
            Assert.Equal((ushort)0, relayed.LineId);
        }

        /// <summary>
        /// A kind-53 header cut short is refused, not read as a nickname length.
        /// </summary>
        /// <remarks>
        /// Its header is two bytes longer than kind 52's, so a message long enough to satisfy the
        /// OLD check can still be too short for this one. That is exactly the case that would read
        /// the id's high byte as a name length.
        /// </remarks>
        [Fact]
        public void ATruncatedRelayIdHeaderIsRefused()
        {
            // Enough bytes for a kind-52 header, one short of a kind-53 one.
            byte[] wire = Convert.FromHexString("35006703");

            ChatMessage decoded;
            bool ok = ChatMessage.TryDecode(wire, out decoded);

            Assert.False(ok);
        }

        /// <summary>
        /// A direct message crossing a trunk has exactly these bytes.
        /// </summary>
        /// <remarks>
        /// <para><b>Same header as TrunkRelayId, and that is the point</b></para>
        /// A direct message two machines away is relayed and de-duplicated exactly as a room line
        /// is, so it carries the same origin, hops and line id rather than a second mechanism.
        /// <para><b>Target in place of the room</b></para>
        /// TrunkSaid packs <c>ROOM/message</c> into the text. This packs <c>TARGET/message</c>, so
        /// the name field stays free for the SENDER, which is what the receiver has to display.
        /// </remarks>
        [Fact]
        public void ATrunkDirectMessageHasExactlyTheseBytes()
        {
            ChatMessage dm = new ChatMessage(
                ChatMessageKind.TrunkDirect, "KARI", "RONNY/are you free", 102, 3, 1234);

            byte[] buffer = new byte[64];
            int written = dm.Encode(buffer);

            //  36            kind = TrunkDirect (54 decimal)
            //  00 66         origin system = 102, HIGH byte first
            //  03            hops remaining
            //  04 D2         line id = 1234, HIGH byte first
            //  04            sender length, UNQUALIFIED
            //  4B 41 52 49   "KARI"
            //  00 12         text length = 18, HIGH byte first
            //  52 4F ...     "RONNY/are you free"
            Assert.Equal(
                "3600660304D2 044B415249 0012 524F4E4E592F61726520796F752066726565"
                    .Replace(" ", string.Empty),
                Convert.ToHexString(buffer, 0, written));
        }

        /// <summary>
        /// A refusal coming home has the same five-byte header and a TARGET/reason text.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The ORIGIN is the machine that could not deliver - 103 here - and NOT the sender's
        /// machine. The line id is the FAILED message's, which is what lets the far end say which
        /// message it is about.
        /// </para>
        /// </remarks>
        [Fact]
        public void ATrunkDirectBadMessageHasExactlyTheseBytes()
        {
            ChatMessage bad = new ChatMessage(
                ChatMessageKind.TrunkDirectBad, "KARI", "RONNY/not logged in", 103, 3, 1234);

            byte[] buffer = new byte[64];
            int written = bad.Encode(buffer);

            //  37            kind = TrunkDirectBad (55 decimal)
            //  00 67         origin system = 103, HIGH byte first - who could NOT deliver
            //  03            hops remaining
            //  04 D2         line id = 1234, the FAILED message's
            //  04            sender length - the person to be told
            //  4B 41 52 49   "KARI"
            //  00 13         text length = 19, HIGH byte first
            //  52 4F ...     "RONNY/not logged in" - target tried, slash, reason
            Assert.Equal(
                "3700670304D2 044B415249 0013 524F4E4E592F6E6F74206C6F6767656420696E"
                    .Replace(" ", string.Empty),
                Convert.ToHexString(buffer, 0, written));
        }

        /// <summary>
        /// Those exact bytes decode back, with the header read as a header.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the test that would have caught the decoder being taught the new kind's NUMBER
        /// without being taught its HEADER. The bound reached 55 first, so the frame decoded, and
        /// the five header bytes were then read as a nickname length and a name - a person would
        /// have been shown garbage instead of being told their message was dropped.
        /// </para>
        /// </remarks>
        [Fact]
        public void ThoseExactBytesDecodeBackToATrunkDirectBadMessage()
        {
            byte[] wire = Convert.FromHexString(
                "3700670304D2" + "044B415249" + "0013"
                    + "524F4E4E592F6E6F74206C6F6767656420696E");

            ChatMessage decoded;
            bool ok = ChatMessage.TryDecode(wire, out decoded);

            Assert.True(ok);
            Assert.Equal(ChatMessageKind.TrunkDirectBad, decoded.Kind);
            Assert.Equal((ushort)103, decoded.OriginSystem);
            Assert.Equal((byte)3, decoded.HopsRemaining);
            Assert.Equal((ushort)1234, decoded.LineId);
            Assert.Equal("KARI", decoded.Nickname);
            Assert.Equal("RONNY/not logged in", decoded.Text);
        }

        /// <summary>
        /// Those exact bytes decode back, sender and target and all.
        /// </summary>
        [Fact]
        public void ThoseExactBytesDecodeBackToATrunkDirectMessage()
        {
            byte[] wire = Convert.FromHexString(
                "3600660304D2" + "044B415249" + "0012" + "524F4E4E592F61726520796F752066726565");

            ChatMessage decoded;
            bool ok = ChatMessage.TryDecode(wire, out decoded);

            Assert.True(ok);
            Assert.Equal(ChatMessageKind.TrunkDirect, decoded.Kind);
            Assert.Equal((ushort)102, decoded.OriginSystem);
            Assert.Equal((byte)3, decoded.HopsRemaining);
            Assert.Equal((ushort)1234, decoded.LineId);
            Assert.Equal("KARI", decoded.Nickname);
            Assert.Equal("RONNY/are you free", decoded.Text);
        }

        /// <summary>
        /// A qualified target survives the round trip with its machine separator intact.
        /// </summary>
        /// <remarks>
        /// The separator is <c>!</c> and not <c>@</c>, because <c>@</c> is the short command prefix
        /// and <c>@RONNY@D100</c> would need a parser that counts at-signs. This pins that the byte
        /// on the wire is 0x21 and not 0x40, so the two ends cannot drift.
        /// </remarks>
        [Fact]
        public void AQualifiedTargetKeepsItsMachineSeparator()
        {
            ChatMessage dm = new ChatMessage(
                ChatMessageKind.TrunkDirect, "KARI", "D100!RONNY/hei", 102, 3, 7);

            byte[] buffer = new byte[64];
            int written = dm.Encode(buffer);
            string hex = Convert.ToHexString(buffer, 0, written);

            // 21 is '!', the machine separator. 40 would be '@' and is the WRONG one.
            Assert.Contains("21", hex);
            Assert.Equal("D100!RONNY/hei", DecodeText(buffer, written));
        }

        /// <summary>
        /// Decodes a message and hands back just its text, for the test above.
        /// </summary>
        private static string DecodeText(byte[] wire, int length)
        {
            ChatMessage decoded;
            Assert.True(ChatMessage.TryDecode(wire.AsSpan(0, length), out decoded));
            return decoded.Text;
        }

        /// <summary>
        /// A relay header cut short is refused, not read as a nickname length.
        /// </summary>
        /// <remarks>
        /// Without the length check this decodes: 0x00 becomes a zero-length nickname, 0x67 and
        /// the next byte become a text length, and a plausible-looking message comes out of
        /// rubbish. Dropping it is the whole point of TryDecode rejecting rather than throwing.
        /// </remarks>
        [Fact]
        public void ATruncatedRelayHeaderIsRefused()
        {
            byte[] wire = Convert.FromHexString("340067");

            ChatMessage decoded;

            Assert.False(ChatMessage.TryDecode(wire, out decoded));
        }

        /// <summary>
        /// TrunkSaid still encodes exactly as it did, with no relay header.
        /// </summary>
        /// <remarks>
        /// This is the promise that made a separate kind worth having: an un-upgraded server must
        /// keep seeing the bytes it already understands while the rollout is half done.
        /// </remarks>
        [Fact]
        public void TrunkSaidIsUnchangedByTheArrivalOfTrunkRelay()
        {
            ChatMessage forwarded = new ChatMessage(ChatMessageKind.TrunkSaid, "ANNA", "LOBBY/hei");

            byte[] buffer = new byte[64];
            int written = forwarded.Encode(buffer);

            Assert.Equal("3304414E4E410009" + "4C4F4242592F686569",
                Convert.ToHexString(buffer, 0, written));
        }

        /// <summary>
        /// A spoken line: kind, name length, name, a two-byte big-endian text length, the text.
        /// </summary>
        [Fact]
        public void ASaidMessageHasExactlyTheseBytes()
        {
            ChatMessage said = new ChatMessage(ChatMessageKind.Said, "ANNA", "hei");

            byte[] buffer = new byte[64];
            int written = said.Encode(buffer);

            //  05        kind = Said
            //  04        nickname length
            //  41 4E 4E 41   "ANNA"
            //  00 03     text length, HIGH byte first
            //  68 65 69  "hei"
            Assert.Equal("0504414E4E41000368 6569".Replace(" ", string.Empty),
                Convert.ToHexString(buffer, 0, written));
        }

        /// <summary>
        /// The text length is two bytes, BIG endian - a length over 255 proves the order.
        /// </summary>
        /// <remarks>
        /// The one field where a mistake is invisible in ordinary use: every short message has a
        /// zero high byte, so a little-endian writer looks perfectly correct until somebody types
        /// more than 255 characters.
        /// </remarks>
        [Fact]
        public void TheTextLengthIsTwoBytesBigEndian()
        {
            string longText = new string('x', 300);
            ChatMessage said = new ChatMessage(ChatMessageKind.Said, "A", longText);

            byte[] buffer = new byte[512];
            said.Encode(buffer);

            // 05 kind, 01 name length, 41 "A", then 300 = 0x012C as high byte then low.
            Assert.Equal(0x05, buffer[0]);
            Assert.Equal(0x01, buffer[1]);
            Assert.Equal(0x41, buffer[2]);
            Assert.Equal(0x01, buffer[3]);   // HIGH byte of 300
            Assert.Equal(0x2C, buffer[4]);   // LOW byte
        }

        /// <summary>
        /// A rename carries the NEW name in the name field and the OLD one as the text.
        /// </summary>
        /// <remarks>
        /// Pinned as bytes because both PLANC programs build this message by hand and the order of
        /// the two names is the thing most easily got backwards. Getting it wrong renames everybody
        /// to their predecessor, which reads as a bizarre chat bug rather than a byte-order one.
        /// </remarks>
        [Fact]
        public void ARenamedMessageCarriesTheNewNameFirst()
        {
            ChatMessage renamed = new ChatMessage(ChatMessageKind.Renamed, "ANNIKA", "ANNA");

            byte[] buffer = new byte[64];
            int written = renamed.Encode(buffer);

            //  0A        kind = Renamed
            //  06        length of the NEW name
            //  414E4E494B41  "ANNIKA"
            //  00 04     length of the OLD name, as the text
            //  414E4E41  "ANNA"
            Assert.Equal("0A06414E4E494B41000441 4E4E41".Replace(" ", string.Empty),
                Convert.ToHexString(buffer, 0, written));
        }

        /// <summary>
        /// The rename a CLIENT sends carries the new name in the name field and an empty text.
        /// </summary>
        /// <remarks>
        /// The companion to <see cref="ARenamedMessageCarriesTheNewNameFirst"/>, and the direction
        /// that was missing. Only the server-to-client Renamed was pinned, so nothing said where
        /// the new name travels on the way IN - and the PLANC client put it in the TEXT while the
        /// PLANC server read it from the NAME field.
        /// <para><b>What that cost</b></para>
        /// Nothing was printed and nothing happened. Measured on D100 on 2026-08-18: "/nick OLAV"
        /// was accepted, sent, and changed nothing, because the server compared the arriving name
        /// against the seat's current name, found them equal, and took its deliberate
        /// "already your name: not news" path. Two self-consistent sides agreeing to do nothing,
        /// which is why it survived a build that was otherwise working.
        /// <para><b>Why a golden and not a round trip</b></para>
        /// The two ends are a C# program and two PLANC programs on an ND-100. They share no code,
        /// only these bytes, so the bytes are the only place the agreement can be written down.
        /// </remarks>
        [Fact]
        public void ARenameMessageCarriesTheNewNameAndAnEmptyText()
        {
            // Exactly what ChatClient.Rename sends: the NEW nickname, and no text at all.
            ChatMessage rename = new ChatMessage(ChatMessageKind.Rename, "OLAV", string.Empty);

            byte[] buffer = new byte[64];
            int written = rename.Encode(buffer);

            //  09        kind = Rename
            //  04        length of the NEW name
            //  4F4C4156  "OLAV"
            //  00 00     an empty text, both length bytes still written
            Assert.Equal("09044F4C41560000", Convert.ToHexString(buffer, 0, written));
            Assert.Equal(8, written);
        }

        /// <summary>
        /// An empty text still writes both length bytes.
        /// </summary>
        /// <remarks>
        /// A Join carries no text, and it is the first message the PLANC client ever sends. If the
        /// zero length were omitted the server would read the next message's bytes as this one's
        /// text, and the very first exchange would fail in a way that looks like a transport fault.
        /// </remarks>
        [Fact]
        public void AnEmptyTextStillWritesBothLengthBytes()
        {
            ChatMessage join = new ChatMessage(ChatMessageKind.Join, "RONNY", string.Empty);

            byte[] buffer = new byte[64];
            int written = join.Encode(buffer);

            //  01 kind, 05 name length, "RONNY", then 00 00 - and nothing after it.
            Assert.Equal("0105524F4E4E590000", Convert.ToHexString(buffer, 0, written));
            Assert.Equal(9, written);
        }

        /// <summary>
        /// A direct message from a client: the target rides in the TEXT, before a slash.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the target is not in the name field</b></para>
        /// The name field carries whoever the message is ABOUT from the receiver's point of view.
        /// On the way in that is nobody the server needs, and on the way out it is the sender - so
        /// packing the target into the text, exactly as TrunkSaid packs the room, keeps one layout
        /// for the whole family instead of a special case per direction.
        /// <para><b>The separator between machine and person is 0x21</b></para>
        /// <c>!</c>, not <c>@</c>. <c>@</c> is the short command prefix, so <c>@RONNY@D100</c>
        /// would need a parser that counts at-signs; <c>machine!alias</c> reads left to right as
        /// route-then-person. This test pins the byte so the PLANC side cannot drift.
        /// </remarks>
        [Fact]
        public void ADirectMessageHasExactlyTheseBytes()
        {
            ChatMessage dm = new ChatMessage(
                ChatMessageKind.Direct, "ANNA", "D102!RONNY/are you free");

            byte[] buffer = new byte[64];
            int written = dm.Encode(buffer);

            //  11            kind = Direct (17 decimal)
            //  04            sender length
            //  41 4E 4E 41   "ANNA"
            //  00 17         text length = 23, HIGH byte first
            //  44 31 30 32   "D102"
            //  21            '!' - the machine separator, NOT 0x40
            //  52 4F ...     "RONNY/are you free"
            Assert.Equal(
                "11 04 414E4E41 0017 4431303221524F4E4E592F61726520796F752066726565"
                    .Replace(" ", string.Empty),
                Convert.ToHexString(buffer, 0, written));
        }

        /// <summary>
        /// Those exact bytes decode back with the target still joined to the message.
        /// </summary>
        [Fact]
        public void ThoseExactBytesDecodeBackToADirectMessage()
        {
            byte[] wire = Convert.FromHexString(
                "1104414E4E4100174431303221524F4E4E592F61726520796F752066726565");

            ChatMessage decoded;
            bool ok = ChatMessage.TryDecode(wire, out decoded);

            Assert.True(ok);
            Assert.Equal(ChatMessageKind.Direct, decoded.Kind);
            Assert.Equal("ANNA", decoded.Nickname);
            Assert.Equal("D102!RONNY/are you free", decoded.Text);
        }

        /// <summary>
        /// A direct message arriving at its target: the sender is QUALIFIED in the name field.
        /// </summary>
        /// <remarks>
        /// What is displayed must also be what can be typed back, so the sender arrives as
        /// <c>D102!KARI</c> and not as a bare <c>KARI</c> that would be ambiguous the moment two
        /// machines each hold one. The text is the message alone - no target, because the target
        /// is whoever is reading it.
        /// </remarks>
        [Fact]
        public void ADirectedMessageCarriesTheSenderQualified()
        {
            ChatMessage dm = new ChatMessage(
                ChatMessageKind.Directed, "D102!KARI", "are you free");

            byte[] buffer = new byte[64];
            int written = dm.Encode(buffer);

            //  12            kind = Directed (18 decimal)
            //  09            sender length, INCLUDING the machine and the '!'
            //  44 31 30 32 21 4B 41 52 49   "D102!KARI"
            //  00 0C         text length = 12
            Assert.Equal(
                "12 09 44313032214B415249 000C 61726520796F752066726565"
                    .Replace(" ", string.Empty),
                Convert.ToHexString(buffer, 0, written));
        }

        /// <summary>
        /// The delivery receipt names WHO IT WENT TO and carries no text at all.
        /// </summary>
        /// <remarks>
        /// Sent on every delivery, not only on ambiguous ones. Refusing an ambiguous alias protects
        /// the sender when the collision is visible; this protects them when it is not - a machine
        /// that just went down, a second RONNY who logged in a moment ago. A wrong delivery becomes
        /// visible at once instead of days later, and it costs one line.
        /// <para><b>The empty text still writes BOTH length bytes</b></para>
        /// Pinned here because a PLANC encoder that writes the length only when there is text
        /// produces a message one byte short, and the receiver then reads the next field from the
        /// wrong offset - which looks like a corrupt name rather than a missing length.
        /// </remarks>
        [Fact]
        public void ADirectSentReceiptNamesTheTargetAndHasNoText()
        {
            ChatMessage receipt = new ChatMessage(
                ChatMessageKind.DirectSent, "D102!KARI", string.Empty);

            byte[] buffer = new byte[64];
            int written = receipt.Encode(buffer);

            //  13 kind, 09 name length, "D102!KARI", then 00 00 - and nothing after.
            Assert.Equal("130944313032214B4152490000",
                Convert.ToHexString(buffer, 0, written));
            Assert.Equal(13, written);
        }

        /// <summary>
        /// A refusal names the target that was tried and gives the reason as text.
        /// </summary>
        /// <remarks>
        /// For an ambiguous alias the reason carries the CANDIDATES, which is what makes the
        /// refusal usable rather than merely annoying - the sender can retype one of them straight
        /// off the screen. Two machines can each hold a RONNY long before they ever trunk to each
        /// other, so no global uniqueness rule could have been applied, and guessing would be a
        /// privacy failure rather than an inconvenience.
        /// </remarks>
        [Fact]
        public void ADirectBadCarriesTheCandidateList()
        {
            ChatMessage bad = new ChatMessage(
                ChatMessageKind.DirectBad, "RONNY", "which RONNY? D100!RONNY, D102!RONNY");

            byte[] buffer = new byte[128];
            int written = bad.Encode(buffer);

            //  14 kind, 05 name length, "RONNY", 00 23 text length = 35, then the reason.
            Assert.Equal(
                "14 05 524F4E4E59 0023 776869636820524F4E4E593F204431303021524F4E4E592C204431303221524F4E4E59"
                    .Replace(" ", string.Empty),
                Convert.ToHexString(buffer, 0, written));
        }
    }
}
