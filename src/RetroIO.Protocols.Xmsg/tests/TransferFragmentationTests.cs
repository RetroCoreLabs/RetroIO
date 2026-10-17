//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using RetroIO.Protocols.Xmsg.Hdlc;
using RetroIO.Protocols.Xmsg.Tests;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Establishes how a COSMOS file-transfer data message is carried when it does not fit in one
    /// LAPB frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this test exists.</b> Every earlier note about the transfer assumed the 1030-byte
    /// transfer message arrived as a single unit, because the message length field says 1030 and the
    /// payload obviously carries 1024 bytes of file content. It does not. The message is split across
    /// two LAPB information frames, and the split is visible in the SINTRAN header:
    /// </para>
    /// <code>
    /// 2113 000a 0064 0066 0178 0406 ...   len=622  first fragment
    /// 2113 000c 0064 0066 0178 0252 ...   len=450  continuation
    /// </code>
    /// <para>
    /// Two things follow, and both are checked below rather than asserted from a hex dump:
    /// </para>
    ///  - Subtypes <c>0x0A</c> and <c>0x0C</c> exist and are NOT in <c>SintranPacketSubtype</c>, which
    ///    only knows <c>0x0E</c> for data. They pair up: one <c>0x0C</c> for each <c>0x0A</c>, sharing
    ///    the same Flags1 datagram sequence.
    ///  - Flags2 means different things in each. On the <c>0x0A</c> it is the TOTAL message length; on
    ///    the <c>0x0C</c> it is the byte OFFSET at which the continuation resumes. That is the
    ///    hypothesis this test exists to confirm or kill: it should hold that
    ///    <c>continuationOffset + continuationBytes == totalLength</c>.
    /// <para>
    /// If the arithmetic below fails, the model is wrong and nothing downstream should be built on
    /// it - which is exactly why it is measured rather than assumed.
    /// </para>
    /// </remarks>
    public sealed class TransferFragmentationTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink, used to report the measured numbers.
        /// </param>
        public TransferFragmentationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private const string CaptureName = "claude-transfer-file-COMPLETE-102-to-100-2026-07-29.pcapng";

        /// <summary>
        /// Packet subtype of the first fragment of a segmented message.
        /// </summary>
        private const byte SubtypeFirstFragment = 0x0A;

        /// <summary>
        /// Packet subtype of a continuation fragment.
        /// </summary>
        private const byte SubtypeContinuation = 0x0C;

        /// <summary>
        /// Size of the SINTRAN header that prefixes every information field.
        /// </summary>
        private const int SintranHeaderSize = 13;

        /// <summary>
        /// Frame offset at which the message body begins on a full-addressing frame (subtypes
        /// <c>0x0E</c> and <c>0x0A</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// 13 bytes of SINTRAN header plus 15 bytes of XMSG sub-header: counter, the <c>21 00</c>
        /// marker, frame flags, role, and the four addressing words XMDSY / XMDPT / XMSSY / XMSPT.
        /// </para>
        /// <para>
        /// <b>This is 28, not 32.</b> <c>XmsgSubHeader</c> documents a 19-byte sub-header whose
        /// offsets 13..16 are a four-byte XMCSM control word. Measured against this capture, only
        /// the HIGH half of that word is a header field - and it is simply Flags2 repeated, which is
        /// why the long-standing rule "Flags2 == XMCSM >> 16" has always held. The LOW half is
        /// already the first word of the message body: the message type (<c>0x07F0</c>,
        /// <c>0x07A2</c>) or, on a transfer, the function code <c>0x0042</c>. Reading it as part of
        /// the header is what made the arithmetic here miss by four bytes on the first attempt.
        /// </para>
        /// </remarks>
        private const int BodyOffsetFullHeader = 28;

        /// <summary>
        /// Frame offset at which the message body resumes on a continuation frame (subtype
        /// <c>0x0C</c>).
        /// </summary>
        /// <remarks>
        /// A continuation does not repeat the addressing words - it carries the 13-byte SINTRAN
        /// header and the single counter byte, then picks the message straight back up.
        /// </remarks>
        private const int BodyOffsetContinuation = 14;

        /// <summary>
        /// Total length of a transfer data message: six bytes of message header plus one 1024-byte
        /// block of file content.
        /// </summary>
        private const int TransferMessageLength = 1030;

        /// <summary>
        /// Confirms the fragmentation model: pairing, and the Flags2 length/offset arithmetic.
        /// </summary>
        /// <summary>
        /// A second transfer, of a file only 167 bytes long.
        /// </summary>
        /// <remarks>
        /// Recorded 2026-07-30 to test whether the fragment split tracks the amount of real data. It
        /// does not - see <see cref="TransferMessage_IsAlwaysAWholeBlockNoMatterHowSmallTheFileIs"/>.
        /// </remarks>
        private const string SmallFileCapture = "claude-transfer-SMALL-167bytes-102-to-100-2026-07-30.pcapng";

        /// <summary>
        /// Requires every transfer data message to be a whole 1030 bytes, split at the same point,
        /// even when the file contains far less than one block.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The experiment.</b> <c>LOAD-MODE:BATC</c> on node 102 is 167 bytes - one page, and only
        /// a sixth of one block. It was transferred to node 100 and recorded.
        /// </para>
        /// <para>
        /// <b>Result: the wire looks exactly like a full transfer.</b> Two data messages, both
        /// declaring 1030, both split 622 + 450 with the continuation resuming at 594. Page 0 with
        /// displacement 0, then page 0 with displacement 512 words. So the sender ships the WHOLE
        /// page - two 1024-byte blocks - padded, and the message length never tracks the file.
        /// </para>
        /// <para>
        /// <b>What this settles.</b> The 594-byte split was previously recorded as possibly an
        /// artefact of one file size. It is not: it holds across two transfers of very different
        /// files. It is safe to treat as fixed FOR TRANSFERS.
        /// </para>
        /// <para>
        /// <b>What it does not settle.</b> Whether 594 would change for a message of some other
        /// length is still UNKNOWN, and cannot be tested with this protocol as we understand it -
        /// the transfer data message is the only message large enough to need splitting, and it is
        /// always 1030 bytes. So the question may simply not arise.
        /// </para>
        /// </remarks>
        [Fact]
        public void TransferMessage_IsAlwaysAWholeBlockNoMatterHowSmallTheFileIs()
        {
            string? path = PcapFiles.File(SmallFileCapture);
            if (path == null)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            IReadOnlyList<LapbFrame> frames = HdlcPcap.ReadFramesInCaptureOrder(path);

            int firstFragments = 0;
            int continuations = 0;

            for (int i = 0; i < frames.Count; i++)
            {
                LapbFrame frame = frames[i];
                if (frame.Kind != LapbFrameKind.Information || frame.Info.Length < SintranHeaderSize)
                {
                    continue;
                }

                ReadOnlySpan<byte> info = frame.Info.Span;
                byte subtype = info[3];
                int flags2 = (info[10] << 8) | info[11];

                if (subtype == SubtypeFirstFragment)
                {
                    firstFragments++;
                    _output.WriteLine("first fragment: infoLen=" + info.Length + " declares=" + flags2);

                    // The declared message length is the fixed 1030, not the 167 bytes of real file.
                    Assert.Equal(TransferMessageLength, flags2);
                }
                else if (subtype == SubtypeContinuation)
                {
                    continuations++;
                    _output.WriteLine("continuation:   infoLen=" + info.Length + " resumesAt=" + flags2);

                    // Same split point as the multi-page transfer recorded the day before.
                    Assert.Equal(594, flags2);
                }
            }

            _output.WriteLine("first fragments=" + firstFragments + " continuations=" + continuations);

            // A 167-byte file still ships a whole page: two blocks, so two messages.
            Assert.Equal(2, firstFragments);
            Assert.Equal(firstFragments, continuations);
        }

        /// <summary>
        /// Confirms the fragmentation model on the original transfer: pairing, and the Flags2
        /// length and offset arithmetic.
        /// </summary>
        [Fact]
        public void SegmentedTransferMessage_FlagsTwoIsLengthThenOffset()
        {
            string? path = PcapFiles.File(CaptureName);
            if (path == null)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            IReadOnlyList<LapbFrame> frames = HdlcPcap.ReadFrames(path);

            // Collect the fragments in flow order. Order WITHIN a flow is preserved by
            // HdlcPcap.ReadFrames even though the flows themselves are not interleaved, and both
            // fragments of a message always travel in the same direction, so this is safe.
            List<int> firstLengths = new List<int>();
            List<int> firstFlags2 = new List<int>();
            List<int> firstSeq = new List<int>();
            List<int> contLengths = new List<int>();
            List<int> contFlags2 = new List<int>();
            List<int> contSeq = new List<int>();

            for (int i = 0; i < frames.Count; i++)
            {
                LapbFrame frame = frames[i];
                if (frame.Kind != LapbFrameKind.Information || frame.Info.Length < SintranHeaderSize)
                {
                    continue;
                }

                ReadOnlySpan<byte> info = frame.Info.Span;
                byte subtype = info[3];
                int flags1 = (info[8] << 8) | info[9];
                int flags2 = (info[10] << 8) | info[11];

                if (subtype == SubtypeFirstFragment)
                {
                    firstLengths.Add(info.Length);
                    firstFlags2.Add(flags2);
                    firstSeq.Add(flags1);
                }
                else if (subtype == SubtypeContinuation)
                {
                    contLengths.Add(info.Length);
                    contFlags2.Add(flags2);
                    contSeq.Add(flags1);
                }
            }

            _output.WriteLine("first fragments (subtype 0x0A): " + firstLengths.Count);
            _output.WriteLine("continuations   (subtype 0x0C): " + contLengths.Count);

            Assert.True(firstLengths.Count > 0, "The capture contained no subtype 0x0A frames.");
            Assert.Equal(firstLengths.Count, contLengths.Count);

            for (int i = 0; i < firstLengths.Count; i++)
            {
                // A fragment's contribution is its message-body bytes, which start after the
                // addressing header on the first fragment and after the counter on a continuation.
                int firstPayload = firstLengths[i] - BodyOffsetFullHeader;
                int contPayload = contLengths[i] - BodyOffsetContinuation;
                int total = firstFlags2[i];
                int offset = contFlags2[i];

                _output.WriteLine(
                    "msg " + i
                    + ": seq=" + firstSeq[i]
                    + " total(Flags2)=" + total
                    + " firstPayload=" + firstPayload
                    + " contOffset(Flags2)=" + offset
                    + " contPayload=" + contPayload
                    + " offset+cont=" + (offset + contPayload));

                // The two fragments belong to the same datagram.
                Assert.Equal(firstSeq[i], contSeq[i]);

                // The continuation resumes exactly where the first fragment's payload ended...
                Assert.Equal(firstPayload, offset);

                // ...and the two payloads together are exactly the advertised message length.
                Assert.Equal(total, offset + contPayload);
            }
        }

        /// <summary>
        /// Checks the body-offset model against every ordinary data frame in every file-server
        /// capture: the body length announced in Flags2 must be exactly the frame length minus 28.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the corpus-wide check behind <see cref="BodyOffsetFullHeader"/>. It covers three
        /// operations and both directions - 98 frames - so if 28 were wrong, or varied by message
        /// type or direction, this would fail rather than one capture happening to fit.
        /// </para>
        /// <para>
        /// <b>The transfer capture is deliberately NOT in this list</b>, because the rule does not
        /// hold there and pretending otherwise would hide two real message classes. Flags2 is the
        /// high half of XMCSM, which is a frame-CLASS word; "body length" is what that word means for
        /// the QFORM request/reply class only. The transfer capture contains two other classes, both
        /// checked in <see cref="TransferClass_FlagsTwoIsTheDeclaredSizeNotTheBodyLength"/>:
        /// </para>
        ///  - Class <c>0x0080</c>, the XSLET opening letters that name <c>*XFTRA</c> - two frames of 90
        ///    and 98 bytes, where 0x0080 is a class code and not a length at all.
        ///  - The transfer acknowledgements, which carry the data message's XMCSM verbatim - so Flags2
        ///    reads 1030 - while their own body is six zero bytes.
        /// </remarks>
        [Theory]
        [InlineData("claude-file-stat-102-to-100-2026-07-29.pcapng")]
        [InlineData("claude-list-files-d100-system-2026-07-29.pcapng")]
        [InlineData("claude-delete-file-102-to-100-2026-07-29.pcapng")]
        public void DataFrame_FlagsTwoIsExactlyTheBodyLength(string captureName)
        {
            string? path = PcapFiles.File(captureName);
            if (path == null)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            IReadOnlyList<LapbFrame> frames = HdlcPcap.ReadFrames(path);

            int checkedFrames = 0;
            int mismatches = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                LapbFrame frame = frames[i];
                if (frame.Kind != LapbFrameKind.Information || frame.Info.Length < SintranHeaderSize)
                {
                    continue;
                }

                ReadOnlySpan<byte> info = frame.Info.Span;

                // Only ordinary, unfragmented data frames: subtype 0x0E.
                if (info[3] != (byte)SintranPacketSubtype.Data)
                {
                    continue;
                }

                int flags2 = (info[10] << 8) | info[11];
                checkedFrames++;

                if (flags2 != info.Length - BodyOffsetFullHeader)
                {
                    mismatches++;
                    _output.WriteLine(
                        "  MISMATCH: len=" + info.Length
                        + " Flags2=" + flags2
                        + " marker2=0x" + info[1].ToString("x2")
                        + " protocolId=0x" + info[12].ToString("x2")
                        + " head=" + Hex(info, 40));
                }
            }

            _output.WriteLine(captureName + ": " + checkedFrames + " data frames, " + mismatches + " mismatches");
            Assert.True(checkedFrames > 0, "The capture contained no subtype 0x0E frames.");
            Assert.Equal(0, mismatches);
        }

        /// <summary>
        /// Records the two message classes in the transfer capture that do not follow the
        /// body-length rule, and shows that the transfer is acknowledged one message at a time.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each 1030-byte data message is answered by exactly one 34-byte frame travelling the other
        /// way, whose body is six zero bytes and whose XMCSM - and therefore Flags2 - is copied from
        /// the message being acknowledged. Four data messages, four acknowledgements.
        /// </para>
        /// <para>
        /// <b>What this does NOT establish.</b> One-ack-per-message is a COUNT, not an ordering.
        /// <c>HdlcPcap.ReadFrames</c> groups frames by flow rather than interleaving them, so this
        /// test cannot see whether the sender waited for each acknowledgement before sending the next
        /// message. Whether the application layer is stop-and-wait or pipelined is still UNKNOWN and
        /// needs a time-ordered merge of the two directions to answer.
        /// </para>
        /// </remarks>
        [Fact]
        public void TransferClass_FlagsTwoIsTheDeclaredSizeNotTheBodyLength()
        {
            string? path = PcapFiles.File(CaptureName);
            if (path == null)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            IReadOnlyList<LapbFrame> frames = HdlcPcap.ReadFrames(path);

            int letterFrames = 0;
            int transferAcks = 0;
            int dataMessages = 0;

            for (int i = 0; i < frames.Count; i++)
            {
                LapbFrame frame = frames[i];
                if (frame.Kind != LapbFrameKind.Information || frame.Info.Length < SintranHeaderSize)
                {
                    continue;
                }

                ReadOnlySpan<byte> info = frame.Info.Span;
                int flags2 = (info[10] << 8) | info[11];

                if (info[3] == SubtypeFirstFragment)
                {
                    dataMessages++;
                    continue;
                }

                if (info[3] != (byte)SintranPacketSubtype.Data) { continue; }

                if (flags2 == 0x0080)
                {
                    // The XSLET opening letters. They name the server in plain ASCII, which is what
                    // confirms the class rather than the length arithmetic.
                    letterFrames++;
                    continue;
                }

                if (flags2 == TransferMessageLength)
                {
                    // An acknowledgement: full addressing header, then a six-byte all-zero body.
                    transferAcks++;
                    Assert.Equal(BodyOffsetFullHeader + 6, info.Length);
                    for (int k = BodyOffsetFullHeader; k < info.Length; k++)
                    {
                        Assert.Equal(0, info[k]);
                    }
                }
            }

            _output.WriteLine("XSLET letter frames (class 0x0080): " + letterFrames);
            _output.WriteLine("transfer data messages: " + dataMessages);
            _output.WriteLine("transfer acknowledgements: " + transferAcks);

            Assert.Equal(2, letterFrames);
            Assert.Equal(dataMessages, transferAcks);
        }

        /// <summary>
        /// Reassembles the segmented transfer messages and reads the transfer message header out of
        /// the result.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Once the two fragments are joined, the body should be exactly the shape the file-transfer
        /// program's own <c>LIST-VARIABLES</c> implies it tracks - a function, a page number and a
        /// displacement - followed by one block of file content:
        /// </para>
        /// <code>
        /// 0042  page  displacement in words  1024 bytes of file data
        /// </code>
        /// <para>
        /// 6 + 1024 = 1030, which is the length Flags2 announces. The displacement advancing
        /// 0 -> 0x0200 (512 words = 1024 bytes) and then resetting as the page advances is what
        /// establishes that the displacement counts WORDS and that a page holds two blocks.
        /// </para>
        /// </remarks>
        [Fact]
        public void ReassembledTransferMessage_IsFunctionPageDisplacementAndOneBlock()
        {
            string? path = PcapFiles.File(CaptureName);
            if (path == null)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            IReadOnlyList<LapbFrame> frames = HdlcPcap.ReadFrames(path);

            // Join each 0x0A/0x0C pair, matching them on the Flags1 datagram sequence.
            Dictionary<int, byte[]> firstBySequence = new Dictionary<int, byte[]>();
            List<byte[]> messages = new List<byte[]>();

            for (int i = 0; i < frames.Count; i++)
            {
                LapbFrame frame = frames[i];
                if (frame.Kind != LapbFrameKind.Information || frame.Info.Length < SintranHeaderSize)
                {
                    continue;
                }

                ReadOnlySpan<byte> info = frame.Info.Span;
                int sequence = (info[8] << 8) | info[9];

                if (info[3] == SubtypeFirstFragment)
                {
                    firstBySequence[sequence] = info.Slice(BodyOffsetFullHeader).ToArray();
                }
                else if (info[3] == SubtypeContinuation)
                {
                    byte[]? head;
                    if (!firstBySequence.TryGetValue(sequence, out head)) { continue; }

                    ReadOnlySpan<byte> tail = info.Slice(BodyOffsetContinuation);
                    byte[] joined = new byte[head!.Length + tail.Length];
                    Array.Copy(head, 0, joined, 0, head.Length);
                    tail.CopyTo(new Span<byte>(joined, head.Length, tail.Length));
                    messages.Add(joined);
                    firstBySequence.Remove(sequence);
                }
            }

            Assert.True(messages.Count > 0, "No segmented messages were reassembled.");

            for (int i = 0; i < messages.Count; i++)
            {
                byte[] message = messages[i];
                int function = (message[0] << 8) | message[1];
                int page = (message[2] << 8) | message[3];
                int displacement = (message[4] << 8) | message[5];

                _output.WriteLine(
                    "message " + i
                    + ": length=" + message.Length
                    + " function=0x" + function.ToString("x4")
                    + " page=" + page
                    + " displacementWords=" + displacement
                    + " dataBytes=" + (message.Length - 6));

                Assert.Equal(1030, message.Length);
                Assert.Equal(0x0042, function);

                // The displacement counts words, and a block is 1024 bytes = 512 words, so it
                // alternates 0 / 512 as the page fills.
                Assert.True(
                    displacement == 0 || displacement == 512,
                    "Unexpected displacement " + displacement + " - the two-blocks-per-page model is wrong.");
            }
        }

        /// <summary>
        /// Settles whether the file transfer is stop-and-wait or pipelined at the application layer,
        /// using a capture-ordered view of both directions.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="TransferClass_FlagsTwoIsTheDeclaredSizeNotTheBodyLength"/> establishes a 1:1
        /// COUNT of acknowledgements to data messages, which on its own says nothing about ordering.
        /// This test walks the frames in the order they completed on the wire and tracks how many
        /// transfer messages are outstanding - sent, not yet acknowledged - at any moment.
        /// </para>
        /// <para>
        /// A peak of 1 means the sender waited for each acknowledgement before starting the next
        /// message: stop-and-wait. A peak above 1 means it kept several in flight: pipelined.
        /// Whichever it is, the number is reported, so the conclusion is readable rather than
        /// inferred.
        /// </para>
        /// </remarks>
        [Fact]
        public void TransferMessages_OutstandingCountShowsStopAndWaitOrPipelining()
        {
            string? path = PcapFiles.File(CaptureName);
            if (path == null)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            IReadOnlyList<LapbFrame> frames = HdlcPcap.ReadFramesInCaptureOrder(path);

            int outstanding = 0;
            int peakOutstanding = 0;
            int sent = 0;
            int acknowledged = 0;

            for (int i = 0; i < frames.Count; i++)
            {
                LapbFrame frame = frames[i];
                if (frame.Kind != LapbFrameKind.Information || frame.Info.Length < SintranHeaderSize)
                {
                    continue;
                }

                ReadOnlySpan<byte> info = frame.Info.Span;
                int flags2 = (info[10] << 8) | info[11];

                // A message is "sent" when its FIRST fragment appears; the continuation is part of
                // the same message and must not be counted twice.
                if (info[3] == SubtypeFirstFragment)
                {
                    sent++;
                    outstanding++;
                    if (outstanding > peakOutstanding) { peakOutstanding = outstanding; }
                    _output.WriteLine("  sent message " + sent + " (outstanding now " + outstanding + ")");
                    continue;
                }

                // The acknowledgement: a data frame carrying the transfer XMCSM with a six-byte body.
                if (info[3] == (byte)SintranPacketSubtype.Data
                    && flags2 == TransferMessageLength
                    && info.Length == BodyOffsetFullHeader + 6)
                {
                    acknowledged++;
                    outstanding--;
                    _output.WriteLine("  acknowledged " + acknowledged + " (outstanding now " + outstanding + ")");
                }
            }

            _output.WriteLine("messages sent: " + sent + ", acknowledged: " + acknowledged);
            _output.WriteLine("PEAK OUTSTANDING: " + peakOutstanding);
            _output.WriteLine(peakOutstanding <= 1
                ? "=> stop-and-wait: the sender waited for each acknowledgement."
                : "=> pipelined: up to " + peakOutstanding + " messages were in flight at once.");

            Assert.Equal(sent, acknowledged);
            Assert.True(sent > 0, "No transfer messages were found.");
        }

        /// <summary>
        /// Guards the capture-ordered reader: it must return exactly the same frames as the
        /// flow-grouped reader, only in a different order.
        /// </summary>
        /// <remarks>
        /// Without this, a bug in the offset-to-segment mapping could silently drop or duplicate
        /// frames and the ordering conclusion above would be drawn from an incomplete stream.
        /// </remarks>
        [Fact]
        public void CaptureOrderedReader_ReturnsTheSameFramesAsTheFlowGroupedReader()
        {
            string? path = PcapFiles.File(CaptureName);
            if (path == null)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            IReadOnlyList<LapbFrame> byFlow = HdlcPcap.ReadFrames(path);
            IReadOnlyList<LapbFrame> byCapture = HdlcPcap.ReadFramesInCaptureOrder(path);

            Assert.Equal(byFlow.Count, byCapture.Count);

            // Compare as multisets of frame bytes, so ordering differences are allowed but content
            // differences are not.
            Dictionary<string, int> tally = new Dictionary<string, int>();
            for (int i = 0; i < byFlow.Count; i++)
            {
                string key = Hex(byFlow[i].Info.Span, int.MaxValue);
                tally[key] = tally.ContainsKey(key) ? tally[key] + 1 : 1;
            }

            for (int i = 0; i < byCapture.Count; i++)
            {
                string key = Hex(byCapture[i].Info.Span, int.MaxValue);
                Assert.True(tally.ContainsKey(key) && tally[key] > 0, "Capture-ordered reader produced a frame the flow-grouped reader did not.");
                tally[key] = tally[key] - 1;
            }

            _output.WriteLine("both readers returned " + byFlow.Count + " identical frames");
        }

        /// <summary>
        /// Dumps every ordinary data-frame body in the transfer capture, in capture order.
        /// </summary>
        /// <remarks>
        /// Reporting only. It exists to confirm the control messages that bracket the four data
        /// messages - in particular the end-of-transfer message - from bytes rather than from notes.
        /// </remarks>
        [Fact]
        public void TransferCapture_DumpControlMessageBodies()
        {
            string? path = PcapFiles.File(CaptureName);
            if (path == null)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            IReadOnlyList<CapturedFrame> frames = HdlcPcap.ReadCapturedFramesInCaptureOrder(path);

            for (int i = 0; i < frames.Count; i++)
            {
                LapbFrame frame = frames[i].Frame;
                if (frame.Kind != LapbFrameKind.Information || frame.Info.Length < BodyOffsetFullHeader)
                {
                    continue;
                }

                ReadOnlySpan<byte> info = frame.Info.Span;
                if (info[3] != (byte)SintranPacketSubtype.Data)
                {
                    continue;
                }

                string dir = frames[i].Key.SourcePort.ToString() + "->" + frames[i].Key.DestinationPort.ToString();
                ReadOnlySpan<byte> body = info.Slice(BodyOffsetFullHeader);

                _output.WriteLine(dir + "  bodyLen=" + body.Length + "  body: " + Hex(body, 64));
            }

            Assert.True(frames.Count > 0);
        }

        /// <summary>
        /// Formats the head of a span as spaced hex, for diagnostic output.
        /// </summary>
        /// <param name="data">
        /// The bytes to format.
        /// </param>
        /// <param name="maximum">
        /// How many bytes at most to show.
        /// </param>
        private static string Hex(ReadOnlySpan<byte> data, int maximum)
        {
            int count = data.Length < maximum ? data.Length : maximum;
            System.Text.StringBuilder text = new System.Text.StringBuilder(count * 3);
            for (int i = 0; i < count; i++)
            {
                text.Append(data[i].ToString("x2"));
                if ((i % 2) == 1) { text.Append(' '); }
            }

            return text.ToString();
        }

    }
}
