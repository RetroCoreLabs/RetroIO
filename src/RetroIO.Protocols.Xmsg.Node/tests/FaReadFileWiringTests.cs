using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using RetroIO.Protocols.Xmsg.Ndfs;
using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Fa;
using RetroIO.Protocols.Xmsg.Qform;
using RetroIO.Protocols.Xmsg.Sintran;
using RetroIO.Protocols.Xmsg.Servers.Fa;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Covers serving a file's CONTENTS: the read request, the reply that carries none, and the two
    /// data messages that follow it - each one split across a fragment pair because 1032 bytes do not
    /// fit in one frame.
    /// </summary>
    /// <remarks>
    /// <para><b>Where the shape comes from</b></para>
    /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-read.txt</c>, in which a real machine reads a
    /// 17905-byte file in nine steps. The fragment pair is visible there as
    /// <c>2113 000A ... 0408</c> (total 1032) followed by <c>2113 000C ... 0252</c> (resumes at 594).
    /// <para><b>Measured over the WHOLE capture, 2026-08-05</b></para>
    /// Not one example - every fragment frame in the file was tabulated, and all four properties
    /// asserted below hold without exception:
    ///  - 36 first fragments and 36 continuations, and all 36 pairs share one Flags 1.
    ///  - Every first fragment declares Flags 2 = 1032, the TOTAL message length.
    ///  - Every continuation declares Flags 2 = 594, the offset it resumes at.
    ///  - Within one delivery the two messages take consecutive Flags 1 (<c>0204</c> then
    ///    <c>0205</c>), and the next delivery opens three higher (<c>0208</c>) because the reply and
    ///    its acknowledgement take numbers of their own in between.
    /// <para><b>What is NOT claimed</b></para>
    /// No real client has read a file from us. These tests prove we build the shape the capture
    /// shows; they cannot prove a client accepts it. The listing is the only path a real machine has
    /// ever accepted, and it is pinned separately in <see cref="FaListingRegressionTests"/>.
    /// </remarks>
    public sealed class FaReadFileWiringTests : IDisposable
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// The temporary folder the server serves during a test.
        /// </summary>
        private readonly string _folder;

        /// <summary>
        /// The file read by these tests.
        /// </summary>
        private const string ReadFileName = "PATCH.SYMB";

        /// <summary>
        /// Where a file-access message body starts inside a serialised datagram.
        /// </summary>
        private const int BodyOffset = SintranHeader.Size + XmsgSubHeader.Size;

        /// <summary>
        /// Creates the temporary folder for one test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public FaReadFileWiringTests(ITestOutputHelper output)
        {
            _output = output;
            _folder = Path.Combine(Path.GetTempPath(), "fa-read-wiring-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        /// <summary>
        /// Removes the temporary folder.
        /// </summary>
        public void Dispose()
        {
            try
            {
                Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not worth failing a test over.
            }
        }

        /// <summary>
        /// A read produces the acknowledgement, the empty reply, and then two data messages - each
        /// one a fragment pair, so six frames in all.
        /// </summary>
        [Fact]
        public void AReadProducesTheReplyAndTwoFragmentedDataMessages()
        {
            byte[] content = MakeContent(FaFileDataCodec.ReadLength);
            File.WriteAllBytes(Path.Combine(_folder, ReadFileName), content);

            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());
            OpenTheFile(host, sequence: 1, flags1: 0x0002);

            IReadOnlyList<XmsgFrame> frames = host.Route(FaTestClient.BuildSessionFrame(
                BuildReadRequest(sequence: 2, position: 0), 0x0004));

            for (int i = 0; i < frames.Count; i++)
            {
                XmsgFrame frame = frames[i];
                _output.WriteLine(
                    "frame " + i + ": subtype 0x" + ((byte)frame.Header.Subtype).ToString("X2")
                    + " flags1 0x" + frame.Header.Flags1.ToString("X4")
                    + " flags2 " + frame.Header.Flags2
                    + " length " + frame.ToArray().Length);
            }

            // acknowledgement, reply, then two data messages of two frames each.
            Assert.Equal(6, frames.Count);

            Assert.Equal(SintranPacketSubtype.Data, frames[0].Header.Subtype);
            Assert.Equal(SintranPacketSubtype.Data, frames[1].Header.Subtype);

            // The reply carries the echo and nothing else - no file content at all.
            byte[] replyBody = BodyOf(frames[1]);
            FaOperation operation;
            ushort sequence;
            Assert.True(FaExchangeCodec.TryReadOperation(replyBody, out operation, out sequence));
            Assert.Equal(FaOperation.ReadFile, operation);
            Assert.Equal(2, sequence);
            Assert.True(
                replyBody.Length <= FaExchangeCodec.QformOffset + 6 + 3 + 1,
                "the reply to a read must carry no data - it was " + replyBody.Length + " bytes");

            AssertFragmentPair(frames[2], frames[3]);
            AssertFragmentPair(frames[4], frames[5]);
        }

        /// <summary>
        /// The two fragments of one data message reassemble into exactly the bytes of the file.
        /// </summary>
        /// <remarks>
        /// This is the test that would catch an off-by-one in the split: rebuilding the message from
        /// its two frames and finding the file's own bytes at offset 8 proves the head, the tail and
        /// the resume offset all agree.
        /// </remarks>
        [Fact]
        public void TheFragmentsReassembleIntoTheFileContent()
        {
            byte[] content = MakeContent(FaFileDataCodec.ReadLength);
            File.WriteAllBytes(Path.Combine(_folder, ReadFileName), content);

            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());
            OpenTheFile(host, sequence: 1, flags1: 0x0002);

            IReadOnlyList<XmsgFrame> frames = host.Route(FaTestClient.BuildSessionFrame(
                BuildReadRequest(sequence: 2, position: 0), 0x0004));

            for (int block = 0; block < FaFileDataCodec.BlocksPerRead; block++)
            {
                byte[] message = Reassemble(frames[2 + (block * 2)], frames[3 + (block * 2)]);
                Assert.Equal(FaFileDataCodec.DataMessageLength, message.Length);

                // The eight-byte file-access prefix, then raw file bytes to the end.
                Assert.Equal(
                    (ushort)FaMessageType.Request,
                    (ushort)((message[0] << 8) | message[1]));
                // The word the connect letter echoed, NOT the 0x0002 constant this used to assert.
                // The fixture's letter deliberately carries 0x0004, so this assertion could have
                // caught the constant in FaServer's data-message path - it named the wrong one
                // instead, and the read path kept the constant after replies, short acknowledgements
                // and the close were fixed. See FaServerConversation.ResponderConversation.
                Assert.Equal(
                    FaTestClient.LetterEchoWord,
                    (ushort)((message[2] << 8) | message[3]));

                for (int i = 0; i < FaFileDataCodec.BlockLength; i++)
                {
                    Assert.Equal(
                        content[(block * FaFileDataCodec.BlockLength) + i],
                        message[FaExchangeCodec.QformOffset + i]);
                }
            }
        }

        /// <summary>
        /// The two data messages of one delivery carry the captured counter and token rule: the
        /// second is the first plus one with bit 7 set, and its token is replaced by 0x0001.
        /// </summary>
        [Fact]
        public void TheDeliveryFollowsTheCapturedCounterAndTokenRule()
        {
            File.WriteAllBytes(Path.Combine(_folder, ReadFileName), MakeContent(FaFileDataCodec.ReadLength));

            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());
            OpenTheFile(host, sequence: 1, flags1: 0x0002);

            IReadOnlyList<XmsgFrame> frames = host.Route(FaTestClient.BuildSessionFrame(
                BuildReadRequest(sequence: 2, position: 0), 0x0004));

            byte[] first = Reassemble(frames[2], frames[3]);
            byte[] last = Reassemble(frames[4], frames[5]);

            byte firstCounter = first[FaExchangeCodec.SessionHeaderOffset];
            byte lastCounter = last[FaExchangeCodec.SessionHeaderOffset];

            _output.WriteLine("counters: 0x" + firstCounter.ToString("X2") + " then 0x" + lastCounter.ToString("X2"));

            // The first of a pair never carries the end flag, the last always does...
            Assert.Equal(0, firstCounter & FaFileDataCodec.LastDataMessageFlag);
            Assert.Equal(
                FaFileDataCodec.LastDataMessageFlag,
                lastCounter & FaFileDataCodec.LastDataMessageFlag);

            // ...and the last is simply the first plus one underneath that flag.
            Assert.Equal(firstCounter + 1, lastCounter & ~FaFileDataCodec.LastDataMessageFlag);

            // The token is our own on the first message and 0x0001 on the last.
            Assert.Equal(
                FaExchangeCodec.SessionTokenResponder,
                (ushort)((first[FaExchangeCodec.SessionHeaderOffset + 2] << 8)
                    | first[FaExchangeCodec.SessionHeaderOffset + 3]));
            Assert.Equal(
                FaFileDataCodec.LastDataMessageToken,
                (ushort)((last[FaExchangeCodec.SessionHeaderOffset + 2] << 8)
                    | last[FaExchangeCodec.SessionHeaderOffset + 3]));
        }

        /// <summary>
        /// Reading the tail of a file still delivers a whole read, zero padded.
        /// </summary>
        /// <remarks>
        /// The capture's last read returns a full 2048 bytes with only 1521 left in the file. There
        /// is no short block and no end marker; the client stops on the size it learned at open. A
        /// server that short counted would be inventing a signal the protocol does not have.
        /// </remarks>
        [Fact]
        public void AShortTailIsStillAWholeReadZeroPadded()
        {
            const int ShortLength = 10;
            byte[] content = MakeContent(ShortLength);
            File.WriteAllBytes(Path.Combine(_folder, ReadFileName), content);

            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());
            OpenTheFile(host, sequence: 1, flags1: 0x0002);

            IReadOnlyList<XmsgFrame> frames = host.Route(FaTestClient.BuildSessionFrame(
                BuildReadRequest(sequence: 2, position: 0), 0x0004));

            Assert.Equal(6, frames.Count);

            byte[] message = Reassemble(frames[2], frames[3]);
            Assert.Equal(FaFileDataCodec.DataMessageLength, message.Length);

            for (int i = 0; i < ShortLength; i++)
            {
                Assert.Equal(content[i], message[FaExchangeCodec.QformOffset + i]);
            }

            for (int i = ShortLength; i < FaFileDataCodec.BlockLength; i++)
            {
                Assert.Equal(0, message[FaExchangeCodec.QformOffset + i]);
            }
        }

        /// <summary>
        /// The length an open reports is the OPENED file's, even when a shorter name in the same
        /// folder is a substring of the specification.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this is a read test and not an open test</b></para>
        /// The read protocol has no end marker - the last read returns a full block whatever is left
        /// - so the length reported at open is the only thing telling a client when to stop. A wrong
        /// one here means a client reads the right bytes and then keeps going, or stops early.
        /// <para><b>The defect</b></para>
        /// The length was found by searching for each file's name INSIDE the specification and
        /// taking the first hit. With <c>A.SYMB</c> in the folder, an open of <c>PATCH:SYMB</c>
        /// reported A's five bytes, because <c>A</c> occurs in <c>PATCH</c>.
        /// </remarks>
        [Fact]
        public void TheReportedLengthBelongsToTheOpenedFileNotAShorterNeighbour()
        {
            byte[] content = MakeContent(FaFileDataCodec.ReadLength);
            File.WriteAllBytes(Path.Combine(_folder, ReadFileName), content);

            // "A" is a substring of "PATCH", and sorts first.
            File.WriteAllText(Path.Combine(_folder, "A.SYMB"), "short");

            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());

            IReadOnlyList<XmsgFrame> frames = host.Route(FaTestClient.BuildSessionFrame(
                BuildOpenRequest(sequence: 1), 0x0002));

            byte[] body = BodyOf(frames[1]);
            _output.WriteLine("open reply: " + Convert.ToHexString(body));

            // The length rides under selector 3 as a 32-bit typed integer.
            int at = IndexOfSelector(body, FaOpenFileCodec.SecondarySelector);
            Assert.True(at >= 0, "the open reply carries no length");

            uint reported = (uint)((body[at + 4] << 24) | (body[at + 5] << 16)
                | (body[at + 6] << 8) | body[at + 7]);

            _output.WriteLine("reported length = " + reported);
            Assert.Equal((uint)FaFileDataCodec.ReadLength, reported);
        }

        /// <summary>
        /// Finds a selector in a reply body and returns its offset.
        /// </summary>
        /// <param name="body">
        /// The reply body.
        /// </param>
        /// <param name="selector">
        /// The selector to find.
        /// </param>
        /// <returns>
        /// The offset of the selector's tag byte, or -1.
        /// </returns>
        private static int IndexOfSelector(byte[] body, ushort selector)
        {
            for (int i = 0; i + 3 < body.Length; i++)
            {
                if (body[i] == (byte)QformTagByte.Selector
                    && body[i + 1] == (byte)(selector >> 8)
                    && body[i + 2] == (byte)selector)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// A read with no file open is REFUSED rather than ignored.
        /// </summary>
        /// <remarks>
        /// The rule that governs every path in this server: a request always gets an answer. A read
        /// on a conversation that never opened anything is the case our own bookkeeping would hit
        /// after a restart.
        /// </remarks>
        [Fact]
        public void AReadWithNothingOpenIsRefusedRatherThanIgnored()
        {
            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());

            IReadOnlyList<XmsgFrame> frames = host.Route(FaTestClient.BuildSessionFrame(
                BuildReadRequest(sequence: 2, position: 0), 0x0002));

            // The acknowledgement and the refusal, and no data messages at all.
            Assert.Equal(2, frames.Count);

            byte[] body = BodyOf(frames[1]);
            int at = FaExchangeCodec.QformOffset + 6;
            Assert.Equal((byte)QformTagByte.Selector, body[at]);
            Assert.Equal(0x01, body[at + 2]);
            Assert.Equal((byte)QformTagByte.TypedInteger, body[at + 3]);
            Assert.Equal(
                (ushort)FaServerStatus.NoSuchFile,
                (ushort)((body[at + 4] << 8) | body[at + 5]));
        }

        /// <summary>
        /// Requires a fragment pair to carry the shape the capture shows.
        /// </summary>
        /// <param name="first">
        /// The first fragment.
        /// </param>
        /// <param name="continuation">
        /// Its continuation.
        /// </param>
        private static void AssertFragmentPair(XmsgFrame first, XmsgFrame continuation)
        {
            Assert.Equal(SintranPacketSubtype.MessageFirstFragment, first.Header.Subtype);
            Assert.Equal(SintranPacketSubtype.MessageContinuation, continuation.Header.Subtype);

            // The pair is tied together by a shared Flags 1 and by nothing else. True of all 36
            // captured pairs, not just the one quoted in the class remarks.
            Assert.Equal(first.Header.Flags1, continuation.Header.Flags1);

            // The first declares the TOTAL message length; the continuation the offset it resumes at.
            Assert.Equal(FaFileDataCodec.DataMessageLength, first.Header.Flags2);
            Assert.Equal(SintranMessageFragment.FirstFragmentBodyLength, continuation.Header.Flags2);

            // Only the first carries addressing, so the bodies start at different offsets.
            Assert.NotNull(first.SubHeader);
            Assert.Null(continuation.SubHeader);

            int firstPayload = first.ToArray().Length - SintranMessageFragment.FirstFragmentBodyOffset;
            int contPayload = continuation.ToArray().Length - SintranMessageFragment.ContinuationBodyOffset;

            Assert.Equal(SintranMessageFragment.FirstFragmentBodyLength, firstPayload);
            Assert.Equal(FaFileDataCodec.DataMessageLength, continuation.Header.Flags2 + contPayload);
        }

        /// <summary>
        /// Joins a fragment pair back into the message it carries.
        /// </summary>
        /// <param name="first">
        /// The first fragment.
        /// </param>
        /// <param name="continuation">
        /// Its continuation.
        /// </param>
        /// <returns>
        /// The whole message body.
        /// </returns>
        private static byte[] Reassemble(XmsgFrame first, XmsgFrame continuation)
        {
            byte[] head = first.ToArray();
            byte[] tail = continuation.ToArray();

            int headLength = head.Length - SintranMessageFragment.FirstFragmentBodyOffset;
            int tailLength = tail.Length - SintranMessageFragment.ContinuationBodyOffset;

            byte[] message = new byte[headLength + tailLength];
            for (int i = 0; i < headLength; i++)
            {
                message[i] = head[SintranMessageFragment.FirstFragmentBodyOffset + i];
            }

            for (int i = 0; i < tailLength; i++)
            {
                message[headLength + i] = tail[SintranMessageFragment.ContinuationBodyOffset + i];
            }

            return message;
        }

        /// <summary>
        /// Builds the host with the file server registered over the test folder.
        /// </summary>
        /// <returns>
        /// The host, which is also the transport the server replies through.
        /// </returns>
        private XmsgServerHost BuildHost()
        {
            FaServer server = new FaServer(new FolderFileStore(_folder));
            server.Log += line => _output.WriteLine(line);

            XmsgServerHost host = new XmsgServerHost(FaTestClient.ServerNode);
            host.Register(server);
            return host;
        }

        /// <summary>
        /// Opens the test file on the conversation.
        /// </summary>
        /// <param name="host">
        /// The host to route through.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence.
        /// </param>
        /// <param name="flags1">
        /// The datagram sequence of the request.
        /// </param>
        /// <remarks>
        /// The request is written out here because there is no request builder to call: the server
        /// is the READING end of an open, so only a test ever needs to produce one. The name string
        /// declares <c>strlen + 2</c> - the characters, the SINTRAN terminator, then one more byte
        /// whose meaning is UNKNOWN and which the reader discards.
        /// </remarks>
        private static void OpenTheFile(XmsgServerHost host, ushort sequence, ushort flags1)
        {
            host.Route(FaTestClient.BuildSessionFrame(BuildOpenRequest(sequence), flags1));
        }

        /// <summary>
        /// Builds the request that opens the test file for reading.
        /// </summary>
        /// <param name="sequence">
        /// The exchange sequence.
        /// </param>
        /// <returns>
        /// The message body.
        /// </returns>
        private static byte[] BuildOpenRequest(ushort sequence)
        {
            int nameFieldLength = ReadFileName.Length + 2;
            byte[] nameField = new byte[nameFieldLength];
            SintranName.Write(nameField, ReadFileName);

            byte[] fields = new byte[3 + 2 + nameFieldLength + 3];
            QformWriter writer = new QformWriter(fields);

            writer.WriteSelector(FaOpenFileCodec.PrimarySelector);
            writer.WriteByteString(nameField);
            writer.WriteEndOfList();
            writer.EnsureComplete("Open request");

            return FaTestClient.BuildRequestEnvelope(FaOperation.OpenFile, sequence, fields);
        }

        /// <summary>
        /// Builds a read request for one block-pair at a position.
        /// </summary>
        /// <param name="sequence">
        /// The exchange sequence.
        /// </param>
        /// <param name="position">
        /// The position, counted in units of <c>FaFileDataCodec.ReadLength</c>.
        /// </param>
        /// <returns>
        /// The message body.
        /// </returns>
        /// <remarks>
        /// The captured request is <c>92 0008 92 seq F2 0001 A4 position F2 00FF</c>. It is
        /// written out here rather than built by a codec because there is no request builder - the
        /// server is the reading end of this exchange, so only the test ever needs one.
        /// </remarks>
        private static byte[] BuildReadRequest(ushort sequence, uint position)
        {
            // F2 0001 (3) + A4 and four position bytes (5) + F2 00FF (3).
            byte[] fields = new byte[3 + 5 + 3];
            QformWriter writer = new QformWriter(fields);

            writer.WriteSelector(FaFileDataCodec.PositionSelector);
            writer.WriteTypedInteger32(position);
            writer.WriteEndOfList();
            writer.EnsureComplete("Read request");

            return FaTestClient.BuildRequestEnvelope(FaOperation.ReadFile, sequence, fields);
        }

        /// <summary>
        /// Makes file content whose every byte is distinguishable from its neighbours.
        /// </summary>
        /// <param name="length">
        /// How many bytes to make.
        /// </param>
        /// <returns>
        /// The content.
        /// </returns>
        /// <remarks>
        /// A repeating pattern would let an off-by-one in the fragment split pass unnoticed, so the
        /// bytes step through the whole range instead.
        /// </remarks>
        private static byte[] MakeContent(int length)
        {
            byte[] content = new byte[length];
            for (int i = 0; i < length; i++)
            {
                content[i] = (byte)((i * 7) + 1);
            }

            return content;
        }

        /// <summary>
        /// Extracts the file-access message body from a reply frame.
        /// </summary>
        /// <param name="frame">
        /// The reply.
        /// </param>
        /// <returns>
        /// The message body.
        /// </returns>
        private static byte[] BodyOf(XmsgFrame frame)
        {
            byte[] all = frame.ToArray();
            byte[] body = new byte[all.Length - BodyOffset];
            for (int i = 0; i < body.Length; i++)
            {
                body[i] = all[i + BodyOffset];
            }

            return body;
        }
    }
}
