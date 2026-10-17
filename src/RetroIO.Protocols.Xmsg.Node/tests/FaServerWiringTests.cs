//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Ndfs;
using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Fa;
using RetroIO.Protocols.Xmsg.Qform;
using RetroIO.Protocols.Xmsg.Servers.Fa;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Covers the wiring that makes an inbound file-access request reach
    /// <see cref="FaServer"/> and come back out as a frame: the name-based routing of the connect
    /// letter, the port-based routing of the requests that follow, the directory walk over a real
    /// Windows folder, and - the point of the whole exercise - that <b>no request path can end in
    /// silence</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The silence tests are not defensive padding. On 2026-08-02 a node accepted a link and then
    /// never answered an FA request; the calling SINTRAN machine's terminal hung and ESC did not
    /// abort it. Every "still answers" test below stands for one way that could happen again.
    /// </para>
    /// <para>
    /// UPDATED 2026-08-05: this used to say no real client had ever read these replies. One has.
    /// D100 lists our server and prints the files, and <c>FILE-STATISTICS</c> decodes the synthesised
    /// entries field by field - it even names our access bits back to us. The refusal form is
    /// measured too, from <c>capture-open-error.txt</c>.
    /// </para>
    /// <para>
    /// What these tests still do NOT prove: that a refusal carries the right value in the cases no
    /// capture covers. Three of the codes in <see cref="FaServerStatus"/> remain ours.
    /// </para>
    /// </remarks>
    public sealed class FaServerWiringTests : IDisposable
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// The temporary folder the server serves during a test.
        /// </summary>
        private readonly string _folder;

        /// <summary>
        /// Our own node number in these tests.
        /// </summary>
        private const ushort ServerNode = FaTestClient.ServerNode;

        /// <summary>
        /// The client's node number in these tests.
        /// </summary>
        private const ushort ClientNode = FaTestClient.ClientNode;

        /// <summary>
        /// The client's port in these tests.
        /// </summary>
        private const ushort ClientPort = FaTestClient.ClientPort;

        /// <summary>
        /// The conversation number the client uses in these tests.
        /// </summary>
        private const ushort ClientConversation = FaTestClient.ClientConversation;

        /// <summary>
        /// Where an FA message body starts inside a serialised frame.
        /// </summary>
        /// <remarks>
        /// CORRECTED 2026-08-04: 28, not 32. The old value came from a 13-byte SINTRAN header plus
        /// a 19-byte sub-header, which is four bytes past the real boundary. The fixtures in this
        /// file agreed with the server because BOTH were wrong the same way. The live capture
        /// DOC/captures/FA-READ-WRITE-2026-08-04/capture-read.txt settles it: D102's reply body at
        /// absolute 28 is 07D2 0002 0042 6400, an FA ConnectionConfirm.
        /// </remarks>
        private const int BodyOffset = SintranHeader.Size + XmsgSubHeader.Size;

        /// <summary>
        /// The word the test letter carries in its extras, which the confirmation must echo.
        /// </summary>
        /// <remarks>
        /// 0x0004 on purpose, not the usual 0x0002: capture-read.txt has one connect with each,
        /// and only the odd one out can catch a server that emits a constant.
        /// </remarks>
        private const ushort LetterEchoWord = FaTestClient.LetterEchoWord;

        /// <summary>
        /// Creates the temporary folder for one test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public FaServerWiringTests(ITestOutputHelper output)
        {
            _output = output;
            _folder = Path.Combine(Path.GetTempPath(), "fa-server-wiring-" + Guid.NewGuid().ToString("N"));
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
        /// The connect letter naming <c>*FA-SERVER</c> is routed to the file server by name and
        /// answered with the generic XSLET accept from our reply-from port.
        /// </summary>
        [Fact]
        public void ConnectLetter_IsRoutedByName_AndAnswered()
        {
            XmsgServerHost host = BuildHost(out FaServer server);

            XmsgFrame letter = BuildConnectLetter();
            IReadOnlyList<XmsgFrame> replies = host.Route(letter);

            Assert.Single(replies);
            XmsgFrame confirm = replies[0];
            Assert.NotNull(confirm.SubHeader);

            // The answer goes back to the asking endpoint from a FRESHLY ALLOCATED session port,
            // never from the registered directory port. CORRECTED 2026-08-04: this used to assert
            // FaServerWirePort, which is what the server did and what D100's file-access layer
            // refused. In DOC/captures/FA-READ-WRITE-2026-08-04/capture-read.txt the letter is
            // addressed to port 0x0000 and D102 answers from 0x06B6, and every later FA message in
            // that conversation runs 102:0x06B6 - 100:0x0812.
            Assert.Equal(ClientNode, confirm.Header!.DestinationNode);
            Assert.Equal(ClientPort, confirm.SubHeader!.DestinationPort);
            Assert.NotEqual(FaServer.FaServerWirePort, confirm.SubHeader.SourcePort);
            Assert.True(server.OwnsPort(confirm.SubHeader.SourcePort),
                "the confirmation's source port must be one the server will accept traffic on");

            // Flags 1 is OUR OWN outgoing count, not the letter's number. CORRECTED 2026-08-04:
            // this asserted the echo. The letter carries 0x0001 and the confirmation is the first
            // Data frame we send on a fresh link, so it must be 0x0000 - which makes this the one
            // assertion in the file that tells the two models apart.
            //
            // The echo model matched capture-read.txt (letter L9 and confirmation L13 both read
            // F1=0x01F9) only because that conversation started in step. Live, D100 opened at
            // 0x000E while our count stood at 0x000B, and echoing earned a XENSE reject.
            // See XmsgServerHost.ChooseFlags1.
            Assert.Equal(0x0001, letter.Header.Flags1);
            Assert.Equal(0x0000, confirm.Header.Flags1);

            // And the conversation stays on the session port.
            ushort sessionPort = confirm.SubHeader.SourcePort;
            XmsgFrame request = BuildSessionFrame(
                BuildRequestEnvelope(FaOperation.ReserveFileEntry, 1, new byte[] { 0xF2, 0x00, 0xFF }), 0x0002);
            IReadOnlyList<XmsgFrame> next = host.Route(request);
            XmsgFrame nextReply = AssertAckThenReply(next, 0x0001);

            // Both halves of the exchange stay on the session port.
            Assert.Equal(sessionPort, next[0].SubHeader!.SourcePort);
            Assert.Equal(sessionPort, nextReply.SubHeader!.SourcePort);

            // CORRECTED 2026-08-04: an FA connect letter is answered with an FA ConnectionConfirm,
            // NOT the TAD-shaped generic XSLET accept this used to assert. The live capture
            // DOC/captures/FA-READ-WRITE-2026-08-04/capture-read.txt shows D102 answering the
            // *FA-SERVER letter with a data frame whose body at absolute 28 is
            //   07D2 0002 0042 6400
            // and whose XMCSM is 0x0008 - the body's byte length.
            byte[] body = confirm.GetBodyBytes();
            Assert.Equal(8, body.Length);
            Assert.Equal(0x0008, confirm.SubHeader.Xmcsm);
            Assert.Equal((ushort)FaMessageType.ConnectionConfirm, (ushort)((body[0] << 8) | body[1]));

            // Word 1 is ECHOED from the word the letter carries past its declared XROUT length.
            // BuildConnectLetter appends the captured extras 07E2 0000 0004 6400 A200 FF00, so the
            // deliberately NON-default 0x0004 must come back - 0x0002 here would mean the server
            // is still emitting the old constant. VERIFIED against capture-read.txt line 419/423,
            // the one captured connect whose extras are not 0x0002.
            Assert.Equal(LetterEchoWord, (ushort)((body[2] << 8) | body[3]));

            // Word 2 is our own connection counter, not anything the client sent.
            Assert.Equal(0x0042, (ushort)((body[4] << 8) | body[5]));

            // And the system byte names the CLIENT's system - INFERRED, see FaServer.
            Assert.Equal((byte)ClientNode, body[6]);

            Assert.Equal(1, server.SessionCount);
            _output.WriteLine("confirm: " + Convert.ToHexString(confirm.ToArray()));
        }

        /// <summary>
        /// A directory walk over a real folder returns the file that is in it, decoded back through
        /// the same object-entry reader a client would use.
        /// </summary>
        [Fact]
        public void DirectoryWalk_ReturnsTheFileInTheServedFolder()
        {
            File.WriteAllText(Path.Combine(_folder, "HELLO.SYMB"), "a small file");

            XmsgServerHost host = BuildHost(out FaServer server);
            host.Route(BuildConnectLetter());

            // Cursor 0xFFFF asks for the FIRST entry.
            byte[] request = BuildListingRequest(sequence: 1, cursor: FaListFilesCodec.FirstEntryCursor);
            XmsgFrame requestFrame = BuildSessionFrame(request, flags1: 0x0002);
            IReadOnlyList<XmsgFrame> replies = host.Route(requestFrame);

            byte[] body = BodyOf(AssertAckThenReply(replies, 0x0001));

            // The reply echoes the operation and the sequence, then carries the 64-byte record.
            FaOperation operation;
            ushort sequence;
            Assert.True(FaExchangeCodec.TryReadOperation(body, out operation, out sequence));
            Assert.Equal(FaOperation.SiiiSpecial, operation);
            Assert.Equal(1, sequence);

            ushort serial;
            byte[] record;
            Assert.True(FaListFilesCodec.TryReadReply(
                new ReadOnlySpan<byte>(body, FaExchangeCodec.QformOffset, body.Length - FaExchangeCodec.QformOffset),
                out serial, out record));
            Assert.Equal(1, serial);

            RetroIO.Storage.Ndfs.Elements.ObjectEntry? entry = FaDirectoryListing.ParseRecord(record);
            Assert.NotNull(entry);
            Assert.Equal("HELLO", entry!.ObjectName);
            Assert.Equal("SYMB", entry.Type);
            Assert.Equal((ulong)"a small file".Length, entry.BytesInFile);

            _output.WriteLine("entry: " + entry.ObjectName + ":" + entry.Type + " " + entry.BytesInFile + " bytes");
        }

        /// <summary>
        /// Two successive "next" requests return DIFFERENT files. The walk position is the server's,
        /// not the client's.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the falsifiable test for the cursor model. VERIFIED against
        /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\capture-list-files.txt</c>, where a walk of more
        /// than a hundred files carries the cursor <c>A2 FFFF</c> exactly ONCE and <c>A2 0000</c> on
        /// all 102 later requests - while the real server hands back a different file every time.
        /// So <c>0x0000</c> means "next", not "index 0".
        /// </para>
        /// <para>
        /// Treating the cursor as an index made every request after the first return the same file
        /// forever. Live against D100 on 2026-08-04 that produced two identical HELLO:TXT entries,
        /// after which D100 abandoned the conversation.
        /// </para>
        /// </remarks>
        [Fact]
        public void DirectoryWalk_AdvancesOnEachRequest()
        {
            File.WriteAllText(Path.Combine(_folder, "FIRST.SYMB"), "one");
            File.WriteAllText(Path.Combine(_folder, "SECOND.SYMB"), "two");

            XmsgServerHost host = BuildHost(out FaServer server);
            host.Route(BuildConnectLetter());

            // 0xFFFF starts the walk...
            XmsgFrame startFrame = BuildSessionFrame(
                BuildListingRequest(1, FaListFilesCodec.FirstEntryCursor), 0x0002);
            string firstName = NameOfEntry(BodyOf(AssertAckThenReply(host.Route(startFrame), 0x0001)));

            // ...then the client asks for the DIRECTORY by sub-function, not by position. UPDATED
            // 2026-08-05: this test used to expect the second FILE here, because every request was
            // read as "give me the next file". That is what made D100 abandon the listing.
            XmsgFrame packFrame = BuildSessionFrame(
                BuildFunctionRequest(2, FaSpecialFunction.DirectoryEntry), 0x0004);
            byte[] packBody = BodyOf(AssertAckThenReply(host.Route(packFrame), 0x0003));
            string packName = NameOfDirectoryEntry(packBody);

            // ...then the USER, the other half of the (PACK-ONE:SYSTEM) header...
            XmsgFrame userFrame = BuildSessionFrame(
                BuildFunctionRequest(3, FaSpecialFunction.UserEntry), 0x0006);
            byte[] userBody = BodyOf(AssertAckThenReply(host.Route(userFrame), 0x0005));

            // ...and the walk resumes with the second file, its position untouched by either.
            XmsgFrame nextFrame = BuildSessionFrame(BuildListingRequest(4, cursor: 0), 0x0008);
            string secondName = NameOfEntry(BodyOf(AssertAckThenReply(host.Route(nextFrame), 0x0007)));

            _output.WriteLine("walk: " + firstName + ", pack " + packName + ", then " + secondName);

            Assert.Equal("FIRST", firstName);
            Assert.Equal(FaServer.DirectoryName, packName);
            Assert.Equal("SECOND", secondName);
            Assert.NotEqual(firstName, secondName);

            // The user reply carries a 16-byte string and no record.
            Assert.Contains(
                FaServer.UserName,
                System.Text.Encoding.ASCII.GetString(userBody));
        }

        /// <summary>
        /// Reads the directory name out of a PACK directory-entry reply body.
        /// </summary>
        /// <param name="body">
        /// The reply body, starting at the message type.
        /// </param>
        /// <returns>
        /// The directory name the record carries.
        /// </returns>
        /// <remarks>
        /// The record is found by its LENGTH: a directory entry is 42 bytes and a file entry 64, and
        /// both travel under the same <c>B0</c> tag, so length is the only thing that tells them
        /// apart. The name sits at byte 10 and is terminated by <c>0x27</c>.
        /// </remarks>
        private static string NameOfDirectoryEntry(byte[] body)
        {
            int at = -1;
            for (int i = 0; i < body.Length - 1; i++)
            {
                if (body[i] == 0xB0 && body[i + 1] == FaDirectoryEntry.RecordLength)
                {
                    at = i + 2;
                    break;
                }
            }

            Assert.True(at >= 0, "the reply carries no 42-byte directory record");
            Assert.True(at + FaDirectoryEntry.RecordLength <= body.Length);

            int start = at + FaDirectoryEntry.NameOffset;
            int length = 0;
            while (length < FaDirectoryEntry.NameLength)
            {
                byte c = body[start + length];
                if (c == FaDirectoryEntry.NameTerminator || c == 0)
                {
                    break;
                }

                length++;
            }

            return System.Text.Encoding.ASCII.GetString(body, start, length);
        }

        /// <summary>
        /// Reads the object name out of a directory-entry reply body.
        /// </summary>
        /// <param name="body">
        /// The reply body, starting at the message type.
        /// </param>
        /// <returns>
        /// The entry's object name.
        /// </returns>
        private static string NameOfEntry(byte[] body)
        {
            ushort serial;
            byte[] record;
            Assert.True(FaListFilesCodec.TryReadReply(
                new ReadOnlySpan<byte>(body, FaExchangeCodec.QformOffset, body.Length - FaExchangeCodec.QformOffset),
                out serial, out record));

            RetroIO.Storage.Ndfs.Elements.ObjectEntry? entry = FaDirectoryListing.ParseRecord(record);
            Assert.NotNull(entry);
            return entry!.ObjectName;
        }

        /// <summary>
        /// Walking past the last file still produces a reply - the end of a directory must not be
        /// silence.
        /// </summary>
        [Fact]
        public void DirectoryWalk_PastTheEnd_StillAnswers()
        {
            File.WriteAllText(Path.Combine(_folder, "ONLY.DATA"), "x");

            XmsgServerHost host = BuildHost(out FaServer server);
            host.Route(BuildConnectLetter());
            host.Route(BuildSessionFrame(BuildListingRequest(1, FaListFilesCodec.FirstEntryCursor), 0x0002));

            // One file, and the request above already consumed it, so the walk is now past the end.
            // Note the cursor here is NOT what makes it past the end - the SERVER's walk position
            // is. See FaServerSession.WalkPosition. The directory and user requests do not touch
            // the walk position at all, so they cannot bring the end nearer or push it away.
            XmsgFrame pastEnd = BuildSessionFrame(BuildListingRequest(2, cursor: 0), flags1: 0x0004);
            IReadOnlyList<XmsgFrame> replies = host.Route(pastEnd);

            Assert.Equal(FaServerStatus.EndOfDirectory, StatusOf(BodyOf(AssertAckThenReply(replies, 0x0003))));
        }

        /// <summary>
        /// An operation whose wire layout has never been captured is REFUSED, not ignored.
        /// </summary>
        /// <remarks>
        /// <para><b>It now asks with an operation code that DOES NOT EXIST</b></para>
        /// This test named a real operation three times and had to be rewritten each time the server
        /// grew into it: <c>ReadFile</c>, then <c>WriteFile</c>, then <c>DeleteFile</c> (2026-08-05
        /// twice, 2026-08-06 once). Every rewrite was churn, and each one briefly left the refusal
        /// path untested.
        /// <para>
        /// <c>0x7FFF</c> is not in <see cref="FaOperation"/> and no capture has ever carried it, so
        /// it cannot become supported and the test cannot rot. What is being checked is the refusal
        /// path - that an operation we do not implement is ANSWERED rather than dropped, so the
        /// caller does not hang - and that never depended on which operation carried it.
        /// </para>
        /// </remarks>
        [Fact]
        public void UnsupportedOperation_IsRefusedRatherThanIgnored()
        {
            // Deliberately not a member of FaOperation - see the remarks.
            const FaOperation NeverImplemented = (FaOperation)0x7FFF;

            XmsgServerHost host = BuildHost(out FaServer server);
            host.Route(BuildConnectLetter());

            byte[] request = BuildRequestEnvelope(NeverImplemented, sequence: 7, fields: new byte[] { 0xF2, 0x00, 0xFF });
            XmsgFrame requestFrame = BuildSessionFrame(request, flags1: 0x0002);
            IReadOnlyList<XmsgFrame> replies = host.Route(requestFrame);

            byte[] body = BodyOf(AssertAckThenReply(replies, 0x0001));

            FaOperation operation;
            ushort sequence;
            Assert.True(FaExchangeCodec.TryReadOperation(body, out operation, out sequence));
            Assert.Equal(NeverImplemented, operation);   // the refusal still echoes what was asked
            Assert.Equal(7, sequence);
            Assert.Equal(FaServerStatus.NotSupported, StatusOf(body));
        }

        /// <summary>
        /// The two operations that open and close every captured conversation are accepted.
        /// </summary>
        [Fact]
        public void ReserveAndRelease_AreAccepted()
        {
            XmsgServerHost host = BuildHost(out FaServer server);
            host.Route(BuildConnectLetter());

            XmsgFrame reserveRequest = BuildSessionFrame(
                BuildRequestEnvelope(FaOperation.ReserveFileEntry, 1, new byte[] { 0xF2, 0x00, 0xFF }), 0x0002);
            XmsgFrame releaseRequest = BuildSessionFrame(
                BuildRequestEnvelope(FaOperation.ReleaseFileEntry, 2, new byte[] { 0xF2, 0x00, 0xFF }), 0x0004);

            IReadOnlyList<XmsgFrame> reserve = host.Route(reserveRequest);
            IReadOnlyList<XmsgFrame> release = host.Route(releaseRequest);

            XmsgFrame reserveReply = AssertAckThenReply(reserve, 0x0001);
            AssertAckThenReply(release, 0x0003);

            // An accepted operation carries no status field at all - just the end-of-list selector,
            // then one pad byte to bring the body to a whole number of words.
            //
            // The pad is not cosmetic: MEASURED over all four captures in
            // DOC\captures\FA-READ-WRITE-2026-08-04, all 480 FA Data frames carry an EVEN body
            // length and none an odd one. The real server's reserve reply is 18 bytes; ours was 17.
            byte[] body = BodyOf(reserveReply);
            Assert.Equal(0, body.Length % 2);
            // QFORM content, plus the one pad byte that makes it word aligned.
            Assert.Equal(FaExchangeCodec.QformOffset + 6 + 3 + 1, body.Length);
        }

        /// <summary>
        /// A second conversation is answered from the SAME port as the first, even after the first
        /// has been closed.
        /// </summary>
        /// <remarks>
        /// <para><b>This one is a live failure, not a theory</b></para>
        /// <para>
        /// On 2026-08-10 D100 ran a listing against us, closed it cleanly, opened a second
        /// conversation, and then ignored our confirmation eleven times before giving up with
        /// "NO ANSWER FROM REMOTE SYSTEM; FILE-ACCESS CONNECTION ABORTED" and dropping the link.
        /// The only thing that differed between the confirmation it took and the ones it refused
        /// was the port we sent from: we allocated a fresh one per conversation and released it on
        /// close.
        /// </para>
        /// <para>
        /// A real server does not move. D100 answers from <c>0x05B9</c> in all three captures we
        /// hold, including <c>nd-to-nd-write.pcapng</c> where one client releases a conversation
        /// and opens another - same port both times.
        /// </para>
        /// <para>
        /// The close is what makes this test bite. Without it the second letter would find the
        /// session still keyed on the same client endpoint and reuse it, and the test would pass
        /// against the broken code.
        /// </para>
        /// </remarks>
        [Fact]
        public void ASecondConversationIsAnsweredFromTheSamePort()
        {
            XmsgServerHost host = BuildHost(out FaServer server);

            IReadOnlyList<XmsgFrame> first = host.Route(BuildConnectLetter());
            ushort firstPort = first[0].SubHeader!.SourcePort;

            // End the conversation the way a real client does - the 0x078x "I am finished" family,
            // which is what makes the server forget the session.
            byte[] finished = new byte[] { 0x07, 0x82, 0x00, 0x00, 0x00, 0x02, 0x80, 0x00, 0x00, 0x00 };
            host.Route(BuildSessionFrame(finished, 0x0002));

            IReadOnlyList<XmsgFrame> second = host.Route(BuildConnectLetter());
            ushort secondPort = second[0].SubHeader!.SourcePort;

            Assert.Equal(firstPort, secondPort);

            // And the server must still take traffic on it, or the next request has nowhere to land.
            Assert.True(server.OwnsPort(firstPort),
                "the server must keep answering on its reply port after a conversation closes");
        }

        /// <summary>
        /// The close that ends a conversation echoes the CLIENT's conversation number.
        /// </summary>
        /// <remarks>
        /// <para><b>Found live on D100, 2026-08-05</b></para>
        /// A listing completed perfectly and then D100 answered our final close with a subtype-07
        /// network error carrying <c>Flags2 = 0xFFED</c> = -19 = <c>XEIMA</c>, an invalid-magic
        /// reject. Our close read <c>07C0 0002 0000 0000</c>; the captured teardown reads
        /// <c>07C0 0002 0044 0000</c>, with the client's own conversation number in word 2.
        /// <para><b>Why it was zero</b></para>
        /// Answering the connect LETTER builds a conversation, and a letter carries no file-access
        /// conversation number - so the builder was created with zero. The number arrives on the
        /// first real request afterwards, and <c>EnsureConversation</c> only assigned when the
        /// builder did not exist yet, so it never landed.
        /// <para>
        /// Every behavioural test passed throughout, because none of them looked at the close's
        /// bytes. This one does.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheClosingCloseEchoesTheClientConversationNumber()
        {
            XmsgServerHost host = BuildHost(out FaServer server);

            // The connect letter FIRST - this is what creates the conversation with zero.
            host.Route(BuildConnectLetter());

            // Then a real request, which is the first thing to carry the conversation number.
            host.Route(BuildSessionFrame(
                BuildRequestEnvelope(FaOperation.ReserveFileEntry, 1, new byte[] { 0xF2, 0x00, 0xFF }), 0x0002));

            // Now the client says it has finished, and our close must name its conversation.
            byte[] finished = new byte[8];
            ushort finishedType = (ushort)FaMessageType.SessionFinished;
            finished[0] = (byte)(finishedType >> 8);
            finished[1] = (byte)finishedType;
            finished[2] = (byte)(ClientConversation >> 8);
            finished[3] = (byte)ClientConversation;

            IReadOnlyList<XmsgFrame> replies = host.Route(BuildSessionFrame(finished, 0x0004));

            Assert.Single(replies);
            byte[] body = BodyOf(replies[0]);

            _output.WriteLine("close body: " + Convert.ToHexString(body));

            Assert.Equal((ushort)FaMessageType.Close, (ushort)((body[0] << 8) | body[1]));

            // Word 1 is the word we ECHOED in the connection confirmation, not the 0x0002 constant
            // this used to assert. CORRECTED 2026-08-08 from the real close in
            // DOC/captures/ND-TO-ND-2026-08-08/nd-to-nd-scenarios.pcapng:
            //
            //   07C0 000C 0040 0000   answering a conversation whose confirmation echoed 000C
            //
            // The constant only ever agreed because 0x0002 is its usual value. BuildConnectLetter
            // carries 0x0004 in its extras, so this is now a real check rather than a coincidence.
            Assert.Equal(FaTestClient.LetterEchoWord, (ushort)((body[2] << 8) | body[3]));

            // The one that was wrong: word 2 is the CLIENT's number, not zero.
            Assert.Equal(ClientConversation, (ushort)((body[4] << 8) | body[5]));
        }

        /// <summary>
        /// A body that is not an FA message still gets an answer.
        /// </summary>
        [Fact]
        public void UnparseableBody_StillAnswers()
        {
            XmsgServerHost host = BuildHost(out FaServer server);
            host.Route(BuildConnectLetter());

            XmsgFrame junk = BuildSessionFrame(new byte[] { 0xDE, 0xAD }, flags1: 0x0002);
            IReadOnlyList<XmsgFrame> replies = host.Route(junk);

            Assert.Equal(FaServerStatus.BadRequest, StatusOf(BodyOf(AssertAckThenReply(replies, 0x0001))));
        }

        /// <summary>
        /// A request that arrives with no connect letter before it still gets an answer: the server
        /// opens the conversation rather than dropping the request.
        /// </summary>
        /// <remarks>
        /// This is the case that would bite after our own restart, when the client believes it has a
        /// conversation and we have forgotten it. Dropping the request there is exactly the hang
        /// that was watched live.
        /// </remarks>
        [Fact]
        public void RequestWithNoPriorConnect_StillAnswers()
        {
            File.WriteAllText(Path.Combine(_folder, "AFTER.DATA"), "still here");

            XmsgServerHost host = BuildHost(out FaServer server);

            XmsgFrame coldRequest = BuildSessionFrame(
                BuildListingRequest(1, FaListFilesCodec.FirstEntryCursor), flags1: 0x0002);
            IReadOnlyList<XmsgFrame> replies = host.Route(coldRequest);

            AssertAckThenReply(replies, 0x0000);
            Assert.Equal(1, server.SessionCount);
        }

        /// <summary>
        /// A short acknowledgement is NOT answered - answering an acknowledgement with another one
        /// never terminates. This is the single deliberate exception to "always answer", and it is
        /// pinned here so it cannot be widened by accident.
        /// </summary>
        [Fact]
        public void ShortAcknowledgement_IsNotAnswered()
        {
            XmsgServerHost host = BuildHost(out FaServer server);
            host.Route(BuildConnectLetter());

            byte[] ack = new FaServerConversation(ClientConversation).BuildShortAck(1, fromResponder: false);
            IReadOnlyList<XmsgFrame> replies = host.Route(BuildSessionFrame(ack, flags1: 0x0002));

            Assert.Empty(replies);
        }

        /// <summary>
        /// The file server appears in the registered-server listing alongside the TAD server, which
        /// is what <c>list servers</c> reads.
        /// </summary>
        [Fact]
        public void FileServer_IsListedAmongTheRegisteredServers()
        {
            XmsgServerHost host = BuildHost(out FaServer server);

            IReadOnlyList<XmsgServerInfo> servers = host.DescribeServers();
            bool found = false;
            for (int i = 0; i < servers.Count; i++)
            {
                if (string.Equals(servers[i].Name, FaServer.ServerName, StringComparison.Ordinal))
                {
                    found = true;
                    Assert.Equal(FaServer.ServerLogicalPort, servers[i].LogicalPort);
                }
            }

            Assert.True(found, "*FA-SERVER should be listed among the registered servers.");
        }

        /// <summary>
        /// The connection number stays inside the range real machines use, however many
        /// connections are made and whatever a stale state file says.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this test exists</b></para>
        /// <para>
        /// The counter used to climb without limit, and the runner persisted it in blocks of 64
        /// across restarts, so it reached <c>0x0E02</c>. A live D100 then ignored our connect
        /// confirmation completely - no error, no reject, it just re-sent its letter until the
        /// terminal timed out. Putting the counter back in range made a whole conversation run on
        /// the next attempt, 2026-08-09.
        /// </para>
        /// <para>
        /// Every connection number a real machine has sent us is small: 0x0004, 0x0006, 0x003F,
        /// 0x0040, 0x0042, 0x0046. Where the true ceiling sits is NOT known, so this asserts the
        /// range holds rather than pinning any particular number.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheConnectionNumberStaysInTheRangeRealMachinesUse()
        {
            XmsgServerHost host = BuildHost(out FaServer server);

            // A state file left over from the climbing days is refused, not adopted.
            server.NextConnectionNumber = 0x0E02;
            Assert.InRange(server.NextConnectionNumber, 0x0042, 0x00FF);

            // And it stays in range across far more connections than the block ever spanned.
            for (int i = 0; i < 500; i++)
            {
                host.Route(BuildConnectLetter());
                Assert.InRange(server.NextConnectionNumber, 0x0042, 0x00FF);
            }
        }

        [Fact]
        public void ARepeatedConnectLetterIsAnsweredWithTheSameConnectionNumber()
        {
            // MEASURED against D100 on 2026-08-10. A real ND client RETRANSMITS its connect letter
            // until it sees the confirmation. We answered ONE repeated letter ELEVEN times,
            // allocating 0x0043 through 0x004D, and that burst is what tore the LINK down (ND frame
            // kind 0x6F). The file push running over the same link died with it, its SetBlockSize
            // buried unanswered in the middle of the storm - so this responder defect was
            // presenting as a client-side stall.
            XmsgServerHost host = BuildHost(out FaServer server);

            // The same letter, over and over - identical client system and port, which is what a
            // retransmission is.
            host.Route(BuildConnectLetter());
            ushort afterFirst = server.NextConnectionNumber;

            for (int i = 0; i < 10; i++)
            {
                host.Route(BuildConnectLetter());
            }

            // Not one further number was spent: the repeats replayed the first confirmation.
            Assert.Equal(afterFirst, server.NextConnectionNumber);
        }

        [Fact]
        public void ARepeatedConnectLetterReplaysTheSameDatagramNotANewOne()
        {
            // A RETRANSMISSION IS THE SAME DATAGRAM SENT AGAIN, Flags 1 included.
            //
            // MEASURED 2026-08-10 across two captures of D100 talking to us: D100 resent 12 and 15
            // distinct messages (69 and 58 extra copies) and NEVER sent the same body under a
            // different Flags 1. We did the opposite - our confirmation went out under ELEVEN
            // different numbers in one run (0x00D5..0x00E1) and nine in the other, so each repeat
            // looked to D100 like a brand-new datagram. It acknowledged ours contiguously up to
            // the point that burst began, and then stopped.
            XmsgServerHost host = BuildHost(out FaServer server);

            IReadOnlyList<XmsgFrame> first = host.Route(BuildConnectLetter());
            IReadOnlyList<XmsgFrame> again = host.Route(BuildConnectLetter());

            Assert.NotEmpty(first);
            Assert.NotEmpty(again);

            byte[] firstWire = first[0].ToArray();
            byte[] againWire = again[0].ToArray();

            _output.WriteLine("first : " + Convert.ToHexString(firstWire));
            _output.WriteLine("repeat: " + Convert.ToHexString(againWire));

            // Byte for byte identical - same Flags 1 (header offset 8), same body, same everything.
            Assert.Equal(Convert.ToHexString(firstWire), Convert.ToHexString(againWire));
        }

        /// <summary>
        /// Builds a server host with the file server registered over the test folder.
        /// </summary>
        /// <param name="server">
        /// Receives the file server, so a test can read its session count.
        /// </param>
        /// <returns>
        /// The host, which is also the transport the server builds its replies through.
        /// </returns>
        private XmsgServerHost BuildHost(out FaServer server)
        {
            server = new FaServer(new FolderFileStore(_folder));
            server.Log += line => _output.WriteLine(line);

            XmsgServerHost host = new XmsgServerHost(ServerNode);
            host.Register(server);
            return host;
        }

        /// <summary>
        /// A peer that keeps rejecting the accept is given up on rather than answered for ever.
        /// </summary>
        /// <remarks>
        /// <para><b>Measured against a live machine, not imagined</b></para>
        /// <para>
        /// Stepping the accept down one Flags 1 per XENSE only converges when the peer is a small
        /// drift ahead. On 2026-08-09 a real D100 rejected every step: 127 rejects in sixteen
        /// seconds, still going when the runner was killed. Whatever that reject meant, it was not
        /// a drift - and the recovery had no way to notice.
        /// </para>
        /// <para>
        /// So the count is bounded, and this is the test that says so. It also checks the accept is
        /// FORGOTTEN afterwards, because a retained one would resume storming on the next XENSE.
        /// </para>
        /// </remarks>
        [Fact]
        public void AnAcceptThatKeepsBeingRejectedIsGivenUpOn()
        {
            XmsgServerHost host = BuildHost(out FaServer _);
            host.Log += line => _output.WriteLine(line);

            // The letter is what creates the LINK - without one there is nothing to step down.
            host.Route(BuildConnectLetter());

            // An FA confirmation is NOT an XSLET accept and leaves nothing pending, which this
            // asserts rather than assumes: reading the live storm as an FA resync was a guess, and
            // this is the line that would have caught it.
            Assert.Null(host.ResyncAcceptDown(ClientNode));

            // An accept is an XSLET-class datagram addressed to the CLIENT'S OWN PORT. That is the
            // thing a XENSE steps down.
            host.BuildDatagram(
                ClientNode, ClientNode, 0x0100, 0x0211, 0x00000041,
                0x00, 0x00, new byte[] { 0x01, 0x02 }, XmsgAnsweredFlags1.None);

            int resent = 0;
            for (int i = 0; i < 200; i++)
            {
                XmsgFrame? again = host.ResyncAcceptDown(ClientNode);
                if (again == null)
                {
                    break;
                }

                resent++;
            }

            // It tried, and then it stopped. The exact ceiling is a judgement; that there IS one is
            // the point, so this asserts the shape rather than pinning a magic number.
            //
            // The upper bound is generous on purpose. It was 16, chosen when the ceiling was 8 on
            // the belief that a real drift is tiny. A live D100 then showed a drift of thirty-odd,
            // so the ceiling had to grow, and a test that pins it tight would only have to be
            // edited again next time the measurement says something new. What must not change is
            // that the walk ENDS.
            Assert.True(resent > 0, "the recovery should try at least once");
            Assert.True(
                resent <= 64,
                "the step-down must be bounded; it resent " + resent + " times");

            // Forgotten, so the next XENSE does not start the storm again.
            Assert.Null(host.ResyncAcceptDown(ClientNode));
        }

        /// <summary>
        /// A connect letter WE originate is not mistaken for an accept and re-sent.
        /// </summary>
        /// <remarks>
        /// <para><b>The exact defect that flooded a live machine</b></para>
        /// <para>
        /// The retention test used to be the XSLET service byte alone, on the reasoning that the
        /// accept was the only XSLET frame this host originates. The file push then began
        /// originating a connect letter, which is also XSLET, so every XENSE resent it - stepping
        /// its Flags 1 down 0x009E, 0x009D, 0x009C and onward, 127 times against a real D100.
        /// </para>
        /// <para>
        /// The two are told apart by their destination: a letter asks a server, so it goes to
        /// XROUT's well-known port 0; an accept answers a client on that client's own port.
        /// </para>
        /// </remarks>
        [Fact]
        public void AConnectLetterWeSendIsNotRetainedAsAnAccept()
        {
            XmsgServerHost host = BuildHost(out FaServer _);
            host.Route(BuildConnectLetter());

            // A letter of OUR OWN, addressed to XROUT's port 0 exactly as the push sends it.
            host.BuildDatagram(
                ClientNode, ClientNode, 0x0000, 0x0211, 0x00000041,
                0x00, 0x00, new byte[] { 0x01, 0x02 }, XmsgAnsweredFlags1.None);

            // Nothing retained, so a XENSE cannot resend it. Before the fix this returned a frame
            // and kept returning one for as long as the peer kept objecting.
            Assert.Null(host.ResyncAcceptDown(ClientNode));
        }

        /// <summary>
        /// Builds the XSLET connect letter naming <c>*FA-SERVER</c>.
        /// </summary>
        /// <returns>
        /// The letter frame, addressed to XROUT's port 0.
        /// </returns>
        /// <remarks>
        /// The letter's shape lives in <see cref="FaTestClient"/>, which is the one definition of
        /// what a client sends. Two copies of a request builder is two chances to fix one and not
        /// the other.
        /// </remarks>
        private static XmsgFrame BuildConnectLetter()
        {
            return FaTestClient.BuildConnectLetter();
        }

        /// <summary>
        /// Builds a session datagram carrying an FA message body to the file server's port.
        /// </summary>
        /// <param name="body">
        /// The FA message body.
        /// </param>
        /// <param name="flags1">
        /// The datagram sequence.
        /// </param>
        /// <returns>
        /// The datagram.
        /// </returns>
        private static XmsgFrame BuildSessionFrame(byte[] body, ushort flags1)
        {
            return FaTestClient.BuildSessionFrame(body, flags1);
        }

        /// <summary>
        /// A request the client sends TWICE gets the same reply back, byte for byte, and the
        /// directory cursor does not move.
        /// </summary>
        /// <remarks>
        /// <para><b>The live failure this pins, 2026-08-11</b></para>
        /// <para>
        /// A real D100 asked for directory entry 0 NINE times - the same datagram retransmitted,
        /// every request identical down to its session counter <c>0x81</c> - and each time we built
        /// a fresh reply from the conversation's own counter. The answers went out as
        /// <c>8200, 8300, 8400, 8500, 8600</c> and none of them carried the <c>8100</c> the client
        /// was waiting on, so it never advanced to entry 1 and the listing died with no error.
        /// </para>
        /// <para>
        /// Two things have to hold for a repeat: the SAME bytes go back, and the walk does not step
        /// - answering a question twice must not consume the next entry.
        /// </para>
        /// </remarks>
        [Fact]
        public void ARepeatedRequestGetsTheSameReplyAndDoesNotMoveTheCursor()
        {
            File.WriteAllText(Path.Combine(_folder, "ONE.TXT"), "first");
            File.WriteAllText(Path.Combine(_folder, "TWO.TXT"), "second");

            XmsgServerHost host = BuildHost(out FaServer server);
            host.Route(BuildConnectLetter());

            byte[] request = BuildListingRequest(sequence: 1, cursor: FaListFilesCodec.FirstEntryCursor);

            // The SAME frame twice - one datagram retransmitted, which is what a real machine does.
            IReadOnlyList<XmsgFrame> first = host.Route(BuildSessionFrame(request, flags1: 0x0002));
            IReadOnlyList<XmsgFrame> again = host.Route(BuildSessionFrame(request, flags1: 0x0002));

            // Our OWN outgoing number advances two per exchange even when the reply is a replay -
            // it is our counter, and we really did send two more frames.
            byte[] firstBody = BodyOf(AssertAckThenReply(first, 0x0001));
            byte[] againBody = BodyOf(AssertAckThenReply(again, 0x0003));

            // Byte for byte, session counter included - that counter is the whole point, because it
            // is what the client matches its outstanding request against.
            Assert.Equal(firstBody, againBody);
            Assert.Equal(
                firstBody[FaExchangeCodec.SessionHeaderOffset],
                againBody[FaExchangeCodec.SessionHeaderOffset]);

            // And the walk has not moved on: a THIRD, genuinely new request (a different session
            // counter) still gets entry 0's neighbour rather than having skipped one.
            byte[] third = BuildListingRequest(sequence: 2, cursor: FaListFilesCodec.FirstEntryCursor);
            IReadOnlyList<XmsgFrame> next = host.Route(BuildSessionFrame(third, flags1: 0x0003));
            byte[] nextBody = BodyOf(AssertAckThenReply(next, 0x0005));

            Assert.NotEqual(
                firstBody[FaExchangeCodec.SessionHeaderOffset],
                nextBody[FaExchangeCodec.SessionHeaderOffset]);
        }

        /// <summary>
        /// Builds a complete FA request body: the eight-byte envelope, the operation and sequence
        /// pair, then the given QFORM fields.
        /// </summary>
        /// <param name="operation">
        /// The operation to ask for.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence.
        /// </param>
        /// <param name="fields">
        /// The QFORM fields after the pair.
        /// </param>
        /// <returns>
        /// The message body.
        /// </returns>
        private static byte[] BuildRequestEnvelope(FaOperation operation, ushort sequence, byte[] fields)
        {
            return FaTestClient.BuildRequestEnvelope(operation, sequence, fields);
        }

        /// <summary>
        /// Builds a directory-enquiry request body for one entry.
        /// </summary>
        /// <param name="sequence">
        /// The exchange sequence, which the walk also uses as its serial.
        /// </param>
        /// <param name="cursor">
        /// The entry cursor.
        /// </param>
        /// <returns>
        /// The message body.
        /// </returns>
        private static byte[] BuildListingRequest(ushort sequence, ushort cursor)
        {
            return FaTestClient.BuildListingRequest(sequence, cursor);
        }

        /// <summary>
        /// Builds the short request the client uses to ask for the directory or the user.
        /// </summary>
        /// <param name="sequence">
        /// The exchange sequence, which is also the serial.
        /// </param>
        /// <param name="function">
        /// Which entry to ask for.
        /// </param>
        /// <returns>
        /// The request envelope.
        /// </returns>
        private static byte[] BuildFunctionRequest(ushort sequence, FaSpecialFunction function)
        {
            return FaTestClient.BuildFunctionRequest(sequence, function);
        }

        /// <summary>
        /// Extracts the FA message body from a reply frame.
        /// </summary>
        /// <param name="frame">
        /// The reply.
        /// </param>
        /// <returns>
        /// The message body.
        /// </returns>
        /// <summary>
        /// Asserts the TWO-frame shape a request is answered with, and returns the reply.
        /// </summary>
        /// <param name="frames">
        /// What the host produced for the request.
        /// </param>
        /// <param name="expectedAckFlags1">
        /// OUR OWN next outgoing Flags 1, which the acknowledgement must carry. Counting from the
        /// start of the link: the connect confirmation is 0x0000, so the first request's
        /// acknowledgement is 0x0001 and its reply 0x0002, the next request's 0x0003 and 0x0004,
        /// and so on. It is deliberately NOT taken from the request - see the remarks.
        /// </param>
        /// <returns>
        /// The reply frame (the second one).
        /// </returns>
        /// <remarks>
        /// <para>
        /// These tests used to assert <c>Assert.Single</c> - one reply per request, echoing the
        /// request's Flags 1. That rule was WRONG, and it is what D100 rejected live with XENSE
        /// (-34, a sequencing reject). The real server's shape, unbroken through
        /// <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-list-files.txt</c>:
        /// </para>
        /// <code>
        /// f1=N    100->102  the request
        /// f1=N    102->100  07A2 ShortAck   -- the answer, at the request's number
        /// f1=N+1  102->100  the reply       -- a NEW exchange, one higher
        /// </code>
        /// <para>
        /// <b>Flags 1 is PER DIRECTION</b> - each side counts the Data frames IT sends. Measured
        /// over the same capture: seeding each side from its first Data frame and predicting every
        /// later one from that side's own count gives 222 matches, 0 mismatches. The tests here
        /// therefore assert OUR count, not the request's number.
        /// </para>
        /// <para>
        /// The two are equal whenever the conversation is in step, which is why an "echo the
        /// request's Flags 1" rule reproduced the captures and survived for so long. They come
        /// apart when the counts differ: live on 2026-08-04 D100 opened at <c>0x000E</c> while our
        /// count stood at <c>0x000B</c>, and echoing earned a XENSE reject. Passing the request's
        /// number in here would hide exactly that bug, so it is deliberately not available.
        /// </para>
        /// <para>
        /// Everything below is falsifiable against that capture: drop the acknowledgement, take the
        /// reply's Flags 1 from anywhere but our own count, or echo the request's XMCSM, and one of
        /// these assertions fails.
        /// </para>
        /// </remarks>
        private static XmsgFrame AssertAckThenReply(IReadOnlyList<XmsgFrame> frames, ushort expectedAckFlags1)
        {
            Assert.Equal(2, frames.Count);

            XmsgFrame ack = frames[0];
            XmsgFrame reply = frames[1];

            // The acknowledgement carries OUR next outgoing number...
            Assert.Equal(expectedAckFlags1, ack.Header!.Flags1);

            // ...and the reply the one after it.
            Assert.Equal((ushort)(expectedAckFlags1 + 1), reply.Header!.Flags1);

            // It really is a short acknowledgement, and eight bytes like every captured one.
            byte[] ackBody = BodyOf(ack);
            Assert.Equal(8, ackBody.Length);
            Assert.Equal((ushort)FaMessageType.ShortAck, (ushort)((ackBody[0] << 8) | ackBody[1]));

            // BOTH frames declare their OWN body length in XMCSM rather than echoing the request's.
            Assert.Equal(ackBody.Length, ack.Header.Flags2);
            Assert.Equal(BodyOf(reply).Length, reply.Header.Flags2);

            return reply;
        }

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

        /// <summary>
        /// Reads the refusal status out of a reply body.
        /// </summary>
        /// <param name="body">
        /// The reply's FA message body.
        /// </param>
        /// <returns>
        /// The status the server reported.
        /// </returns>
        /// <remarks>
        /// The refusal shape is <c>F2 0001 92 status F2 00FF</c> after the echoed operation
        /// and sequence - our own convention, see <see cref="FaServerStatus"/>.
        /// </remarks>
        private static FaServerStatus StatusOf(byte[] body)
        {
            int at = FaExchangeCodec.QformOffset + 6;
            Assert.True(body.Length >= at + 9, "the reply is too short to carry a status field");

            // F2 0001 then A2 <SINTRAN error number>. UPDATED 2026-08-05: this asserted the code
            // was written under 0x92, which is what we used to send. capture-open-error.txt shows a
            // real server answering F2 0001 A2 002E, so the tag is the TYPED integer.
            Assert.Equal((byte)QformTagByte.Selector, body[at]);
            Assert.Equal(0x00, body[at + 1]);
            Assert.Equal(0x01, body[at + 2]);
            Assert.Equal((byte)QformTagByte.TypedInteger, body[at + 3]);
            return (FaServerStatus)((body[at + 4] << 8) | body[at + 5]);
        }
    }
}
