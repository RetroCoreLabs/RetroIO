//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Fa;
using RetroIO.Protocols.Xmsg.Qform;
using RetroIO.Protocols.Xmsg.Servers.Fa;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// The read driver, driven against a simulated server all the way to a finished file.
    /// </summary>
    /// <remarks>
    /// <para><b>Drive the client offline first</b></para>
    /// <para>
    /// This is the same discipline the push was built with, and it earned its keep there - driving
    /// the write client against a simulated server offline found a defect in our own driver before
    /// a real ND ever saw it. A live run costs minutes and a hung terminal; this costs
    /// milliseconds and says exactly which step went wrong.
    /// </para>
    /// <para><b>What it does and does not prove</b></para>
    /// <para>
    /// It proves the ladder, the block arithmetic, the content assembly and the trim. It does NOT
    /// prove the wire bytes are what a real server accepts - the simulated server here answers
    /// whatever we send it. Only the capture tests and a live run can say that, which is why both
    /// exist.
    /// </para>
    /// <para>
    /// The fake transport is local to this file rather than shared with
    /// <c>FaWriteDriverTests</c>. Moving it into <c>RetroIO.Protocols.Xmsg.TestSupport</c> would mean giving that
    /// project references to the node and protocol assemblies, which every test project would then
    /// pull in - a bigger change than the duplication is worth.
    /// </para>
    /// </remarks>
    public sealed class FaReadDriverTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Creates the fixture.
        /// </summary>
        /// <param name="output">
        /// Where to write the ladder trace.
        /// </param>
        public FaReadDriverTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// The conversation number the simulated server assigns.
        /// </summary>
        private const ushort ServerConversation = 0x0006;

        /// <summary>
        /// The word the letter asks the server to echo, and which it then stamps on its traffic.
        /// </summary>
        private const ushort LetterEchoWord = 0x0002;

        /// <summary>
        /// A whole ten-block read reassembles the file byte for byte, padding trimmed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// 20400 bytes is the file from the capture, chosen so the arithmetic is the same one a
        /// real machine did: ten blocks of 2048, twenty messages of 1024, 20480 delivered, 80 bytes
        /// of padding to drop.
        /// </para>
        /// <para>
        /// The content is a pattern where every byte depends on its own position, so a block
        /// delivered out of order, dropped, or written at the wrong offset changes the comparison.
        /// A constant fill would pass all three of those defects.
        /// </para>
        /// </remarks>
        [Fact]
        public void AWholeReadReassemblesTheFileByteForByte()
        {
            byte[] source = BuildPattern(20400);

            RecordingTransport transport = new RecordingTransport();
            FaReadDriver driver = BuildDriver();

            RunToCompletion(driver, transport, source);

            Assert.Equal(string.Empty, driver.Failure);
            Assert.True(driver.Done, "the read should have finished");

            Assert.Equal(20400, driver.FileLength);
            Assert.Equal(10, driver.BlockCount);

            byte[] pulled = driver.Content();

            // Length first, then content: a length mismatch has a clearer cause than a byte one.
            Assert.Equal(source.Length, pulled.Length);
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] != pulled[i])
                {
                    Assert.Fail(
                        "byte " + i + " of " + source.Length + " differs: source 0x"
                        + source[i].ToString("x2") + ", pulled 0x" + pulled[i].ToString("x2"));
                }
            }
        }

        /// <summary>
        /// A file that does not fill its last block still comes back at its true length.
        /// </summary>
        /// <remarks>
        /// The case the trim exists for. A 100-byte file arrives as one full 2048-byte block, so a
        /// driver that kept what it was given would return 2048 bytes and call it the file. Nothing
        /// in the content marks where the padding starts.
        /// </remarks>
        [Fact]
        public void AShortFileIsTrimmedBackToItsRealLength()
        {
            byte[] source = BuildPattern(100);

            RecordingTransport transport = new RecordingTransport();
            FaReadDriver driver = BuildDriver();

            RunToCompletion(driver, transport, source);

            Assert.Equal(string.Empty, driver.Failure);
            Assert.Equal(1, driver.BlockCount);

            byte[] pulled = driver.Content();
            Assert.Equal(100, pulled.Length);
            for (int i = 0; i < source.Length; i++)
            {
                Assert.Equal(source[i], pulled[i]);
            }
        }

        /// <summary>
        /// A file of exactly one block is not given a second one.
        /// </summary>
        /// <remarks>
        /// The boundary an off-by-one in the block arithmetic lands on. Asking for a block past the
        /// end of a file is the kind of request a real server answers with an error, which would
        /// present as a read that fails only for files whose size is an exact multiple of 2048.
        /// </remarks>
        [Fact]
        public void AFileOfExactlyOneBlockAsksForOneBlock()
        {
            byte[] source = BuildPattern(2048);

            RecordingTransport transport = new RecordingTransport();
            FaReadDriver driver = BuildDriver();

            RunToCompletion(driver, transport, source);

            Assert.Equal(string.Empty, driver.Failure);
            Assert.Equal(1, driver.BlockCount);
            Assert.Equal(2048, driver.Content().Length);
        }

        /// <summary>
        /// Every message the reader sends is an even number of bytes.
        /// </summary>
        /// <remarks>
        /// An ODD body is not rejected by a real machine - it is DROPPED IN SILENCE, which is far
        /// harder to diagnose than an error. This cost a live debugging session on the write side:
        /// a 23-byte SetBlockSize was the exact message D100 stopped answering, with no reply and
        /// no XENSE. Checked here for every message the read ladder produces.
        /// </remarks>
        [Fact]
        public void EveryMessageTheReaderSendsIsWordAligned()
        {
            byte[] source = BuildPattern(20400);

            RecordingTransport transport = new RecordingTransport();
            FaReadDriver driver = BuildDriver();

            RunToCompletion(driver, transport, source);

            Assert.Equal(string.Empty, driver.Failure);
            Assert.True(transport.Bodies.Count > 0, "the reader should have sent something");

            for (int i = 0; i < transport.Bodies.Count; i++)
            {
                byte[] body = transport.Bodies[i];
                if ((body.Length & 1) != 0)
                {
                    Assert.Fail(
                        "message " + i + " is " + body.Length
                        + " bytes, which is odd - a real machine drops it without a word");
                }
            }
        }

        /// <summary>
        /// Everything the reader sends ORIGINATES; nothing echoes.
        /// </summary>
        /// <remarks>
        /// An FA short acknowledgement travels as an ordinary Data message and spends one of OUR
        /// Flags 1 numbers - only the datagram acknowledgement, subtype <c>0x03</c>, echoes.
        /// Getting this backwards put the write path's acknowledgements behind the peer's
        /// expectation, where a datagram is dropped in silence, and it took days to find because a
        /// lockstep capture cannot tell an echo from an origination.
        /// </remarks>
        [Fact]
        public void EveryMessageTheReaderSendsOriginates()
        {
            byte[] source = BuildPattern(4096);

            RecordingTransport transport = new RecordingTransport();
            FaReadDriver driver = BuildDriver();

            RunToCompletion(driver, transport, source);

            Assert.Equal(string.Empty, driver.Failure);

            for (int i = 0; i < transport.AnsweredFlags1.Count; i++)
            {
                Assert.Equal(XmsgAnsweredFlags1.None, transport.AnsweredFlags1[i]);
            }
        }

        /// <summary>
        /// A server that sends more content than it declared fails the read.
        /// </summary>
        /// <remarks>
        /// Rather than growing the buffer to fit. It means the server and we disagree about the
        /// file, and a read that silently accepted the extra would produce a file whose length
        /// nobody agreed on.
        /// </remarks>
        [Fact]
        public void ContentBeyondTheDeclaredLengthFailsTheRead()
        {
            byte[] source = BuildPattern(2048);

            RecordingTransport transport = new RecordingTransport();
            FaReadDriver driver = BuildDriver();

            // Walk the ladder normally, then keep pushing content at it.
            RunToCompletion(driver, transport, source);
            Assert.Equal(string.Empty, driver.Failure);

            for (int i = 0; i < 4; i++)
            {
                driver.OnFrame(FrameWithBody(
                    FaFileDataCodec.BuildDataMessage(
                        ServerConversation, 0x05, 0x90BB, new byte[FaFileDataCodec.BlockLength])));
            }

            Assert.NotEqual(string.Empty, driver.Failure);
        }

        /// <summary>
        /// Another conversation's traffic on the same link does not touch the pull.
        /// </summary>
        /// <remarks>
        /// <para><b>Found on the FIRST live run, 2026-08-10</b></para>
        /// <para>
        /// Our node is a file SERVER as well as a client. Starting an Ethernet transfer REQUIRES
        /// making the far machine address us first - that is the only way the link learns its id -
        /// and the way to do that is to have it list our directory. Its short acknowledgements for
        /// that listing then arrive at the pull, which had not sent anything yet, and it died with
        /// "A short acknowledgement arrived before any request was sent".
        /// </para>
        /// <para>
        /// So the very act of enabling the transfer was what broke it. No simulated server can
        /// produce this, because a simulated server sends only our own conversation's traffic -
        /// which is exactly why the offline tests passed and the live run failed in one second.
        /// </para>
        /// <para>
        /// The port separates them: another conversation is addressed to our well-known
        /// <c>*FA-SERVER</c> port, ours to the session port we allocated.
        /// </para>
        /// </remarks>
        [Fact]
        public void AnotherConversationsTrafficDoesNotDisturbThePull()
        {
            RecordingTransport transport = new RecordingTransport();
            FaReadDriver driver = BuildDriver();

            // BEFORE WE HAVE SENT ANYTHING. This is the window that actually bites: an Ethernet
            // transfer cannot start until the far machine addresses us, so its traffic always
            // arrives before our first frame leaves. The first attempt at this fix guarded only
            // "we have a port and it is not ours", which let all of this straight through.
            driver.OnFrame(FrameForPort(BuildShortAck(), FaServer.FaServerWirePort));
            Assert.Equal(string.Empty, driver.Failure);

            // Get our port allocated and the letter sent, the way the runner does.
            driver.BuildNext(transport);

            // Somebody else's conversation, addressed to our file-server port rather than ours.
            driver.OnFrame(FrameForPort(BuildShortAck(), FaServer.FaServerWirePort));
            driver.OnFrame(FrameForPort(BuildShortAck(), FaServer.FaServerWirePort));

            Assert.Equal(string.Empty, driver.Failure);

            // And ours still works: the confirmation arrives on our own port and connects us.
            driver.OnFrame(FrameForPort(BuildConnectionConfirm(), OurSessionPort));
            Assert.Equal(FaClientAction.SendRequest, driver.NextAction());
            Assert.Equal(string.Empty, driver.Failure);
        }

        /// <summary>
        /// The port the recording transport hands out, so a test can address a frame at us.
        /// </summary>
        private const ushort OurSessionPort = 0x02F7;

        /// <summary>
        /// Wraps a body in a frame addressed to a particular port of ours.
        /// </summary>
        /// <param name="body">
        /// The message body.
        /// </param>
        /// <param name="destinationPort">
        /// The port the sender addressed.
        /// </param>
        /// <returns>
        /// The frame.
        /// </returns>
        private static XmsgFrame FrameForPort(byte[] body, ushort destinationPort)
        {
            XmsgFrame frame = new XmsgFrame();
            frame.Header.Flags1 = 0x0501;
            frame.SubHeader = new XmsgSubHeader();
            frame.SubHeader.DestinationPort = destinationPort;
            frame.SubHeader.SourcePort = 0x05B9;
            frame.TrailingBytes = body;
            return frame;
        }

        /// <summary>
        /// Builds a driver for a file on a notional server.
        /// </summary>
        /// <returns>
        /// The driver.
        /// </returns>
        private static FaReadDriver BuildDriver()
        {
            FaReadSource source = new FaReadSource(100, "D100", "BIGPSH3:TXT");
            source.LetterEchoWord = LetterEchoWord;
            return new FaReadDriver(source);
        }

        /// <summary>
        /// Drives the whole ladder, playing the server's part.
        /// </summary>
        /// <param name="driver">
        /// The driver under test.
        /// </param>
        /// <param name="transport">
        /// The recording transport.
        /// </param>
        /// <param name="source">
        /// The file the simulated server is serving.
        /// </param>
        /// <remarks>
        /// The loop is bounded rather than "until done": a driver that never finishes is a defect,
        /// and an unbounded loop turns it into a hung test run instead of a failure.
        /// </remarks>
        private void RunToCompletion(
            FaReadDriver driver, RecordingTransport transport, byte[] source)
        {
            int nextBlock = 0;

            for (int tick = 0; tick < 2000; tick++)
            {
                FaClientAction action = driver.NextAction();
                if (action == FaClientAction.Done || action == FaClientAction.Failed)
                {
                    return;
                }

                if (action == FaClientAction.Wait)
                {
                    // The server owes content. Deliver the block just asked for, as the two
                    // messages a real server sends.
                    DeliverBlock(driver, source, nextBlock - 1);
                    continue;
                }

                if (action == FaClientAction.SendConnectLetter)
                {
                    driver.BuildNext(transport);

                    // The server answers the letter. Without this the session never leaves its
                    // unconnected state and every later action is Wait - which is exactly what a
                    // real run looks like when the letter is refused.
                    driver.OnFrame(FrameWithBody(BuildConnectionConfirm()));
                    continue;
                }

                bool wasRequest = action == FaClientAction.SendRequest;
                FaOperation operation = wasRequest ? driver.CurrentOperation : FaOperation.ReadFile;
                ushort sequence = wasRequest ? driver.CurrentSequence : (ushort)0;

                driver.BuildNext(transport);

                if (!wasRequest)
                {
                    continue;
                }

                _output.WriteLine("-> " + operation + " seq " + sequence);

                // The server acknowledges the request, then replies as a new exchange.
                driver.OnFrame(FrameWithBody(BuildShortAck()));
                driver.OnFrame(FrameWithBody(BuildReply(operation, sequence, source.Length)));

                if (operation == FaOperation.ReadFile)
                {
                    nextBlock++;
                }
            }

            Assert.Fail("the read did not finish within the tick budget");
        }

        /// <summary>
        /// Sends one block as the two content messages a real server sends.
        /// </summary>
        /// <param name="driver">
        /// The driver to feed.
        /// </param>
        /// <param name="source">
        /// The file being served.
        /// </param>
        /// <param name="block">
        /// Which block, counting from zero.
        /// </param>
        /// <remarks>
        /// The block is PADDED to a full 2048, never shortened - which is what a real server does
        /// and the reason the trim has to exist at all.
        /// </remarks>
        private static void DeliverBlock(FaReadDriver driver, byte[] source, int block)
        {
            if (block < 0)
            {
                return;
            }

            for (int i = 0; i < FaWriteLadder.MessagesPerBlock; i++)
            {
                byte[] payload = new byte[FaFileDataCodec.BlockLength];

                long from = ((long)block * FaWriteLadder.ContentBytesPerBlock)
                    + ((long)i * FaFileDataCodec.BlockLength);

                for (int at = 0; at < payload.Length; at++)
                {
                    long index = from + at;
                    payload[at] = index < source.Length ? source[index] : (byte)0x00;
                }

                bool last = i == FaWriteLadder.MessagesPerBlock - 1;

                driver.OnFrame(FrameWithBody(
                    FaFileDataCodec.BuildDataMessage(
                        ServerConversation,
                        (byte)(last ? 0x86 : 0x05),
                        last ? FaFileDataCodec.LastDataMessageToken : (ushort)0x90BB,
                        payload)));
            }
        }

        /// <summary>
        /// Builds file content where every byte depends on its own position.
        /// </summary>
        /// <param name="length">
        /// How many bytes.
        /// </param>
        /// <returns>
        /// The pattern.
        /// </returns>
        /// <remarks>
        /// Two independent terms, so neither a block-sized nor a message-sized shift produces the
        /// same bytes. A pattern that repeated every 1024 or 2048 would let a misplaced block
        /// compare equal.
        /// </remarks>
        private static byte[] BuildPattern(int length)
        {
            byte[] content = new byte[length];
            for (int i = 0; i < length; i++)
            {
                content[i] = (byte)((i * 31) ^ (i >> 8));
            }

            return content;
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
        /// Builds the server's answer to the connect letter.
        /// </summary>
        /// <returns>
        /// The confirmation body.
        /// </returns>
        /// <remarks>
        /// The confirmation is where the conversation's real address arrives: the number the SERVER
        /// assigned sits in the THIRD word, and the driver stamps it on everything afterwards.
        /// </remarks>
        private static byte[] BuildConnectionConfirm()
        {
            FaServerConversation server = new FaServerConversation(ServerConversation);
            return server.BuildConnectionConfirm(
                LetterEchoWord, ServerConversation, FaExchangeCodec.ConfirmTrailingWord);
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
                body, FaMessageType.ShortAck, LetterEchoWord, 0x01,
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
        /// <param name="fileLength">
        /// The file's length, which only the OPEN reply carries.
        /// </param>
        /// <returns>
        /// The reply body.
        /// </returns>
        private static byte[] BuildReply(FaOperation operation, ushort sequence, int fileLength)
        {
            FaServerConversation server = new FaServerConversation(ServerConversation);
            server.BuildConnectionConfirm(
                LetterEchoWord, ServerConversation, FaExchangeCodec.ConfirmTrailingWord);

            if (operation == FaOperation.OpenFile)
            {
                // THE reply that matters: it carries the file's byte length under selector 3, and
                // everything after it - the block count, the trim - follows from that number.
                //
                // The FIELDS only, because BuildReply writes the operation and sequence itself.
                // F2 0002 A2 <file number>  F2 0003 A4 <byte length>  F2 00FF, which is the
                // captured shape of D100's open reply.
                byte[] fields = new byte[3 + 3 + 3 + 5 + 3];
                QformWriter writer = new QformWriter(fields);
                writer.WriteSelector(FaOpenFileCodec.PrimarySelector);
                writer.WriteTypedInteger(0x0040);
                writer.WriteSelector(FaOpenFileCodec.SecondarySelector);
                writer.WriteTypedInteger32((uint)fileLength);
                writer.WriteEndOfList();
                writer.EnsureComplete("simulated open reply fields");

                return server.BuildReply(operation, sequence, fields);
            }

            return server.BuildReply(operation, sequence, ReadOnlySpan<byte>.Empty);
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
                get { return 19999; }
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
            /// Answers whether a node can be addressed.
            /// </summary>
            /// <param name="remoteNode">
            /// Ignored; this fake has one notional link.
            /// </param>
            /// <returns>
            /// Always true.
            /// </returns>
            public bool CanReach(ushort remoteNode)
            {
                return true;
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
            /// A bare frame.
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
    }
}
