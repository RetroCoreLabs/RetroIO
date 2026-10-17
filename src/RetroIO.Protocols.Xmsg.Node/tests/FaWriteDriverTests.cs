//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Fa;
using RetroIO.Protocols.Xmsg.Servers.Fa;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// The client that ORIGINATES a file push, driven without a machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything before this answered a file server. These tests drive the other half - the one we
    /// had only ever watched on the wire - so the order, the counters and the echo/originate
    /// decision are checked before a real machine ever sees them.
    /// </para>
    /// <para>
    /// The transport is a recording fake rather than a real host. That is deliberate: a real
    /// <c>XmsgServerHost</c> refuses to build a datagram until it has learned a link seed from an
    /// inbound frame, which is a genuine property of the protocol and not something to work around
    /// in a test of sequencing.
    /// </para>
    /// </remarks>
    public sealed class FaWriteDriverTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Creates the fixture.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public FaWriteDriverTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// No step repeats itself while it is waiting for the server.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the test the whole suite was missing</b></para>
        /// <para>
        /// Every other test here feeds a reply after each step, so the driver was never asked what
        /// to do twice in a row with nothing having arrived. A real caller does exactly that - it
        /// asks on every loop tick - and the connect letter had no "already sent" state, so it went
        /// out again on every tick: 333 letters to a live machine in forty-five seconds.
        /// </para>
        /// <para>
        /// Pumping WITHOUT answering is therefore the shape of this test, and it is checked at
        /// every stage of the ladder rather than only at the start.
        /// </para>
        /// </remarks>
        [Fact]
        public void NothingIsSentTwiceWhileWaitingForTheServer()
        {
            RecordingTransport transport = new RecordingTransport();
            FaWriteDriver driver = BuildDriver(new byte[] { 1, 2, 3 });

            // The connect letter: sent once, then silence until the server confirms.
            Assert.Equal(FaClientAction.SendConnectLetter, driver.NextAction());
            Assert.Single(driver.BuildNext(transport));

            for (int i = 0; i < 20; i++)
            {
                Assert.Equal(FaClientAction.Wait, driver.NextAction());
                Assert.Empty(driver.BuildNext(transport));
            }

            // Confirmed - now the first request goes, once.
            driver.OnFrame(FrameWithBody(BuildConfirm()));
            Assert.Equal(FaClientAction.SendRequest, driver.NextAction());
            Assert.NotEmpty(driver.BuildNext(transport));

            for (int i = 0; i < 20; i++)
            {
                Assert.Equal(FaClientAction.Wait, driver.NextAction());
                Assert.Empty(driver.BuildNext(transport));
            }
        }

        /// <summary>
        /// A transport that records what it was asked to send instead of sending it.
        /// </summary>
        private sealed class RecordingTransport : IXmsgServerTransport
        {
            /// <summary>
            /// Every message body handed to the transport, in order.
            /// </summary>
            public readonly List<byte[]> Bodies = new List<byte[]>();

            /// <summary>
            /// The answeredFlags1 value each message carried, in the same order.
            /// </summary>
            public readonly List<int> AnsweredFlags1 = new List<int>();

            /// <summary>
            /// Gets this node's number.
            /// </summary>
            public ushort NodeNumber
            {
                get { return 19998; }
            }

            /// <summary>
            /// Hands out a fixed port, so a test can recognise it.
            /// </summary>
            /// <returns>
            /// The port.
            /// </returns>
            public ushort AllocateSessionPort()
            {
                return 0x02F7;
            }

            /// <summary>
            /// Whether this fake claims a link to the node. Settable so a test can hold the push
            /// back the way a real transport does before its seed is learned.
            /// </summary>
            public bool Reachable = true;

            /// <summary>
            /// Answers whether a node can be addressed.
            /// </summary>
            /// <param name="remoteNode">
            /// Ignored; this fake has one notional link.
            /// </param>
            /// <returns>
            /// <see cref="Reachable"/>.
            /// </returns>
            public bool CanReach(ushort remoteNode)
            {
                return Reachable;
            }

            /// <summary>
            /// Hands out a fixed session number.
            /// </summary>
            /// <returns>
            /// The number.
            /// </returns>
            public int AllocateSessionNumber()
            {
                return 1;
            }

            /// <summary>
            /// Records a letter.
            /// </summary>
            /// <param name="remoteNode">Unused.</param>
            /// <param name="clientSystem">Unused.</param>
            /// <param name="clientPort">Unused.</param>
            /// <param name="sourcePort">Unused.</param>
            /// <param name="controlService">Unused.</param>
            /// <param name="frameFlags">Unused.</param>
            /// <param name="role">Unused.</param>
            /// <param name="payload">The letter body, recorded.</param>
            /// <param name="answeredFlags1">Recorded.</param>
            /// <returns>
            /// A bare frame; the tests read the recorded bodies, not the frames.
            /// </returns>
            public XmsgFrame BuildDatagram(
                ushort remoteNode, ushort clientSystem, ushort clientPort, ushort sourcePort,
                uint controlService, byte frameFlags, byte role, byte[] payload, int answeredFlags1)
            {
                Bodies.Add(payload);
                AnsweredFlags1.Add(answeredFlags1);
                return new XmsgFrame();
            }

            /// <summary>
            /// Records a body message.
            /// </summary>
            /// <param name="remoteNode">Unused.</param>
            /// <param name="clientSystem">Unused.</param>
            /// <param name="clientPort">Unused.</param>
            /// <param name="sourcePort">Unused.</param>
            /// <param name="xmcsm">Unused.</param>
            /// <param name="frameFlags">Unused.</param>
            /// <param name="role">Unused.</param>
            /// <param name="body">The body, recorded.</param>
            /// <param name="answeredFlags1">Recorded.</param>
            /// <returns>
            /// A bare frame.
            /// </returns>
            public XmsgFrame BuildBodyDatagram(
                ushort remoteNode, ushort clientSystem, ushort clientPort, ushort sourcePort,
                ushort xmcsm, byte frameFlags, byte role, byte[] body, int answeredFlags1)
            {
                Bodies.Add(body);
                AnsweredFlags1.Add(answeredFlags1);
                return new XmsgFrame();
            }

            /// <summary>
            /// Records a body message that the real transport would fragment.
            /// </summary>
            /// <param name="remoteNode">Unused.</param>
            /// <param name="clientSystem">Unused.</param>
            /// <param name="clientPort">Unused.</param>
            /// <param name="sourcePort">Unused.</param>
            /// <param name="frameFlags">Unused.</param>
            /// <param name="role">Unused.</param>
            /// <param name="body">The whole body, recorded ONCE however it would be split.</param>
            /// <param name="answeredFlags1">Recorded.</param>
            /// <returns>
            /// One frame.
            /// </returns>
            public IReadOnlyList<XmsgFrame> BuildFragmentedBodyDatagram(
                ushort remoteNode, ushort clientSystem, ushort clientPort, ushort sourcePort,
                byte frameFlags, byte role, byte[] body, int answeredFlags1)
            {
                Bodies.Add(body);
                AnsweredFlags1.Add(answeredFlags1);
                return new XmsgFrame[] { new XmsgFrame() };
            }
        }

        /// <summary>
        /// Builds a driver for a small file.
        /// </summary>
        /// <param name="content">
        /// The file bytes.
        /// </param>
        /// <returns>
        /// The driver.
        /// </returns>
        private static FaWriteDriver BuildDriver(byte[] content)
        {
            FaWriteTarget target = new FaWriteTarget(102, "D19998", "\"PUSHED:DATA\"");
            return new FaWriteDriver(target, content);
        }

        /// <summary>
        /// Answers whatever the driver just sent, so the ladder can advance.
        /// </summary>
        /// <param name="driver">
        /// The driver to feed.
        /// </param>
        /// <param name="operation">
        /// The operation the reply echoes.
        /// </param>
        /// <param name="sequence">
        /// The sequence the reply echoes.
        /// </param>
        /// <remarks>
        /// A step is FOUR messages: our request, the server's short acknowledgement of it, the
        /// server's reply as a NEW exchange, and our acknowledgement of that. This plays the
        /// server's two.
        /// </remarks>
        private static void AnswerRequest(FaWriteDriver driver, FaOperation operation, ushort sequence)
        {
            driver.OnFrame(FrameWithBody(BuildShortAck()));
            driver.OnFrame(FrameWithBody(BuildReply(operation, sequence)));
        }

        /// <summary>
        /// Wraps a body in the least frame that <c>OnFrame</c> will read.
        /// </summary>
        /// <param name="body">
        /// The message body.
        /// </param>
        /// <returns>
        /// The frame.
        /// </returns>
        private static XmsgFrame FrameWithBody(byte[] body)
        {
            XmsgFrame frame = new XmsgFrame();
            frame.Header.Flags1 = 0x0501;
            frame.TrailingBytes = body;
            return frame;
        }

        /// <summary>
        /// Builds the server's short acknowledgement.
        /// </summary>
        /// <returns>
        /// The eight-byte body.
        /// </returns>
        private static byte[] BuildShortAck()
        {
            byte[] body = new byte[FaExchangeCodec.QformOffset];
            FaExchangeCodec.WriteEnvelope(
                body, FaMessageType.ShortAck, 0x0002, 0x01,
                FaServerConversation.ResponderShortAckConstant);
            return body;
        }

        /// <summary>
        /// Builds the server's reply, echoing an operation and sequence.
        /// </summary>
        /// <param name="operation">
        /// The operation to echo.
        /// </param>
        /// <param name="sequence">
        /// The sequence to echo.
        /// </param>
        /// <returns>
        /// The reply body.
        /// </returns>
        /// <remarks>
        /// A reply is message type <c>0x07F0</c> - the SAME as a request - carrying the word the
        /// server echoed in its confirmation. The driver tells them apart by that word.
        /// </remarks>
        private static byte[] BuildReply(FaOperation operation, ushort sequence)
        {
            FaServerConversation server = new FaServerConversation(0x0044);
            server.BuildConnectionConfirm(0x0002, 0x0042, FaExchangeCodec.ConfirmTrailingWord);
            return server.BuildReply(operation, sequence, ReadOnlySpan<byte>.Empty);
        }

        /// <summary>
        /// The driver walks the whole captured ladder and finishes.
        /// </summary>
        /// <remarks>
        /// A file of one block takes the three setup operations, one write, its content, then the
        /// three closing operations. A client that stopped after its blocks would leave the file
        /// unclosed and the entry reserved - which is what the first version of the ladder record
        /// would have produced.
        /// </remarks>
        [Fact]
        public void TheDriverWalksTheWholeLadder()
        {
            byte[] content = new byte[64];
            for (int i = 0; i < content.Length; i++) { content[i] = (byte)('A' + (i % 26)); }

            FaWriteDriver driver = BuildDriver(content);
            RecordingTransport transport = new RecordingTransport();

            Assert.Equal(1, driver.BlockCount);
            Assert.Equal(FaClientAction.SendConnectLetter, driver.NextAction());

            // The letter, then the confirmation.
            driver.BuildNext(transport);
            driver.OnFrame(FrameWithBody(BuildConfirm()));

            List<FaOperation> sent = new List<FaOperation>();
            int guard = 0;

            while (!driver.Done && driver.Failure.Length == 0 && guard++ < 100)
            {
                FaClientAction action = driver.NextAction();

                if (action == FaClientAction.SendRequest)
                {
                    FaOperation operation = CurrentOperation(driver);
                    ushort sequence = CurrentSequence(driver);
                    sent.Add(operation);
                    driver.BuildNext(transport);
                    AnswerRequest(driver, operation, sequence);
                    continue;
                }

                if (action == FaClientAction.Wait)
                {
                    break;      // nothing more we can play; the loop below reports it
                }

                driver.BuildNext(transport);
            }

            _output.WriteLine("operations: " + string.Join(", ", sent));
            _output.WriteLine("failure   : " + (driver.Failure.Length == 0 ? "(none)" : driver.Failure));

            Assert.Equal(string.Empty, driver.Failure);
            Assert.True(driver.Done, "the driver should reach Done");

            // The captured ladder for one block: three to set up, one write, three to finish.
            Assert.Equal(
                new FaOperation[]
                {
                    FaOperation.ReserveFileEntry,
                    FaOperation.OpenFile,
                    FaOperation.SetBlockSize,
                    FaOperation.WriteFile,
                    FaOperation.SiiiSpecial,
                    FaOperation.CloseFile,
                    FaOperation.ReleaseFileEntry,
                },
                sent.ToArray());
        }

        /// <summary>
        /// The content goes out as two messages of a whole block each.
        /// </summary>
        [Fact]
        public void OneBlockLeavesAsTwoFullMessages()
        {
            byte[] content = new byte[10];
            content[0] = 0x41;

            FaWriteDriver driver = BuildDriver(content);
            RecordingTransport transport = new RecordingTransport();

            driver.BuildNext(transport);
            driver.OnFrame(FrameWithBody(BuildConfirm()));

            // Walk the setup steps, answering as we go, and stop on the WriteFile.
            //
            // UPDATED 2026-08-10: the loop used to run a fixed four steps and expect the data to
            // be owed only after the fourth had been fully answered and acknowledged. A real
            // client does not wait - it sends WriteFile and the content back to back, and only
            // then acknowledges (see FaClientWriteSession.OnRequestSent for the capture). So the
            // walk now stops as soon as the block step comes up.
            while (CurrentOperation(driver) != FaWriteLadder.BlockOperation)
            {
                FaOperation operation = CurrentOperation(driver);
                ushort sequence = CurrentSequence(driver);
                driver.BuildNext(transport);
                AnswerRequest(driver, operation, sequence);
                driver.BuildNext(transport);            // our short ack
            }

            // The WriteFile request itself, after which the block is owed IMMEDIATELY - no reply
            // and no acknowledgement in between.
            driver.BuildNext(transport);
            Assert.Equal(FaClientAction.SendData, driver.NextAction());

            int before = transport.Bodies.Count;
            driver.BuildNext(transport);

            Assert.Equal(before + FaWriteLadder.MessagesPerBlock, transport.Bodies.Count);
            Assert.Equal(
                FaWriteLadder.CapturedContentMessageLength, transport.Bodies[before].Length);
            Assert.Equal(
                FaWriteLadder.CapturedContentMessageLength, transport.Bodies[before + 1].Length);

            // The first byte of file content sits straight after the envelope, and the rest of the
            // block is pad - there is no short block in this protocol.
            Assert.Equal(0x41, transport.Bodies[before][FaExchangeCodec.QformOffset]);
            Assert.Equal(0x00, transport.Bodies[before][FaExchangeCodec.QformOffset + 1]);
        }

        /// <summary>
        /// Every FA message we send originates - requests and short acknowledgements alike.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>CORRECTED 2026-08-10.</b> This test was called
        /// <c>RequestsOriginateAndAcknowledgementsEcho</c> and its remark read "on a real link
        /// Flags 1 is ONE value per exchange, shared by both directions". That is the shared-pool
        /// model, and it is WRONG - each side runs its OWN counter.
        /// </para>
        /// <para>
        /// The mistake is easy to make from a capture, which is why the wrong version is recorded
        /// here rather than quietly replaced: the write ladder alternates one-for-one, so two
        /// independent counters stay level and an echo looks exactly like an origination. Only a
        /// run where the two sides are NOT level tells them apart.
        /// </para>
        /// <para>
        /// Measured against D100: its originations ran a contiguous <c>008A..009B</c> with its FA
        /// short acknowledgements INSIDE that series. An FA short acknowledgement acknowledges at
        /// the FA level but travels as an ordinary Data message (subtype <c>0x0E</c>), so it spends
        /// one of the sender's own numbers. Only the DATAGRAM acknowledgement (subtype <c>0x03</c>)
        /// echoes.
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryFaMessageWeSendOriginatesIncludingShortAcknowledgements()
        {
            FaWriteDriver driver = BuildDriver(new byte[8]);
            RecordingTransport transport = new RecordingTransport();

            driver.BuildNext(transport);                       // the letter
            driver.OnFrame(FrameWithBody(BuildConfirm()));

            FaOperation operation = CurrentOperation(driver);
            ushort sequence = CurrentSequence(driver);
            driver.BuildNext(transport);                       // the first request
            AnswerRequest(driver, operation, sequence);
            driver.BuildNext(transport);                       // our short ack

            // letter, request, ack - and NONE of them answers a number of the server's.
            Assert.Equal(3, transport.AnsweredFlags1.Count);
            Assert.Equal(XmsgAnsweredFlags1.None, transport.AnsweredFlags1[0]);
            Assert.Equal(XmsgAnsweredFlags1.None, transport.AnsweredFlags1[1]);
            Assert.Equal(XmsgAnsweredFlags1.None, transport.AnsweredFlags1[2]);
        }

        /// <summary>
        /// The block count follows from the file length, not from the capture.
        /// </summary>
        [Fact]
        public void TheBlockCountFollowsFromTheFileLength()
        {
            Assert.Equal(1, BuildDriver(new byte[1]).BlockCount);
            Assert.Equal(1, BuildDriver(new byte[2048]).BlockCount);
            Assert.Equal(2, BuildDriver(new byte[2049]).BlockCount);

            // The captured session: 17905 bytes in nine blocks.
            Assert.Equal(9, BuildDriver(new byte[17905]).BlockCount);

            // An empty file still needs one block - the ladder cannot say "no content".
            Assert.Equal(1, BuildDriver(Array.Empty<byte>()).BlockCount);
        }

        /// <summary>
        /// A close from the server fails the push rather than being ignored.
        /// </summary>
        [Fact]
        public void AServerCloseFailsThePush()
        {
            FaWriteDriver driver = BuildDriver(new byte[8]);
            RecordingTransport transport = new RecordingTransport();

            driver.BuildNext(transport);
            driver.OnFrame(FrameWithBody(BuildConfirm()));

            byte[] close = new byte[8];
            NdEndian.PutBe16(close, 0, (ushort)FaMessageType.Close);
            driver.OnFrame(FrameWithBody(close));

            Assert.NotEqual(string.Empty, driver.Failure);
            Assert.Equal(FaClientAction.Failed, driver.NextAction());
        }

        /// <summary>
        /// The connect letter we put on the wire has the same shape as the one a real machine
        /// puts on the wire.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this is built through a REAL host and not the recording fake</b></para>
        /// <para>
        /// The fake records what the driver ASKED for. Both defects this test exists for lived in
        /// the translation from that ask to bytes, so a fake could not see either of them:
        /// </para>
        /// <para>
        /// 1. The letter went out through <c>BuildDatagram</c>, which composes an XROUT header of
        /// its own. <see cref="FaConnectLetter.BuildBody"/> had already built one, so the header
        /// went on TWICE - <c>0041 0023</c> in front of the real <c>1B41 0014</c> - and the
        /// receiver read the wrong four bytes as its header.
        /// </para>
        /// <para>
        /// 2. The letter named OUR system. Every recording names the DESTINATION's, which is what
        /// XROUT looks the server up by.
        /// </para>
        /// <para>
        /// D100 answered the result with a network error, 2026-08-09. The reference bytes below
        /// are the mirror of D100's own letter to us in
        /// <c>DOC/captures/FA-OPERATIONS-2026-08-08/fa-ops.pcapng</c>: same builder, our node and
        /// its node swapped.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheConnectLetterHasTheShapeARealMachineSends()
        {
            // A real host will not address a node until an inbound frame has taught it the link,
            // so the client's own letter seeds it. Node numbers: the fixture is node 19999 and the
            // machine is node 100, which is exactly the live push.
            XmsgServerHost host = new XmsgServerHost(FaTestClient.ServerNode);
            host.Route(FaTestClient.BuildConnectLetter());

            FaWriteTarget target = new FaWriteTarget(FaTestClient.ClientNode, "D100", "\"PUSHED:DATA\"");
            FaWriteDriver driver = new FaWriteDriver(target, new byte[] { 1, 2, 3 });

            IReadOnlyList<XmsgFrame> frames = driver.BuildNext(host);
            Assert.Single(frames);

            byte[] wire = frames[0].ToArray();
            _output.WriteLine("letter: " + Convert.ToHexString(wire));

            // The body starts at 28, after the 14-byte header and the 14-byte sub-header.
            const int BodyStart = 28;
            byte[] body = new byte[wire.Length - BodyStart];
            for (int i = 0; i < body.Length; i++)
            {
                body[i] = wire[BodyStart + i];
            }

            // The XROUT header appears ONCE, and it is the letter builder's: serial 0x1B, service
            // 0x41, and a declared length covering only the two strings.
            byte[] expected = FaConnectLetter.BuildBody(0x1B, "*FA-SERVER", "D100", target.LetterEchoWord);
            Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(body));
            Assert.Equal(0x1B, body[0]);
            Assert.Equal(FaConnectLetter.XsletService, body[1]);
            Assert.Equal(2 + 10 + 2 + 4, (body[2] << 8) | body[3]);

            // It names the machine we are ASKING, not ourselves. This is the whole reason the
            // first live push was refused.
            string text = System.Text.Encoding.ASCII.GetString(body);
            Assert.Contains("D100", text);
            Assert.DoesNotContain("D19999", text);

            // The sub-header's last word is the body length - 0x0022 for a four-character system
            // name, which is what node 103's letter to node 102 declared for the same shape.
            int xmcsm = (wire[26] << 8) | wire[27];
            Assert.Equal(body.Length, xmcsm);
            Assert.Equal(0x0022, xmcsm);

            // The sub-header runs 2100, 86E4, destination system, destination port, source system,
            // source port, length - so the destination port is at 20. A letter asks a server, so
            // it goes to XROUT's port 0, never to a session port.
            Assert.Equal(FaTestClient.ClientNode, (wire[18] << 8) | wire[19]);
            Assert.Equal(0x0000, (wire[20] << 8) | wire[21]);
            Assert.Equal(FaTestClient.ServerNode, (wire[22] << 8) | wire[23]);
        }

        /// <summary>
        /// The requests after the confirmation use the port and conversation number the SERVER
        /// assigned, not the ones we opened with.
        /// </summary>
        /// <remarks>
        /// <para><b>Measured against D100, 2026-08-09</b></para>
        /// <para>
        /// The push reached the server, D100 confirmed from port <c>0x05B9</c> with conversation
        /// <c>0x003F</c>, and we then sent the first request to <c>0x0257</c> - the well-known
        /// <c>*FA-SERVER</c> port - stamped with our own <c>0x0044</c>. D100 answered
        /// <c>XEIMA</c>, "invalid magic", -19.
        /// </para>
        /// <para>
        /// Both values are the server's to give. The well-known port takes the LETTER; everything
        /// after it belongs to the session the server opened. The numbers below are the ones off
        /// that wire, so this test fails the way the machine failed.
        /// </para>
        /// </remarks>
        [Fact]
        public void RequestsGoToThePortAndConversationTheServerAssigned()
        {
            const ushort AssignedPort = 0x05B9;
            const ushort AssignedConversation = 0x003F;

            XmsgServerHost host = new XmsgServerHost(FaTestClient.ServerNode);
            host.Route(FaTestClient.BuildConnectLetter());

            FaWriteTarget target = new FaWriteTarget(FaTestClient.ClientNode, "D100", "\"PUSHED:DATA\"");
            FaWriteDriver driver = new FaWriteDriver(target, new byte[] { 1, 2, 3 });

            // The letter first, which is what the server is answering.
            driver.BuildNext(host);

            // The server's confirmation, from ITS session port and carrying ITS conversation
            // number - both different from anything we chose.
            Assert.NotEqual(AssignedPort, target.ServerPort);
            Assert.NotEqual(AssignedConversation, target.Conversation);
            driver.OnFrame(ConfirmFrameFrom(AssignedPort, AssignedConversation, driver.OurPort));

            Assert.Equal(FaClientAction.SendRequest, driver.NextAction());
            IReadOnlyList<XmsgFrame> frames = driver.BuildNext(host);
            Assert.NotEmpty(frames);

            byte[] wire = frames[0].ToArray();
            _output.WriteLine("request: " + Convert.ToHexString(wire));

            // Destination port is at 20 in the sub-header.
            Assert.Equal(AssignedPort, (ushort)((wire[20] << 8) | wire[21]));

            // And the conversation word sits at the body's ConversationOffset, body starting at 28.
            const int BodyStart = 28;
            int at = BodyStart + FaExchangeCodec.ConversationOffset;
            Assert.Equal(AssignedConversation, (ushort)((wire[at] << 8) | wire[at + 1]));
        }

        [Fact]
        public void ARepeatedConnectionConfirmDoesNotRewindTheMessageCounter()
        {
            // MEASURED 2026-08-10 against D100. It sent its ConnectionConfirm TWICE - the same
            // datagram retransmitted, both at Flags 1 0x005F - and acting on the second REBUILT the
            // client conversation, which resets the message counter and the session token.
            //
            // On the wire our two requests came out
            //     07F0 0046 8000 0001   ReserveFileEntry   (112 bytes)
            //     07F0 0046 8000 0001   OpenFile           ( 42 bytes)   <- 8000 AGAIN, token 0001
            // where a real client sends 8000 then 8100, and switches off the first-exchange token
            // 0001 after the opening exchange. D100 refused the repeat and resent its reply until
            // the push died. That is the "stalls at OpenFile" symptom which was chased for days as
            // a ShortAck defect - the ShortAck was correct all along.
            const ushort AssignedPort = 0x05B9;
            const ushort AssignedConversation = 0x0046;

            XmsgServerHost host = new XmsgServerHost(FaTestClient.ServerNode);
            host.Route(FaTestClient.BuildConnectLetter());

            FaWriteTarget target = new FaWriteTarget(FaTestClient.ClientNode, "D100", "\"PUSHED:DATA\"");
            FaWriteDriver driver = new FaWriteDriver(target, new byte[] { 1, 2, 3 });

            driver.BuildNext(host);                                        // our connect letter
            driver.OnFrame(ConfirmFrameFrom(AssignedPort, AssignedConversation, driver.OurPort));

            // First request: the opening exchange.
            byte[] first = driver.BuildNext(host)[0].ToArray();

            // The server repeats its confirmation while we are mid-session. This must change
            // nothing.
            driver.OnFrame(ConfirmFrameFrom(AssignedPort, AssignedConversation, driver.OurPort));

            // Finish the first exchange so the ladder moves on to the second request: the server's
            // short acknowledgement, then its reply, then our own short acknowledgement.
            FaOperation firstOperation = CurrentOperation(driver);
            ushort firstSequence = CurrentSequence(driver);
            AnswerRequest(driver, firstOperation, firstSequence);
            driver.BuildNext(host);                                        // our ShortAck
            byte[] second = driver.BuildNext(host)[0].ToArray();

            // The counter word sits after the type and conversation, at the body's message-counter
            // offset; the body starts at 28.
            const int BodyStart = 28;
            int counterAt = BodyStart + 4;
            ushort firstCounter = (ushort)((first[counterAt] << 8) | first[counterAt + 1]);
            ushort secondCounter = (ushort)((second[counterAt] << 8) | second[counterAt + 1]);

            _output.WriteLine("first  counter: " + firstCounter.ToString("X4"));
            _output.WriteLine("second counter: " + secondCounter.ToString("X4"));

            Assert.NotEqual(firstCounter, secondCounter);
        }

        /// <summary>
        /// A file big enough to run the message counter past its wrap still counts by one per
        /// message, and the low seven bits are what carries.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a BIG file is the test</b></para>
        /// <para>
        /// The counter is seven bits with bit 7 used as a last-of-content flag, so it wraps every
        /// 128 messages. At two messages per 2048-byte block that is roughly 128 KB of file - so
        /// every capture we own, and every test we had, stays comfortably inside the first turn of
        /// the counter and could never have shown what happens at the fold.
        /// </para>
        /// <para>
        /// This drives 300 blocks (614400 bytes), which turns the counter over four times.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheMessageCounterKeepsCountingAcrossItsWrap()
        {
            const int Blocks = 300;
            byte[] content = new byte[Blocks * FaWriteLadder.ContentBytesPerBlock];
            for (int i = 0; i < content.Length; i++)
            {
                content[i] = (byte)i;
            }

            RecordingTransport transport = new RecordingTransport();
            FaWriteTarget target = new FaWriteTarget(102, "D102", "\"BIG:DATA\"");
            FaWriteDriver driver = new FaWriteDriver(target, content);

            Assert.Equal(Blocks, driver.BlockCount);

            driver.BuildNext(transport);
            driver.OnFrame(FrameWithBody(BuildConfirm()));

            RunLadderToCompletion(driver, transport);

            // Two INDEPENDENT counters run on this side, which is the first thing a long run makes
            // obvious: requests and content share the message counter, while short acknowledgements
            // have one of their own that starts at 1.
            int expectedMessage = 0;
            int expectedAck = 1;
            int messageFolds = 0;
            int ackCount = 0;

            for (int i = 1; i < transport.Bodies.Count; i++)
            {
                byte[] body = transport.Bodies[i];
                int type = (body[0] << 8) | body[1];
                byte counter = body[FaExchangeCodec.SessionHeaderOffset];

                if (type == (int)FaMessageType.ShortAck)
                {
                    Assert.Equal(expectedAck, counter);
                    expectedAck = (byte)(expectedAck + 1);
                    ackCount++;
                    continue;
                }

                if (type == (int)FaMessageType.Close
                    || ((FaMessageType)type).IsSessionFinished())
                {
                    // The teardown carries no session counter at all - its word at that offset is
                    // the conversation number. So it is neither counted nor checked here.
                    //
                    // BOTH types are skipped because the message this driver sends CHANGED on
                    // 2026-08-18: it used to send a Close (0x07C0), which is the SERVER's message,
                    // and now sends a Release (0x0782) as a real ND client does. The old test named
                    // only Close, so it started counting the Release as an ordinary message and
                    // failed on the counter. The property being asserted did not change - the
                    // teardown has a conversation number where a counter would be - so the test is
                    // widened to the family rather than the exact value.
                    continue;
                }

                // Everything else this side sends draws on the message counter, and only its LOW
                // SEVEN BITS are the count - bit 7 is the last-of-content flag.
                Assert.Equal(expectedMessage, counter & 0x7F);
                expectedMessage = (expectedMessage + 1) & 0x7F;
                if (expectedMessage == 0)
                {
                    messageFolds++;
                }
            }

            _output.WriteLine(
                $"messages={transport.Bodies.Count} acks={ackCount} counter folds={messageFolds}");

            // 300 blocks is over a thousand messages, so the seven-bit counter turns over many
            // times. If this ever reads zero the test has stopped testing what it was written for.
            Assert.True(
                messageFolds >= 4,
                "a file this size must turn the counter over repeatedly; folds=" + messageFolds);
        }

        /// <summary>
        /// Where a very large file runs the ladder's sequence number into the one bit nobody has
        /// explained.
        /// </summary>
        /// <remarks>
        /// <para><b>Arithmetic, not a transfer - deliberately</b></para>
        /// <para>
        /// Proving this by pushing a real file would mean allocating 67 MB and running 65000 ladder
        /// steps for one boundary. The sequence is a pure function of the step index, so it is
        /// checked as one.
        /// </para>
        /// <para><b>What the boundary is</b></para>
        /// <para>
        /// <see cref="FaWriteLadder.SequenceForStep"/> returns <c>stepIndex + 1</c> in a
        /// <c>ushort</c>, and the captured WriteFile sequences carry <c>0x8000</c> on every other
        /// request for reasons nothing establishes. Those two meet: once a file is big enough for
        /// the step count to reach 0x8000, the plain sequence sets that bit on its own. From then
        /// on a reader cannot tell a large sequence from a flagged one.
        /// </para>
        /// <para>
        /// This test records where that happens rather than asserting the protocol is fine there -
        /// it almost certainly is not, and no capture goes anywhere near it.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheSequenceNumberRunsIntoTheUnexplainedBitOnAVeryLargeFile()
        {
            // The step whose sequence first sets 0x8000, and the file size that reaches it.
            int firstStepWithHighBit = 0x8000 - 1;
            Assert.Equal(0x8000, FaWriteLadder.SequenceForStep(firstStepWithHighBit));
            Assert.Equal(0x7FFF, FaWriteLadder.SequenceForStep(firstStepWithHighBit - 1));

            // How many of those steps are content blocks, and therefore how big the file is.
            int overhead = FaWriteLadder.ForBlockCount(0).Length;
            int blocks = firstStepWithHighBit - overhead;
            long bytes = (long)blocks * FaWriteLadder.ContentBytesPerBlock;

            _output.WriteLine(
                $"ladder overhead {overhead} steps; sequence reaches 0x8000 at step "
                + $"{firstStepWithHighBit} = {blocks} blocks = {bytes} bytes ({bytes / 1024 / 1024} MB)");

            // A sanity floor so this cannot silently become "any file at all". If the ladder ever
            // grows enough overhead to move this materially, that is worth knowing about.
            Assert.True(bytes > 60L * 1024 * 1024, "the boundary should be tens of megabytes");

            // And the ladder itself still builds correctly at that size - the step array is the
            // prologue, one entry per block, then the epilogue, however many blocks there are.
            FaOperation[] huge = FaWriteLadder.ForBlockCount(blocks);
            Assert.Equal(firstStepWithHighBit, huge.Length);
        }

        /// <summary>
        /// Runs every remaining ladder step, answering each one the way a server does.
        /// </summary>
        /// <param name="driver">
        /// The driver to drive.
        /// </param>
        /// <param name="transport">
        /// The recording transport.
        /// </param>
        /// <remarks>
        /// A step is our request, the server's short acknowledgement, the server's reply, then our
        /// acknowledgement. Content deliveries need no answer, so they simply go out.
        /// </remarks>
        private static void RunLadderToCompletion(
            FaWriteDriver driver, RecordingTransport transport)
        {
            for (int guard = 0; guard < 20000; guard++)
            {
                FaClientAction action = driver.NextAction();
                if (action == FaClientAction.Done || action == FaClientAction.Failed)
                {
                    return;
                }

                if (action == FaClientAction.SendRequest)
                {
                    FaOperation operation = CurrentOperation(driver);
                    ushort sequence = CurrentSequence(driver);
                    driver.BuildNext(transport);
                    AnswerRequest(driver, operation, sequence);
                    continue;
                }

                driver.BuildNext(transport);
            }

            Assert.Fail("the ladder did not finish");
        }

        /// <summary>
        /// A resent reply for the step we just finished is ignored, not treated as a failure.
        /// </summary>
        /// <remarks>
        /// <para><b>Measured against D100, 2026-08-09</b></para>
        /// <para>
        /// The push got through four ladder steps and then died with "Expected a reply to OpenFile
        /// sequence 2 but got ReserveFileEntry sequence 1". Nothing was wrong with the transfer:
        /// the server had simply resent a reply it had not seen acknowledged, which is ordinary
        /// behaviour on a real link.
        /// </para>
        /// <para>
        /// The tolerance is ONE step back on purpose, so a peer that repeats everything still
        /// shows up rather than being quietly absorbed - see the sibling test below.
        /// </para>
        /// </remarks>
        [Fact]
        public void OurShortAckTakesOurOwnFlags1RatherThanEchoingTheServers()
        {
            // MEASURED against D100 on 2026-08-10. An FA short acknowledgement acknowledges at the
            // FA level but travels as an ordinary Data message (subtype 0x0E), so it spends one of
            // OUR Flags 1 numbers. Only the DATAGRAM acknowledgement (subtype 0x03) echoes.
            //
            // D100's originations ran a contiguous 008A..009B with its FA acknowledgements INSIDE
            // that series. We echoed, so ours went out at 0090 and 0094 while our own sequence was
            // already past 009A - BEHIND the peer's expectation, which is silently dropped. D100
            // never saw them and resent its reply until it gave up on the link.
            XmsgServerHost host = new XmsgServerHost(FaTestClient.ServerNode);
            host.Route(FaTestClient.BuildConnectLetter());

            FaWriteTarget target = new FaWriteTarget(FaTestClient.ClientNode, "D100", "\"PUSHED:DATA\"");
            FaWriteDriver driver = new FaWriteDriver(target, new byte[] { 1, 2, 3 });

            driver.BuildNext(host);                                   // connect letter
            driver.OnFrame(ConfirmFrameFrom(0x05B9, 0x0046, driver.OurPort));

            byte[] request = driver.BuildNext(host)[0].ToArray();      // step 0's request
            ushort requestFlags1 = (ushort)((request[8] << 8) | request[9]);

            FaOperation operation = CurrentOperation(driver);
            ushort sequence = CurrentSequence(driver);
            AnswerRequest(driver, operation, sequence);

            byte[] shortAck = driver.BuildNext(host)[0].ToArray();     // our acknowledgement
            ushort ackFlags1 = (ushort)((shortAck[8] << 8) | shortAck[9]);

            _output.WriteLine("request Flags1: " + requestFlags1.ToString("X4"));
            _output.WriteLine("ack     Flags1: " + ackFlags1.ToString("X4"));

            // It carries the NEXT number in our own series, never a value from the server's.
            Assert.Equal((ushort)(requestFlags1 + 1), ackFlags1);
        }

        [Fact]
        public void AResentShortAckAfterTheStepMovedOnIsIgnored()
        {
            // MEASURED against D100 on 2026-08-10. A real server resends anything it has not seen
            // answered, and its short acknowledgement keeps arriving AFTER we have acknowledged its
            // reply and moved to the next step. On the wire D100 repeated both its ShortAck
            // (Flags 1 0x0082) and its reply (0x0083) over and over.
            //
            // The duplicate-REPLY tolerance already existed, so the push got one step further and
            // then died on the duplicate ACK with "A short acknowledgement arrived before any
            // request was sent." Same class of defect, one message type along.
            RecordingTransport transport = new RecordingTransport();
            FaWriteDriver driver = BuildDriver(new byte[] { 1, 2, 3 });

            driver.BuildNext(transport);
            driver.OnFrame(FrameWithBody(BuildConfirm()));

            // Step 0, run to completion: our request, the server's two messages, our short ack.
            FaOperation first = CurrentOperation(driver);
            ushort firstSequence = CurrentSequence(driver);
            driver.BuildNext(transport);
            AnswerRequest(driver, first, firstSequence);
            driver.BuildNext(transport);

            // The step has moved on and step 1's request has NOT gone out yet. Now the server's
            // acknowledgement for step 0 turns up again.
            driver.OnFrame(FrameWithBody(BuildShortAck()));

            Assert.Equal(string.Empty, driver.Failure);
            Assert.NotEqual(FaClientAction.Failed, driver.NextAction());
        }

        [Fact]
        public void AResentReplyForTheStepJustFinishedIsIgnored()
        {
            RecordingTransport transport = new RecordingTransport();
            FaWriteDriver driver = BuildDriver(new byte[] { 1, 2, 3 });

            driver.BuildNext(transport);
            driver.OnFrame(FrameWithBody(BuildConfirm()));

            // Step 0, run to completion: our request, the server's two messages, our short ack.
            FaOperation first = CurrentOperation(driver);
            ushort firstSequence = CurrentSequence(driver);
            driver.BuildNext(transport);
            AnswerRequest(driver, first, firstSequence);
            driver.BuildNext(transport);

            // Step 1's request goes out and the server acknowledges it.
            FaOperation second = CurrentOperation(driver);
            ushort secondSequence = CurrentSequence(driver);
            driver.BuildNext(transport);
            driver.OnFrame(FrameWithBody(BuildShortAck()));

            // ...and then step 0's reply turns up AGAIN, while step 1's is still outstanding.
            driver.OnFrame(FrameWithBody(BuildReply(first, firstSequence)));

            Assert.Equal(string.Empty, driver.Failure);
            Assert.NotEqual(FaClientAction.Failed, driver.NextAction());

            // The reply we were actually waiting for still lands, and the ladder moves on.
            driver.OnFrame(FrameWithBody(BuildReply(second, secondSequence)));
            Assert.Equal(string.Empty, driver.Failure);
            Assert.Equal(FaClientAction.SendShortAck, driver.NextAction());
        }

        /// <summary>
        /// A reply that is neither the current step nor the one just finished still fails the push.
        /// </summary>
        /// <remarks>
        /// The duplicate tolerance must not turn into "accept anything". A reply from further back
        /// than one step means the conversation has genuinely slipped, and carrying on would send
        /// the next request against a state the server does not share.
        /// </remarks>
        [Fact]
        public void AReplyFromFurtherBackStillFailsThePush()
        {
            RecordingTransport transport = new RecordingTransport();
            FaWriteDriver driver = BuildDriver(new byte[] { 1, 2, 3 });

            driver.BuildNext(transport);
            driver.OnFrame(FrameWithBody(BuildConfirm()));

            // Run two whole steps, remembering the first.
            FaOperation first = CurrentOperation(driver);
            ushort firstSequence = CurrentSequence(driver);
            driver.BuildNext(transport);
            AnswerRequest(driver, first, firstSequence);
            driver.BuildNext(transport);

            FaOperation second = CurrentOperation(driver);
            ushort secondSequence = CurrentSequence(driver);
            driver.BuildNext(transport);
            AnswerRequest(driver, second, secondSequence);
            driver.BuildNext(transport);

            // Now on step 2, and step 0's reply turns up - two steps back, not one.
            driver.BuildNext(transport);
            driver.OnFrame(FrameWithBody(BuildShortAck()));
            driver.OnFrame(FrameWithBody(BuildReply(first, firstSequence)));

            Assert.NotEqual(string.Empty, driver.Failure);
        }

        /// <summary>
        /// Builds a connection confirmation that arrives from a given port carrying a given
        /// conversation number, the way a real server's does.
        /// </summary>
        /// <param name="fromPort">
        /// The session port the server answers from.
        /// </param>
        /// <param name="conversation">
        /// The conversation number the server assigns.
        /// </param>
        /// <returns>
        /// The frame.
        /// </returns>
        private static XmsgFrame ConfirmFrameFrom(
            ushort fromPort, ushort conversation, ushort toPort)
        {
            FaServerConversation server = new FaServerConversation(conversation);
            byte[] body = server.BuildConnectionConfirm(
                0x0002, conversation, FaExchangeCodec.ConfirmTrailingWord);

            XmsgFrame frame = new XmsgFrame();
            frame.Header.Flags1 = 0x0501;
            frame.SubHeader = new XmsgSubHeader();
            frame.SubHeader.SourcePort = fromPort;

            // ADDRESSED TO US. This used to be left zero, which no real server ever sends: a
            // confirmation comes back to the session port our connect letter named. The driver now
            // ignores frames addressed elsewhere - that is how it tells our own conversation from
            // another machine's conversation with our file server - so a frame with no destination
            // is no longer a frame it will look at.
            frame.SubHeader.DestinationPort = toPort;
            frame.TrailingBytes = body;
            return frame;
        }

        /// <summary>
        /// Builds the server's connection confirmation.
        /// </summary>
        /// <returns>
        /// The eight-byte body.
        /// </returns>
        private static byte[] BuildConfirm()
        {
            FaServerConversation server = new FaServerConversation(0x0044);
            return server.BuildConnectionConfirm(
                0x0002, 0x0042, FaExchangeCodec.ConfirmTrailingWord);
        }

        /// <summary>
        /// Reads the operation the driver's current step will send.
        /// </summary>
        /// <param name="driver">
        /// The driver.
        /// </param>
        /// <returns>
        /// The operation.
        /// </returns>
        /// <remarks>
        /// The driver does not expose the ladder, so this rebuilds the same answer from the same
        /// source it uses. If the two ever disagree the ladder has two readers, which is the thing
        /// to fix rather than paper over.
        /// </remarks>
        private static FaOperation CurrentOperation(FaWriteDriver driver)
        {
            return driver.CurrentOperation;
        }

        /// <summary>
        /// Reads the sequence the driver's current step will send.
        /// </summary>
        /// <param name="driver">
        /// The driver.
        /// </param>
        /// <returns>
        /// The sequence.
        /// </returns>
        private static ushort CurrentSequence(FaWriteDriver driver)
        {
            return driver.CurrentSequence;
        }
    }
}
